import Foundation
import Security
SecKeychainSetUserInteractionAllowed(false)
let args = CommandLine.arguments
func check(_ status: OSStatus) { if status != errSecSuccess { print("status=\(status)"); exit(1) } }
let path = args[2]
var keychain: SecKeychain?
if args[1] == "create" {
    let password = UUID().uuidString
    check(SecKeychainCreate(path, UInt32(password.utf8.count), password, false, nil, &keychain))
    for (name, trusted) in [("master", args[3]), ("agent", args[4])] {
        var application: SecTrustedApplication?
        check(SecTrustedApplicationCreateFromPath(trusted, &application))
        var access: SecAccess?
        check(SecAccessCreate("WCB isolated probe" as CFString, [application!] as CFArray, &access))
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "WCB.Spike", kSecAttrAccount as String: name, kSecValueData as String: Data(UUID().uuidString.utf8), kSecAttrAccess as String: access!, kSecUseKeychain as String: keychain!]
        check(SecItemAdd(query as CFDictionary, nil))
    }
    print("created")
} else {
    check(SecKeychainOpen(path, &keychain))
    var result: CFTypeRef?
    let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "WCB.Spike", kSecAttrAccount as String: args[3], kSecReturnData as String: true, kSecMatchSearchList as String: [keychain!] ]
    let status = SecItemCopyMatching(query as CFDictionary, &result)
    print("\(args[3]): \(status)")
    if args[1] == "allow" { check(status) } else if status == errSecSuccess { exit(2) }
}
