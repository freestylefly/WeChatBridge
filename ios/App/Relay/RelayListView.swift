import SwiftUI
import WeChatBridgeCore

enum RelayFilter: String, CaseIterable, Identifiable {
    case all = "全部"
    case undelivered = "未投递"
    case collecting = "收集中"
    var id: Self { self }
}

/// What a delivery sheet works on: one batch, a one-off merge, or a saved
/// collection (which has its own ledger bookkeeping).
struct DeliverySelection: Identifiable {
    let id = UUID()
    let batchIDs: [UUID]
    var collectionID: UUID?
    /// From a notification: preselect this target and send without a tap.
    var quickTargetID: String?
}

struct RelayListView: View {
    @EnvironmentObject private var store: RelayStore
    @Binding var showsOnboarding: Bool
    @State private var filter: RelayFilter = .all
    @State private var editMode: EditMode = .inactive
    @State private var selection = Set<UUID>()
    @State private var delivering: DeliverySelection?
    @ObservedObject private var router = QuickDeliverRouter.shared
    @EnvironmentObject private var preferences: Preferences
    @State private var path: [UUID] = []
    /// What the last 「存图片和附件」 did, for the confirmation.
    @State private var exported: String?
    /// How to pick up the next batch in WeChat, after 「继续收集」.
    @State private var collectNote: String?

    private var rows: [RelayEntry] {
        let entries = store.visibleEntries
        switch filter {
        case .all: return entries
        case .undelivered: return entries.filter { !$0.isDelivered }
        case .collecting: return entries.filter { store.collectionContaining($0.id) != nil }
        }
    }

    var body: some View {
        NavigationStack(path: $path) {
            // Bound only while editing: a live selection swallows taps that
            // should push the detail view.
            List(selection: editMode.isEditing ? $selection : nil) {
                if !store.failures.isEmpty {
                    FailureBanner(failures: store.failures) { store.failures = [] }
                        .relayCardRow()
                }
                if filter != .undelivered {
                    ForEach(store.collections) { collection in
                        NavigationLink {
                            CollectionDetailView(collectionID: collection.id)
                        } label: {
                            CollectionCardView(collection: collection) {
                                continueCollecting(collection)
                            } deliver: {
                                delivering = DeliverySelection(batchIDs: store.orderedBatchIDs(of: collection), collectionID: collection.id)
                            }
                        }
                        .selectionDisabled()
                        .relayCardRow()
                    }
                }
                ForEach(rows) { entry in
                    NavigationLink(value: entry.id) {
                        RelayCardView(entry: entry, collecting: store.collectionContaining(entry.id) != nil) {
                            store.addToCollection(entry.id)
                            if let collection = store.collectionContaining(entry.id) { continueCollecting(collection) }
                        }
                    }
                    .relayCardRow()
                    .contextMenu { deliverMenu(entry) }
                    .swipeActions(edge: .trailing) {
                        Button(role: .destructive) { store.delete([entry.id]) } label: { Label("删除", systemImage: "trash") }
                    }
                    .swipeActions(edge: .leading) {
                        Button { delivering = DeliverySelection(batchIDs: [entry.id]) } label: { Label("投递", systemImage: "paperplane") }
                            .tint(.accentColor)
                    }
                }
            }
            .listStyle(.plain)
            .scrollContentBackground(.hidden)
            .background(Color(.systemGroupedBackground))
            .overlay {
                if store.visibleEntries.isEmpty && store.collections.isEmpty {
                    EmptyRelayView { showsOnboarding = true }
                }
            }
            .navigationTitle("中转站")
            .navigationDestination(for: UUID.self) { id in
                RelayDetailView(batchID: id)
            }
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Picker("筛选", selection: $filter) {
                        ForEach(RelayFilter.allCases) { Text($0.rawValue).tag($0) }
                    }
                    .pickerStyle(.menu)
                }
                ToolbarItem(placement: .topBarTrailing) {
                    EditButton().disabled(store.visibleEntries.isEmpty)
                }
                if editMode.isEditing {
                    ToolbarItemGroup(placement: .bottomBar) {
                        Button("合并投递") {
                            delivering = DeliverySelection(batchIDs: orderedSelection)
                            editMode = .inactive
                        }
                        .disabled(selection.isEmpty)
                        Spacer()
                        // Bar items get a tight width; without this the count truncates to "已选…".
                        Text("已选 \(selection.count) 条").font(.footnote).foregroundStyle(.secondary).fixedSize()
                        Spacer()
                        Button("删除", role: .destructive) {
                            store.delete(selection)
                            selection.removeAll()
                            editMode = .inactive
                        }
                        .disabled(selection.isEmpty)
                    }
                }
            }
            // The tab bar would cover the edit actions in the bottom bar.
            // `.automatic`, not `.visible`: a pushed detail view hides it too.
            .toolbar(editMode.isEditing ? .hidden : .automatic, for: .tabBar)
            .environment(\.editMode, $editMode)
            .refreshable { store.reload() }
            .safeAreaInset(edge: .bottom) {
                if !store.pendingDeletion.isEmpty {
                    UndoToast(count: store.pendingDeletion.count) { store.undoDeletion() }
                }
            }
            .alert("出错了", isPresented: Binding(get: { store.lastError != nil }, set: { if !$0 { store.lastError = nil } })) {
                Button("好") { store.lastError = nil }
            } message: {
                Text(store.lastError ?? "")
            }
            .alert("存图片和附件", isPresented: Binding(get: { exported != nil }, set: { if !$0 { exported = nil } })) {
                Button("好") { exported = nil }
            } message: {
                Text(exported ?? "")
            }
            .alert("接着收集", isPresented: Binding(get: { collectNote != nil }, set: { if !$0 { collectNote = nil } })) {
                Button("好") { collectNote = nil }
            } message: {
                Text(collectNote ?? "")
            }
            .sheet(item: $delivering) { selection in
                DeliverySheet(selection: selection)
            }
            .onAppear(perform: takeQuickRequest)
            .onChange(of: router.pending) { _ in takeQuickRequest() }
            .onChange(of: router.open) { open in
                guard let open, store.entry(open.batchID) != nil else { return }
                editMode = .inactive
                // The finished delivery sheet would otherwise sit over the chat.
                delivering = nil
                path = [open.batchID]
            }
        }
    }

    /// Long-press: straight to a recent target's share sheet, or the full panel.
    @ViewBuilder
    private func deliverMenu(_ entry: RelayEntry) -> some View {
        Section("投递到") {
            ForEach(preferences.targets.prefix(QuickDeliver.maxTargets)) { profile in
                Button {
                    delivering = DeliverySelection(batchIDs: [entry.id], quickTargetID: profile.id)
                } label: {
                    if let logo = profile.logo {
                        Label(profile.displayName, image: logo)
                    } else {
                        Label(profile.displayName, systemImage: profile.symbol)
                    }
                }
            }
            Button {
                delivering = DeliverySelection(batchIDs: [entry.id])
            } label: {
                Label("选择目标…", systemImage: "square.grid.2x2")
            }
        }
        Button { exportForPicking(entry) } label: { Label("存图片和附件", systemImage: "square.and.arrow.down.on.square") }
        if store.collectionContaining(entry.id) == nil {
            Button { store.addToCollection(entry.id) } label: { Label("加入收集", systemImage: "tray.and.arrow.down") }
        }
        Button(role: .destructive) { store.delete([entry.id]) } label: { Label("删除", systemImage: "trash") }
    }

    /// The extension cannot be opened from here, so this points the way:
    /// the collection takes the next batches, and the last message goes to
    /// the clipboard for WeChat's search.
    private func continueCollecting(_ collection: BatchCollection) {
        store.resumeCollection(collection.id)
        let steps = "从下一条开始多选，分享时在下方列表选「分批收集到微信流」。"
        if let anchor = store.lastBatch(of: collection)?.summary?.anchor {
            UIPasteboard.general.string = anchor
            collectNote = "已复制最后一句「\(anchor)」。回微信在这个聊天里搜索它定位，\(steps)"
        } else {
            collectNote = "回微信找到上一批的最后一条，\(steps)"
        }
    }

    private func exportForPicking(_ entry: RelayEntry) {
        let job = FilesExport.Job(batch: entry.batch, name: FilesExport.folderName(for: entry))
        Task {
            do {
                exported = try await FilesExport.exportForPicking([job]).message
            } catch {
                store.lastError = error.localizedDescription
            }
        }
    }

    private func takeQuickRequest() {
        guard let request = router.pending else { return }
        router.pending = nil
        // The share may have landed a moment ago, before any reload.
        store.reload()
        guard store.entry(request.batchID) != nil else {
            store.lastError = "这批内容已不在中转站。"
            return
        }
        editMode = .inactive
        delivering = DeliverySelection(batchIDs: [request.batchID], quickTargetID: request.targetID)
    }

    /// Time order, so a merge reads oldest first whatever order the user tapped.
    private var orderedSelection: [UUID] {
        store.visibleEntries
            .filter { selection.contains($0.id) }
            .sorted { ($0.summary?.start ?? $0.batch.createdAt) < ($1.summary?.start ?? $1.batch.createdAt) }
            .map(\.id)
    }
}

