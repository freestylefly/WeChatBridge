import Foundation
import WeChatBridgeCore

struct RelayEntry: Identifiable, Hashable {
    let batch: ReadyBatch
    var summary: RelaySummary?
    var id: UUID { batch.id }

    var isDelivered: Bool { batch.outcome?.kind == .delivered }
}

/// The relay's list, and the only writer of `Ready` and the collection ledger
/// on this side. The share extension adds batches; everything that removes or
/// annotates one goes through here.
@MainActor
final class RelayStore: ObservableObject {
    @Published private(set) var entries: [RelayEntry] = []
    @Published private(set) var ledger = BatchCollectionLedger()
    @Published var failures: [ShareFailure] = []
    @Published var lastError: String?
    /// Hidden from the list and deleted when the undo window closes.
    @Published private(set) var pendingDeletion: Set<UUID> = []

    let inbox: Inbox?
    private var reader: InboxReader? { inbox.map { InboxReader(inbox: $0, removal: .delete) } }
    private var summarizing: Set<UUID> = []
    private var deletionTask: Task<Void, Never>?

    init(inbox: Inbox? = try? Inbox.resolve()) {
        self.inbox = inbox
        if let inbox {
            try? inbox.prepareDirectories()
        }
        loadLedger()
    }

    var visibleEntries: [RelayEntry] {
        entries.filter { !pendingDeletion.contains($0.id) }
    }

    func entry(_ id: UUID) -> RelayEntry? {
        entries.first { $0.id == id }
    }

    func entries(_ ids: [UUID]) -> [RelayEntry] {
        ids.compactMap(entry)
    }

    // MARK: - Loading

    func reload() {
        guard let reader else {
            // A signing problem: the group id is for the log, not the user.
            NSLog("WeChatBridge: %@", InboxError.containerUnavailable(identifier: AppGroup.identifier).errorDescription ?? "")
            lastError = "微信流没装好，读不到分享进来的内容。请重新安装后再试。"
            return
        }
        let known = Dictionary(uniqueKeysWithValues: entries.map { ($0.id, $0.summary) })
        entries = reader.loadBatches().map { batch in
            RelayEntry(batch: batch, summary: known[batch.id] ?? RelaySummary.cached(in: batch.directory))
        }
        let fresh = reader.consumeFailures()
        if !fresh.isEmpty { failures = fresh }
        ingestMarked()
        summarizeMissing()
    }

    /// 「分批收集到微信流」 batches join the open collection, oldest first.
    private func ingestMarked() {
        let ids = CollectMarker.unseen(entries.map(\.batch.directory), seen: ledger.seenBatchIDs)
        guard !ids.isEmpty else { return }
        for id in ids { ledger.append(id) }
        saveLedger()
    }

    /// Parsing reads whole archives, so it runs off the main actor, one batch
    /// at a time, and is cached beside the batch for every later launch.
    private func summarizeMissing() {
        let missing = entries.filter { $0.summary == nil && !summarizing.contains($0.id) }.map(\.batch)
        guard !missing.isEmpty else { return }
        summarizing.formUnion(missing.map(\.id))
        Task.detached(priority: .utility) {
            for batch in missing {
                let summary = RelaySummary.make(for: batch)
                summary.store(in: batch.directory)
                await self.apply(summary, to: batch.id)
            }
        }
    }

    private func apply(_ summary: RelaySummary, to id: UUID) {
        summarizing.remove(id)
        guard let index = entries.firstIndex(where: { $0.id == id }) else { return }
        entries[index].summary = summary
    }

    /// Run once per launch: history older than the user's retention goes,
    /// except anything a collection still needs.
    func pruneHistory(retentionDays: Int) {
        guard retentionDays > 0, let reader else { return }
        let removed = reader.pruneHistory(
            olderThan: TimeInterval(retentionDays) * 86_400,
            excluding: ledger.protectedBatchIDs
        )
        if removed > 0 { reload() }
        cleanCaches()
    }

    /// Unpacked copies whose batch is gone.
    private func cleanCaches() {
        let live = Set(entries.map(\.id.uuidString))
        let cached = (try? FileManager.default.contentsOfDirectory(atPath: RelayFiles.root.path)) ?? []
        for name in cached where !live.contains(name) {
            try? FileManager.default.removeItem(at: RelayFiles.root.appendingPathComponent(name))
        }
    }

    // MARK: - Deleting, with a short undo window

