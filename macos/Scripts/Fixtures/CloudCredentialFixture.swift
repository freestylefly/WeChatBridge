import Foundation
import Security
import WeChatBridgeCore

SecKeychainSetUserInteractionAllowed(false)
let args = CommandLine.arguments
let origin = try CloudOrigin.validate(URL(string: args[2])!, allowLoopback: true)
let executable = URL(fileURLWithPath: args[0]).standardizedFileURL.path
if args[1] == "store" {
    guard try CloudKeychain.read(origin: origin, kind: .user, interaction: false) == nil,
          try CloudKeychain.read(origin: origin, kind: .agent, interaction: false) == nil else { exit(2) }
    let values = try JSONDecoder().decode(CloudRecord.self, from: FileHandle.standardInput.readDataToEndOfFile())
    try CloudKeychain.store(values.text("user"), origin: origin, kind: .user, appPath: executable)
    try CloudKeychain.store(values.text("agent"), origin: origin, kind: .agent, appPath: executable, cliPath: args[3])
    guard try CloudKeychain.read(origin: origin, kind: .user, interaction: false) == values.text("user") else { exit(3) }
    guard try CloudKeychain.read(origin: origin, kind: .agent, interaction: false) == values.text("agent") else { exit(6) }
    print("stored test credentials without exposing tokens")
} else if args[1] == "clear" {
    try CloudKeychain.remove(origin: origin, kind: .agent)
    try CloudKeychain.remove(origin: origin, kind: .user)
    try CloudKeychain.forgetVault(origin: origin)
    print("test credentials removed")
} else if args[1] == "verify-agent" {
    guard let token = try CloudKeychain.read(origin: origin, kind: .agent, interaction: false), !token.isEmpty else { exit(7) }
    print("restricted credential accessible to the signed CLI identity")
} else if args[1] == "deny-master" {
    do { _ = try CloudKeychain.read(origin: origin, kind: .user, interaction: false); exit(4) }
    catch let error as CloudFailure { guard error.code == "keychain_access_-25293" || error.code == "keychain_access_-25308" else { exit(5) }; print("master access denied") }
}
