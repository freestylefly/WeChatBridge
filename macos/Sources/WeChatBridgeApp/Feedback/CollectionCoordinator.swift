import AppKit
import Combine
import SwiftUI
import WeChatBridgeCore

@MainActor
final class CollectionCoordinator: ObservableObject {
    let model: AppModel
    let targets: ForwardTargets
    let preferences: Preferences
    private let runner: ActionRunner
    private var panel: CollectionPanel?
    private var subscriptions = Set<AnyCancellable>()
    private var returningStatus: BatchCollection.Status = .collecting
    private var frameCollectionID: UUID?
    private var pointer = NSPoint.zero
    @Published private(set) var groupID: UUID?
    @Published private(set) var isDelivery = false
    @Published var showAnchors = false
    @Published var anchorBatchID: UUID?
    private var lastBatchID: UUID?
    @Published var errorMessage: String?
    @Published var selection: String
    @Published var sceneID: String

    init(model: AppModel, targets: ForwardTargets, preferences: Preferences, runner: ActionRunner) {
        self.model = model
        self.targets = targets
        self.preferences = preferences
        self.runner = runner
        selection = UserDefaults.standard.string(forKey: "collection.lastTarget") ?? ""
        sceneID = UserDefaults.standard.string(forKey: "collection.lastScene") ?? ""
        model.didCollect.sink { [weak self] id in
            guard let self else { return }
            if self.isDelivery, self.panel?.isVisible == true, self.groupID != id {
                self.errorMessage = L10n.text("新批次已加入另一组收集，可在记录里查看。")
            } else { self.show(id) }
        }.store(in: &subscriptions)
        model.$collectionLedger.dropFirst().sink { [weak self] _ in self?.resizeSoon() }.store(in: &subscriptions)
        model.$collectionMetadata.dropFirst().sink { [weak self] _ in self?.resizeSoon() }.store(in: &subscriptions)
        model.$collectionIsImporting.dropFirst().sink { [weak self] importing in
            if importing, self?.panel?.isVisible != true { self?.showImport() }
            else { self?.resizeSoon() }
        }.store(in: &subscriptions)
    }

    var group: BatchCollection? { groupID.flatMap(model.collection) }
    var destinations: [ForwardDestination] {
        targets.destinations.filter { destination in
            if destination.action == .obsidian { return preferences.obsidianVaultPath?.isEmpty == false }
            guard let bundle = destination.target?.bundleIdentifier ?? destination.action.targetBundleIdentifier else { return false }
            return InstalledApp.lookup(bundle).isInstalled
        }
    }
    var chosenDestination: ForwardDestination? { destinations.first { $0.id == selection } }
    var isFileDestination: Bool { selection == "copy" || selection == "folder" }
    var scenes: [WeChatScene] {
        guard !isFileDestination else { return [] }
        let bundle = chosenDestination?.target?.bundleIdentifier ?? chosenDestination?.action.targetBundleIdentifier
        let agent = bundle.flatMap(AgentID.matching(bundleIdentifier:))
        return preferences.scenes.enabledScenes.filter { scene in agent.map { scene.compatibleAgents.contains($0) } ?? true }
    }
    var buttonTitle: String {
        let count = group.map { model.collectionBatches($0).flatMap(\.items).count } ?? 0
        if selection == "copy" { return L10n.format("复制 %d 个文件", count) }
        if selection == "folder" { return L10n.format("保存 %d 个文件", count) }
        if chosenDestination?.action == .obsidian { return L10n.text("保存到 Obsidian") }
        return L10n.format("附加到 %@", chosenDestination?.title ?? L10n.text("目标应用"))
    }

    func show(_ id: UUID, delivery: Bool = false) {
        guard let group = model.collection(id), group.status != .delivering else { return }
        if groupID != id || anchorBatchID == lastBatchID || !group.batchIDs.contains(where: { $0 == anchorBatchID }) {
            anchorBatchID = group.batchIDs.last
        }
        lastBatchID = group.batchIDs.last
        let sameDelivery = groupID == id && isDelivery && !delivery
        groupID = id
        if !sameDelivery { isDelivery = delivery }
        if delivery { returningStatus = group.status }
        errorMessage = group.status == .retry ? group.detail : nil
        pointer = NSEvent.mouseLocation
        present(key: delivery)
    }

