import AppKit
import SwiftUI

/// WeChatBridge's settings window, opened by AppKit and only when something asks for
/// it.
///
/// This started life as a SwiftUI `Window` scene, which is §4.1's other option.
/// Measured on the signed bundle: the scene orders its window on screen during a
/// plain `open WeChatBridge.app`, before the delegate has decided anything — so a
/// launch that was only meant to put a glyph in the menu bar dropped a settings
/// window in front of whatever the user was doing, on 通用 rather than on the
/// pane first run is supposed to show. There is no pre-macOS-15 way to tell the
/// scene not to; `defaultLaunchBehavior(.suppressed)` is 15+ and WeChatBridge targets
/// 14. An `NSWindow` that nobody has ordered in simply is not there.
///
/// Everything about the chrome exists to let the content run to the top edge:
/// no title, transparent title bar, `fullSizeContentView`, and the traffic
/// lights end up floating over the navigation column.
///
/// The window resizes and remembers the size it was left at. The panes are
/// written against `Metrics.settingsWidth × settingsHeight`, so that pair is
/// the floor rather than the whole story: nothing may be dragged smaller than
/// the design, and everything past it is the user's to spend on a wider 记录
/// list or a taller 场景 page.
@MainActor
final class SettingsWindowController {
    private let content: () -> SettingsView
    private let router: SettingsRouter
    private var window: NSWindow?

    init(router: SettingsRouter, content: @escaping () -> SettingsView) {
        self.router = router
        self.content = content
    }

    var isVisible: Bool { window?.isVisible ?? false }

    /// `tab` is set before the window is ordered in, so the pane the caller
    /// asked for is the first thing drawn rather than a flash of 通用.
    func show(_ tab: SettingsTab?) {
        if let tab { router.tab = tab }

        // An accessory app is not activated by the system on its own, so a
        // window ordered in without this call would appear behind the app the
        // user is looking at and take a second click to reach.
        NSApp.activate(ignoringOtherApps: true)

        if let window {
            window.makeKeyAndOrderFront(nil)
            // An external link can change the pane while a control in the old
            // pane still owns focus. Let the next Tab start in this pane.
            if tab != nil { window.makeFirstResponder(nil) }
            return
        }

        let controller = NSHostingController(rootView: content())
        // Measured: left at its default, the hosting controller pushes its
        // preferred content size at the window and AppKit adds a title bar's
        // 28 pt on top of it, so a 560 pt design came out 588 pt tall. The size
        // here is not negotiable, so the controller does not get a vote.
        controller.sizingOptions = []
        // Nor does the pane: SwiftUI reports the pane's own layout minimum as
        // constraints on the hosting view, and AppKit sizes the window to
        // satisfy them — the 记录 pane asked for a 1521 pt window on its own,
        // and the frame autosave then remembered that size for 通用. These
        // panes are scroll views: they are written to take the window they are
        // given. An autoresizing mask says so, and leaves the size to the user.
        controller.view.translatesAutoresizingMaskIntoConstraints = true
        controller.view.autoresizingMask = [.width, .height]
        let window = NSWindow(contentViewController: controller)
        window.title = ""
        window.titleVisibility = .hidden
        window.titlebarAppearsTransparent = true
        window.styleMask = [.titled, .closable, .resizable, .fullSizeContentView]
        // The design size is a floor, not a cage: below it the panes are the
        // ones that break, and above it they simply get more room.
        window.contentMinSize = NSSize(width: Metrics.settingsWidth, height: Metrics.settingsHeight)
        // The window has no title bar to grab, so the background is the handle.
        window.isMovableByWindowBackground = true
        // Closing a menu bar app's only window must not deallocate it.
        window.isReleasedWhenClosed = false
        // `fullSizeContentView` puts the content view over the whole frame, so
        // the frame is the design size — setting the *content* size would add
        // the title bar to it again.
        window.setFrame(
            NSRect(origin: .zero, size: NSSize(width: Metrics.settingsWidth, height: Metrics.settingsHeight)),
            display: false
        )
        window.center()
        // Last, so the frame the user dragged to wins over the centred default.
        // A window that can be resized and then forgets reads as one that is
        // still fixed.
        _ = window.setFrameAutosaveName("WeChatBridgeSettings")
        self.window = window

        window.makeKeyAndOrderFront(nil)
        // Opening a pane is not a request to edit its first field or focus the
        // first sidebar item. Keyboard navigation starts with the user's Tab.
        window.makeFirstResponder(nil)
    }

    func close() {
        window?.close()
    }
}