private struct FailureBanner: View {
    let failures: [ShareFailure]
    let dismiss: () -> Void

    var body: some View {
        HStack(alignment: .top) {
            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
            VStack(alignment: .leading, spacing: 2) {
                Text("有 \(failures.count) 次分享没有保存成功").font(.subheadline.weight(.semibold))
                if let last = failures.last {
                    Text(last.message).font(.footnote).foregroundStyle(.secondary)
                }
            }
            Spacer()
            Button(action: dismiss) { Image(systemName: "xmark") }
                .buttonStyle(.borderless)
                .accessibilityLabel("关闭")
        }
        .padding(.vertical, 4)
    }
}

private struct EmptyRelayView: View {
    let showGuide: () -> Void

    var body: some View {
        VStack(spacing: 14) {
            Image(systemName: "tray.and.arrow.down.fill")
                .font(.system(size: 38))
                .foregroundStyle(Color.accentColor)
                .frame(width: 88, height: 88)
                .background(Color.accentColor.opacity(0.12), in: Circle())
            Text("还没有收到聊天记录").font(.title3.weight(.semibold))
            Text("在微信里把聊天记录导出，分享到「微信流」，就会出现在这里。")
                .font(.subheadline)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
            Button("怎么从微信发过来", action: showGuide)
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .padding(.top, 4)
        }
        .padding(32)
    }
}

private struct UndoToast: View {
    let count: Int
    let undo: () -> Void

    var body: some View {
        HStack {
            Text("已删除 \(count) 条")
            Spacer()
            Button("撤销", action: undo).bold()
        }
        .padding()
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
        .padding(.horizontal)
        .padding(.bottom, 8)
        .transition(.move(edge: .bottom).combined(with: .opacity))
    }
}
