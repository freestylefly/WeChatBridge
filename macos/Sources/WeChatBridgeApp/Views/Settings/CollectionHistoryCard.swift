import SwiftUI
import WeChatBridgeCore

struct CollectionHistoryCard: View {
    @ObservedObject var model: AppModel
    let collection: BatchCollection
    let actions: SettingsActions
    @State private var expanded = false
    @State private var renaming = false
    @State private var name = ""
    @State private var confirmingTrash = false

    private var busy: Bool { collection.status == .delivering }
    private var status: String {
        switch collection.status {
        case .collecting: L10n.text("收集中")
        case .draft: L10n.text("待发送")
        case .delivering: L10n.text("交付中")
        case .delivered: L10n.text("已交付")
        case .retry: L10n.text("待重试")
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: Space.m) {
            HStack {
                Image(systemName: "tray.full").foregroundStyle(Theme.accent)
                Text(model.collectionName(collection)).font(Typo.paneBodyStrong)
                Spacer()
                StatusPill(text: status, tone: collection.status == .retry ? .warn : .neutral)
                Menu {
                    Button(L10n.text("修改名称")) { name = collection.name; renaming = true }
                    if collection.status == .collecting {
                        Button(L10n.text("保存为待发送")) { model.parkCollection() }
                        Button(L10n.text("开始新的收集")) { model.parkCollection() }
                    }
                    Divider()
                    Button(L10n.text("移到废纸篓"), role: .destructive) { confirmingTrash = true }
                } label: { Image(systemName: "ellipsis") }
                .menuStyle(.borderlessButton).fixedSize().disabled(busy)
            }
            Text(model.collectionSummary(collection)).font(Typo.paneCaption).foregroundStyle(Theme.inkSecondary)
            if let range = model.collectionRange(collection) {
                Text(L10n.format("上一批：%@", range)).font(Typo.paneCaption).foregroundStyle(Theme.inkSecondary)
            }
            if let detail = collection.detail {
                Text(detail).font(Typo.paneCaption).foregroundStyle(collection.status == .retry ? Theme.warning : Theme.inkSecondary)
            }
            if renaming {
                HStack {
                    TextField(L10n.text("收集名称"), text: $name)
                    Button(L10n.text("保存")) {
                        if model.changeCollections({ try $0.rename(collection.id, to: name) }) { renaming = false }
                    }.buttonStyle(SettingsActionButtonStyle())
                    Button(L10n.text("取消")) { renaming = false }.buttonStyle(SettingsActionButtonStyle())
                }
            }
            ViewThatFits(in: .horizontal) {
                HStack { expandButton; Spacer(); controls }
                VStack(alignment: .leading, spacing: Space.s) { expandButton; controls }
            }
            if model.deletedCollectionBatch?.collectionID == collection.id {
                Notice(text: L10n.text("批次已移到废纸篓。")) {
                    Button(L10n.text("撤销")) {
                        _ = model.undoDeletedCollectionBatch()
                    }.buttonStyle(SettingsActionButtonStyle()).disabled(busy)
                }
            }
            if expanded {
                ForEach(Array(collection.batchIDs.enumerated()), id: \.element) { index, id in
                    Divider()
                    HStack {
                        Image(systemName: "doc.zipper").foregroundStyle(Theme.inkSecondary)
                        VStack(alignment: .leading, spacing: Space.xs) {
                            Text(L10n.format("第 %d 批", index + 1)).font(Typo.paneBody)
                            BatchConversationField(model: model, batchID: id, collectionID: collection.id)
                                .disabled(busy)
                            if let batch = model.batch(id: id) {
                                Text(batch.items.map(\.displayName).joined(separator: "、") + " · " + ByteFormat.string(batch.byteCount))
                                    .font(Typo.paneCaption).foregroundStyle(Theme.inkSecondary)
                                if let metadata = model.collectionMetadata[id], let first = metadata.first, let last = metadata.last {
                                    Text(first.date.formatted(date: .abbreviated, time: .shortened) + " — " + last.date.formatted(date: .abbreviated, time: .shortened))
                                        .font(Typo.paneCaption).foregroundStyle(Theme.inkSecondary)
                                }
                            } else { Text(L10n.text("部分收集文件已不存在，请查看批次。")).font(Typo.paneCaption).foregroundStyle(Theme.warning) }
                        }
                        Spacer()
                        if let item = model.batch(id: id)?.items.first {
                            Button(L10n.text("在 Finder 中显示")) { model.reveal(id: item.id) }
                                .buttonStyle(SettingsActionButtonStyle())
                        }
                        Button { _ = model.deleteCollectionBatch(id, from: collection.id) } label: {
                            Label(L10n.text("删除批次"), systemImage: "trash")
                        }.buttonStyle(SettingsActionButtonStyle()).disabled(busy)
                    }
                    DisclosureGroup(L10n.text("查看首尾")) {
                        VStack(alignment: .leading, spacing: Space.s) {
                            CollectionBatchAnchors(model: model, batchID: id)
                        }.padding(.top, Space.s)
                    }
                }
            }
        }
        .padding(Space.l)
        .background(Theme.sunken, in: RoundedRectangle(cornerRadius: Radius.card))
        .alert(L10n.text("将这组收集移到废纸篓？"), isPresented: $confirmingTrash) {
            Button(L10n.text("取消"), role: .cancel) {}
            Button(L10n.text("移到废纸篓"), role: .destructive) { model.discardCollection(collection.id) }
        } message: {
            Text(L10n.format("将 %d 批原始文件移到废纸篓，可从废纸篓恢复。", collection.batchIDs.count))
        }
    }

