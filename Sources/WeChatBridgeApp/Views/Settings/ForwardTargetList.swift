import AppKit
import WeChatBridgeCore
import SwiftUI
import UniformTypeIdentifiers

/// The apps 「发送到自定义」 offers, and the only place they are edited.
///
/// The Share menu itself cannot grow a row per app — every entry is a signed
/// `.appex` fixed at build time — so one entry asks instead, and this list is
/// what it asks from. The order here is the order of the share panel and of both
/// 发给 ▸ menus, which is why the rows can be dragged.
struct ForwardTargetList: View {
    @ObservedObject var targets: ForwardTargets

    /// Beyond this the list starts to own the pane instead of sitting in it;
    /// the rest scrolls.
    private static let visibleRows = 6
    private static let rowHeight: CGFloat = 38

    var body: some View {
        VStack(alignment: .leading, spacing: Space.s) {
            if targets.isEmpty {
                empty
            } else {
                well
                AddForwardTargetButton(targets: targets)
            }
        }
    }

    private var well: some View {
        list
            .background(Theme.surface, in: RoundedRectangle(cornerRadius: Radius.card, style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: Radius.card, style: .continuous)
                    .strokeBorder(Theme.stroke, lineWidth: Stroke.hairline)
            )
    }

    private var empty: some View {
        HStack(spacing: Space.m) {
            Image(systemName: "app.dashed")
                .font(.system(size: 14, weight: .medium))
                .foregroundStyle(Theme.accent)
                .frame(width: 28, height: 28)
                .background(Theme.accentSoft, in: RoundedRectangle(cornerRadius: Radius.row, style: .continuous))
                .accessibilityHidden(true)

            Text(L10n.text("还没有添加应用。加进来的 App 会出现在「发送到自定义」的面板里。"))
                .font(Typo.paneCaption)
                .foregroundStyle(Theme.inkSecondary)
                .fixedSize(horizontal: false, vertical: true)

            Spacer(minLength: Space.s)
            AddForwardTargetButton(targets: targets)
        }
        .padding(Space.m)
        .background(Theme.surface, in: RoundedRectangle(cornerRadius: Radius.card, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: Radius.card, style: .continuous)
                .strokeBorder(Theme.stroke, lineWidth: Stroke.hairline)
        )
    }

    /// A `List` rather than the `LazyVStack` the rest of the window uses: it is
    /// the only container that gives `onMove` a real drag-to-reorder on macOS,
    /// and reordering is what makes this list mean anything.
    private var list: some View {
        List {
            ForEach(targets.targets) { target in
                ForwardTargetRow(
                    target: target,
                    isHighlighted: targets.highlighted == target.id,
                    setPathOnly: { targets.setPastesPathOnly($0, for: target) },
                    remove: { targets.remove(target) }
                )
                .frame(height: Self.rowHeight)
                .listRowInsets(EdgeInsets())
                .listRowSeparator(.hidden)
                .listRowBackground(Color.clear)
            }
            .onMove { targets.move(fromOffsets: $0, toOffset: $1) }
        }
        .listStyle(.plain)
        .scrollContentBackground(.hidden)
        .scrollBounceBehavior(.basedOnSize)
        .frame(height: Self.rowHeight * CGFloat(min(targets.targets.count, Self.visibleRows)))
        .padding(Space.xs)
    }
}

private struct ForwardTargetRow: View {
    let target: ForwardTarget
    let isHighlighted: Bool
    let setPathOnly: (Bool) -> Void
    let remove: () -> Void

    @State private var hovering = false

