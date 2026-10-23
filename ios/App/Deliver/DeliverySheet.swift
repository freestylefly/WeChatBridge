import SwiftUI
import UIKit
import WeChatBridgeCore

struct DeliverySheet: View {
    @EnvironmentObject private var store: RelayStore
    @EnvironmentObject private var preferences: Preferences
    @Environment(\.dismiss) private var dismiss
    @Environment(\.openURL) private var openURL

    let selection: DeliverySelection

    @State private var target: TargetProfile?
    /// Empty means 「不带场景」.
    @State private var sceneID = ""
    @State private var prompt = ""
    @State private var package: DeliveryPackage?
    @State private var planning = false
    @State private var planError: String?
    /// Per-delivery override from 「改发原始 ZIP」; not saved.
    @State private var forceArchive = false
    @State private var nextGroup = 0
    @State private var busy = false
    @State private var toast: String?
    @State private var hint: String?
    @State private var savedNote: URL?
    /// Taken once on open: recording a delivery moves its target to the front,
    /// and the tile the user just tapped must not jump away mid-sheet.
    @State private var targets: [TargetProfile] = []

    private var entries: [RelayEntry] { store.entries(selection.batchIDs) }
    private var summaries: [RelaySummary] { entries.compactMap(\.summary) }
    private var chatName: String? { summaries.first?.title }
    private var senders: Set<String> { Set(summaries.flatMap(\.senders)) }
    private var end: Date? { summaries.compactMap(\.end).max() }
    private var parseFailed: Bool { !summaries.isEmpty && summaries.allSatisfy(\.parseFailed) }

    /// The chosen target as it will actually be used this time.
    private var effectiveTarget: TargetProfile? {
        guard var target else { return nil }
        if target.format.usesShareSheet && (forceArchive || parseFailed) { target.format = .archive }
        return target
    }

