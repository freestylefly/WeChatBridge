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
    /// A notification's one-tap delivery fires once, not on every replan.
    @State private var quickSent = false

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
                    Section { scenePicker }
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
                        .multilineTextAlignment(.center)
                        .padding(24)
                        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16))
                }
            }
        }
        // Full height: at medium only the grid showed, with send and preview
        // below the fold and the chat bleeding through the glass.
        .presentationDetents([.large])
        .presentationBackground(Color(.systemGroupedBackground))
        .onAppear(perform: setUp)
        .onChange(of: target) { _ in replan() }
        .onChange(of: forceArchive) { _ in replan() }
        .onChange(of: sceneID) { _ in renderPrompt() }
    }

    // MARK: - Sections

    private var targetGrid: some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 76), spacing: 12)], spacing: 14) {
            ForEach(targets) { profile in
                let selected = target?.id == profile.id
                Button { target = profile } label: {
                    VStack(spacing: 6) {
                        TargetIcon(profile: profile, size: 50)
                            .padding(3)
                            .overlay {
                                RoundedRectangle(cornerRadius: 14, style: .continuous)
                                    .strokeBorder(Color.accentColor, lineWidth: selected ? 2.5 : 0)
                            }
                            .overlay(alignment: .topTrailing) {
                                if selected {
                                    Image(systemName: "checkmark.circle.fill")
                                        .font(.system(size: 18))
                                        .symbolRenderingMode(.palette)
                                        .foregroundStyle(.white, Color.accentColor)
                                        .offset(x: 6, y: -6)
                                }
                            }
                        Text(profile.displayName)
                            .font(.caption.weight(selected ? .semibold : .regular))
                            .foregroundStyle(selected ? Color.accentColor : .primary)
                            .lineLimit(1)
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
                    ForEach(Self.rows(package), id: \.title) { row in
                        HStack(spacing: 12) {
                            Image(systemName: row.symbol)
                                .foregroundStyle(Color.accentColor)
                                .frame(width: 24)
                            Text(row.title).lineLimit(1).truncationMode(.middle)
                            Spacer()
                            Text(ByteFormat.string(row.bytes)).font(.footnote).foregroundStyle(.secondary)
                        }
                    }
                    if package.shares.count > 1 {
                        Text("文件较多，将分 \(package.shares.count) 次发送").font(.caption).foregroundStyle(.secondary)
                    }
                    if !package.omitted.isEmpty {
                        VStack(alignment: .leading, spacing: 6) {
                            Text(Self.sideNote(package.omitted))
                                .font(.caption)
                                .foregroundStyle(.secondary)
                                .lineLimit(3)
                            Button("改发原始 ZIP") { forceArchive = true }
                                .font(.footnote)
                                .buttonStyle(.borderless)
                        }
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
        target = selection.quickTargetID.flatMap { id in targets.first { $0.id == id } } ?? targets.first
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
        guard let target = effectiveTarget, target.format.usesShareSheet else {
            // Writers need no planning; a quick delivery writes straight away.
            if effectiveTarget != nil { sendIfQuick() }
            return
        }
        planning = true
        let request = DeliveryRequest(batches: entries.map(\.batch), target: target, prompt: nil)
        let imagesInPDF = preferences.imagesInPDF
        Task {
            let result = await Task.detached(priority: .userInitiated) { Result {
                let package = try DeliveryPlanner.plan(request)
                guard request.target.format == .transcriptAndImages else { return package }
                return imagesInPDF ? try package.foldedIntoPDF() : package.transcriptOnly()
            } }.value
            // A newer choice may have replaced this one while it ran.
            guard effectiveTarget == target else {
                if case .success(let stale) = result { try? FileManager.default.removeItem(at: stale.workDirectory) }
                return
            }
            planning = false
            switch result {
            case .success(let package):
                self.package = package
                sendIfQuick()
            case .failure(let error): planError = error.localizedDescription
            }
        }
    }

    // MARK: - Sending

    /// The notification already said where this goes; the user's next tap
    /// should be the app in the share sheet, not 「发送」.
    private func sendIfQuick() {
        guard selection.quickTargetID != nil, !quickSent, canSend else { return }
        quickSent = true
        send()
    }

    /// The images and attachments the TXT names: media into Photos for the
    /// target's 相册 picker, the rest into 「我的iPhone › 微信流」 for its file
    /// picker. Run only once the share went through, so a cancelled one
    /// leaves nothing behind. Images the PDF already carries are skipped.
    private func exportForPicking(_ package: DeliveryPackage) async -> String? {
        let jobs = entries.map { FilesExport.Job(batch: $0.batch, name: FilesExport.folderName(for: $0), inPDF: package.inPDF) }
        do {
            let summary = try await FilesExport.exportForPicking(jobs)
            return summary.isEmpty ? nil : summary.message
        } catch {
            return "图片和附件没能存下：\(error.localizedDescription)"
        }
    }

    /// Whether a completed share of the first group also exports.
    private func exports(_ package: DeliveryPackage, target: TargetProfile) -> Bool {
        target.format == .transcriptAndImages && !package.omitted.isEmpty
    }

    /// 「发送时存到…」 under the preview: which names go where.
    static func sideNote(_ omitted: [String]) -> String {
        let media = omitted.filter(FilesExport.isMedia)
        let files = omitted.filter { !FilesExport.isMedia($0) }
        var parts: [String] = []
        if !media.isEmpty { parts.append("图片和视频存到「照片」：\(media.joined(separator: "、"))") }
        if !files.isEmpty { parts.append("附件放到「我的iPhone › 微信流」：\(files.joined(separator: "、"))") }
        return "发送时" + parts.joined(separator: "；")
    }

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
            let asksPhotos = nextGroup == 0 && exports(package, target: target) && package.omitted.contains(where: FilesExport.isMedia)
            busy = true
            let message = target.sendHint(hasPrompt: !text.isEmpty)
            withAnimation { hint = message.isEmpty ? nil : message }
            if nextGroup == 0, let batchID = selection.batchIDs.first {
                DeliveryActivityController.start(
                    batchID: batchID,
                    title: chatName ?? entries.first?.batch.items.first?.displayName ?? "聊天记录",
                    detail: activityDetail,
                    target: target,
                    prompt: text,
                    enabled: preferences.liveActivity
                )
            }
            Task {
                // Before the share sheet, so the one-time prompt never lands on it.
                if asksPhotos { _ = await PhotosSaver.authorize() }
                // Long enough to read the second line when there is one.
                try? await Task.sleep(nanoseconds: message.contains("\n") ? 2_500_000_000 : 1_000_000_000)
                withAnimation { hint = nil }
                let items: [Any] = (nextGroup == 0 && !text.isEmpty && !target.dropsSharedText ? [text] : []) + group
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
            if nextGroup == 0 { DeliveryActivityController.cancel() }
            if nextGroup == 0, let id = selection.collectionID {
                store.finishCollectionDelivery(id, succeeded: false, target: target.displayName, scene: sceneName)
            }
            return
        }
        let exporting = nextGroup == 0 && exports(package, target: target)
        if nextGroup == 0 {
            recordDelivery(target: target, detail: "\(target.format.label) · \(result.activityType ?? "未知去向")")
            DeliveryActivityController.markSent()
        }
        nextGroup += 1
        let hasPrompt = !prompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        let done = nextGroup < package.shares.count ? nil
            : hasPrompt && target.dropsSharedText ? TargetProfile.pasteHint : "已交给分享面板"
        guard exporting else {
            if let done { toast = done }
            return
        }
        busy = true
        Task {
            // Said once the share sheet is gone; a toast under it would never be seen.
            let note = await exportForPicking(package)
            busy = false
            toast = [done, note].compactMap { $0 }.joined(separator: "\n")
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

    /// 「4 条消息 · 1 个附件」 for the Live Activity; empty for plain text.
    private var activityDetail: String {
        var parts: [String] = []
        let messages = summaries.compactMap(\.messageCount).reduce(0, +)
        if messages > 0 { parts.append("\(messages) 条消息") }
        let media = summaries.map(\.mediaCount).reduce(0, +)
        if media > 0 { parts.append("\(media) 个附件") }
        return parts.joined(separator: " · ")
    }

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

    struct PreviewRow {
        let symbol: String
        let title: String
        let bytes: Int64
    }

    /// One row per file, except that images fold into one 「3 张图片」 row.
    static func rows(_ package: DeliveryPackage, fileManager: FileManager = .default) -> [PreviewRow] {
        func size(_ url: URL) -> Int64 {
            ((try? fileManager.attributesOfItem(atPath: url.path))?[.size] as? NSNumber)?.int64Value ?? 0
        }
        let urls = package.shares.flatMap { $0 }
        let images = urls.filter { DeliveryPlanner.isImage($0.lastPathComponent) }
        var rows = urls.filter { !DeliveryPlanner.isImage($0.lastPathComponent) }.map { url in
            PreviewRow(
                symbol: url.pathExtension.lowercased() == "zip" ? "doc.zipper" : "doc.text",
                title: url.lastPathComponent,
                bytes: size(url)
            )
        }
        if !images.isEmpty {
            rows.append(PreviewRow(symbol: "photo.on.rectangle", title: "\(images.count) 张图片", bytes: images.map(size).reduce(0, +)))
        }
        return rows
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
