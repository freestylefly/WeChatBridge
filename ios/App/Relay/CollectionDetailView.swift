import SwiftUI
import WeChatBridgeCore

struct CollectionDetailView: View {
    @EnvironmentObject private var store: RelayStore
    @Environment(\.dismiss) private var dismiss
    let collectionID: UUID
    @State private var name = ""
    @State private var delivering: DeliverySelection?
    @State private var confirmForget = false

    private var collection: BatchCollection? { store.ledger.collections.first { $0.id == collectionID } }

    var body: some View {
        List {
            if let collection {
                Section("名称") {
                    TextField("未命名收集", text: $name)
                        .onSubmit { store.renameCollection(collectionID, to: name) }
                }
                ContinueCollectingSection(last: store.lastBatch(of: collection)?.summary) {
                    store.resumeCollection(collectionID)
                }
                Section {
                    ForEach(store.entries(collection.batchIDs)) { entry in
                        RelayCardView(entry: entry)
                    }
                    .onDelete { offsets in
                        for id in offsets.map({ collection.batchIDs[$0] }) {
                            store.removeFromCollection(id, collection: collectionID)
                        }
                    }
                } header: {
                    Text("\(collection.batchIDs.count) 批")
                } footer: {
                    Text("投递时按时间合并，前后批重叠的消息只留一份。")
                }
                Section {
                    Button("解散收集", role: .destructive) { confirmForget = true }
                } footer: {
                    Text("解散后批次仍留在中转站。")
                }
            }
        }
        .navigationTitle(collection?.name.isEmpty == false ? collection!.name : "收集")
        .navigationBarTitleDisplayMode(.inline)
        .safeAreaInset(edge: .bottom) {
            Button {
                store.renameCollection(collectionID, to: name)
                delivering = DeliverySelection(batchIDs: orderedIDs, collectionID: collectionID)
            } label: {
                Label("投递整个收集", systemImage: "paperplane.fill").frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .padding()
            .background(.bar)
            .disabled(collection?.batchIDs.isEmpty ?? true)
        }
        .onAppear { name = collection?.name ?? "" }
        .onDisappear { if collection != nil, name != collection?.name { store.renameCollection(collectionID, to: name) } }
        .sheet(item: $delivering) { DeliverySheet(selection: $0) }
        .confirmationDialog("解散这个收集？", isPresented: $confirmForget, titleVisibility: .visible) {
            Button("解散", role: .destructive) {
                store.forgetCollection(collectionID)
                dismiss()
            }
        }
        .onChange(of: collection == nil) { gone in if gone { dismiss() } }
    }

    private var orderedIDs: [UUID] {
        collection.map(store.orderedBatchIDs) ?? []
    }
}

/// 「怎么接着收」: WeChat forwards at most 100 messages, so a long chat comes
/// in batches. The last message is the bookmark: searched in WeChat, it puts
/// the user where the next batch starts.
struct ContinueCollectingSection: View {
    let last: RelaySummary?
    /// Copying means the user is about to collect more: into this collection.
    let willContinue: () -> Void
    @State private var copied = false

    var body: some View {
        Section {
            if let last = last?.last {
                VStack(alignment: .leading, spacing: 6) {
                    Text("上一批最后一句 · \(last.sender) · \(RelayFormat.day(last.date))")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Text(last.text).font(.subheadline).lineLimit(3)
                    if let anchor = self.last?.anchor {
                        Button {
                            UIPasteboard.general.string = anchor
                            willContinue()
                            copied = true
                        } label: {
                            Label(copied ? "已复制「\(anchor)」" : "复制关键词", systemImage: copied ? "checkmark" : "doc.on.doc")
                        }
                        .buttonStyle(.bordered)
                        .padding(.top, 2)
                    }
                }
                .padding(.vertical, 4)
            }
            VStack(alignment: .leading, spacing: 8) {
                step(1, "复制关键词，回微信在这个聊天里搜索，定位到这一句")
                step(2, "从它后面开始多选，一次最多 100 条；多选几条重叠的也没关系")
                step(3, "像平时一样分享出来，在分享面板下方的列表里选「分批收集到微信流」")
            }
            .padding(.vertical, 4)
        } header: {
            Text("怎么接着收")
        } footer: {
            Text("找不到「分批收集到微信流」？在分享面板最下方点「编辑操作」，把它加到个人收藏。")
        }
    }

    private func step(_ number: Int, _ text: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Text("\(number)")
                .font(.caption.weight(.bold))
                .foregroundStyle(.white)
                .frame(width: 18, height: 18)
                .background(Color.accentColor, in: Circle())
            Text(text).font(.subheadline)
        }
    }
}
