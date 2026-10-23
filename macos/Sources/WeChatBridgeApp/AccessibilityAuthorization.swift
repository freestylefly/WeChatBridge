import AppKit
import Combine
import WeChatBridgeCore
import Foundation

/// Tracks whether WeChatBridge may synthesise keystrokes.
///
/// The status has to be observed rather than remembered: the user grants it in
/// System Settings, in another process, at a moment WeChatBridge is not involved in.
/// macOS broadcasts a distributed notification when the Accessibility list
/// changes, which makes the grant take effect immediately instead of at the next
/// launch; a slow timer covers the case where that notification is missed.
@MainActor
final class AccessibilityAuthorization: ObservableObject {
    /// Posted by the system whenever the Accessibility list changes.
    private static let changeNotification = Notification.Name("com.apple.accessibility.api")

    @Published private(set) var isTrusted: Bool
    private let guide = AccessibilityGuide()
    private var timer: Timer?

    init() {
        isTrusted = AutoPaste.isTrusted

        DistributedNotificationCenter.default().addObserver(
            forName: Self.changeNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.refresh() }
        }

        // The notification is the fast path, not a guarantee. Two seconds is
        // slow enough to cost nothing and quick enough that a user toggling the
        // switch sees WeChatBridge notice.
        let timer = Timer(timeInterval: 2, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.refresh() }
        }
        timer.tolerance = 0.5
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    deinit {
        timer?.invalidate()
    }

    func refresh() {
        let current = AutoPaste.isTrusted
        if current != isTrusted { isTrusted = current }
    }

    /// Opens the guided flow, unless the permission is already there.
    ///
    /// There is no API that grants this: the user has to put WeChatBridge into a list
    /// themselves, so "requesting" it means showing them where and handing them
    /// something to drop.
    func guideIfNeeded() {
        refresh()
        guard !isTrusted else { return }
        guide.present()
    }
}
