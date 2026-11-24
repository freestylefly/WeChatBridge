import AppKit
import Combine
import Foundation
import WeChatBridgeCore

@MainActor
final class CloudAccount: ObservableObject {
    @Published var originText = ""
    @Published private(set) var username = ""
    @Published private(set) var wallet: CloudRecord = [:]
    @Published private(set) var grants: [CloudRecord] = []
    @Published private(set) var owned: [CloudRecord] = []
    @Published private(set) var catalog: [CloudRecord] = []
    @Published private(set) var requests: [CloudRecord] = []
    @Published private(set) var executions: [CloudRecord] = []
    @Published private(set) var ledger: [CloudRecord] = []
    @Published private(set) var workflowDetail:CloudRecord = [:]
    @Published private(set) var notice = ""
    @Published private(set) var busy = false
    private var origin: URL?
    private var token: String?
    private var localInputs: [String: (String, String, String)] = [:]
    var signedIn: Bool { token != nil }
    init(loadSavedAccount: Bool = true) {
        guard loadSavedAccount else { return }
        if let url = try? CommerceAPI.loadConfig() {
            originText = url.absoluteString; origin = url
            token = try? CloudKeychain.read(origin: url, kind: .user)
        } else {
            #if DEBUG
            originText = "http://127.0.0.1:8780"
            #endif
        }
    }
    private var installationID: String {
        let key = "WeChatBridge.CloudInstallation.v1"
        if let existing = UserDefaults.standard.string(forKey: key) { return existing }
        let id = "mac-" + UUID().uuidString.lowercased()
        UserDefaults.standard.set(id, forKey: key)
        return id
    }
    private func api() throws -> CommerceAPI {
        guard let origin, let token else { throw CloudFailure("unauthenticated") }
        return try CommerceAPI(baseURL: origin, token: token)
    }
    func perform(_ action: @escaping () async throws -> Void) {
        guard !busy else { return }
        busy = true; notice = ""
        Task {
            defer { busy = false }
            do { try await action() }
            catch {
                notice = error.localizedDescription
                if (error as? CloudFailure)?.code == "unauthenticated" {
                    self.token = nil
                    if let origin = self.origin { try? CloudKeychain.remove(origin: origin, kind: .user) }
                }
            }
        }
    }
    func login(name: String, password: String) {
        perform {
            guard let url = URL(string: self.originText) else { throw CloudFailure("untrusted_service_origin") }
            let client = try CommerceAPI(baseURL: url, token: nil)
            let response = try await client.request("v1/auth/login", body: ["username": .string(name), "password": .string(password), "installation_id": .string(self.installationID)])
            guard !response.text("token").isEmpty, let executable = Bundle.main.executableURL else { throw CloudFailure("invalid_response") }
            try CloudKeychain.store(response.text("token"), origin: client.baseURL, kind: .user, appPath: executable.path)
            try CommerceAPI.saveConfig(client.baseURL)
            self.origin = client.baseURL; self.token = response.text("token")
            try await self.bindDevice()
            try await self.refresh()
        }
    }
    private func bindDevice() async throws {
        guard let origin, let executable = Bundle.main.executableURL else { throw CloudFailure("service_not_configured") }
        let id = installationID
        let response = try await api().request("v1/devices", body: ["device_id": .string(String(id))])
        try CloudKeychain.store(response.text("token"), origin: origin, kind: .agent, appPath: executable.path, cliPath: executable.deletingLastPathComponent().appendingPathComponent("wcb").path)
    }
    func refresh() async throws {
        let client = try api()
        let me = try await client.request("v1/me")
        let funds = try await client.request("v1/wallet")
        let list = try await client.request("v1/catalog")
        let pending = try await client.request("v1/authorization-requests")
        let jobs = try await client.request("v1/executions")
        let history = try await client.request("v1/wallet/ledger")
        let authorizations = try await client.request("v1/grants")
        username = me.text("username"); owned = me.records("entitlements"); wallet = funds; catalog = list.records("skills")
        requests = pending.records("items"); executions = jobs.records("items"); ledger = history.records("items"); grants = authorizations.records("items")
        if !workflowDetail.text("id").isEmpty {workflowDetail=try await client.request("v1/executions/\(workflowDetail.text("id"))")}
    }
    func restore() { if signedIn { perform {
        if let origin = self.origin { _ = try CloudKeychain.read(origin: origin, kind: .user) }
        try await self.refresh()
    } } }
    func logout() {
        perform {
            _ = try await self.api().request("v1/auth/logout", body: [:])
            if let origin = self.origin {
                try CloudKeychain.remove(origin: origin, kind: .user)
                try CloudKeychain.remove(origin: origin, kind: .agent)
                try CloudKeychain.forgetVault(origin: origin)
            }
            self.token = nil; self.username = ""; self.wallet = [:]; self.catalog = []
            self.requests = []; self.executions = []; self.ledger = []; self.workflowDetail = [:]; self.owned = []; self.grants = []; self.localInputs = [:]
        }
    }
    func buy(_ id: String, expectedPoints: Int64, expectedOffer: String) {
        perform {
            let client = try self.api()
            let quote = try await client.request("v1/quotes", body: ["skill_id": .string(id)])
            guard quote.points("points") == expectedPoints, quote.text("offer_id") == expectedOffer else { try await self.refresh(); throw CloudFailure("price_changed") }
            _ = try await client.request("v1/purchases", body: ["quote_id": .string(quote.text("id")), "operation_id": .string(UUID().uuidString)])
            try await self.refresh()
        }
    }
    func prepare(_ id: String, text: String) {
        perform {
            guard !text.isEmpty, text.utf8.count <= 65536 else { throw CloudFailure("invalid_input") }
            let client = try self.api()
            let quote = try await client.request("v1/quotes", body: ["skill_id": .string(id), "input_sha256": .string(CloudOrigin.sha256(text)), "input_bytes": .integer(Int64(text.utf8.count))])
            let request = try await client.request("v1/authorization-requests", body: ["quote_id": .string(quote.text("id"))])
            self.localInputs[request.text("id")] = (quote.text("id"), text, UUID().uuidString)
            try await self.refresh()
        }
    }
    func canResume(_ id: String) -> Bool { localInputs[id] != nil }
    func decide(_ id: String, approve: Bool) {
        perform {
            let client = try self.api()
            let request = try await client.request("v1/authorization-requests/\(id)/\(approve ? "approve" : "reject")", body: [:])
            if approve, let (quote, text, operation) = self.localInputs[id] {
                _ = try await client.request("v1/executions", body: ["quote_id": .string(quote), "grant_id": .string(request.text("grant_id")), "operation_id": .string(operation), "text": .string(text)])
                self.localInputs.removeValue(forKey: id)
            }
            try await self.refresh()
        }
    }
    func budget(skill: CloudRecord, points: Int64, calls: Int) {
        perform {
            guard let origin = self.origin else { throw CloudFailure("service_not_configured") }
            let device = self.installationID
            _ = try await self.api().request("v1/grants", body: ["offer_id": .string(skill.text("offer_id")), "device_id": .string(String(device)), "limit_points": .integer(points), "max_executions": .integer(Int64(calls)), "expires_in_seconds": .integer(600)])
            try await self.refresh()
        }
    }
    func revoke(_ id: String) {
        perform { _ = try await self.api().request("v1/grants/\(id)/revoke", body: [:]); try await self.refresh() }
    }

    func installPackage(_ skill:CloudRecord,library:SkillLibrary) {
        perform {
            let client=try self.api(),id=skill.text("id")
            let release=self.owned.first{$0.text("skill_id")==id}?.text("release_id") ?? skill.text("release_id")
            let archive=try await client.downloadPackage(skillID:id,releaseID:release)
            defer{try? FileManager.default.removeItem(at:archive)}
            try library.installCloudArchive(archive,skillID:id,releaseID:release,origin:client.baseURL)
            self.notice=L10n.text("完整技能包已安装，脚本、资料与模板保留在技能库中。")
        }
    }
    func retryWorkflow(_ id:String,node:String) {
        perform {_ = try await self.api().request("v1/workflows/\(id)/retry",body:["node_id":.string(node)]);try await self.refresh()}
    }
    func showResult(_ id: String) {
        perform {
            let job = try await self.api().request("v1/executions/\(id)")
            if job.text("kind")=="workflow" {self.workflowDetail=job}
            let data = try JSONEncoder().encode(job)
            self.notice = String(data: data, encoding: .utf8) ?? job.text("state")
        }
    }
}