    private var expandButton: some View {
        Button(expanded ? L10n.text("收起批次") : L10n.text("查看批次")) { expanded.toggle() }
            .buttonStyle(SettingsActionButtonStyle())
    }
    private var controls: some View {
        HStack(spacing: Space.s) {
            if collection.status != .delivered {
                Button(L10n.text("继续收集")) { model.resumeCollection(collection.id) }
                    .buttonStyle(SettingsActionButtonStyle()).disabled(busy || collection.batchIDs.isEmpty)
            }
            Button(collection.status == .retry ? L10n.text("重试交付") : L10n.text("完成并发送")) {
                actions.openCollection(collection.id, true)
            }
            .buttonStyle(SettingsActionButtonStyle(primary: true))
            .disabled(busy || collection.batchIDs.isEmpty)
        }
    }
}

struct BatchConversationField: View {
    @ObservedObject var model: AppModel
    let batchID: UUID
    let collectionID: UUID
    @State private var editing = false
    @State private var name = ""
    @State private var useAsDefault = false
    @State private var error: String?

    var body: some View {
        VStack(alignment: .leading, spacing: Space.xs) {
            if !editing, model.recognizingConversations.contains(batchID) {
                HStack(spacing: Space.s) {
                    ProgressView().controlSize(.small)
                    Text(L10n.text("正在识别群名或聊天人…")).font(Typo.paneCaption)
                }
            } else if editing || model.batchConversation(batchID) == nil {
                HStack(spacing: Space.s) {
                    TextField(L10n.text("群名或聊天人"), text: Binding(get: { name }, set: { name = $0; editing = true }))
                        .textFieldStyle(.roundedBorder)
                        .onSubmit(save)
                    Button(L10n.text("保存"), action: save)
                        .buttonStyle(SettingsActionButtonStyle(width: nil))
                        .disabled(name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
                Toggle(L10n.text("用于本组未命名批次和后续批次"), isOn: $useAsDefault)
                    .toggleStyle(.checkbox).font(Typo.paneCaption)
                Button(L10n.text("识别当前微信")) {
                    editing = false
                    error = nil
                    model.recognizeBatchConversation(batchID)
                }.buttonStyle(SettingsActionButtonStyle(width: nil))
                if let error { Text(error).font(Typo.paneCaption).foregroundStyle(Theme.danger) }
            } else if let source = model.batchConversation(batchID) {
                HStack(alignment: .top, spacing: Space.s) {
                    Text(source).font(Typo.paneBodyStrong).lineLimit(2)
                    Spacer(minLength: 0)
                    Button { name = source; editing = true } label: { Image(systemName: "pencil") }
                        .buttonStyle(IconButtonStyle(size: 24))
                        .help(L10n.text("修改群名或聊天人"))
                        .accessibilityLabel(L10n.text("修改群名或聊天人"))
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .onAppear { name = model.batchConversation(batchID) ?? "" }
        .onChange(of: model.batchConversation(batchID)) { _, source in
            if !editing, let source { name = source }
        }
    }

    private func save() {
        if model.setBatchConversation(batchID, name: name, defaultFor: useAsDefault ? collectionID : nil) {
            editing = false
            error = nil
        } else { error = model.inboxFailure }
    }
}
