import AppKit
import WeChatBridgeCore
import SwiftUI

/// What the Share menu offers, and the switches that decide which of it shows.
///
/// The list itself is fixed at build time — macOS builds that menu from signed
/// extension bundles — but which entries are live is the user's call, and it is
/// made here rather than three panes deep in System Settings. See
/// `ShareEntryProbe` for why the app is not sandboxed.
struct EntriesPane: View {
    @ObservedObject var targets: ForwardTargets
    @ObservedObject var preferences: Preferences
    @StateObject private var probe = ShareEntryProbe()
    @State private var configuration: EntryConfiguration?

    var body: some View {
        VStack(alignment: .leading, spacing: Space.section) {
            Text(L10n.text("选择要出现在微信「转发到其他应用」里的操作。"))
                .font(Typo.paneBody)
                .foregroundStyle(Theme.inkSecondary)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: 460, alignment: .leading)

            ShareEntryList(
                probe: probe,
                compactDetails: true,
                obsidianVaultPath: preferences.obsidianVaultPath,
                folderPath: preferences.folderDeliveryPath,
                customTargetCount: targets.targets.count
            ) { action in
                switch action {
                case .obsidian: configuration = .obsidian
                case .folder: configuration = .folder
                case .custom: configuration = .custom
                default: break
                }
            }

            Button {
                LoginItem.openExtensionsSettings()
            } label: {
                Label(L10n.text("入口没有出现在微信菜单里？"), systemImage: "questionmark.circle")
                    .font(Typo.paneCaption)
                    .foregroundStyle(Theme.systemBlue)
            }
            .buttonStyle(PlainPressButtonStyle(staticFeedback: true))
            .accessibilityIdentifier("entries.system-settings")
        }
        .sheet(item: $configuration) { item in
            VStack(alignment: .leading, spacing: Space.xl) {
                HStack {
                    Text(item.title)
                        .font(Typo.paneTitle)
                        .foregroundStyle(Theme.ink)
                    Spacer(minLength: Space.l)
                    Button(L10n.text("完成")) { configuration = nil }
                        .buttonStyle(SettingsActionButtonStyle())
                        .keyboardShortcut(.defaultAction)
                }

                switch item {
                case .obsidian:
                    obsidianSettings
                case .folder:
                    folderSettings
                case .custom:
                    ForwardTargetList(targets: targets)
                }
            }
            .padding(Space.xl)
            .frame(width: 540)
            .frame(minHeight: 220, alignment: .topLeading)
        }
        .onAppear { probe.refresh() }
        // The entries can still be changed in System Settings, and the user
        // comes straight back afterwards, so this is re-read on every
        // activation rather than once.
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            probe.refresh()
        }
    }

    private var obsidianSettings: some View {
        VStack(spacing: 0) {
            SettingRow(
                title: L10n.text("知识库文件夹"),
                detail: preferences.obsidianVaultPath.map {
                    URL(fileURLWithPath: $0, isDirectory: true).lastPathComponent
                } ?? L10n.text("尚未选择"),
                alignment: .center
            ) {
                HStack(spacing: Space.s) {
                    if let path = preferences.obsidianVaultPath {
                        Button(L10n.text("在 Finder 中显示")) {
                            NSWorkspace.shared.activateFileViewerSelecting([
                                URL(fileURLWithPath: path, isDirectory: true)
                            ])
                        }
                        .buttonStyle(SettingsActionButtonStyle())
                    }
                    Button(L10n.text("选择文件夹…")) { chooseObsidianVault() }
                        .buttonStyle(SettingsActionButtonStyle())
                }
            }
            .padding(Space.m)

            Rectangle()
                .fill(Theme.stroke)
                .frame(height: Stroke.hairline)
                .padding(.leading, Space.m)

            SettingRow(
                title: L10n.text("子文件夹"),
                detail: L10n.text("聊天 Markdown 与原始 ZIP 会写入这个目录。"),
                alignment: .center
            ) {
                TextField(L10n.text("子文件夹"), text: $preferences.obsidianSubfolder)
                    .textFieldStyle(SettingsTextFieldStyle())
                    .frame(width: SettingsControlMetrics.actionWidth)
            }
            .padding(Space.m)
        }
        .background(Theme.surface, in: RoundedRectangle(cornerRadius: Radius.card, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: Radius.card, style: .continuous)
                .strokeBorder(Theme.stroke, lineWidth: Stroke.hairline)
        )
    }

    /// Two rows, the same pair the Obsidian entry shows: where the folder is,
    /// then the subfolder of it the note lands in (`微信流` unless named).
    private var folderSettings: some View {
        VStack(spacing: 0) {
            SettingRow(
                title: L10n.text("目标文件夹"),
                detail: URL(
                    fileURLWithPath: preferences.folderDeliveryPath,
                    isDirectory: true
                ).lastPathComponent,
                alignment: .center
            ) {
                HStack(spacing: Space.s) {
                    Button(L10n.text("在 Finder 中显示")) {
                        NSWorkspace.shared.activateFileViewerSelecting([
                            URL(fileURLWithPath: preferences.folderDeliveryPath, isDirectory: true)
                        ])
                    }
                    .buttonStyle(SettingsActionButtonStyle())
                    Button(L10n.text("选择文件夹…")) { chooseDeliveryFolder() }
                        .buttonStyle(SettingsActionButtonStyle())
                }
            }
            .padding(Space.m)

            Rectangle()
                .fill(Theme.stroke)
                .frame(height: Stroke.hairline)
                .padding(.leading, Space.m)

            SettingRow(
                title: L10n.text("子文件夹"),
                detail: L10n.text("聊天 Markdown 与原始 ZIP 会写入这个目录。"),
                alignment: .center
            ) {
                TextField(L10n.text("子文件夹"), text: $preferences.folderDeliverySubfolder)
                    .textFieldStyle(SettingsTextFieldStyle())
                    .frame(width: SettingsControlMetrics.actionWidth)
            }
            .padding(Space.m)
        }
        .background(Theme.surface, in: RoundedRectangle(cornerRadius: Radius.card, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: Radius.card, style: .continuous)
                .strokeBorder(Theme.stroke, lineWidth: Stroke.hairline)
        )
    }

    private func chooseObsidianVault() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.canCreateDirectories = true
        panel.prompt = L10n.text("选择知识库")
        guard panel.runModal() == .OK, let folder = panel.url else { return }
        preferences.obsidianVaultPath = folder.path
    }

    private func chooseDeliveryFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.canCreateDirectories = true
        panel.prompt = L10n.text("选择目标文件夹")
        panel.directoryURL = URL(
            fileURLWithPath: preferences.folderDeliveryPath,
            isDirectory: true
        )
        guard panel.runModal() == .OK, let folder = panel.url else { return }
        preferences.folderDeliveryPath = folder.path
    }
}

private enum EntryConfiguration: String, Identifiable {
    case obsidian
    case folder
    case custom

    var id: String { rawValue }

    var title: String {
        switch self {
        case .obsidian: L10n.text("Obsidian 沉淀")
        case .folder: L10n.text("文件夹沉淀")
        case .custom: L10n.text("「发送到自定义」的应用")
        }
    }
}
