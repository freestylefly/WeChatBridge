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
                Section("\(collection.batchIDs.count) 批，投递时按时间合并") {
                    ForEach(store.entries(collection.batchIDs)) { entry in
                        RelayCardView(entry: entry)
                    }
                    .onDelete { offsets in
                        for id in offsets.map({ collection.batchIDs[$0] }) {
                            store.removeFromCollection(id, collection: collectionID)
                        }
                    }
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
        store.entries(collection?.batchIDs ?? [])
            .sorted { ($0.summary?.start ?? $0.batch.createdAt) < ($1.summary?.start ?? $1.batch.createdAt) }
            .map(\.id)
    }
}