    func resume(_ id: UUID) {
        let previous = model.collectionLedger.current?.id
        model.resumeCollection(id)
        if previous != nil, previous != id {
            errorMessage = L10n.text("上一组已保存为待发送；后续分享加入当前这组。")
        }
    }

    func hide() { panel?.orderOut(nil) }

    private func showImport() {
        groupID = model.collectionLedger.current?.id
        isDelivery = false
        pointer = NSEvent.mouseLocation
        present(key: false)
    }

    func finish() {
        guard let groupID, let group else { return }
        if let missing = group.batchIDs.first(where: { model.batchConversation($0) == nil }) {
            anchorBatchID = missing
            showAnchors = true
            errorMessage = L10n.text("请先补充每批的群名或聊天人。")
            resizeSoon()
            return
        }
        show(groupID, delivery: true)
    }

    func cancelDelivery() {
        if returningStatus == .collecting { isDelivery = false; present(key: false) }
        else { hide() }
    }

    func importFailed(_ failure: ShareFailure) {
        if let current = model.collectionLedger.current { show(current.id) }
        else { showImport() }
        errorMessage = L10n.format("这一批未收到，前 %d 批已保存：%@", model.collectionLedger.current?.batchIDs.count ?? 0, failure.message)
    }

    private func present(key: Bool) {
        let window = panel ?? {
            let p = CollectionPanel(contentRect: .zero, styleMask: [.nonactivatingPanel, .borderless], backing: .buffered, defer: false)
            FloatingCapsule.configure(p)
            p.isMovableByWindowBackground = true
            p.onCancel = { [weak self] in
                guard let self else { return }
                if self.isDelivery { self.cancelDelivery() } else { self.hide() }
            }
            panel = p
            return p
        }()
        let root = CollectionPanelView(coordinator: self, model: model, targets: targets, preferences: preferences)
        window.contentView = NSHostingView(rootView: root)
        resize()
        if key { window.makeKeyAndOrderFront(nil) }
        window.orderFrontRegardless()
    }

    func resizeSoon() {
        DispatchQueue.main.async { [weak self] in
            guard let self, self.panel?.isVisible == true else { return }
            if let group = self.group, !group.batchIDs.contains(where: { $0 == self.anchorBatchID }) {
                self.anchorBatchID = group.batchIDs.last
            }
            self.resize()
        }
    }

    private func resize() {
        guard let window = panel, let hosting = window.contentView else { return }
        let visible = FloatingCapsule.visibleFrame(containing: pointer)
        hosting.frame.size = NSSize(width: isDelivery ? 390 : 344, height: visible.height)
        hosting.layoutSubtreeIfNeeded()
        let size = hosting.fittingSize
        var frame = NSRect(origin: window.frame.origin, size: size)
        if frameCollectionID != groupID || window.frame.width == 0 {
            frame.origin = NSPoint(x: visible.maxX - size.width - Metrics.screenMargin, y: visible.maxY - size.height - Metrics.screenMargin)
            frameCollectionID = groupID
        } else {
            frame.origin.y = window.frame.maxY - size.height
        }
        window.setFrame(FloatingCapsule.clamped(frame, in: visible), display: true)
        hosting.setFrameSize(window.contentLayoutRect.size)
        hosting.layoutSubtreeIfNeeded()
    }