    var body: some View {
        let installed = InstalledApp.lookup(target.bundleIdentifier)

        HStack(spacing: Space.s) {
            Image(nsImage: installed.icon)
                .resizable()
                .interpolation(.high)
                .frame(width: 24, height: 24)
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 0) {
                Text(target.displayName)
                    .font(Typo.paneBody)
                    .foregroundStyle(Theme.ink)
                    .lineLimit(1)
                Text(target.bundleIdentifier)
                    .font(Typo.micro)
                    .foregroundStyle(Theme.inkTertiary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }

            Spacer(minLength: Space.s)

            // Said out loud rather than left to fail at forward time: the app
            // may have been uninstalled since it was added, and the only place
            // that can be noticed calmly is here.
            if !installed.isInstalled {
                StatusPill(text: L10n.text("未安装"), tone: .warn)
                    .fixedSize()
            }

            // Always visible, not only on hover: it is a setting the user has
            // to be able to read off the row, and the one thing that decides
            // whether a terminal gets anything from the forward at all.
            Toggle(isOn: Binding(get: { target.pastesPathOnly }, set: setPathOnly)) {
                Text(L10n.text("只粘贴文件路径"))
                    .font(Typo.caption)
                    .foregroundStyle(Theme.inkSecondary)
            }
            .toggleStyle(CheckboxToggleStyle())
            .fixedSize()
            .help(L10n.text("给终端类应用用：粘贴的是带引号的文件路径，跟把文件拖进终端一样。"))

            Button(action: remove) {
                Image(systemName: "minus")
            }
            .buttonStyle(IconButtonStyle(size: SettingsControlMetrics.height, staticFeedback: true))
            .help(L10n.text("移除"))
            .accessibilityLabel(Text(L10n.format("移除 %@", target.displayName)))
            .opacity(hovering ? 1 : 0.55)
        }
        .padding(.horizontal, 10)
        .frame(maxHeight: .infinity)
        .background(
            RoundedRectangle(cornerRadius: Radius.row, style: .continuous)
                .fill(background)
        )
        .onHover { hovering = $0 }
        .animation(Motion.reduced(Motion.ui), value: isHighlighted)
        // Contained, not combined: the row now holds two controls, and folding
        // them into one element would leave VoiceOver a checkbox it can read
        // but not flip.
        .accessibilityElement(children: .contain)
    }

    /// The highlight is the answer to "I clicked add and nothing happened": the
    /// app was already in the list, and this is the row that says so.
    private var background: Color {
        if isHighlighted { return Theme.accentSoft }
        return hovering ? Theme.hover : .clear
    }
}

/// Running apps are directly visible, with a pinned Finder action below them.
private struct AddForwardTargetButton: View {
    @ObservedObject var targets: ForwardTargets
    @State private var applications: [RunningApp] = []
    @State private var expanded = false
    @State private var pendingFinder = false
    @FocusState private var focused: Bool
    private static let finderAction = "choose-from-finder"

    var body: some View {
        Button {
            applications = targets.runningApplications()
            expanded.toggle()
        } label: {
            Label(L10n.text("添加应用"), systemImage: "plus")
        }
        .buttonStyle(SettingsActionButtonStyle())
        .focused($focused)
        .focusEffectDisabled()
        .modifier(SettingsFocusRing(focused: focused))
        .popover(isPresented: $expanded, arrowEdge: .bottom) {
            SettingsChoiceList(
                title: L10n.text("运行中的应用程序"), selection: "",
                choices: applications.map { .init(id: $0.id, title: $0.name, image: $0.icon) },
                identifier: "entries.add",
                disabledValues: Set(targets.targets.map(\.bundleIdentifier)),
                footer: .init(id: Self.finderAction, title: L10n.text("手动从访达中选择…"), symbol: "folder")
            ) { id in
                if id == Self.finderAction {
                    pendingFinder = true
                } else if let app = applications.first(where: { $0.id == id }) {
                    targets.add(bundleIdentifier: app.id, displayName: app.name)
                }
                expanded = false
            } dismiss: {
                expanded = false
            }
            .frame(width: 320)
            .onDisappear {
                // The popover is torn down inside a Core Animation transaction,
                // and AppKit refuses a modal panel started from there:
                //
                //   Suppressing invocation of -[NSApplication runModalForWindow:].
                //   ... cannot run inside a transaction begin/commit pair
                //
                // `runModal()` then returns `.abort`, which the guard in
                // `chooseFromFinder()` reads as "the user cancelled" — no panel,
                // no error, nothing. Opening the popover and dismissing it are
                // one turn of the runloop, so hand the panel to the next one.
                guard pendingFinder else { return }
                pendingFinder = false
                Task { @MainActor in chooseFromFinder() }
            }
        }
        .onChange(of: expanded) { _, value in if !value { focused = true } }
        .accessibilityIdentifier("entries.add")
    }

    private func chooseFromFinder() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.applicationBundle]
        panel.allowsMultipleSelection = true
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.treatsFilePackagesAsDirectories = false
        panel.directoryURL = FileManager.default
            .urls(for: .applicationDirectory, in: .localDomainMask)
            .first
        guard panel.runModal() == .OK else { return }
        for url in panel.urls { targets.add(applicationAt: url) }
    }
}