    var body: some View {
        NavigationStack {
            Form {
                Section("投递到") { targetGrid }
                if effectiveTarget != nil {
                    Section("场景") { scenePicker }
                    Section("预览") { preview }
                }
            }
            .navigationTitle(entries.count > 1 ? "合并投递 \(entries.count) 批" : "投递")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("关闭") { finishSheet() } }
            }
            .safeAreaInset(edge: .bottom) { sendBar }
            .overlay(alignment: .top) { ToastView(text: $toast) }
            .overlay {
                if let hint {
                    Text(hint)
                        .font(.title3.weight(.semibold))
                        .padding(24)
                        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16))
                }
            }
        }
        .presentationDetents([.medium, .large])
        .onAppear(perform: setUp)
        .onChange(of: target) { _ in replan() }
        .onChange(of: forceArchive) { _ in replan() }
        .onChange(of: sceneID) { _ in renderPrompt() }
    }

    // MARK: - Sections

    private var targetGrid: some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 76), spacing: 12)], spacing: 12) {
            ForEach(targets) { profile in
                Button { target = profile } label: {
                    VStack(spacing: 6) {
                        Image(systemName: profile.symbol)
                            .font(.title2)
                            .frame(width: 48, height: 48)
                            .background(
                                (target?.id == profile.id ? Color.accentColor : Color(.tertiarySystemFill)),
                                in: RoundedRectangle(cornerRadius: 12)
                            )
                            .foregroundStyle(target?.id == profile.id ? .white : .primary)
                        Text(profile.displayName).font(.caption).lineLimit(1)
                    }
                }
                .buttonStyle(.plain)
                .accessibilityAddTraits(target?.id == profile.id ? .isSelected : [])
            }
        }
        .padding(.vertical, 4)
    }

    private var scenePicker: some View {
        Group {
            Picker("场景", selection: $sceneID) {
                Text("不带场景").tag("")
                ForEach(preferences.scenes.enabledScenes) { Text($0.name).tag($0.id) }
            }
            if !sceneID.isEmpty {
                TextEditor(text: $prompt)
                    .frame(minHeight: 100)
                    .font(.footnote)
                Text("发送时会复制到剪贴板，可在对方输入框里粘贴。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
    }

    @ViewBuilder
    private var preview: some View {
        if let target = effectiveTarget {
            switch target.format {
            case .knowledgeNote:
                LabeledContent("写入", value: FolderBookmark.name(of: preferences.obsidianBookmark).map { "\($0)/\(preferences.obsidianSubfolder)" } ?? "未选择 Obsidian 库")
            case .folder:
                LabeledContent("写入", value: FolderBookmark.name(of: preferences.folderBookmark).map { "\($0)/\(preferences.folderSubfolder)" } ?? "未选择文件夹")
            case .archive, .transcriptAndImages:
                if planning {
                    ProgressView()
                } else if let planError {
                    Text(planError).foregroundStyle(.red)
                } else if let package {
                    Text("将发送：\(Self.describe(package))")
                    if package.shares.count > 1 {
                        Text("文件较多，将分 \(package.shares.count) 次发送").font(.caption).foregroundStyle(.secondary)
                    }
                    if !package.omitted.isEmpty {
                        DisclosureGroup("未随附 \(package.omitted.count) 个文件") {
                            ForEach(package.omitted, id: \.self) { Text($0).font(.caption) }
                        }
                        Button("改发原始 ZIP") { forceArchive = true }
                    }
                }
            }
        }
    }

    private var sendBar: some View {
        VStack(spacing: 8) {
            if let savedNote, effectiveTarget?.id == "obsidian", let url = Self.obsidianURL(for: savedNote) {
                Button("在 Obsidian 中打开") { openURL(url) }
            }
            Button(action: send) {
                Text(sendTitle).frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .disabled(!canSend)
        }
        .padding()
        .background(.bar)
    }

    private var sendTitle: String {
        guard let package, nextGroup > 0 else { return "发送" }
        // Past the last group the counter would read "2/1".
        if nextGroup >= package.shares.count { return "已发送" }
        return "继续发送第 \(nextGroup + 1)/\(package.shares.count) 组"
    }

    private var canSend: Bool {
        guard let target = effectiveTarget, !busy else { return false }
        if target.format.usesShareSheet { return package != nil && nextGroup < (package?.shares.count ?? 0) }
        return true
    }

    // MARK: - Planning

    private func setUp() {
        targets = preferences.targets
        target = targets.first
        let resolution = SceneResolver.resolve(
            groupName: chatName,
            settings: preferences.scenes,
            memories: preferences.groupMemory,
            senders: senders
        )
        sceneID = resolution.scene?.id ?? ""
        renderPrompt()
    }

    private func renderPrompt() {
        guard let scene = preferences.scenes.scene(id: sceneID) else { prompt = ""; return }
        let memory = chatName.flatMap { preferences.groupMemory[GroupName.normalize($0)] }
        prompt = ScenePrompt.render(scene: scene, previousSummaryAt: memory?.lastSummaryAt, currentEnd: end) ?? ""
    }

    private func replan() {
        if let package { try? FileManager.default.removeItem(at: package.workDirectory) }
        package = nil
        planError = nil
        nextGroup = 0
        savedNote = nil
        guard let target = effectiveTarget, target.format.usesShareSheet else { return }
        planning = true
        let request = DeliveryRequest(batches: entries.map(\.batch), target: target, prompt: nil)
        Task {
            let result = await Task.detached(priority: .userInitiated) { Result { try DeliveryPlanner.plan(request) } }.value
            // A newer choice may have replaced this one while it ran.
            guard effectiveTarget == target else {
                if case .success(let stale) = result { try? FileManager.default.removeItem(at: stale.workDirectory) }
                return
            }
            planning = false
            switch result {
            case .success(let package): self.package = package
            case .failure(let error): planError = error.localizedDescription
            }
        }
    }

    // MARK: - Sending

    private func send() {
        guard let target = effectiveTarget else { return }
        if nextGroup == 0, let id = selection.collectionID, store.beginCollectionDelivery(id) == nil { return }
        let text = prompt.trimmingCharacters(in: .whitespacesAndNewlines)
        switch target.format {
        case .archive, .transcriptAndImages:
            guard let package, nextGroup < package.shares.count else { return }
            if !text.isEmpty {
                UIPasteboard.general.string = text
                toast = "提示词已复制，可在对方输入框粘贴"
            }
            let group = package.shares[nextGroup]
            busy = true
            withAnimation { hint = target.hint.isEmpty ? nil : target.hint }
            Task {
                try? await Task.sleep(nanoseconds: 1_000_000_000)
                withAnimation { hint = nil }
                let items: [Any] = (nextGroup == 0 && !text.isEmpty ? [text] : []) + group
                ActivitySheet.present(items: items) { result in
                    busy = false
                    shareFinished(result, target: target, package: package)
                }
            }
        case .knowledgeNote, .folder:
            writeNote(target: target)
        }
    }

    private func shareFinished(_ result: ActivitySheet.Result, target: TargetProfile, package: DeliveryPackage) {
        guard result.completed else {
            if nextGroup == 0, let id = selection.collectionID {
                store.finishCollectionDelivery(id, succeeded: false, target: target.displayName, scene: sceneName)
            }
            return
        }
        if nextGroup == 0 { recordDelivery(target: target, detail: "\(target.format.label) · \(result.activityType ?? "未知去向")") }
        nextGroup += 1
        if nextGroup >= package.shares.count {
            toast = "已交给分享面板"
        }
    }

    private func writeNote(target: TargetProfile) {
        let urls = entries.flatMap(\.batch.items).map(\.url)
        let isObsidian = target.format == .knowledgeNote
        busy = true
        defer { busy = false }
        do {
            let written = try FolderBookmark.access(
                isObsidian ? preferences.obsidianBookmark : preferences.folderBookmark,
                refresh: { renewed in
                    if isObsidian { preferences.obsidianBookmark = renewed } else { preferences.folderBookmark = renewed }
                }
            ) { folder in
                isObsidian
                    ? try KnowledgeDelivery.deliver(urls: urls, vaultPath: folder.path, subfolder: preferences.obsidianSubfolder, chatName: chatName, sceneName: sceneName)
                    : try KnowledgeDelivery.deliver(urls: urls, folderPath: folder.path, subfolder: preferences.folderSubfolder, chatName: chatName, sceneName: sceneName)
            }
            let note = written.first { $0.pathExtension == "md" }
            savedNote = note
            recordDelivery(target: target, detail: note?.lastPathComponent ?? target.displayName)
            toast = "已保存到 \(target.displayName) · \(note?.deletingPathExtension().lastPathComponent ?? "")"
        } catch {
            if let id = selection.collectionID {
                store.finishCollectionDelivery(id, succeeded: false, target: target.displayName, scene: sceneName)
            }
            toast = error.localizedDescription
        }
    }

    private var sceneName: String? { preferences.scenes.scene(id: sceneID)?.name }

    private func recordDelivery(target: TargetProfile, detail: String) {
        let scene = preferences.scenes.scene(id: sceneID)
        store.markDelivered(selection.batchIDs, targetName: target.displayName, detail: detail, chatName: chatName, scene: scene)
        if let id = selection.collectionID {
            store.finishCollectionDelivery(id, succeeded: true, target: target.displayName, scene: scene?.name)
        }
        if let scene, let chatName {
            preferences.rememberGroup(displayName: chatName, sceneID: scene.id, senders: senders, end: end)
        }
        preferences.lastTarget = target.id
    }

    private func finishSheet() {
        if let package { try? FileManager.default.removeItem(at: package.workDirectory) }
        dismiss()
    }

    // MARK: - Text

    static func describe(_ package: DeliveryPackage) -> String {
        let names = package.shares.flatMap { $0 }.map(\.lastPathComponent)
        let images = names.filter(DeliveryPlanner.isImage).count
        if names.first == DeliveryPlanner.transcriptName {
            return images > 0 ? "\(DeliveryPlanner.transcriptName) + \(images) 张图片" : DeliveryPlanner.transcriptName
        }
        return names.count == 1 ? names[0] : "\(names[0]) 等 \(names.count) 个文件"
    }

    static func obsidianURL(for note: URL) -> URL? {
        var components = URLComponents()
        components.scheme = "obsidian"
        components.host = "open"
        components.queryItems = [URLQueryItem(name: "path", value: note.path)]
        guard let url = components.url, UIApplication.shared.canOpenURL(url) else { return nil }
        return url
    }
}