    func deliver() {
        guard let id = groupID, let group, group.status != .delivering,
              isFileDestination || chosenDestination != nil else { return }
        let destination = chosenDestination
        let chosenScene = isFileDestination ? nil : scenes.first { $0.id == sceneID }
        var folder: URL?
        if selection == "folder" {
            let picker = NSOpenPanel()
            picker.canChooseDirectories = true
            picker.canChooseFiles = false
            picker.allowsMultipleSelection = false
            picker.prompt = L10n.text("保存")
            guard picker.runModal() == .OK, let url = picker.url else { return }
            folder = url
        }
        guard let urls = model.freezeCollection(id) else { errorMessage = model.inboxFailure; return }
        UserDefaults.standard.set(selection, forKey: "collection.lastTarget")
        UserDefaults.standard.set(chosenScene?.id ?? "", forKey: "collection.lastScene")
        hide()
        if isFileDestination {
            let copied = selection == "copy"
            runner.enqueueExclusive { [weak self] in
                guard let self else { return }
                do {
                    if let folder {
                        _ = try await Task.detached(priority: .userInitiated) {
                            try FolderDelivery.save(urls, to: folder, checkCancellation: { try Task.checkCancellation() })
                        }.value
                    } else if !FilePasteboard.write(urls) { throw CocoaError(.fileWriteUnknown) }
                    self.model.recordDelivery(urls: urls, action: .clipboard)
                    self.complete(id, succeeded: true, target: copied ? L10n.text("剪贴板") : L10n.text("文件夹"), detail: copied ? L10n.text("已复制文件") : L10n.text("已保存到文件夹"))
                } catch {
                    self.complete(id, succeeded: false, target: copied ? L10n.text("剪贴板") : L10n.text("文件夹"), detail: error.localizedDescription)
                }
            }
        } else if let destination {
            let arrival = ArrivedBatch(action: destination.action, target: destination.target, urls: urls)
            runner.deliverCollection(arrival, scene: chosenScene) { [weak self] success, failure in
                guard let self else { return }
                let detail = success
                    ? (destination.action == .obsidian ? L10n.text("已保存到 Obsidian") : L10n.format("已执行粘贴，请在 %@ 确认附件后发送。", destination.title))
                    : (failure ?? L10n.text("目标未能接收附件，原始文件已保留。"))
                self.complete(id, succeeded: success, target: destination.title, detail: detail, scene: chosenScene?.name)
            }
        }
    }

    private func complete(_ id: UUID, succeeded: Bool, target: String, detail: String, scene: String? = nil) {
        model.finishCollection(id, succeeded: succeeded, target: target, detail: detail, scene: scene)
        // A later share may already have opened the next group's float.
        guard groupID == id else { return }
        if !succeeded { show(id, delivery: true) }
    }
}

private final class CollectionPanel: NSPanel {
    var onCancel: (() -> Void)?
    override var canBecomeKey: Bool { true }
    override func cancelOperation(_ sender: Any?) { onCancel?() }
}

