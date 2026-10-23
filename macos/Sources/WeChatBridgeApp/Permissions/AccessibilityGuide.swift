import AppKit
import WeChatBridgeCore
import SwiftUI

/// Walks the user through granting Accessibility.
///
/// There is no way to *ask* for this permission: the system dialog only sends
/// the user to a list they still have to find and populate themselves. So WeChatBridge
/// opens the right pane, then parks a card on top of the Settings window holding
/// a draggable copy of its own icon — the row the user is about to create. The
/// card watches for the grant and leaves on its own.
///
/// The Settings window is tracked through `CGWindowListCopyWindowInfo`, which
/// needs no permission at all. Tracking it through the Accessibility API would
/// mean a permission helper that first requires the permission it is helping to
/// obtain.
@MainActor
final class AccessibilityGuide {
    /// Transparent margin around the drawn card so its shadow is not clipped by
    /// the borderless panel. All positioning accounts for it.
    static let shadowPadding: CGFloat = 24

    private var panel: NSPanel?
    private var model = GuideModel()
    private var watcher: Task<Void, Never>?
    /// True while the user is dragging the row out: following must not move the
    /// card mid-gesture.
    private var followSuspended = false

    func present() {
        AutoPaste.openAccessibilitySettings()
        if let panel {
            panel.orderFrontRegardless()
            return
        }

        model = GuideModel()
        model.close = { [weak self] in self?.dismiss() }
        model.dragBegan = { [weak self] in self?.followSuspended = true }
        model.dragEnded = { [weak self] operation, endPoint in
            guard let self else { return }
            self.followSuspended = false
            // Letting go is the card's cue to leave: macOS may put an
            // administrator prompt right there, and a floating card has no
            // business sitting on top of it. Settings does not reliably report
            // an operation for this drop, so landing on its window counts too.
            let accepted = !operation.isEmpty
            let overSettings = Self.systemSettingsFrame()?.contains(endPoint) ?? false
            if accepted || overSettings { self.dismiss() }
        }

        let hosting = NSHostingView(rootView: GuideCard(model: model))
        hosting.frame.size = hosting.fittingSize

        // Borderless and non-activating: dragging the icon out must not pull
        // focus away from the drop target, which is the Settings window.
        let panel = NSPanel(
            contentRect: NSRect(origin: .zero, size: hosting.fittingSize),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false // the SwiftUI card draws its own
        panel.level = .floating
        // Not `.transient`: Stage Manager treats a transient window as part of
        // its owner's stage and sweeps it away the moment Settings activates —
        // exactly when the card has to stay put.
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        // Never let a mouse-down move the window: window dragging wins the
        // gesture race against the row's own drag, and the whole card would
        // travel instead. It follows Settings anyway, so moving it by hand is
        // meaningless.
        panel.isMovable = false
        panel.isMovableByWindowBackground = false
        panel.contentView = hosting
        // The card pretends to be a strip of the Settings window, so it follows
        // the system appearance rather than WeChatBridge's.
        let globals = UserDefaults.standard.persistentDomain(forName: UserDefaults.globalDomain)
        let systemDark = (globals?["AppleInterfaceStyle"] as? String) == "Dark"
        panel.appearance = NSAppearance(named: systemDark ? .darkAqua : .aqua)
        self.panel = panel

        // Placed before it is shown, never after. The previous build had the
        // card born under the pointer and then fly to the Settings window, which
        // is a third of a second of a window crossing the screen for no
        // information at all.
        let perch = Self.systemSettingsFrame().map { self.perch(beside: $0, size: hosting.fittingSize) }
        panel.setFrameOrigin(perch ?? fallbackPerch(size: hosting.fittingSize))
        panel.alphaValue = Motion.systemReducesMotion ? 1 : 0
        panel.orderFrontRegardless()
        appear(panel)
    }

    func dismiss() {
        watcher?.cancel()
        watcher = nil
        panel?.orderOut(nil)
        panel = nil
    }

    /// Fades in where it already stands. A cold-launched System Settings is
    /// still animating in at this point, so the card usually starts on the
    /// fallback corner; the watcher below slides it onto the real perch as soon
    /// as there is a window to sit beside. That slide is following, not an
    /// entrance — it is capped at `Motion.follow` and it happens to a card the
    /// user is already looking at.
    private func appear(_ panel: NSPanel) {
        model.appeared = true
        guard !Motion.systemReducesMotion else {
            startWatcher()
            return
        }
        NSAnimationContext.runAnimationGroup { context in
            context.duration = Motion.panelIn
            context.timingFunction = CAMediaTimingFunction(name: .easeOut)
            panel.animator().alphaValue = 1
        }
        startWatcher()
    }

    private func startWatcher() {
        watcher?.cancel()
        watcher = Task { [weak self] in
            // 150 ms keeps the card stuck to a Settings window being dragged; a
            // slower beat shows a visible lag. The permission probe runs on a
            // slower beat — a grant does not need checking seven times a second.
            var beat = 0
            while let self, self.panel != nil, !Task.isCancelled {
                self.tick(probing: beat.isMultiple(of: 5))
                beat += 1
                try? await Task.sleep(nanoseconds: 150_000_000)
            }
        }
    }

    private func tick(probing: Bool) {
        if !followSuspended { glideAlongsideSettings() }
        guard probing, !model.granted, AutoPaste.isTrusted else { return }
        model.granted = true
        // Long enough for the check mark to register, short enough that the card
        // is gone by the time the user switches back.
        Task { [weak self] in
            try? await Task.sleep(nanoseconds: 1_600_000_000)
            self?.dismiss()
        }
    }

    /// Holds station with Settings, so dragging that window feels like the card
    /// is attached to it.
    private func glideAlongsideSettings() {
        guard let panel, let settings = Self.systemSettingsFrame() else { return }
        let target = perch(beside: settings, size: panel.frame.size)
        guard abs(panel.frame.origin.x - target.x) > 1 || abs(panel.frame.origin.y - target.y) > 1
        else { return }
        // Slightly longer than the poll, so successive eased hops blend into one
        // continuous follow instead of discrete steps.
        NSAnimationContext.runAnimationGroup { context in
            context.duration = Motion.duration(Motion.follow)
            context.timingFunction = CAMediaTimingFunction(name: .easeOut)
            panel.animator().setFrame(NSRect(origin: target, size: panel.frame.size), display: true)
        }
    }

    /// The bottom-right corner *inside* the Settings window, like a strip laid
    /// over the permission list.
    private func perch(beside settings: NSRect, size: NSSize) -> NSPoint {
        let screen = NSScreen.screens.first { $0.frame.intersects(settings) } ?? NSScreen.main
        let visible = screen?.visibleFrame ?? settings
        let inset: CGFloat = 18
        var x = settings.maxX - inset + Self.shadowPadding - size.width
        var y = settings.minY + inset - Self.shadowPadding
        x = max(visible.minX - Self.shadowPadding, min(x, visible.maxX + Self.shadowPadding - size.width))
        y = max(visible.minY - Self.shadowPadding, min(y, visible.maxY + Self.shadowPadding - size.height))
        return NSPoint(x: x, y: y)
    }

    private func fallbackPerch(size: NSSize) -> NSPoint {
        guard let visible = NSScreen.main?.visibleFrame else { return .zero }
        return NSPoint(
            x: visible.maxX + Self.shadowPadding - size.width - 24,
            y: visible.minY - Self.shadowPadding + 24
        )
    }

    /// The System Settings window in AppKit coordinates. Matched by PID rather
    /// than by owner name, so a localised system does not break it; window
    /// bounds and owner PIDs need no permission to read.
    private static func systemSettingsFrame() -> NSRect? {
        guard let app = NSRunningApplication.runningApplications(
            withBundleIdentifier: "com.apple.systempreferences"
        ).first else { return nil }
        guard let windows = CGWindowListCopyWindowInfo(
            [.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID
        ) as? [[String: Any]] else { return nil }

        // The largest window, not the frontmost: the administrator prompt that
        // Settings raises after a drop is another layer-0 window of the same
        // process, and ordering would lock the card on top of it.
        var best: NSRect?
        for entry in windows {
            guard let pid = entry[kCGWindowOwnerPID as String] as? pid_t,
                  pid == app.processIdentifier,
                  (entry[kCGWindowLayer as String] as? Int) == 0,
                  let bounds = entry[kCGWindowBounds as String] as? [String: CGFloat],
                  let x = bounds["X"], let y = bounds["Y"],
                  let width = bounds["Width"], let height = bounds["Height"],
                  width > 200, height > 200
            else { continue }
            // CG rectangles grow downward from the main display's top-left;
            // AppKit grows upward from the bottom-left.
            let primaryHeight = NSScreen.screens.first?.frame.height ?? 0
            let rect = NSRect(x: x, y: primaryHeight - y - height, width: width, height: height)
            if rect.width * rect.height > (best.map { $0.width * $0.height } ?? 0) {
                best = rect
            }
        }
        return best
    }
}

@MainActor
private final class GuideModel: ObservableObject {
    @Published var granted = false
    @Published var appeared = false
    var close: () -> Void = {}
    var dragBegan: () -> Void = {}
    var dragEnded: (NSDragOperation, NSPoint) -> Void = { _, _ in }
}

/// A strip drawn in System Settings' own visual language — system colours, not
/// WeChatBridge's — holding one instruction and the row the user is about to create.
private struct GuideCard: View {
    @ObservedObject var model: GuideModel

    private var instruction: AttributedString {
        let markdown = L10n.text("把 **WeChatBridge** 拖进上方列表，然后打开它的开关")
        return (try? AttributedString(markdown: markdown)) ?? AttributedString(markdown)
    }

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            Button { model.close() } label: {
                Image(systemName: "chevron.left")
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(.secondary)
                    .frame(width: 22, height: 44)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel(Text(L10n.text("关闭引导")))

            VStack(alignment: .leading, spacing: 11) {
                HStack(spacing: 8) {
                    Image(systemName: model.granted ? "checkmark.circle.fill" : "arrow.up")
                        .font(.system(size: 14, weight: model.granted ? .semibold : .bold))
                        .foregroundStyle(model.granted ? Theme.brandPrimary : Theme.systemBlue)
                        .accessibilityHidden(true)
                    if model.granted {
                        Text(L10n.text("已授权，转发时可以自动粘贴了。"))
                            .font(.system(size: 13, weight: .semibold))
                            .lineLimit(1)
                    } else {
                        Text(instruction)
                            .font(.system(size: 13))
                            .lineLimit(1)
                    }
                }

                // The draggable payload, dressed as the row it becomes once
                // dropped.
                HStack(spacing: 9) {
                    Image(nsImage: NSApp.applicationIconImage ?? NSImage())
                        .resizable()
                        .frame(width: 24, height: 24)
                        .accessibilityHidden(true)
                    Text(verbatim: "微信流")
                        .font(.system(size: 13))
                    Spacer(minLength: 0)
                }
                .padding(.horizontal, 10)
                .padding(.vertical, 7)
                .background(
                    Color.primary.opacity(0.065),
                    in: RoundedRectangle(cornerRadius: 8, style: .continuous)
                )
                .overlay(AppDragHandle(began: model.dragBegan, ended: model.dragEnded))
                .accessibilityLabel(Text(L10n.text("WeChatBridge 应用，拖到辅助功能列表")))
            }
        }
        .padding(.leading, 10)
        .padding(.trailing, 18)
        .padding(.vertical, 14)
        .frame(width: 460)
        .background(
            Color(nsColor: .windowBackgroundColor),
            in: RoundedRectangle(cornerRadius: 22, style: .continuous)
        )
        .overlay(
            RoundedRectangle(cornerRadius: 22, style: .continuous)
                .strokeBorder(Color.primary.opacity(0.08), lineWidth: Stroke.hairline)
        )
        .shadow(color: .black.opacity(0.28), radius: 18, y: 6)
        .padding(AccessibilityGuide.shadowPadding)
        // 0.96, not 0.55: the card settles into place rather than growing out
        // of a point, which is the same entrance the toast uses.
        .scaleEffect(model.appeared ? 1 : 0.96, anchor: .center)
        .animation(.easeOut(duration: Motion.duration(Motion.panelIn)), value: model.appeared)
        .animation(Motion.reduced(.easeOut(duration: 0.2)), value: model.granted)
    }
}

/// An AppKit drag source laid over the row: it starts a file drag after a few
/// points of movement (SwiftUI's `.onDrag` has a perceptible hold delay) and —
/// which SwiftUI cannot do at all — reports where the drag ended and whether it
/// was accepted, which is how the card knows to get out of the way.
private struct AppDragHandle: NSViewRepresentable {
    let began: () -> Void
    let ended: (NSDragOperation, NSPoint) -> Void

