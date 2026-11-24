import Foundation
import Security
import WeChatBridgeCore

SecKeychainSetUserInteractionAllowed(false)
struct RunState: Codable {
    let origin: String
    let skill: String
    let inputDigest: String
    let operation: String
    let quote: String
    let authorizationRequest: String
    let budgetGrant: String?
}
func emit(_ record: CloudRecord) throws {
    let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
    FileHandle.standardOutput.write(try encoder.encode(record))
    FileHandle.standardOutput.write(Data("\n".utf8))
}
func value(_ flag: String, in args: [String]) throws -> String? {
    let found = args.indices.filter { args[$0] == flag }
    guard found.count <= 1 else { throw CloudFailure("invalid_arguments") }
    guard let i = found.first else { return nil }
    guard args.indices.contains(i + 1), !args[i + 1].hasPrefix("--") else { throw CloudFailure("invalid_arguments") }
    return args[i + 1]
}
func identifier(_ value: String) throws -> String {
    guard !value.isEmpty, value.count <= 100, value.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || "-_.".contains($0)) }) else { throw CloudFailure("invalid_identifier") }
    return value
}
func run(_ args: [String]) async throws {
    guard !args.isEmpty else { throw CloudFailure("usage: wcb status | skill load ID | skill run ID --input-file PATH [--operation-id ID] | execution get ID | workflow retry RUN --node NODE | skill download ID --release-id RELEASE") }
    let origin = try CommerceAPI.loadConfig()
    let token = try CloudKeychain.read(origin: origin, kind: .agent, interaction: false)
    let api = try CommerceAPI(baseURL: origin, token: token)
    if let expectedOrigin = try value("--origin-id", in: args), expectedOrigin != CloudOrigin.sha256(origin.absoluteString) { throw CloudFailure("service_origin_changed") }
    if args == ["status"] { try emit(try await api.request("v1/me")); return }
    if args.count == 3, args[0] == "execution", args[1] == "get" {
        try emit(try await api.request("v1/executions/\(identifier(args[2]))")); return
    }
    if args.count>=3,args[0]=="workflow",args[1]=="retry",let node=try value("--node",in:args) {
        try emit(try await api.request("v1/workflows/\(identifier(args[2]))/retry",body:["node_id":.string(try identifier(node))]));return
    }
    guard args.count >= 3, args[0] == "skill", SkillId.isValid(args[2]) else { throw CloudFailure("invalid_arguments") }
    let skill = args[2]
    if args[1]=="download" {
        guard let release=try value("--release-id",in:args) else {throw CloudFailure("release_id_required")}
        let archive=try await api.downloadPackage(skillID:skill,releaseID:identifier(release))
        try emit(["state":.string("downloaded"),"skill_id":.string(skill),"release_id":.string(release),"path":.string(archive.path)])
        return
    }
    if args[1] == "load" {
        let release = try value("--release-id", in: args)
        let path = try release.map { "v1/skills/\(skill)/releases/\(try identifier($0))/content" } ?? "v1/skills/\(skill)/content"
        let response = try await api.request(path)
        FileHandle.standardOutput.write(Data((response.text("content") + "\n").utf8)); return
    }
    guard args[1] == "run", let path = try value("--input-file", in: args) else { throw CloudFailure("invalid_arguments") }
    let operation = try identifier(try value("--operation-id", in: args) ?? UUID().uuidString)
    let input = try FileHandle(forReadingFrom: URL(fileURLWithPath: path)); defer { try? input.close() }
    let data = try input.read(upToCount: 65537) ?? Data()
    guard !data.isEmpty, data.count <= 65536, let text = String(data: data, encoding: .utf8) else { throw CloudFailure("invalid_input") }
    let inputDigest = CloudOrigin.sha256(text)
    let actor = try await api.request("v1/me")
    let directory = SkillCloudConfig.defaultStateDirectory.appendingPathComponent("CloudOperations", isDirectory: true)
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
    let file = directory.appendingPathComponent(CloudOrigin.sha256(origin.absoluteString + actor.text("account_id") + actor.text("device_id") + operation) + ".json")
    let state: RunState
    if FileManager.default.fileExists(atPath: file.path) {
        state = try JSONDecoder().decode(RunState.self, from: Data(contentsOf: file))
        guard state.origin == origin.absoluteString, state.skill == skill, state.inputDigest == inputDigest else { throw CloudFailure("idempotency_conflict") }
    } else {
        let quote = try await api.request("v1/quotes", body: ["skill_id": .string(skill), "input_sha256": .string(inputDigest), "input_bytes": .integer(Int64(data.count))])
        let budget = try value("--grant-id", in: args)
        let request = try await budget == nil ? api.request("v1/authorization-requests", body: ["quote_id": .string(quote.text("id"))]) : [:]
        state = RunState(origin: origin.absoluteString, skill: skill, inputDigest: inputDigest, operation: operation, quote: quote.text("id"), authorizationRequest: request.text("id"), budgetGrant: budget)
        try JSONEncoder().encode(state).write(to: file, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
    }
    let request: CloudRecord
    if let budget = state.budgetGrant { request = ["state": .string("approved"), "grant_id": .string(budget)] }
    else { request = try await api.request("v1/authorization-requests/\(state.authorizationRequest)") }
    if request.text("state") == "pending" {
        let link = "wechatbridge://cloud/authorize?id=\(state.authorizationRequest)"
        let executable = URL(fileURLWithPath: CommandLine.arguments[0]).standardizedFileURL
        let app = executable.deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        if app.pathExtension == "app" {
            let process = Process(); process.executableURL = URL(fileURLWithPath: "/usr/bin/open"); process.arguments = ["-a", app.path, link]
            process.standardOutput = FileHandle.nullDevice; process.standardError = FileHandle.nullDevice
            try? process.run()
        }
        try emit(["state": .string("authorization_required"), "operation_id": .string(operation), "authorization_request_id": .string(state.authorizationRequest), "quote": request["quote"] ?? .null])
        return
    }
    guard request.text("state") == "approved" else { throw CloudFailure("authorization_\(request.text("state"))") }
    var response = try await api.request("v1/executions", body: ["quote_id": .string(state.quote), "grant_id": .string(request.text("grant_id")), "operation_id": .string(operation), "text": .string(text)])
    response["operation_id"] = .string(operation)
    try emit(response)
}
do { try await run(Array(CommandLine.arguments.dropFirst())) }
catch {
    let code = (error as? CloudFailure)?.code ?? "transport_or_local_error"
    try? emit(["error": .object(["code": .string(code)])])
    switch code {
    case "unauthenticated", "service_not_configured": exit(3)
    case "forbidden", "not_entitled", "authorization_required": exit(4)
    case "insufficient_points": exit(5)
    case "not_found": exit(7)
    default: exit(6)
    }
}
