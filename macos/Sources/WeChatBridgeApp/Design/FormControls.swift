import AppKit
import SwiftUI

/// One size system for settings. Width belongs to the layout; action columns
/// share a floor width, and every input, choice and action shares the same
/// height.
enum SettingsControlMetrics {
    static let height: CGFloat = 28
    /// A floor, not a fixed width: it lines up a column of short buttons, and
    /// a longer label — 「添加 Prompt」, 「在 Finder 中显示」 — grows past it
    /// rather than being truncated to 「添加 Pro…」.
    static let actionWidth: CGFloat = 104
    static let font = Font.system(size: 12.5, weight: .medium)
    static let radius: CGFloat = 8
    static let inset: CGFloat = 10
}

private struct SettingsKeyboardNavigationKey: EnvironmentKey {
    static let defaultValue = false
}

extension EnvironmentValues {
    var settingsKeyboardNavigation: Bool {
        get { self[SettingsKeyboardNavigationKey.self] }
        set { self[SettingsKeyboardNavigationKey.self] = newValue }
    }
}

/// Focus stays on the trigger after a popover closes. Its outline is only
/// visible for keyboard input, like focus-visible, rather than every click.
private final class SettingsInputMethod: ObservableObject {
    @Published var keyboard = false
    private var monitor: Any?

    func start() {
        guard monitor == nil else { return }
        monitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .leftMouseDown, .rightMouseDown, .otherMouseDown]) { [weak self] event in
            let keyboard = event.type == .keyDown
            if self?.keyboard != keyboard { self?.keyboard = keyboard }
            return event
        }
    }

    func stop() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
        keyboard = false
    }

    deinit { if let monitor { NSEvent.removeMonitor(monitor) } }
}

struct SettingsFocusScope: ViewModifier {
    @StateObject private var input = SettingsInputMethod()

    func body(content: Content) -> some View {
        content
            .environment(\.settingsKeyboardNavigation, input.keyboard)
            .onAppear { input.start() }
            .onDisappear { input.stop() }
    }
}

struct SettingsFocusRing: ViewModifier {
    let focused: Bool
    var radius = SettingsControlMetrics.radius
    @Environment(\.settingsKeyboardNavigation) private var keyboard

    func body(content: Content) -> some View {
        content.overlay {
            RoundedRectangle(cornerRadius: radius)
                .strokeBorder(focused && keyboard ? Theme.ink : .clear, lineWidth: Stroke.focus)
                .allowsHitTesting(false)
        }
    }
}

struct SettingsActionButtonStyle: ButtonStyle {
    var primary = false
    /// The narrowest the button may be; its label decides the rest.
    var width: CGFloat? = SettingsControlMetrics.actionWidth
    @Environment(\.isEnabled) private var isEnabled

    func makeBody(configuration: Configuration) -> some View {
        Surface(primary: primary, width: width, enabled: isEnabled, pressed: configuration.isPressed) {
            configuration.label
        }
    }

    private struct Surface<Label: View>: View {
        let primary: Bool
        let width: CGFloat?
        let enabled: Bool
        let pressed: Bool
        @ViewBuilder let label: () -> Label
        @State private var hovering = false

        var body: some View {
            label()
                .font(SettingsControlMetrics.font)
                .lineLimit(1)
                // The label is measured at its natural width, so a row that is
                // short of space shrinks something else rather than the words.
                .fixedSize(horizontal: true, vertical: false)
                .foregroundStyle(
                    primary && enabled
                        ? Theme.onBrand
                        : (enabled ? Theme.secondaryActionText : Theme.inkTertiary)
                )
                .padding(.horizontal, SettingsControlMetrics.inset)
                .frame(height: SettingsControlMetrics.height)
                .frame(minWidth: width)
                .background {
                    RoundedRectangle(cornerRadius: SettingsControlMetrics.radius)
                        .fill(fill)
                }
                .overlay {
                    RoundedRectangle(cornerRadius: SettingsControlMetrics.radius)
                        .strokeBorder(primary && enabled ? .clear : Theme.buttonBorder, lineWidth: Stroke.hairline)
                        .allowsHitTesting(false)
                }
                .contentShape(RoundedRectangle(cornerRadius: SettingsControlMetrics.radius))
                .onHover { hovering = $0 }
        }

