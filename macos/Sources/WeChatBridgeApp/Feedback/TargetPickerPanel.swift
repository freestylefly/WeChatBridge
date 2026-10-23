import AppKit
import WeChatBridgeCore
import SwiftUI

/// The question 「发送到自定义」 asks, now that the extension has no interface
/// to ask it in.
///
/// The share extension used to draw this list inside the host's share sheet.
/// That is the panel the user called 「无敌丑」 (2026-09-05): it arrives with the
/// system's sheet animation, over WeChat, before anything has happened. Asking
/// here instead costs one floating capsule, the same surface every other
/// WeChatBridge message uses, and it only appears when there is a genuine choice
/// to make: no apps and one app are answered without showing anything at all
/// (`CustomForwardDecision`).
///
/// Two rules learned from a bug the user found in the first build of this panel
/// (a grey fragment showing a sliver of two app icons):
///
/// 1. **The window is built and thrown away with the question.** A panel that
///    outlives the request it belongs to is a window nothing is waiting for and
///    nothing will ever close.
/// 2. **The size comes from a value, not from a published property.** The list
///    used to reach the view through an `ObservableObject`, and SwiftUI applies
///    those on a later turn of the run loop — so `fittingSize`, measured on the
///    spot, reported the height of the *previous* content. The window was sized
///    for an empty list and the two rows that arrived a moment later were drawn
///    clipped inside it. The rows are a plain `let` now, so assigning `rootView`
///    and laying out is enough to measure them.
@MainActor
final class TargetPickerPanel {
    /// How a question ended. A dismissal and an abandonment are different
    /// things in 记录: one is the user saying no, the other is nobody saying
    /// anything.
    enum Answer: Sendable {
        case picked(ForwardTarget)
        /// Esc, a click on nothing, or a second question superseding this one.
        case cancelled
        /// Unanswered for a whole `BatchIntent.freshnessWindow` — see `deadline`.
        case expired
    }

    /// The list scrolls; the keyboard still reaches every row.
    static let visibleRows = 8
    static let rowHeight: CGFloat = 32
    static let width: CGFloat = 260

    private let state = TargetPickerState()
    private var window: TargetPickerWindow?
    private var pending: CheckedContinuation<Answer, Never>?
    private var targets: [ForwardTarget] = []
    /// Closes an abandoned question — see `present`.
    private var deadline: Task<Void, Never>?

    /// `place` is asked for a frame once the panel has measured itself, because
    /// where it goes depends on how tall it turned out to be.
    func choose(
        from targets: [ForwardTarget],
        place: (NSSize) -> NSRect
    ) async -> Answer {
        await withCheckedContinuation { continuation in
            present(targets, place: place, continuation: continuation)
        }
    }

    private func present(
        _ targets: [ForwardTarget],
        place: (NSSize) -> NSRect,
        continuation: CheckedContinuation<Answer, Never>
    ) {
        // A second question supersedes the first. `ActionRunner` chains forwards
        // so this should not happen, but the alternative — refusing the new one
        // and leaving the old panel up — is exactly how a window nobody is
        // waiting for ends up parked on the user's screen.
        if pending != nil { finish(.cancelled) }

        pending = continuation
        self.targets = targets
        // The list arrives last-used-first, so the row Return picks is already
        // the one the user chose the previous time.
        state.highlighted = 0

        // A fresh window every time: nothing is carried over from the last
        // question — not its size, not its content view, not a fade that was
        // still running when it was closed.
        let window = makeWindow()
        self.window = window
        let hosting = ToastHostingView(rootView: view)
        window.contentView = hosting

        // Computed from the list, then checked against what the view actually
        // laid out. The arithmetic is the primary source because it cannot be
        // early: `fittingSize` is only as current as SwiftUI's last layout pass,
        // and that is exactly how the first build of this panel ended up framed
        // around an empty list. The two agree — both say 106 pt for two rows,
        // measured on the signed build — so the max of them is the honest size
        // and the assertion in `fitted` still fires if they ever diverge.
        let size = NSSize(
            width: Self.width,
            height: max(Self.height(rows: targets.count), FloatingCapsule.measure(hosting).height)
        )
        window.setFrame(FloatingCapsule.fitted(place(size), to: hosting), display: true)
        window.alphaValue = Motion.systemReducesMotion ? 1 : 0
        // Key without activating: the app stays in the background — the user is
        // still in WeChat — and a `.nonactivatingPanel` takes keystrokes without
        // its app becoming active. `orderFrontRegardless` as well, because an
        // inactive app's `makeKey` does not by itself raise a window over a
        // full-screen host.
        window.makeKeyAndOrderFront(nil)
        window.orderFrontRegardless()
        // An unanswered question is not allowed to wait forever. `ActionRunner`
        // runs forwards one at a time, so a panel nobody answers parks every
        // later share behind it — and the answer, whenever it came, would paste
        // files shared minutes ago. The bound is `BatchIntent.freshnessWindow`
        // for the same reason that window exists: past it, this is no longer a
        // gesture the user is still making. The files stay on the clipboard and
        // 记录 says 未执行.
        deadline = Task { [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(BatchIntent.freshnessWindow * 1_000_000_000))
            guard !Task.isCancelled else { return }
            self?.finish(.expired)
        }

        guard !Motion.systemReducesMotion else { return }
        NSAnimationContext.runAnimationGroup { context in
            context.duration = Motion.panelIn
            context.timingFunction = CAMediaTimingFunction(name: .easeOut)
            window.animator().alphaValue = 1
        }
    }

