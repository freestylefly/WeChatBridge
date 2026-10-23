import AppKit
import WeChatBridgeCore
import SwiftUI

/// A status capsule in the top-right corner of the screen, for failures only.
///
/// Forwarding to another app succeeds by leaving the user in *that* app, so
/// WeChatBridge has no window of its own to report into. A non-activating panel that
/// fades on its own is the only feedback that does not undo the thing it is
/// reporting. When there is something to fix, the toast grows a button and stops
/// being short-lived.
///
/// Nothing here reports success any more (2026-09-05, at the user's request):
/// every destination leaves its own evidence on screen, so a capsule saying
/// 「已发给 Claude」 was one more thing to dismiss over a paste the user had
/// just watched happen. One exception since 「沉淀到文件夹」: a note that lands
/// in a folder shows nothing anywhere, so that delivery reports its own success
/// — through a system notification (`DeliveryNotifier`), which is visible even
/// while this app sits in the background, where the capsule was not.
///
/// It used to appear next to the pointer, which put it over whatever the user
/// was about to click and moved it somewhere new every time. One fixed corner
/// means the user learns where to look once.
@MainActor
final class ToastPresenter {
    /// No action: long enough to read four words, short enough to be gone
    /// before the user has finished switching apps.
    private static let plainDuration: TimeInterval = 1.8
    /// Something to click has to outlive a glance.
    private static let actionableDuration: TimeInterval = 8

    private var panel: NSPanel?
    private var dismissal: Task<Void, Never>?
    private var deadline: Date?
    private var remaining: TimeInterval = 0
    private var isHovering = false

    struct Action {
        let title: String
        let perform: () -> Void
    }

    func show(
        _ message: String,
        symbol: String,
        tone: ToastView.Tone = .neutral,
        action: Action? = nil
    ) {
        dismissal?.cancel()

        let view = ToastView(message: message, symbol: symbol, tone: tone, actionTitle: action?.title) { [weak self] in
            action?.perform()
            self?.dismiss()
        }
        let panel = self.panel ?? FloatingCapsule.panel()
        self.panel = panel
        let showing = panel.isVisible

        // The hosting view has to be in a window before it can measure itself;
        // asking a detached one for `fittingSize` yields zero, and a zero-sized
        // panel is a toast nobody ever sees.
        let hosting = (panel.contentView as? ToastHostingView<ToastView>) ?? {
            let created = ToastHostingView(rootView: view)
            created.onHover = { [weak self] hovering in self?.setHovering(hovering) }
            panel.contentView = created
            return created
        }()
        hosting.rootView = view
        // The default `sizingOptions` are what make `fittingSize` report the
        // capsule's ideal width.
        let size = FloatingCapsule.measure(hosting)
        let origin = origin(for: size)

        if showing {
            // A second toast replaces the first one's content where it already
            // stands. Replaying the entrance for every file that lands would
            // make a burst of shares flicker.
            panel.setFrame(FloatingCapsule.fitted(NSRect(origin: origin, size: size), to: hosting), display: true)
            // A dismissal may have been fading it out when this one arrived;
            // without this the replacement inherits the fade and is never read.
            if panel.alphaValue < 1 {
                NSAnimationContext.runAnimationGroup { context in
                    context.duration = 0
                    panel.animator().alphaValue = 1
                }
            }
        } else {
            enter(panel, at: origin, size: FloatingCapsule.fitted(NSRect(origin: origin, size: size), to: hosting).size)
        }

        isHovering = false
        startTimer(action == nil ? Self.plainDuration : Self.actionableDuration)
    }

    func dismiss() {
        cancelTimer()
        remaining = 0
        guard let panel, panel.isVisible else { return }
        // No reduce-motion branch: §6 asks the toast to keep its fade and lose
        // only the 6 pt drop. A capsule that blinks out of existence over
        // someone else's window is harder to follow than one that fades, and
        // fading is not the kind of movement reduce motion is about.
        NSAnimationContext.runAnimationGroup { context in
            context.duration = Motion.toastOut
            panel.animator().alphaValue = 0
        } completionHandler: { [weak panel] in
            // A toast raised during the fade has already turned the alpha back
            // up; ordering out here would hide a message nobody has read.
            guard let panel, panel.alphaValue == 0 else { return }
            panel.orderOut(nil)
        }
    }

    /// Fades in six points above where it belongs and drops into place. The
    /// only movement a toast is allowed: it is what makes a replacement toast
    /// distinguishable from a new one.
    private func enter(_ panel: NSPanel, at origin: NSPoint, size: NSSize) {
        let resting = NSRect(origin: origin, size: size)
        let drops = !Motion.systemReducesMotion
        // Reduce motion removes the drop, not the fade: it lands where it
        // belongs and still fades in.
        panel.setFrame(drops ? resting.offsetBy(dx: 0, dy: Metrics.toastDrop) : resting, display: false)
        panel.alphaValue = 0
        panel.orderFrontRegardless()
        NSAnimationContext.runAnimationGroup { context in
            context.duration = Motion.toastIn
            context.timingFunction = CAMediaTimingFunction(name: .easeOut)
            panel.animator().alphaValue = 1
            if drops { panel.animator().setFrame(resting, display: true) }
        }
    }