    func delete(_ ids: Set<UUID>) {
        pendingDeletion.formUnion(ids)
        deletionTask?.cancel()
        deletionTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 5_000_000_000)
            guard !Task.isCancelled else { return }
            self?.commitDeletion()
        }
    }

    func undoDeletion() {
        deletionTask?.cancel()
        pendingDeletion.removeAll()
    }

    /// Also called when the app leaves the foreground: a batch the user
    /// deleted must not come back because the timer never fired.
    func commitDeletion() {
        deletionTask?.cancel()
        guard let reader, !pendingDeletion.isEmpty else { return }
        for id in pendingDeletion {
            do {
                try reader.discard(batchID: id)
                RelayFiles.remove(batchID: id)
                for collection in ledger.collections where collection.batchIDs.contains(id) {
                    _ = try? ledger.remove(id, from: collection.id)
                }
            } catch {
                lastError = error.localizedDescription
            }
        }
        pendingDeletion.removeAll()
        saveLedger()
        reload()
    }

    // MARK: - Delivery bookkeeping

    func markDelivered(_ ids: [UUID], targetName: String, detail: String, chatName: String?, scene: WeChatScene?) {
        guard let reader else { return }
        for id in ids {
            try? reader.recordOutcome(
                BatchOutcome(kind: .delivered, detail: detail, at: Date()),
                targetName: targetName,
                for: id
            )
            try? reader.recordContext(chatName: chatName, sceneID: scene?.id, sceneName: scene?.name, for: id)
        }
        reload()
    }

    // MARK: - Collections

    private var ledgerURL: URL? { inbox.map(CollectMarker.ledgerURL) }

    var collections: [BatchCollection] {
        ledger.collections.filter { $0.status != .delivered && !$0.batchIDs.isEmpty }
    }

    private func loadLedger() {
        guard let url = ledgerURL else { return }
        do {
            ledger = try BatchCollectionLedger.load(from: url)
        } catch {
            lastError = error.localizedDescription
            ledger = BatchCollectionLedger()
        }
        ledger.recoverInterruptedDeliveries()
        saveLedger()
    }

    private func saveLedger() {
        guard let url = ledgerURL else { return }
        do { try ledger.save(to: url) } catch { lastError = error.localizedDescription }
    }

    /// Adds to the collection being gathered, starting one if none is.
    func addToCollection(_ id: UUID) {
        ledger.append(id)
        saveLedger()
    }

    func removeFromCollection(_ batchID: UUID, collection: UUID) {
        do { _ = try ledger.remove(batchID, from: collection) } catch { lastError = error.localizedDescription }
        saveLedger()
    }

    func renameCollection(_ id: UUID, to name: String) {
        do { try ledger.rename(id, to: name) } catch { lastError = error.localizedDescription }
        saveLedger()
    }

    func forgetCollection(_ id: UUID) {
        do { try ledger.forget(id) } catch { lastError = error.localizedDescription }
        saveLedger()
    }

    /// Makes this the collection 「分批收集到微信流」 adds to; a no-op when it already is.
    func resumeCollection(_ id: UUID) {
        guard ledger.current?.id != id else { return }
        do { try ledger.resume(id) } catch { lastError = error.localizedDescription }
        saveLedger()
    }

    /// Time order, the order a merge reads in.
    func orderedBatchIDs(of collection: BatchCollection) -> [UUID] {
        entries(collection.batchIDs)
            .sorted { ($0.summary?.start ?? $0.batch.createdAt) < ($1.summary?.start ?? $1.batch.createdAt) }
            .map(\.id)
    }

    /// The batch with the latest messages: where the next batch picks up.
    func lastBatch(of collection: BatchCollection) -> RelayEntry? {
        orderedBatchIDs(of: collection).last.flatMap(entry)
    }

    func collectionContaining(_ batchID: UUID) -> BatchCollection? {
        ledger.collections.first { $0.status != .delivered && $0.batchIDs.contains(batchID) }
    }

    func beginCollectionDelivery(_ id: UUID) -> [UUID]? {
        do {
            let ids = try ledger.beginDelivery(id)
            saveLedger()
            return ids
        } catch {
            lastError = error.localizedDescription
            return nil
        }
    }

    func finishCollectionDelivery(_ id: UUID, succeeded: Bool, target: String, scene: String?) {
        try? ledger.finishDelivery(id, succeeded: succeeded, target: target, detail: succeeded ? "已投递" : "已取消", scene: scene)
        saveLedger()
    }
}