    /// Padding twice, the 「发给…」 line, the gap under it, and the rows the
    /// list will show — the same arithmetic `TargetPickerView` lays out.
    private static func height(rows: Int) -> CGFloat {
        let padding = (Space.s + 2) * 2
        let title: CGFloat = 14
        let visible = CGFloat(min(max(rows, 1), visibleRows))
        return padding + title + Space.s + rowHeight * visible
    }

    private var view: TargetPickerView {
        TargetPickerView(targets: targets, state: state) { [weak self] index in
            self?.finish(at: index)
        }
    }

    private func makeWindow() -> TargetPickerWindow {
        let window = TargetPickerWindow()
        window.onMove = { [weak self] delta in
            guard let self else { return }
            self.state.move(by: delta, count: self.targets.count)
        }
        window.onConfirm = { [weak self] in
            guard let self else { return }
            self.finish(at: self.state.highlighted)
        }
        // 5 pressed over a list of three is a mistyped shortcut. It used to
        // reach a `finish` that read "no row" as "cancelled" and threw the
        // share away without a word; an unusable digit now does nothing, like
        // every other key the panel does not answer.
        window.onDigit = { [weak self] index in self?.finish(at: index) }
        window.onCancel = { [weak self] in self?.finish(.cancelled) }
        return window
    }

    /// A row was chosen — by click, by Return, or by its digit. An index that
    /// is not a row is not an answer at all: it is ignored, never read as a
    /// cancellation.
    private func finish(at index: Int) {
        guard targets.indices.contains(index) else { return }
        finish(.picked(targets[index]))
    }

    /// The one exit. Every path through the panel — a click, Return, a digit,
    /// Esc, the deadline, a second question arriving — ends here, and the
    /// continuation is resumed exactly once.
    ///
    /// Nothing is activated on the way out, in either direction. The panel is
    /// non-activating and never took the app forward, so there is no focus to
    /// give back: re-activating whoever was frontmost when the question opened
    /// would drag an app the user has since left back over the one they are
    /// reading.
    private func finish(_ answer: Answer) {
        guard let continuation = pending else { return }
        pending = nil
        deadline?.cancel()
        deadline = nil
        targets = []
        close()
        continuation.resume(returning: answer)
    }

    /// Off screen and gone. The reference is dropped first, so nothing can
    /// re-show this window while it is fading out — the next question builds
    /// its own.
    private func close() {
        guard let window else { return }
        self.window = nil
        // `resignKey()` is not called here: AppKit documents it as an override
        // point and invoking it does not actually hand key status over — the
        // panel would stay `NSApp.keyWindow` for the whole fade while its
        // delegate chain had already been told otherwise. `close()` below
        // resigns it properly.
        guard !Motion.systemReducesMotion else {
            window.close()
            return
        }
        NSAnimationContext.runAnimationGroup { context in
            context.duration = Motion.toastOut
            window.animator().alphaValue = 0
        } completionHandler: {
            window.close()
        }
    }
}

/// Which row is lit. Only this: the list itself is a value the view is built
/// with, because the panel's size is measured from it the moment it is set —
/// see the class comment.
@MainActor
final class TargetPickerState: ObservableObject {
    @Published var highlighted = 0

    /// Stops at both ends rather than wrapping: with the last-used app on top,
    /// ↑ from the first row is a gesture towards the row the user already has,
    /// and jumping to the bottom of the list would be a surprise.
    func move(by delta: Int, count: Int) {
        guard count > 0 else { return }
        highlighted = min(max(highlighted + delta, 0), count - 1)
    }
}

/// A non-activating panel that takes the keyboard.
///
/// Every key the panel answers is handled here rather than with SwiftUI's
/// `.keyboardShortcut`: a shortcut needs the view to be in the responder chain
/// of an *active* app, and WeChatBridge is never active while this panel is up.
final class TargetPickerWindow: NSPanel {
    var onMove: (Int) -> Void = { _ in }
    var onConfirm: () -> Void = {}
    var onDigit: (Int) -> Void = { _ in }
    var onCancel: () -> Void = {}