        private var fill: Color {
            guard enabled else { return Theme.surfaceSecondary }
            if primary {
                if pressed { return Theme.brandPressed }
                if hovering { return Theme.brandHover }
                return Theme.brandPrimary
            }
            if pressed { return Theme.selected }
            if hovering { return Theme.hover }
            return Theme.surface
        }
    }
}

/// Keep the system text editor (selection, IME, undo); draw only its surround.
struct SettingsTextFieldStyle: TextFieldStyle {
    var numeric = false
    var invalid = false
    /// A field that grows with its text — the prompt editor. The fixed control
    /// height becomes a floor rather than a ceiling.
    var multiline = false
    @Environment(\.isEnabled) private var isEnabled
    @FocusState private var focused: Bool
    @State private var hovering = false

    func _body(configuration: TextField<Self._Label>) -> some View {
        configuration
            .textFieldStyle(.plain)
            .font(numeric ? SettingsControlMetrics.font.monospacedDigit() : SettingsControlMetrics.font)
            .foregroundStyle(Theme.ink)
            .padding(.horizontal, SettingsControlMetrics.inset)
            .padding(.vertical, multiline ? 6 : 0)
            .frame(height: multiline ? nil : SettingsControlMetrics.height)
            .frame(minHeight: SettingsControlMetrics.height)
            .background(hovering && isEnabled ? Theme.hover : Theme.sunken,
                        in: RoundedRectangle(cornerRadius: SettingsControlMetrics.radius))
            .overlay {
                RoundedRectangle(cornerRadius: SettingsControlMetrics.radius)
                    .strokeBorder(invalid ? Theme.danger : (focused ? Theme.ink : Theme.inputStroke),
                                  lineWidth: focused ? Stroke.focus : Stroke.hairline)
                    .allowsHitTesting(false)
            }
            .focused($focused)
            .compatFocusEffectDisabled()
            .onHover { hovering = $0 }
            .opacity(isEnabled ? 1 : 0.45)
    }
}

struct SettingsChoice<Value: Hashable>: Identifiable {
    let id: Value
    let title: String
    var symbol: String? = nil
    var image: NSImage? = nil
}

/// The same choice control for a compact preference and a full-width app list.
/// Its popover owns arrow-key navigation; a selection commits only on activation.
struct SettingsSelect<Value: Hashable>: View {
    let title: String
    @Binding var selection: Value
    let choices: [SettingsChoice<Value>]
    let identifier: String
    var placeholder = ""

    @State private var expanded = false
    @State private var width: CGFloat = 220
    @FocusState private var focused: Bool

    private var selected: SettingsChoice<Value>? { choices.first { $0.id == selection } }
    private var valueTitle: String { selected?.title ?? placeholder }

    var body: some View {
        Button { expanded.toggle() } label: {
            HStack(spacing: Space.s) {
                if let selected { ChoiceIcon(choice: selected) }
                Text(valueTitle)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .frame(maxWidth: .infinity, alignment: .leading)
                Image(systemName: "chevron.down")
                    .font(.system(size: 10, weight: .semibold))
                    .foregroundStyle(Theme.inkSecondary)
                    .accessibilityHidden(true)
            }
        }
        .buttonStyle(SettingsControlButtonStyle())
        .focused($focused)
        .compatFocusEffectDisabled()
        .modifier(SettingsFocusRing(focused: focused))
        .background {
            GeometryReader { proxy in
                Color.clear.onAppear { width = proxy.size.width }
                    .compatOnChange(of: proxy.size.width) { _, value in width = value }
            }
        }
        .compatKeyCommands(up: { expanded = true }, down: { expanded = true })
        .popover(isPresented: $expanded, arrowEdge: .bottom) {
            SettingsChoiceList(title: title, selection: selection, choices: choices, identifier: identifier) {
                selection = $0
                expanded = false
            } dismiss: {
                expanded = false
            }
            .frame(width: max(180, min(width, 420)))
        }
        .compatOnChange(of: expanded) { _, value in if !value { focused = true } }
        .disabled(choices.isEmpty)
        .accessibilityLabel(Text(title))
        .accessibilityValue(Text(valueTitle))
        .accessibilityIdentifier(identifier)
    }
}