    // MARK: - Timing

    /// The countdown stops while the pointer is on the toast: reading a failure
    /// and reaching for its button is exactly the moment it must not vanish.
    private func setHovering(_ hovering: Bool) {
        guard hovering != isHovering else { return }
        isHovering = hovering
        if hovering {
            remaining = deadline.map { max(0, $0.timeIntervalSinceNow) } ?? remaining
            cancelTimer()
        } else {
            resumeTimer()
        }
    }

    private func startTimer(_ duration: TimeInterval) {
        remaining = duration
        resumeTimer()
    }

    private func resumeTimer() {
        cancelTimer()
        let seconds = remaining
        guard !isHovering, seconds > 0 else { return }
        deadline = Date().addingTimeInterval(seconds)
        dismissal = Task { [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
            guard !Task.isCancelled else { return }
            self?.dismiss()
        }
    }

    private func cancelTimer() {
        dismissal?.cancel()
        dismissal = nil
        deadline = nil
    }

    /// The screen's top-right corner.
    private func origin(for size: NSSize) -> NSPoint {
        let visible = FloatingCapsule.visibleFrame
        let frame = NSRect(
            x: visible.maxX - Metrics.screenMargin - size.width,
            y: visible.maxY - Metrics.screenMargin - size.height,
            width: size.width,
            height: size.height
        )
        return FloatingCapsule.clamped(frame, in: visible).origin
    }
}

/// Hover is read here rather than with SwiftUI's `.onHover`, which installs a
/// tracking area scoped to the active app — and WeChatBridge is never the active app
/// when a toast is up.
final class ToastHostingView<Content: View>: NSHostingView<Content> {
    var onHover: (Bool) -> Void = { _ in }
    private var trackingArea: NSTrackingArea?

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let trackingArea { removeTrackingArea(trackingArea) }
        let area = NSTrackingArea(
            rect: bounds,
            options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect],
            owner: self
        )
        addTrackingArea(area)
        trackingArea = area
    }

    override func mouseEntered(with event: NSEvent) { onHover(true) }
    override func mouseExited(with event: NSEvent) { onHover(false) }
}

struct ToastView: View {
    enum Tone {
        case neutral, warning
    }

    let message: String
    let symbol: String
    let tone: Tone
    let actionTitle: String?
    let onAction: () -> Void

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: Space.s) {
            Image(systemName: symbol)
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(tint)
                .frame(width: 16)
            Text(message)
                .font(.system(size: 12))
                .foregroundStyle(.primary)
                .fixedSize(horizontal: false, vertical: true)
                // The cap lives here, not in the panel: clamping the window
                // instead would crop the sentence it was clamped for. The
                // allowance is what the rest of the row actually takes — the
                // fixed 140 was the width of an action button that a success
                // toast never has, so every plain message wrapped 90 pt early.
                .frame(maxWidth: Metrics.toastMaxWidth - (actionTitle == nil ? 60 : 140), alignment: .leading)
            if let actionTitle {
                Button(actionTitle, action: onAction)
                    .controlSize(.small)
                    .buttonStyle(.borderless)
                    // Follows the tone, and never `Color.accentColor`, which is
                    // whatever the user picked in System Settings. The brand
                    // green on a warning capsule painted the one remedy in the
                    // colour WeChatBridge uses for「this went well」.
                    .foregroundStyle(actionTint)
            }
        }
        .padding(.horizontal, Space.m)
        // Measured on the signed build: the capsule comes out at padding × 2 + 18,
        // so 12 made it 42 pt and §6's band is 36–40. 11 lands on 40 — the top of
        // the band, because a capsule shorter than a menu bar item reads as a
        // tooltip rather than a status report.
        .padding(.vertical, 11)
        .background(.regularMaterial)
        .clipShape(RoundedRectangle(cornerRadius: Radius.card, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: Radius.card, style: .continuous)
                .strokeBorder(Palette.hairline, lineWidth: 1)
        )
        .fixedSize()
    }

    /// `.orange` is the system's and fails 4.5:1 on a light material at 12 pt.
    /// `Palette` picks a value per appearance.
    private var tint: Color {
        switch tone {
        case .neutral: return .secondary
        case .warning: return Palette.warning
        }
    }

    /// Not the icon's tint: `.secondary` is the right weight for a symbol and
    /// too quiet for the only thing on the capsule that can be clicked.
    private var actionTint: Color {
        switch tone {
        case .neutral: return Theme.ink
        case .warning: return Palette.warning
        }
    }
}