    init() {
        super.init(
            contentRect: NSRect(x: 0, y: 0, width: TargetPickerPanel.width, height: 120),
            styleMask: [.nonactivatingPanel, .borderless],
            backing: .buffered,
            defer: false
        )
        FloatingCapsule.configure(self)
        // The toast only needs to be seen; this one has to be typed into, so it
        // must not wait for a control that wants first responder status.
        becomesKeyOnlyIfNeeded = false
    }

    /// A borderless window refuses key status by default, which would leave
    /// every key below dead. It still does not activate the app: the style mask
    /// is `.nonactivatingPanel`.
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }

    override func cancelOperation(_ sender: Any?) { onCancel() }

    override func keyDown(with event: NSEvent) {
        switch event.keyCode {
        case 125: onMove(1)   // ↓
        case 126: onMove(-1)  // ↑
        case 36, 76: onConfirm() // Return, keypad Enter
        default:
            // 1–9 pick by position, which is what every row prints on its right
            // edge. Modified digits are somebody else's shortcut.
            guard event.modifierFlags.intersection(.deviceIndependentFlagsMask).isEmpty,
                  let digit = event.charactersIgnoringModifiers.flatMap({ Int($0) }),
                  (1...9).contains(digit)
            else {
                super.keyDown(with: event)
                return
            }
            onDigit(digit - 1)
        }
    }
}

struct TargetPickerView: View {
    /// A value, not a published list: the panel measures itself from this the
    /// instant it is assigned.
    let targets: [ForwardTarget]
    @ObservedObject var state: TargetPickerState
    let onPick: (Int) -> Void

    @State private var hovered: Int?

    var body: some View {
        VStack(alignment: .leading, spacing: Space.s) {
            Text(L10n.text("发给…"))
                .font(Typo.captionStrong)
                .foregroundStyle(.secondary)
                .padding(.horizontal, Space.xs)
            // The list scrolls, so the highlight has to be able to leave the
            // eight rows on show — and if the view does not follow it, ↓ on the
            // last visible row looks like the arrow keys stopped working while
            // Return quietly sends the files to an app the user cannot see.
            ScrollViewReader { proxy in
                ScrollView(.vertical) {
                    LazyVStack(spacing: 1) {
                        ForEach(Array(targets.enumerated()), id: \.element.id) { index, target in
                            row(target, index: index)
                        }
                    }
                }
                .scrollBounceBehavior(.basedOnSize)
                .onChange(of: state.highlighted) { _, highlighted in
                    guard targets.indices.contains(highlighted) else { return }
                    proxy.scrollTo(targets[highlighted].id, anchor: .center)
                }
            }
            .frame(
                height: TargetPickerPanel.rowHeight
                    * CGFloat(min(max(targets.count, 1), TargetPickerPanel.visibleRows))
            )
        }
        .padding(Space.s + 2)
        .frame(width: TargetPickerPanel.width)
        .background(.regularMaterial)
        .clipShape(RoundedRectangle(cornerRadius: Radius.panel, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: Radius.panel, style: .continuous)
                .strokeBorder(Palette.hairline, lineWidth: Stroke.hairline)
        )
        .fixedSize()
    }

    /// A `Button`, not an `.onTapGesture`.
    ///
    /// Measured 2026-09-05 on the signed build: a tap gesture inside this panel
    /// never fired. The panel belongs to a background app, and SwiftUI only
    /// routes the click to its own controls there — `acceptsFirstMouse` on the
    /// hosting view is necessary and not sufficient. Two clicks on a row left
    /// the picker sitting exactly where it was; the same row as a button
    /// answers the first one.
    private func row(_ target: ForwardTarget, index: Int) -> some View {
        Button { onPick(index) } label: { label(target, index: index) }
            .buttonStyle(.plain)
            .onHover { hovered = $0 ? index : (hovered == index ? nil : hovered) }
            .accessibilityLabel(Text(L10n.format("发给 %@", target.displayName)))
    }

    private func label(_ target: ForwardTarget, index: Int) -> some View {
        let installed = InstalledApp.lookup(target.bundleIdentifier)
        return HStack(spacing: Space.s) {
            Image(nsImage: installed.icon)
                .resizable()
                .interpolation(.high)
                .frame(width: 20, height: 20)
            Text(target.displayName)
                .font(Typo.label)
                .lineLimit(1)
                .truncationMode(.middle)
            Spacer(minLength: Space.s)
            // Printed rather than merely bound: a key that works and is
            // invisible is a key nobody presses.
            if index < 9 {
                Text(verbatim: "\(index + 1)")
                    .font(Font.numeral(10))
                    .foregroundStyle(.tertiary)
            }
        }
        .padding(.horizontal, Space.s)
        .frame(height: TargetPickerPanel.rowHeight - 1)
        .background(background(index))
        .clipShape(RoundedRectangle(cornerRadius: Radius.row, style: .continuous))
        .contentShape(Rectangle())
    }

    private func background(_ index: Int) -> Color {
        if index == state.highlighted { return Palette.rowSelected }
        if index == hovered { return Palette.rowHover }
        return .clear
    }
}