private struct CollectionPanelView: View {
    @ObservedObject var coordinator: CollectionCoordinator
    @ObservedObject var model: AppModel
    @ObservedObject var targets: ForwardTargets
    @ObservedObject var preferences: Preferences

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: Space.m) {
                HStack {
                    Text(coordinator.isDelivery ? L10n.text("完成并发送") : L10n.text("分批收集"))
                        .font(Typo.paneBodyStrong)
                    Spacer()
                    if !coordinator.isDelivery, coordinator.group != nil {
                        StatusPill(text: coordinator.group?.status == .collecting ? L10n.text("收集中") : L10n.text("待发送"), tone: .live)
                    }
                    Button {
                        if coordinator.isDelivery { coordinator.cancelDelivery() } else { coordinator.hide() }
                    } label: { Image(systemName: "xmark") }
                    .buttonStyle(.plain)
                    .help(L10n.text("隐藏浮条，保留收集"))
                    .accessibilityLabel(L10n.text("隐藏浮条，保留收集"))
                }
                if let group = coordinator.group {
                    Text(model.collectionName(group)).font(Typo.paneCaption).foregroundStyle(Theme.inkSecondary)
                    Text(model.collectionSummary(group)).font(Typo.paneBodyStrong)
                    if coordinator.isDelivery { delivery(group) }
                    else { collecting(group) }
                } else {
                    Text(model.collectionIsImporting ? L10n.text("正在保存第一批…") : L10n.text("尚未收到文件"))
                        .font(Typo.paneBodyStrong)
                }
                if let error = coordinator.errorMessage {
                    Notice(text: error, tone: .warn) {
                        Button(L10n.text("知道了")) { coordinator.errorMessage = nil }
                            .buttonStyle(SettingsActionButtonStyle())
                    }
                }
            }
            .padding(Space.l)
        }
        .scrollBounceBehavior(.basedOnSize)
        .frame(width: coordinator.isDelivery ? 390 : 344)
        .frame(maxHeight: FloatingCapsule.visibleFrame.height - 2 * Metrics.screenMargin)
        .fixedSize(horizontal: false, vertical: true)
        .background(.regularMaterial)
        .clipShape(RoundedRectangle(cornerRadius: Radius.panel))
        .overlay(RoundedRectangle(cornerRadius: Radius.panel).strokeBorder(Palette.hairline, lineWidth: Stroke.hairline))
        .onChange(of: coordinator.showAnchors) { _, _ in coordinatorLayout() }
        .onChange(of: coordinator.anchorBatchID) { _, _ in coordinatorLayout() }
        .onChange(of: coordinator.errorMessage) { _, _ in coordinatorLayout() }
        .onChange(of: model.inboxFailure) { _, _ in coordinatorLayout() }
        .onChange(of: model.recognizingConversations) { _, _ in coordinatorLayout() }
    }

    private func coordinatorLayout() {
        // Allow SwiftUI to finish its new intrinsic size before fitting the panel.
        coordinator.resizeSoon()
    }

    @ViewBuilder
    private func collecting(_ group: BatchCollection) -> some View {
        if model.collectionIsImporting {
            HStack { ProgressView().controlSize(.small); Text(L10n.format("正在保存第 %d 批…", group.batchIDs.count + 1)) }
                .font(Typo.paneCaption)
        }
        if !coordinator.showAnchors, let range = model.collectionRange(group) {
            Text(L10n.format("上一批：%@", range)).font(Typo.paneCaption).foregroundStyle(Theme.inkSecondary)
        }
        if let id = coordinator.anchorBatchID, group.batchIDs.contains(id) {
            HStack(alignment: .top, spacing: Space.s) {
                BatchConversationField(model: model, batchID: id, collectionID: group.id)
                    .id(id)
                Button {
                    if !model.deleteCollectionBatch(id, from: group.id) { coordinator.errorMessage = model.inboxFailure }
                } label: { Image(systemName: "trash") }
                .buttonStyle(IconButtonStyle())
                .help(L10n.text("删除当前批次"))
                .accessibilityLabel(L10n.text("删除当前批次"))
                .disabled(group.status == .delivering)
            }
        }
        if coordinator.showAnchors {
            if group.batchIDs.count <= 5 {
                batchPicker(group).pickerStyle(.segmented)
            } else {
                batchPicker(group).pickerStyle(.menu)
            }
            if let id = coordinator.anchorBatchID {
                if let range = model.batchRange(id) {
                    Text(L10n.format("本批：%@", range)).font(Typo.paneCaption).foregroundStyle(Theme.inkSecondary)
                }
                CollectionBatchAnchors(model: model, batchID: id)
                    .id(id)
            }
        }
        Text(L10n.text("继续在微信分享到同一入口。"))
            .font(Typo.paneCaption).foregroundStyle(Theme.inkSecondary)
        if model.deletedCollectionBatch?.collectionID == group.id {
            Notice(text: L10n.text("批次已移到废纸篓。")) {
                Button(L10n.text("撤销")) { _ = model.undoDeletedCollectionBatch() }
                    .buttonStyle(SettingsActionButtonStyle())
            }
        }
        HStack {
            Button(coordinator.showAnchors ? L10n.text("收起首尾") : L10n.text("查看首尾")) { coordinator.showAnchors.toggle() }
                .buttonStyle(SettingsActionButtonStyle())
            Spacer()
            Button(L10n.text("完成并发送")) { coordinator.finish() }
                .buttonStyle(SettingsActionButtonStyle(primary: true))
                .disabled(model.collectionIsImporting || group.batchIDs.isEmpty)
        }
    }

    private func batchPicker(_ group: BatchCollection) -> some View {
        Picker(L10n.text("查看批次"), selection: $coordinator.anchorBatchID) {
            ForEach(Array(group.batchIDs.enumerated()), id: \.element) { index, id in
                Text(L10n.format("第 %d 批", index + 1)).tag(Optional(id))
            }
        }
    }

    private func delivery(_ group: BatchCollection) -> some View {
        VStack(alignment: .leading, spacing: Space.s) {
            Text(L10n.text("文字、图片和视频保留在原始 ZIP 中。"))
                .font(Typo.paneCaption).foregroundStyle(Theme.inkSecondary)
            Text(L10n.text("交付到")).font(Typo.captionStrong)
            ForEach(coordinator.destinations) { destination in
                destinationButton(destination.title, id: destination.id)
            }
            Divider()
            destinationButton(L10n.text("复制文件"), id: "copy")
            destinationButton(L10n.text("保存到文件夹"), id: "folder")
            Picker(L10n.text("场景"), selection: $coordinator.sceneID) {
                Text(L10n.text("直接交付，不附加场景")).tag("")
                ForEach(coordinator.scenes) { Text($0.name).tag($0.id) }
            }
            .disabled(coordinator.isFileDestination)
            .onChange(of: coordinator.selection) { _, _ in
                if coordinator.isFileDestination || !coordinator.scenes.contains(where: { $0.id == coordinator.sceneID }) { coordinator.sceneID = "" }
            }
            HStack {
                Button(L10n.text("继续收集")) { coordinator.cancelDelivery() }
                    .buttonStyle(SettingsActionButtonStyle())
                Spacer()
                Button(coordinator.buttonTitle) { coordinator.deliver() }
                    .buttonStyle(SettingsActionButtonStyle(primary: true))
                    .disabled(model.collectionIsImporting || group.status == .delivering || (!coordinator.isFileDestination && coordinator.chosenDestination == nil))
            }.padding(.top, Space.s)
        }
    }

    private func destinationButton(_ name: String, id: String) -> some View {
        Button { coordinator.selection = id } label: {
            HStack {
                Image(systemName: coordinator.selection == id ? "largecircle.fill.circle" : "circle")
                    .foregroundStyle(coordinator.selection == id ? Theme.accent : Theme.inkSecondary)
                Text(name).font(Typo.paneBody)
                Spacer()
            }
            .padding(.horizontal, Space.s).padding(.vertical, 6)
            .background(coordinator.selection == id ? Theme.accentSoft : .clear, in: RoundedRectangle(cornerRadius: Radius.row))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(coordinator.selection == id ? .isSelected : [])
    }
}