private struct ChoiceIcon<Value: Hashable>: View {
    let choice: SettingsChoice<Value>

    var body: some View {
        Group {
            if let image = choice.image {
                Image(nsImage: image).resizable().interpolation(.high)
                    .frame(width: 16, height: 16)
            } else if let symbol = choice.symbol {
                Image(systemName: symbol)
                    .font(.system(size: 13, weight: .medium))
                    .frame(width: 16, height: 16)
            }
        }
        .accessibilityHidden(true)
    }
}

/// Shared popover list: app selection and adding a forward destination use
/// the same rows, disabled states, keyboard navigation and optional footer.
struct SettingsChoiceList<Value: Hashable>: View {
    let title: String
    let selection: Value
    let choices: [SettingsChoice<Value>]
    let identifier: String
    var disabledValues: Set<Value> = []
    var footer: SettingsChoice<Value>? = nil
    let select: (Value) -> Void
    let dismiss: () -> Void
    @State private var highlighted: Value?
    @State private var pointerLocation = NSEvent.mouseLocation
    @FocusState private var focused: Bool

    private var allChoices: [SettingsChoice<Value>] { choices + (footer.map { [$0] } ?? []) }
    private var enabledChoices: [SettingsChoice<Value>] { allChoices.filter { !disabledValues.contains($0.id) } }

    var body: some View {
        VStack(alignment: .leading, spacing: Space.xs) {
            Text(title)
                .font(Typo.paneCaption)
                .foregroundStyle(Theme.inkSecondary)
                .padding(.horizontal, Space.s)
                .padding(.vertical, Space.xs)
            ScrollViewReader { reader in
                ScrollView {
                    VStack(spacing: Space.xxs) {
                        ForEach(choices) { choice in row(choice).id(choice.id) }
                    }
                }
                .frame(height: min(max(CGFloat(choices.count) * (SettingsControlMetrics.height + Space.xxs) - Space.xxs, 0), 286))
                .compatScrollBounceBasedOnSize()
                .compatOnChange(of: highlighted) { _, value in
                    if let value, value != footer?.id { reader.scrollTo(value) }
                }
            }
            if let footer {
                Rectangle().fill(Theme.stroke).frame(height: Stroke.hairline)
                    .padding(.vertical, Space.xs)
                row(footer)
            }
        }
        .padding(Space.s)
        .background(Theme.surface)
        .focusable()
        .focused($focused)
        .compatFocusEffectDisabled()
        .onAppear {
            highlighted = enabledChoices.contains { $0.id == selection } ? selection : enabledChoices.first?.id
            focused = true
        }
        .compatOnChange(of: enabledChoices.map(\.id)) { _, ids in
            if !ids.contains(where: { $0 == highlighted }) { highlighted = ids.first }
            if ids.isEmpty { dismiss() }
        }
        .compatKeyCommands(up: { move(-1) }, down: { move(1) }, commit: commit)
        .onExitCommand(perform: dismiss)
        .accessibilityElement(children: .contain)
    }

