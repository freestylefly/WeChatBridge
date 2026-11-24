import CryptoKit
import Foundation
import Security

public enum CloudValue: Codable, Sendable, Equatable {
    case string(String), integer(Int64), bool(Bool), object([String: CloudValue]), array([CloudValue]), null
    public init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        if c.decodeNil() { self = .null }
        else if let v = try? c.decode(Bool.self) { self = .bool(v) }
        else if let v = try? c.decode(Int64.self) { self = .integer(v) }
        else if let v = try? c.decode(String.self) { self = .string(v) }
        else if let v = try? c.decode([String: CloudValue].self) { self = .object(v) }
        else { self = .array(try c.decode([CloudValue].self)) }
    }
    public func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        switch self {
        case .string(let v): try c.encode(v)
        case .integer(let v): try c.encode(v)
        case .bool(let v): try c.encode(v)
        case .object(let v): try c.encode(v)
        case .array(let v): try c.encode(v)
        case .null: try c.encodeNil()
        }
    }
    public var string: String { if case .string(let v) = self { return v }; return "" }
    public var integer: Int64 { if case .integer(let v) = self { return v }; return 0 }
    public var object: [String: CloudValue] { if case .object(let v) = self { return v }; return [:] }
    public var array: [CloudValue] { if case .array(let v) = self { return v }; return [] }
}
public typealias CloudRecord = [String: CloudValue]
public extension Dictionary where Key == String, Value == CloudValue {
    func text(_ key: String) -> String { self[key]?.string ?? "" }
    func points(_ key: String) -> Int64 { self[key]?.integer ?? 0 }
    func records(_ key: String) -> [CloudRecord] { self[key]?.array.map(\.object) ?? [] }
}
public struct CloudFailure: Error, LocalizedError, Sendable {
    public let code: String
    public init(_ code: String) { self.code = code }
    public var errorDescription: String? {
        switch code {
        case "unauthenticated": L10n.text("登录已过期，请重新登录。")
        case "invalid_credentials": L10n.text("用户名或密码不正确。")
        case "insufficient_points": L10n.text("可用积分不足，请先检查余额与预留。")
        case "price_changed", "quote_expired": L10n.text("报价已变化或过期，请刷新后重新确认。")
        case "not_entitled": L10n.text("当前账号尚未购买此技能内容。")
        case "input_mismatch": L10n.text("输入在授权后发生变化，请重新发起。")
        case "untrusted_service_origin": L10n.text("请使用可信的 HTTPS 服务地址。")
        default:
            if code.hasPrefix("keychain_") || code.hasPrefix("vault_") { L10n.text("系统凭证访问失败，请检查签名或钥匙串授权。") }
            else { code }
        }
    }
}
public enum CloudOrigin {
    public static func validate(_ url: URL, allowLoopback: Bool = false) throws -> URL {
        guard let c = URLComponents(url: url, resolvingAgainstBaseURL: false),
              let host = c.host, !host.isEmpty, c.user == nil, c.password == nil,
              c.query == nil, c.fragment == nil, c.path.isEmpty || c.path == "/",
              c.scheme == "https" || (allowLoopback && c.scheme == "http" && ["127.0.0.1", "localhost", "[::1]", "::1"].contains(host))
        else { throw CloudFailure("untrusted_service_origin") }
        return URL(string: "\(c.scheme!)://\(url.host(percentEncoded: false)!.contains(":") ? "[\(host.trimmingCharacters(in: CharacterSet(charactersIn: "[]")))]" : host)\(c.port.map { ":\($0)" } ?? "")")!
    }
    public static var development: Bool {
        #if DEBUG
        true
        #else
        false
        #endif
    }
    public static func sha256(_ text: String) -> String { SHA256.hash(data: Data(text.utf8)).map { String(format: "%02x", $0) }.joined() }
}
private final class NoCloudRedirect: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }
}
public struct CommerceAPI: Sendable {
    public typealias Transport = @Sendable (URLRequest) async throws -> (Data, HTTPURLResponse)
    public let baseURL: URL
    private let token: String?
    private let transport: Transport
    private let customTransport: Transport?
    private static let session = URLSession(configuration: .ephemeral, delegate: NoCloudRedirect(), delegateQueue: nil)
    public init(baseURL: URL, token: String?, allowLoopback: Bool = CloudOrigin.development, transport: Transport? = nil) throws {
        self.baseURL = try CloudOrigin.validate(baseURL, allowLoopback: allowLoopback)
        self.token = token
        self.customTransport=transport
        self.transport = transport ?? { request in
            let (data, response) = try await Self.session.data(for: request)
            guard let http = response as? HTTPURLResponse else { throw CloudFailure("invalid_response") }
            return (data, http)
        }
    }
    public func request(_ path: String, body: CloudRecord? = nil) async throws -> CloudRecord {
        guard path.hasPrefix("v1/"), !path.contains(".."), !path.contains("://") else { throw CloudFailure("invalid_api_path") }
        var r = URLRequest(url: baseURL.appendingPathComponent(path))
        r.timeoutInterval = 15
        r.setValue("application/json", forHTTPHeaderField: "Accept")
        r.setValue("2",forHTTPHeaderField:"X-WCB-Client-Protocol")
        if let token { r.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
        if let body { r.httpMethod = "POST"; r.httpBody = try JSONEncoder().encode(body); r.setValue("application/json", forHTTPHeaderField: "Content-Type") }
        let (data, response) = try await transport(r)
        guard data.count <= 8_388_608 else { throw CloudFailure("response_too_large") }
        let v = try JSONDecoder().decode(CloudRecord.self, from: data)
        guard (200..<300).contains(response.statusCode) else { throw CloudFailure(v["error"]?.object.text("code") ?? "invalid_response") }
        return v
    }
    /// Download the complete immutable package with redirects disabled and a byte digest check.
    /// The caller owns the temporary archive and must remove it after import.
    public func downloadPackage(skillID:String,releaseID:String) async throws -> URL {
        guard SkillId.isValid(skillID),!releaseID.isEmpty,releaseID.count<=100,
              releaseID.allSatisfy({$0.isASCII && ($0.isLetter || $0.isNumber || "-_.".contains($0))}) else {throw CloudFailure("invalid_identifier")}
        var request=URLRequest(url:baseURL.appendingPathComponent("v1/skills/\(skillID)/releases/\(releaseID)/package"))
        request.timeoutInterval=120;request.setValue("application/zip",forHTTPHeaderField:"Accept")
        if let token {request.setValue("Bearer \(token)",forHTTPHeaderField:"Authorization")}
        let temporary:URL,response:HTTPURLResponse
        if let customTransport {
            let (bytes,http)=try await customTransport(request)
            guard bytes.count<=128*1024*1024 else {throw CloudFailure("package_too_large")}
            temporary=FileManager.default.temporaryDirectory.appendingPathComponent("cloud-package-"+UUID().uuidString+".zip")
            try bytes.write(to:temporary,options:.atomic);response=http
        } else {
            let session=URLSession(configuration:.ephemeral,delegate:CloudPackageTransfer(),delegateQueue:nil)
            defer {session.finishTasksAndInvalidate()}
            let downloaded=try await session.download(for:request)
            guard let http=downloaded.1 as? HTTPURLResponse else {throw CloudFailure("invalid_response")}
            temporary=downloaded.0;response=http
        }
        do {
            if response.statusCode==401 {throw CloudFailure("unauthenticated")}
            if response.statusCode==403 {throw CloudFailure("not_entitled")}
            guard response.statusCode==200,response.value(forHTTPHeaderField:"Content-Type")?.hasPrefix("application/zip")==true,
                  let expected=response.value(forHTTPHeaderField:"X-Artifact-SHA256"),expected.count==64,
                  expected.allSatisfy({$0.isHexDigit}) else {throw CloudFailure("invalid_package_response")}
            let reader=try FileHandle(forReadingFrom:temporary);defer{try? reader.close()};var hash=SHA256();var total=0
            while let chunk=try reader.read(upToCount:1_048_576),!chunk.isEmpty {total+=chunk.count;guard total<=128*1024*1024 else {throw CloudFailure("package_too_large")};hash.update(data:chunk)}
            let actual=hash.finalize().map{String(format:"%02x",$0)}.joined()
            guard actual==expected.lowercased() else {throw CloudFailure("package_digest_mismatch")}
            try FileManager.default.setAttributes([.posixPermissions:0o600],ofItemAtPath:temporary.path)
            return temporary
        } catch {try? FileManager.default.removeItem(at:temporary);throw error}
    }
    public static func loadConfig() throws -> URL {
        guard let config = SkillCloudConfig.load() else { throw CloudFailure("service_not_configured") }
        return try CloudOrigin.validate(config.baseURL, allowLoopback: CloudOrigin.development)
    }
    public static func saveConfig(_ origin: URL) throws {
        let url = try CloudOrigin.validate(origin, allowLoopback: CloudOrigin.development)
        let directory = SkillCloudConfig.defaultStateDirectory
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try JSONEncoder().encode(SkillCloudConfig(baseURL: url)).write(to: directory.appendingPathComponent(SkillCloudConfig.fileName), options: .atomic)
    }
}

// File keychains and SecAccess ACLs exist only on macOS.
#if os(macOS)
/// A dedicated encrypted file keychain per origin avoids relying on default
/// keychain cross-process policy. Its unlock anchor trusts only the app;
/// the separate device item trusts the app and signed CLI. Never sync.
public enum CloudKeychain {
    public enum Kind: String { case user = "User.v1", agent = "Agent.v1" }
    private static func query(_ origin: URL, _ kind: Kind, interaction: Bool = true) throws -> [String: Any] {
        let keychain = try vault(origin: origin, unlock: interaction)
        return [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "WeChatBridge.Cloud.\(kind.rawValue)", kSecAttrAccount as String: origin.absoluteString, kSecMatchSearchList as String: [keychain]]
    }
    private static func vaultPath(_ origin: URL) -> URL {
        SkillCloudConfig.defaultStateDirectory.appendingPathComponent("Keychains", isDirectory: true).appendingPathComponent(CloudOrigin.sha256(origin.absoluteString) + ".keychain")
    }
    private static func anchorQuery(_ origin: URL) throws -> [String: Any] {
        var keychain: SecKeychain?
        guard SecKeychainCopyDefault(&keychain) == errSecSuccess, let keychain else { throw CloudFailure("default_keychain_unavailable") }
        return [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "WeChatBridge.Cloud.Vault.v1", kSecAttrAccount as String: origin.absoluteString, kSecMatchSearchList as String: [keychain]]
    }
    /// App-only anchor unlocks a dedicated encrypted keychain. CLI never reads
    /// the anchor, and an unlocked vault still enforces each item's app ACL.
    private static func vault(origin: URL, unlock: Bool, createFor appPath: String? = nil) throws -> SecKeychain {
        let path = vaultPath(origin)
        var keychain: SecKeychain?
        let exists = FileManager.default.fileExists(atPath: path.path) || FileManager.default.fileExists(atPath: path.path + "-db")
        if exists, SecKeychainOpen(path.path, &keychain) == errSecSuccess, let keychain {
            if unlock {
                var q = try anchorQuery(origin); q[kSecReturnData as String] = true
                var value: CFTypeRef?
                let status = SecItemCopyMatching(q as CFDictionary, &value)
                guard status == errSecSuccess, let data = value as? Data else { throw CloudFailure("vault_anchor_access_\(status)") }
                let status2 = data.withUnsafeBytes { SecKeychainUnlock(keychain, UInt32(data.count), $0.baseAddress, true) }
                guard status2 == errSecSuccess else { throw CloudFailure("vault_unlock_failed") }
            }
            return keychain
        }
        guard let appPath else { throw CloudFailure("vault_missing_open_app") }
        try FileManager.default.createDirectory(at: path.deletingLastPathComponent(), withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        var app: SecTrustedApplication?; var access: SecAccess?
        guard SecTrustedApplicationCreateFromPath(appPath, &app) == errSecSuccess, let app,
              SecAccessCreate("WeChatBridge vault unlock" as CFString, [app] as CFArray, &access) == errSecSuccess, let access else { throw CloudFailure("vault_acl_failed") }
        let password = Data((UUID().uuidString + UUID().uuidString).utf8)
        var q = try anchorQuery(origin)
        let attrs: [String: Any] = [kSecValueData as String: password, kSecAttrAccess as String: access]
        let update = SecItemUpdate(q as CFDictionary, attrs as CFDictionary)
        if update == errSecItemNotFound {
            let search = q.removeValue(forKey: kSecMatchSearchList as String) as! [SecKeychain]
            q[kSecUseKeychain as String] = search[0]
            attrs.forEach { q[$0.key] = $0.value }
            guard SecItemAdd(q as CFDictionary, nil) == errSecSuccess else { throw CloudFailure("vault_anchor_write_failed") }
        } else if update != errSecSuccess { throw CloudFailure("vault_anchor_write_failed") }
        let status = password.withUnsafeBytes { SecKeychainCreate(path.path, UInt32(password.count), $0.baseAddress, false, nil, &keychain) }
        guard status == errSecSuccess, let keychain else { throw CloudFailure("vault_create_failed") }
        var settings = SecKeychainSettings(version: UInt32(SEC_KEYCHAIN_SETTINGS_VERS1), lockOnSleep: true, useLockInterval: true, lockInterval: 300)
        let settingsStatus = SecKeychainSetSettings(keychain, &settings)
        guard settingsStatus == errSecSuccess else { throw CloudFailure("vault_settings_failed_\(settingsStatus)") }
        return keychain
    }
    public static func forgetVault(origin: URL) throws {
        var keychain: SecKeychain?
        let path = vaultPath(origin).path
        if (FileManager.default.fileExists(atPath: path) || FileManager.default.fileExists(atPath: path + "-db")), SecKeychainOpen(path, &keychain) == errSecSuccess, let keychain {
            guard SecKeychainDelete(keychain) == errSecSuccess else { throw CloudFailure("vault_delete_failed") }
        }
        let status = SecItemDelete(try anchorQuery(origin) as CFDictionary)
        if status != errSecSuccess && status != errSecItemNotFound { throw CloudFailure("vault_anchor_delete_failed") }
    }
    public static func read(origin: URL, kind: Kind, interaction: Bool = true) throws -> String? {
        var q: [String: Any]
        do { q = try query(origin, kind, interaction: interaction) }
        catch let error as CloudFailure where error.code == "vault_missing_open_app" { return nil }
        q[kSecReturnData as String] = true; q[kSecMatchLimit as String] = kSecMatchLimitOne
        if !interaction { q[kSecUseAuthenticationUI as String] = kSecUseAuthenticationUIFail }
        var value: CFTypeRef?
        let status = SecItemCopyMatching(q as CFDictionary, &value)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = value as? Data, let token = String(data: data, encoding: .utf8) else { throw CloudFailure("keychain_access_\(status)") }
        return token
    }
    public static func store(_ token: String, origin: URL, kind: Kind, appPath: String, cliPath: String? = nil) throws {
        var trusted: [SecTrustedApplication] = []
        var paths = [appPath]
        if kind == .agent {
            guard let cliPath, FileManager.default.isExecutableFile(atPath: cliPath) else { throw CloudFailure("signed_cli_missing") }
            paths.append(cliPath)
        }
        for path in paths {
            var app: SecTrustedApplication?
            let status = SecTrustedApplicationCreateFromPath(path, &app)
            guard status == errSecSuccess, let app else { throw CloudFailure("untrusted_executable") }
            trusted.append(app)
        }
        var access: SecAccess?
        let status = SecAccessCreate("WeChatBridge \(kind.rawValue)" as CFString, trusted as CFArray, &access)
        guard status == errSecSuccess, let access else { throw CloudFailure("keychain_acl_failed") }
        let attrs: [String: Any] = [kSecValueData as String: Data(token.utf8), kSecAttrAccess as String: access]
        _ = try vault(origin: origin, unlock: true, createFor: appPath)
        let q = try query(origin, kind)
        let updated = SecItemUpdate(q as CFDictionary, attrs as CFDictionary)
        if updated == errSecItemNotFound {
            var add = q
            let search = add.removeValue(forKey: kSecMatchSearchList as String) as! [SecKeychain]
            add[kSecUseKeychain as String] = search[0]
            attrs.forEach { add[$0.key] = $0.value }
            guard SecItemAdd(add as CFDictionary, nil) == errSecSuccess else { throw CloudFailure("keychain_write_failed") }
        } else if updated != errSecSuccess { throw CloudFailure("keychain_write_failed") }
    }
    public static func remove(origin: URL, kind: Kind) throws {
        let status = SecItemDelete(try query(origin, kind) as CFDictionary)
        if status != errSecSuccess && status != errSecItemNotFound { throw CloudFailure("keychain_delete_failed") }
    }
}
#endif

private final class CloudPackageTransfer:NSObject,URLSessionDownloadDelegate,@unchecked Sendable {
    func urlSession(_ session:URLSession,task:URLSessionTask,willPerformHTTPRedirection response:HTTPURLResponse,newRequest request:URLRequest,completionHandler:@escaping(URLRequest?)->Void){completionHandler(nil)}
    func urlSession(_ session:URLSession,downloadTask:URLSessionDownloadTask,didWriteData bytesWritten:Int64,totalBytesWritten:Int64,totalBytesExpectedToWrite:Int64){if totalBytesWritten>128*1024*1024||totalBytesExpectedToWrite>128*1024*1024{downloadTask.cancel()}}
    func urlSession(_ session:URLSession,downloadTask:URLSessionDownloadTask,didFinishDownloadingTo location:URL){}
}
