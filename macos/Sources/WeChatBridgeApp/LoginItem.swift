import AppKit
import WeChatBridgeCore
import Foundation
import ServiceManagement

/// The user-facing "open at login" switch.
///
/// `SMAppService.mainApp` registers the app itself as a login item, and macOS —
/// not WeChatBridge — decides whether that registration takes effect: the user can
/// revoke it in System Settings, and a build in an unusual location can be
/// refused outright. The published status is therefore always read back from
/// the service rather than assumed from the last write.
@MainActor
final class LoginItem: ObservableObject {
    @Published private(set) var status: SMAppService.Status = .notRegistered
    @Published private(set) var lastError: String?

    private let service = SMAppService.mainApp

    init() { refresh() }

    var isEnabled: Bool { status == .enabled }

    /// `.requiresApproval` means the registration exists but the user has to
    /// allow it in System Settings; the UI has to say so rather than showing a
    /// switch that looks on and does nothing.
    var needsApproval: Bool { status == .requiresApproval }

    func refresh() {
        status = service.status
    }

    func setEnabled(_ enabled: Bool) {
        lastError = nil
        do {
            if enabled {
                try service.register()
            } else {
                try service.unregister()
            }
        } catch {
            lastError = error.localizedDescription
        }
        refresh()
    }

    static func openLoginItemsSettings() {
        guard let url = URL(string: "x-apple.systempreferences:com.apple.LoginItems-Settings.extension") else { return }
        NSWorkspace.shared.open(url)
    }

    static func openExtensionsSettings() {
        guard let url = URL(string: "x-apple.systempreferences:com.apple.ExtensionsPreferences") else { return }
        NSWorkspace.shared.open(url)
    }
}
