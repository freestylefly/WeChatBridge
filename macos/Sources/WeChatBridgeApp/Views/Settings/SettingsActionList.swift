import AppKit
import WeChatBridgeCore
import SwiftUI

/// One row of a `SettingsActionList`.
struct SettingsAction: Identifiable {
    let id: String
    let title: String
    /// A hairline above the row: the point where a list stops offering things
    /// to do with the files and starts offering to get rid of them.
    var isSeparatorBefore = false
    let perform: () -> Void
}

/// A menu of commands, drawn the way the settings window draws everything else.
///
/// `Menu` hands its rows to AppKit, which paints them in the system's own
/// vocabulary — no `Theme` colour, no `Typo`, no arrival, and a row height that
/// belongs to no other control in the window. This is `SettingsChoiceList` with
/// the selection taken out: the same metrics, hover highlight, hairline and
/// keyboard handling, inside a popover the window system fades in for us.
///
/// A popover cannot nest a submenu, so a caller with a 发给 ▸ to offer flattens
/// it into one row per destination before it gets here.
struct SettingsActionList: View {
    let items: [SettingsAction]
    let identifier: String
    let dismiss: () -> Void

    @State private var highlighted: String?
    @State private var pointerLocation = NSEvent.mouseLocation
    @FocusState private var focused: Bool

    /// Past this the list owns the screen rather than the row it hangs off; the
    /// rest scrolls. The same ceiling `SettingsChoiceList` uses.
    private static let maxHeight: CGFloat = 286
    private static let separatorHeight = Stroke.hairline + Space.xs * 2

    var body: some View {
        ScrollViewReader { reader in
            ScrollView {
                VStack(spacing: Space.xxs) {
                    ForEach(items) { item in
                        if item.isSeparatorBefore {
                            Rectangle().fill(Theme.stroke)
                                .frame(height: Stroke.hairline)
                                .padding(.vertical, Space.xs)
                                .accessibilityHidden(true)
                        }
                        row(item).id(item.id)
                    }
                }
            }
            .frame(height: min(contentHeight, Self.maxHeight))
            .scrollBounceBehavior(.basedOnSize)
            .onChange(of: highlighted) { _, value in
                if let value { reader.scrollTo(value) }
            }
        }
        .padding(Space.s)
        .background(Theme.surface)
        .focusable()
        .focused($focused)
        .focusEffectDisabled()
        .onAppear {
            // No selection to return to, so the keyboard starts at the top.
            highlighted = items.first?.id
            focused = true
        }
        .onKeyPress(.downArrow) { move(1); return .handled }
        .onKeyPress(.upArrow) { move(-1); return .handled }
        .onKeyPress(.return) { commit(); return .handled }
        .onKeyPress(.space) { commit(); return .handled }
        .onExitCommand(perform: dismiss)
        .accessibilityElement(children: .contain)
    }

    /// The exact height of the stack above, so the scroll view is only a scroll
    /// view once there is something to scroll: rows and separators are both
    /// fixed, and `ScrollView` has no height of its own to offer.
    private var contentHeight: CGFloat {
        let separators = items.filter(\.isSeparatorBefore).count
        let elements = items.count + separators
        guard elements > 0 else { return 0 }
        return CGFloat(items.count) * SettingsControlMetrics.height
            + CGFloat(separators) * Self.separatorHeight
            + CGFloat(elements - 1) * Space.xxs
    }

    private func row(_ item: SettingsAction) -> some View {
        Button {
            // Closed first: 移到废纸篓 takes the row this popover hangs off with
            // it, and a popover outliving its anchor is AppKit's problem, not
            // something the view can recover from.
            dismiss()
            item.perform()
        } label: {
            Text(item.title)
                .lineLimit(1)
                .truncationMode(.middle)
                .frame(maxWidth: .infinity, alignment: .leading)
                .font(SettingsControlMetrics.font)
                .foregroundStyle(Theme.ink)
                .padding(.horizontal, Space.s)
                .frame(height: SettingsControlMetrics.height)
                .background(highlighted == item.id ? Palette.rowSelected : .clear,
                            in: RoundedRectangle(cornerRadius: Radius.row))
                .contentShape(Rectangle())
        }
        .buttonStyle(PlainPressButtonStyle(staticFeedback: true))
        .focusable(false)
        .onContinuousHover { phase in
            guard case .active = phase else { return }
            let location = NSEvent.mouseLocation
            // Layout changes under a stationary pointer cannot override keys.
            guard location != pointerLocation else { return }
            pointerLocation = location
            highlighted = item.id
        }
        .accessibilityIdentifier(identifier + ".action.\(item.id)")
    }

    private func move(_ offset: Int) {
        guard !items.isEmpty else { return }
        let index = items.firstIndex { $0.id == highlighted } ?? 0
        highlighted = items[min(max(index + offset, 0), items.count - 1)].id
    }

    private func commit() {
        guard let highlighted, let item = items.first(where: { $0.id == highlighted }) else { return }
        dismiss()
        item.perform()
    }
}

/// The 「…」 button and the list it opens.
///
/// Round rather than the settings window's rounded rectangle — it sits at the
/// end of a row of pill buttons and is the only one with no word in it — which
/// is why the focus ring has to be told its radius.
struct SettingsMoreActionsButton: View {
    let items: [SettingsAction]
    let identifier: String
    var width: CGFloat = 240

    @State private var expanded = false
    @FocusState private var focused: Bool
    private static let side = SettingsControlMetrics.height

    var body: some View {
        Button { expanded.toggle() } label: {
            Image(systemName: "ellipsis")
        }
        .buttonStyle(IconButtonStyle(size: Self.side, staticFeedback: true))
        .focused($focused)
        .focusEffectDisabled()
        .modifier(SettingsFocusRing(focused: focused, radius: Self.side / 2))
        .fixedSize()
        // NSPopover brings its own arrival, which is the whole reason this is a
        // popover and not a menu: it grows out of the button rather than
        // appearing beside it.
        .popover(isPresented: $expanded, arrowEdge: .bottom) {
            SettingsActionList(items: items, identifier: identifier) { expanded = false }
                .frame(width: width)
        }
        // Focus stays on the trigger, so the next Tab carries on from here
        // rather than from the top of the pane.
        .onChange(of: expanded) { _, value in if !value { focused = true } }
        .help(L10n.text("更多操作"))
        .accessibilityLabel(Text(L10n.text("更多操作")))
        .accessibilityIdentifier(identifier)
    }
}