    private func row(_ choice: SettingsChoice<Value>) -> some View {
        let disabled = disabledValues.contains(choice.id)
        return Button { select(choice.id) } label: {
            HStack(spacing: Space.s) {
                ChoiceIcon(choice: choice)
                Text(choice.title).lineLimit(1).truncationMode(.middle)
                Spacer(minLength: Space.s)
                Image(systemName: "checkmark")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(Theme.brandPrimary)
                    .opacity(selection == choice.id || disabled ? 1 : 0)
                    .accessibilityHidden(true)
            }
            .font(SettingsControlMetrics.font)
            .foregroundStyle(disabled ? Theme.inkSecondary : Theme.ink)
            .padding(.horizontal, Space.s)
            .frame(height: SettingsControlMetrics.height)
            .background(
                selection == choice.id && !disabled
                    ? Theme.brandTint
                    : (highlighted == choice.id && !disabled ? Palette.rowSelected : .clear),
                in: RoundedRectangle(cornerRadius: Radius.row)
            )
            .overlay {
                RoundedRectangle(cornerRadius: Radius.row)
                    .strokeBorder(
                        selection == choice.id && !disabled ? Theme.brandPrimary : .clear,
                        lineWidth: Stroke.hairline
                    )
            }
            .opacity(disabled ? 0.6 : 1)
            .contentShape(Rectangle())
        }
        .buttonStyle(PlainPressButtonStyle(staticFeedback: true))
        .focusable(false)
        .disabled(disabled)
        .onContinuousHover { phase in
            guard case .active = phase, !disabled else { return }
            let location = NSEvent.mouseLocation
            // Layout changes under a stationary pointer cannot override keys.
            guard location != pointerLocation else { return }
            pointerLocation = location
            highlighted = choice.id
        }
        .accessibilityAddTraits(selection == choice.id || disabled ? [.isSelected] : [])
        .accessibilityIdentifier(identifier + ".option.\(choice.id)")
    }

    private func move(_ offset: Int) {
        guard !enabledChoices.isEmpty else { return }
        let index = enabledChoices.firstIndex { $0.id == highlighted } ?? 0
        highlighted = enabledChoices[min(max(index + offset, 0), enabledChoices.count - 1)].id
    }

    private func commit() {
        if let highlighted, enabledChoices.contains(where: { $0.id == highlighted }) { select(highlighted) }
    }
}

private struct SettingsControlButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var isEnabled

    func makeBody(configuration: Configuration) -> some View {
        ControlSurface(pressed: configuration.isPressed, enabled: isEnabled) {
            configuration.label
        }
    }

    private struct ControlSurface<Label: View>: View {
        let pressed: Bool
        let enabled: Bool
        @ViewBuilder let label: () -> Label
        @State private var hovering = false

        var body: some View {
            label()
                .font(SettingsControlMetrics.font)
                .foregroundStyle(Theme.ink)
                .padding(.horizontal, SettingsControlMetrics.inset)
                .frame(height: SettingsControlMetrics.height)
                .background(pressed ? Theme.selected : (hovering ? Theme.hover : Theme.sunken),
                            in: RoundedRectangle(cornerRadius: SettingsControlMetrics.radius))
                .overlay {
                    RoundedRectangle(cornerRadius: SettingsControlMetrics.radius)
                        .strokeBorder(Theme.strokeStrong, lineWidth: Stroke.hairline)
                }
                .contentShape(RoundedRectangle(cornerRadius: SettingsControlMetrics.radius))
                .opacity(enabled ? 1 : 0.45)
                .onHover { hovering = $0 }
        }
    }
}

private struct SettingsChoiceButtonStyle: ButtonStyle {
    let selected: Bool
    @Environment(\.isEnabled) private var isEnabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .foregroundStyle(selected ? Theme.ink : Theme.inkSecondary)
            .background {
                RoundedRectangle(cornerRadius: SettingsControlMetrics.radius - Space.xxs)
                    .fill(selected ? Theme.brandTint : (configuration.isPressed ? Theme.hover : .clear))
                    .overlay {
                        RoundedRectangle(cornerRadius: SettingsControlMetrics.radius - Space.xxs)
                            .strokeBorder(selected ? Theme.brandPrimary : .clear, lineWidth: Stroke.hairline)
                    }
                    .shadow(color: .black.opacity(selected ? 0.04 : 0), radius: 2, y: 1)
            }
            .contentShape(RoundedRectangle(cornerRadius: SettingsControlMetrics.radius - Space.xxs))
            .opacity(isEnabled ? 1 : 0.45)
    }
}
