import SwiftUI

// The app ships to macOS 13 (Ventura) as well, because plenty of Intel Macs
// stop there. These wrappers use the macOS 14 SwiftUI API when it exists and the
// closest macOS 13 equivalent otherwise, so call sites stay one line.
extension View {
    /// `onChange(of:initial:_:)` on macOS 14; the single-value form on 13, with
    /// the previous value tracked here so the closure shape stays the same.
    @ViewBuilder
    func compatOnChange<V: Equatable>(of value: V, perform action: @escaping (V, V) -> Void) -> some View {
        if #available(macOS 14, *) {
            onChange(of: value) { old, new in action(old, new) }
        } else {
            modifier(LegacyOnChange(value: value, action: action))
        }
    }

    /// The bounce is cosmetic, so macOS 13.0–13.2 simply keeps the default.
    @ViewBuilder
    func compatScrollBounceBasedOnSize() -> some View {
        if #available(macOS 13.3, *) {
            scrollBounceBehavior(.basedOnSize)
        } else {
            self
        }
    }

    /// A scroll view already starts at the top on macOS 13.
    @ViewBuilder
    func compatDefaultScrollAnchorTop() -> some View {
        if #available(macOS 14, *) {
            defaultScrollAnchor(.top)
        } else {
            self
        }
    }

    /// macOS 13 draws its focus ring on these custom controls; nothing breaks.
    @ViewBuilder
    func compatFocusEffectDisabled() -> some View {
        if #available(macOS 14, *) {
            focusEffectDisabled()
        } else {
            self
        }
    }

    /// Keyboard navigation for a focused custom control. macOS 14 uses
    /// `onKeyPress`; macOS 13 has no key-press modifier, so arrows go through
    /// `onMoveCommand` and Return/Space are left to the mouse.
    @ViewBuilder
    func compatKeyCommands(
        up: (() -> Void)? = nil,
        down: (() -> Void)? = nil,
        left: (() -> Void)? = nil,
        right: (() -> Void)? = nil,
        commit: (() -> Void)? = nil
    ) -> some View {
        if #available(macOS 14, *) {
            let handlers = Self.keyHandlers(up: up, down: down, left: left, right: right, commit: commit)
            onKeyPress(keys: Set(handlers.keys)) { press in
                guard let handler = handlers[press.key] else { return .ignored }
                handler()
                return .handled
            }
        } else {
            onMoveCommand { direction in
                switch direction {
                case .up: up?()
                case .down: down?()
                case .left: left?()
                case .right: right?()
                @unknown default: break
                }
            }
        }
    }

    private static func keyHandlers(
        up: (() -> Void)?,
        down: (() -> Void)?,
        left: (() -> Void)?,
        right: (() -> Void)?,
        commit: (() -> Void)?
    ) -> [KeyEquivalent: () -> Void] {
        var handlers: [KeyEquivalent: () -> Void] = [:]
        if let up { handlers[.upArrow] = up }
        if let down { handlers[.downArrow] = down }
        if let left { handlers[.leftArrow] = left }
        if let right { handlers[.rightArrow] = right }
        if let commit {
            handlers[.return] = commit
            handlers[.space] = commit
        }
        return handlers
    }
}

private struct LegacyOnChange<V: Equatable>: ViewModifier {
    let value: V
    let action: (V, V) -> Void
    @State private var previous: V?

    func body(content: Content) -> some View {
        content
            .onAppear { previous = value }
            .onChange(of: value) { new in
                action(previous ?? new, new)
                previous = new
            }
    }
}
