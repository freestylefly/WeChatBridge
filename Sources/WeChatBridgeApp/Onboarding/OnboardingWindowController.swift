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
        // Left at its default the hosting controller pushes its preferred size
        // at the window and AppKit adds a title bar on top of it; the guide is
        // designed at one width and does not get a vote on it.
        controller.sizingOptions = []
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
        // The design's size. `fullSizeContentView` puts the content view over
        // the whole frame, so the frame is the design size — setting the
        // *content* size would add a title bar's height back on.
        //
        // Where the guide's own layout asks for more — a step whose roster has
        // outgrown the design, which is how the footer button came to sit under
        // the bottom edge of step one — the hosting view's constraints take the
        // window with them: the size below is where it starts, not a cage.
        window.setFrame(
            NSRect(
                origin: .zero,
                size: NSSize(width: Metrics.onboardingWidth, height: Metrics.onboardingHeight)
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
