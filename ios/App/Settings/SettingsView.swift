import SwiftUI
import UIKit
import UniformTypeIdentifiers
import UserNotifications
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
                    // A bare TextField shows only its value, which reads as a stray "微信流" row.
                    LabeledContent("子文件夹") {
                        TextField("子文件夹", text: $preferences.obsidianSubfolder).multilineTextAlignment(.trailing)
                    }
                } header: {
                    Text("Obsidian")
                } footer: {
                    Text("需要先在「文件」App 里能看到 Obsidian 的库。笔记写入「库/子文件夹」。")
                }
                Section("默认文件夹") {
                    LabeledContent("文件夹", value: FolderBookmark.name(of: preferences.folderBookmark) ?? "未选择")
                    Button("选择默认文件夹") { picking = .folder }
                    LabeledContent("子文件夹") {
                        TextField("子文件夹", text: $preferences.folderSubfolder).multilineTextAlignment(.trailing)
                    }
                }
                Section {
                    Toggle("分享后提醒我投递", isOn: $preferences.quickDeliver)
                        .onChange(of: preferences.quickDeliver) { on in
                            guard on else { return }
                            Task { _ = try? await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) }
                        }
                    Toggle("投递时显示在灵动岛", isOn: $preferences.liveActivity)
                        .onChange(of: preferences.liveActivity) { on in
                            if !on { DeliveryActivityController.endAll() }
                        }
                } header: {
                    Text("快速投递")
                } footer: {
                    Text("从微信分享进来后弹一条通知：点一下直接打开分享面板，投递到上次的目标；长按通知可以换成别的目标。\n投递到别的 App 后，灵动岛和锁屏上会留一个入口：点一下回到这段聊天，也能再复制一次提示词。")
                }
                Section("投递目标") {
                    NavigationLink("调整目标") { TargetSettingsView() }
                }
                Section {
                    Picker("图片怎么发", selection: $preferences.imagesInPDF) {
                        Text("和文字合成一个 PDF").tag(true)
                        Text("只发 TXT，图片存到「照片」").tag(false)
                    }
                } header: {
                    Text("豆包、千问等")
                } footer: {
                    Text("PDF：聊天记录和图片一次发过去，图片插在对应的消息下面。\nTXT：对方收到纯文字，图片和视频存到「照片」，需要时在对方 App 里点「相册」再加。\n两种方式下，视频存到「照片」，其他附件都放到「文件 › 我的iPhone › 微信流」。")
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
                            Label { Text(profile.displayName) } icon: { TargetIcon(profile: profile, size: 28) }
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
