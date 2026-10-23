import SwiftUI
import UIKit
import UniformTypeIdentifiers
import WeChatBridgeCore

struct SettingsView: View {
    @EnvironmentObject private var preferences: Preferences
    @Binding var showsOnboarding: Bool
    @State private var picking: FolderKind?
    @State private var error: String?

    enum FolderKind: Identifiable {
        case obsidian, folder
        var id: Self { self }
    }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    LabeledContent("Obsidian 库", value: FolderBookmark.name(of: preferences.obsidianBookmark) ?? "未选择")
                    Button("选择 Obsidian 库") { picking = .obsidian }
                    TextField("子文件夹", text: $preferences.obsidianSubfolder)
                } header: {
                    Text("Obsidian")
                } footer: {
                    Text("需要先在「文件」App 里能看到 Obsidian 的库。笔记写入「库/子文件夹」。")
                }
                Section("默认文件夹") {
                    LabeledContent("文件夹", value: FolderBookmark.name(of: preferences.folderBookmark) ?? "未选择")
                    Button("选择默认文件夹") { picking = .folder }
                    TextField("子文件夹", text: $preferences.folderSubfolder)
                }
                Section("投递目标") {
                    NavigationLink("调整目标") { TargetSettingsView() }
                }
                Section("历史") {
                    Picker("保留已收到的记录", selection: $preferences.retentionDays) {
                        Text("1 天").tag(1)
                        Text("7 天").tag(7)
                        Text("30 天").tag(30)
                        Text("一直保留").tag(0)
                    }
                }
                Section {
                    Button("重新查看使用引导") { showsOnboarding = true }
                }
                Section {
                    LabeledContent("版本", value: Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "")
                } footer: {
                    Text("聊天记录只保存在本机，不上传任何服务器。")
                }
            }
            .navigationTitle("设置")
            .sheet(item: $picking) { kind in
                FolderPicker { url in
                    do {
                        let bookmark = try FolderBookmark.make(from: url)
                        switch kind {
                        case .obsidian: preferences.obsidianBookmark = bookmark
                        case .folder: preferences.folderBookmark = bookmark
                        }
                    } catch {
                        self.error = error.localizedDescription
                    }
                }
                .ignoresSafeArea()
            }
            .alert("无法使用这个文件夹", isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
                Button("好") { error = nil }
            } message: {
                Text(error ?? "")
            }
        }
    }
}

struct TargetSettingsView: View {
    @EnvironmentObject private var preferences: Preferences

    /// Every built-in target in the user's order, hidden ones included.
    private var all: [TargetProfile] {
        TargetProfile.arranged(TargetProfile.builtIn, order: preferences.targetOrder, hidden: [], overrides: preferences.formatOverrides, lastUsed: nil)
    }

    var body: some View {
        List {
            Section {
                ForEach(all) { profile in
                    VStack(alignment: .leading, spacing: 6) {
                        Toggle(isOn: visible(profile.id)) {
                            Label(profile.displayName, systemImage: profile.symbol)
                        }
                        if profile.canChangeFormat {
                            Picker("发送方式", selection: format(profile)) {
                                Text(DeliveryFormat.archive.label).tag(DeliveryFormat.archive)
                                Text(DeliveryFormat.transcriptAndImages.label).tag(DeliveryFormat.transcriptAndImages)
                            }
                            .pickerStyle(.segmented)
                        }
                    }
                }
                .onMove { from, to in
                    var ids = all.map(\.id)
                    ids.move(fromOffsets: from, toOffset: to)
                    preferences.targetOrder = ids
                }
            } footer: {
                Text("有的 App 不收 ZIP（比如豆包、千问），就改成「发送 TXT 和图片」。上次用过的目标总会排在第一个。")
            }
            Section {
                Button("恢复默认") {
                    preferences.targetOrder = []
                    preferences.hiddenTargets = []
                    preferences.formatOverrides = [:]
                }
            }
        }
        .environment(\.editMode, .constant(.active))
        .navigationTitle("投递目标")
    }

    private func visible(_ id: String) -> Binding<Bool> {
        Binding {
            !preferences.hiddenTargets.contains(id)
        } set: { shown in
            preferences.hiddenTargets.removeAll { $0 == id }
            if !shown { preferences.hiddenTargets.append(id) }
        }
    }

    private func format(_ profile: TargetProfile) -> Binding<DeliveryFormat> {
        Binding {
            profile.format
        } set: { format in
            let builtIn = TargetProfile.builtIn.first { $0.id == profile.id }?.format
            preferences.formatOverrides[profile.id] = format == builtIn ? nil : format.rawValue
        }
    }
}

/// The Files folder picker. `asCopy: false` so the result is the folder
/// itself, which a bookmark can then reopen.
struct FolderPicker: UIViewControllerRepresentable {
    let picked: (URL) -> Void

    func makeUIViewController(context: Context) -> UIDocumentPickerViewController {
        let picker = UIDocumentPickerViewController(forOpeningContentTypes: [.folder], asCopy: false)
        picker.delegate = context.coordinator
        return picker
    }

    func updateUIViewController(_ controller: UIDocumentPickerViewController, context: Context) {}

    func makeCoordinator() -> Coordinator { Coordinator(picked: picked) }

    final class Coordinator: NSObject, UIDocumentPickerDelegate {
        let picked: (URL) -> Void
        init(picked: @escaping (URL) -> Void) { self.picked = picked }

        func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
            if let url = urls.first { picked(url) }
        }
    }
}