    func makeNSView(context: Context) -> DragView {
        let view = DragView()
        view.began = began
        view.ended = ended
        return view
    }

    func updateNSView(_ nsView: DragView, context: Context) {
        nsView.began = began
        nsView.ended = ended
    }

    final class DragView: NSView, NSDraggingSource {
        var began: () -> Void = {}
        var ended: (NSDragOperation, NSPoint) -> Void = { _, _ in }
        private var mouseDownLocation: NSPoint?

        override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

        /// A non-opaque view
        /// hands the press to window dragging by default, and the row would
        /// never start its own drag. This card's panel is `isMovable = false`
        /// today, so this is belt and braces — but the day that changes, the
        /// one gesture the card exists for must not be the thing that breaks.
        override var mouseDownCanMoveWindow: Bool { false }

        override func mouseDown(with event: NSEvent) {
            mouseDownLocation = event.locationInWindow
        }

        override func mouseDragged(with event: NSEvent) {
            guard let start = mouseDownLocation else { return }
            let dx = event.locationInWindow.x - start.x
            let dy = event.locationInWindow.y - start.y
            guard (dx * dx + dy * dy).squareRoot() > 3 else { return }
            mouseDownLocation = nil

            // Only a real installed bundle can be dropped into the list; a
            // `swift run` build has no .app to hand over.
            let bundleURL = Bundle.main.bundleURL
            guard bundleURL.pathExtension == "app" else { return }

            let item = NSDraggingItem(pasteboardWriter: bundleURL as NSURL)
            let icon = NSWorkspace.shared.icon(forFile: bundleURL.path)
            icon.size = NSSize(width: 32, height: 32)
            let origin = convert(event.locationInWindow, from: nil)
            item.setDraggingFrame(
                NSRect(x: origin.x - 16, y: origin.y - 16, width: 32, height: 32),
                contents: icon
            )
            began()
            beginDraggingSession(with: [item], event: event, source: self)
        }

        override func mouseUp(with event: NSEvent) {
            mouseDownLocation = nil
        }

        func draggingSession(
            _ session: NSDraggingSession,
            sourceOperationMaskFor context: NSDraggingContext
        ) -> NSDragOperation {
            [.copy, .generic]
        }

        func draggingSession(
            _ session: NSDraggingSession,
            endedAt screenPoint: NSPoint,
            operation: NSDragOperation
        ) {
            ended(operation, screenPoint)
        }
    }
}
