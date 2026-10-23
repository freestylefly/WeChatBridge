import AppKit
import SwiftUI

/// The window the first-run guide lives in.
///
/// AppKit rather than a SwiftUI scene for the same reason the settings window
/// is: a scene orders itself on screen during a plain `open WeChatBridge.app`, before
/// the delegate has decided whether the guide is even wanted. An `NSWindow`
/// nobody has ordered in simply is not there.
///
/// The window is rebuilt on every `show()` rather than kept: the guide reads its
/// starting step once, when its view is created, so a kept window would reopen
/// on 完成 forever after the first run — which is exactly what
/// 重新运行设置向导 must not do.
@MainActor
final class OnboardingWindowController: NSObject, NSWindowDelegate {
    private let content: () -> OnboardingFlow
    private var window: NSWindow?

    init(content: @escaping () -> OnboardingFlow) {
        self.content = content
    }

    var isVisible: Bool { window?.isVisible ?? false }

    func show() {
        // An accessory app is never activated by the system on its own, so a
        // window ordered in without this appears behind whatever the user is
        // looking at.
        NSApp.activate(ignoringOtherApps: true)

        if let window {
            window.makeKeyAndOrderFront(nil)
            return
        }

        let controller = NSHostingController(rootView: content())
        // The width is part of the layout; the height is the current step's
        // intrinsic height. Without `.preferredContentSize`, macOS 15 leaves
        // this fixed at the starting frame and clips step one's footer.
        controller.sizingOptions = [.preferredContentSize]
        let window = NSWindow(contentViewController: controller)
        window.title = ""
        window.titleVisibility = .hidden
        window.titlebarAppearsTransparent = true
        // No `.resizable`: every step is laid out to fit the design width, and
        // the height is settled below.
        window.styleMask = [.titled, .closable, .fullSizeContentView]
        window.isMovableByWindowBackground = true
        window.isReleasedWhenClosed = false
        // The permission step sends the user to System Settings, which macOS may
        // open on another Space. The guide has to follow them there.
        window.collectionBehavior = [.canJoinAllSpaces]
        window.delegate = self
        // `fullSizeContentView` puts the content view over the whole frame, so
        // use the hosting view's size as the frame rather than adding a title
        // bar back through `setContentSize`.
        let preferredHeight = max(Metrics.onboardingHeight, controller.preferredContentSize.height)
        window.setFrame(
            NSRect(
                origin: .zero,
                size: NSSize(width: Metrics.onboardingWidth, height: preferredHeight)
            ),
            display: false
        )
        window.center()
        self.window = window

        window.makeKeyAndOrderFront(nil)
    }

    func close() {
        window?.close()
    }

    /// Covers the red button as well as `close()`: either way the next `show()`
    /// has to build a fresh view, so the reference goes here rather than in
    /// `close()`.
    ///
    /// The content controller is dropped first. `isReleasedWhenClosed` is false,
    /// so once this reference goes nothing releases the window — and the whole
    /// SwiftUI tree hanging off it would stay subscribed to
    /// `didBecomeActiveNotification` and fork nine `pluginkit` children on every
    /// activation, once per abandoned run of the guide.
    func windowWillClose(_ notification: Notification) {
        window?.contentViewController = nil
        window = nil
    }
}