struct CollectionAnchorView: View {
    let title: String
    let record: WeChatTranscriptRecord
    @State private var copied = false
    private var stamp: String {
        let formatter = DateFormatter()
        formatter.dateFormat = L10n.text("M月d日 HH:mm")
        return formatter.string(from: record.date) + " · " + record.sender
    }
    var body: some View {
        VStack(alignment: .leading, spacing: Space.xs) {
            Text(title + " · " + stamp).font(Typo.paneCaption).foregroundStyle(Theme.inkSecondary)
            Text(String(record.text.prefix(300))).font(Typo.paneCaption).lineLimit(4)
            if !record.text.isEmpty {
                Button {
                    copied = FilePasteboard.writeText(String(record.text.prefix(120)))
                } label: {
                    Label(copied ? L10n.text("已复制") : L10n.text("复制关键词"), systemImage: copied ? "checkmark" : "doc.on.doc")
                }
                .buttonStyle(SettingsActionButtonStyle())
                .task(id: copied) {
                    guard copied else { return }
                    try? await Task.sleep(nanoseconds: 1_500_000_000)
                    guard !Task.isCancelled else { return }
                    copied = false
                }
            }
        }
        .padding(Space.s)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Theme.sunken, in: RoundedRectangle(cornerRadius: Radius.row))
    }
}

struct CollectionBatchAnchors: View {
    @ObservedObject var model: AppModel
    let batchID: UUID

    var body: some View {
        if let metadata = model.collectionMetadata[batchID], let first = metadata.first, let last = metadata.last {
            CollectionAnchorView(title: L10n.text("较早一条"), record: first)
            CollectionAnchorView(title: L10n.text("较晚一条"), record: last)
        } else {
            Text(L10n.text("未能读取位置参考，原始文件已保存。"))
                .font(Typo.paneCaption).foregroundStyle(Theme.inkSecondary)
        }
    }
}
