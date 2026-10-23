import AppKit
import Combine
import WeChatBridgeCore
import Foundation

/// A batch the app has just noticed, plus what the user asked to happen to it.
///
/// Also raised by 记录, which asks for the same destinations for files that are
/// already here — the batch they belong to is resolved from the URLs.
struct ArrivedBatch: Sendable {
    let action: ShareAction
    /// Where 「发送到自定义」 was pointed. Nil for every fixed entry, whose
    /// destination the action already names.
    let target: ForwardTarget?
    let urls: [URL]
    /// When the user asked for this — the intent's own timestamp for a share,
    /// and now for a menu inside WeChatBridge. Carried because a forward can wait in
    /// `ActionRunner`'s queue behind another one, and by the time it runs it
    /// may no longer be a gesture anybody is still making.
    let requestedAt: Date
    /// Only a fresh share from WeChat may inspect WeChat's title bar. Actions
    /// replayed from 记录 have no relationship to the current foreground window.
    let capturesGroupName: Bool

    init(
        action: ShareAction,
        target: ForwardTarget? = nil,
        urls: [URL],
        requestedAt: Date = Date(),
        capturesGroupName: Bool = false
    ) {
        self.action = action
        self.target = target
        self.urls = urls
        self.requestedAt = requestedAt
        self.capturesGroupName = capturesGroupName
    }

    /// The same window `InboxReader` applies when it reads an intent off disk.
    var isFresh: Bool {
        Date().timeIntervalSince(requestedAt) < BatchIntent.freshnessWindow
    }
}

/// Everything the status menu and the settings window read.
///
/// The model owns no copy of the truth: `reload()` re-reads `Ready` and replaces
/// both lists. That is what makes a lost notification, a crash mid-import or a
/// user deleting a batch in Finder all recover to the same state.
@MainActor
final class AppModel: ObservableObject {
    /// Every batch still on disk, newest first — the history.
    @Published private(set) var batches: [ReadyBatch] = []
    private var batchesByID: [UUID: ReadyBatch] = [:]
    @Published private(set) var collectionLedger = BatchCollectionLedger()
    @Published private(set) var collectionMetadata: [UUID: CollectionBatchMetadata] = [:]
    @Published private(set) var collectionIsImporting = false
    let didCollect = PassthroughSubject<UUID, Never>()
    private var collectionStateAvailable = false
    private var readingMetadata = Set<UUID>()
    private var capturesCollectionSources = false
    private let readCollectionSource: @Sendable () -> String?
    private let hasInjectedSourceReader: Bool
    private var importSourceSnapshots: [UUID: Task<String?, Never>] = [:]
    @Published private(set) var recognizingConversations = Set<UUID>()
    struct DeletedCollectionBatch {
        let batchID: UUID
        let collectionID: UUID
        let position: Int
        let trashURL: URL
        let wasCollecting: Bool
    }
    @Published private(set) var deletedCollectionBatch: DeletedCollectionBatch?
    /// Set only for conditions the user can act on — a missing group container
    /// is a build fault, but they still deserve to see it rather than an empty
    /// history that never fills.
    @Published private(set) var inboxFailure: String?

    /// Raised once per batch that a rescan finds for the first time, carrying
    /// what the user asked for when they shared it.
    let didArrive = PassthroughSubject<ArrivedBatch, Never>()

    /// A share that never became a batch. The extension has no interface to
    /// report one in, so it leaves the message in the app group and this is
    /// where the app picks it up — once, on the scan that follows.
    let didFailToReceive = PassthroughSubject<ShareFailure, Never>()

    private let preferences: Preferences
    private(set) var inbox: Inbox?
    private var reader: InboxReader?
    private var watcher: InboxWatcher?
    private var intakeDeferralCount = 0

    func deferIntake() { intakeDeferralCount += 1 }
    func resumeIntake() {
        intakeDeferralCount = max(0, intakeDeferralCount - 1)
        if intakeDeferralCount == 0 { reload() }
    }

    init(preferences: Preferences, readCollectionSource: (@Sendable () -> String?)? = nil) {
        self.preferences = preferences
        self.hasInjectedSourceReader = readCollectionSource != nil
        self.readCollectionSource = readCollectionSource ?? { (try? WeChatTitleReader.read())?.name }
    }

    /// Summed from the batches already in memory rather than from disk: the
    /// settings window reads this while it draws, and a directory walk per frame
    /// is not a thing to do in a view body.
    var historyByteCount: Int64 {
        batches.reduce(0) { $0 + $1.byteCount }
    }
    var historyEntryCount: Int {
        let grouped = Set(collectionLedger.collections.flatMap(\.batchIDs))
        return collectionLedger.collections.count + batches.filter { !grouped.contains($0.id) }.count
    }

    func start(inbox providedInbox: Inbox? = nil, watch: Bool = true, removal: InboxReader.Removal = .trash) {
        do {
            let inbox = try providedInbox ?? Inbox.resolve()
            try inbox.prepareDirectories()
            self.inbox = inbox
            reader = InboxReader(inbox: inbox, removal: removal)
            capturesCollectionSources = providedInbox == nil || hasInjectedSourceReader
            do {
                var ledger = try BatchCollectionLedger.load(from: collectionLedgerURL!)
                let previous = ledger
                ledger.recoverInterruptedDeliveries()
                if ledger != previous { try ledger.save(to: collectionLedgerURL!) }
                collectionLedger = ledger
                collectionStateAvailable = true
            } catch {
                inboxFailure = L10n.text("收集记录无法读取，原始文件已保留。")
            }
            // Debris from an extension that was killed mid-copy. Safe here
            // because the app runs long after any such copy would have died.
            inbox.pruneStaging()
            if watch {
                let watcher = InboxWatcher(inbox: inbox) { [weak self] in self?.reload() }
                self.watcher = watcher
                watcher.start()
            }
        } catch {
            inboxFailure = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        }
    }

    /// Re-reads `Ready`, announces anything the app has never processed, then
    /// ages out what the retention window says is finished with.
    func reload() {
        guard intakeDeferralCount == 0, let reader else { return }
        publish(reader.loadBatches())
        let imports = ((try? FileManager.default.contentsOfDirectory(at: reader.inbox.staging, includingPropertiesForKeys: nil)) ?? [])
            .filter { FileManager.default.fileExists(atPath: $0.appendingPathComponent("collection-import").path) }
        collectionIsImporting = !imports.isEmpty
        if capturesCollectionSources {
            for directory in imports {
                guard let id = UUID(uuidString: directory.deletingPathExtension().lastPathComponent), importSourceSnapshots[id] == nil else { continue }
                let reader = readCollectionSource
                importSourceSnapshots[id] = Task.detached(priority: .userInitiated) { reader() }
            }
        }
        ingestCollections()
        let liveImports = Set(imports.compactMap { UUID(uuidString: $0.deletingPathExtension().lastPathComponent) })
        for id in Array(importSourceSnapshots.keys) where !liveImports.contains(id) && batch(id: id) == nil {
            importSourceSnapshots.removeValue(forKey: id)?.cancel()
        }

        // `isFirstSeen` comes from `state.json` having had to be written, so it
        // is true exactly once in the app's whole lifetime for a given batch —
        // across relaunches, rescans and second windows. That is what a forward
        // needs: replaying one hours later would paste into someone else's app.
        var recorded = false
        for batch in batches where batch.isFirstSeen {
            recorded = announce(batch, reader: reader) || recorded
        }
        if recorded { publish(reader.loadBatches()) }

        // Read and deleted in one go, so a message is said exactly once however
        // many times the inbox is rescanned.
        for failure in reader.consumeFailures() {
            didFailToReceive.send(failure)
        }

        pruneHistory()
    }

    private func publish(_ loaded: [ReadyBatch]) {
        batchesByID = Dictionary(loaded.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        batches = loaded
    }

    /// Returns true when it wrote an outcome, so the caller knows the batches it
    /// is holding are stale.
    private func announce(_ batch: ReadyBatch, reader: InboxReader) -> Bool {
        if batch.action == .collect { return false }
        let urls = batch.items.map(\.url)
        switch reader.consumeIntent(forBatch: batch.id) {
        case .ready(let intent):
            didArrive.send(
                ArrivedBatch(
                    action: intent.action,
                    target: intent.target,
                    urls: urls,
                    requestedAt: intent.requestedAt,
                    capturesGroupName: true
                )
            )
            return false
        case .expired(let intent):
            // Only a paste can go stale. A copy was finished by the extension
            // itself, however long ago — only an older extension build writes
            // an intent for one — and `BatchState.initial` already said 已复制.
            guard intent.action != .clipboard else { return false }
            // The user did ask for this; it simply cannot be honoured now.
            record(.expired, for: [batch.id])
            return true
        case .none:
            switch batch.action {
            case .collect, .clipboard:
                // Already copied, by the extension, and already recorded as
                // such by `BatchState.initial`. Nothing left to do.
                return false
            case .codex, .claude, .doubao, .qwen, .workBuddy, .weSight, .deepSeekHarness, .obsidian, .folder, .custom:
                // A forward whose one-shot request is already gone: the file was
                // consumed by a run that then died before it could act, or the
                // extension never managed to write it. Either way nothing can be
                // carried out, and the history must not claim otherwise.
                record(.expired, for: [batch.id])
                return true
            }
        }
    }

    // MARK: - Lookup

    func item(id: UUID) -> ReadyItem? {
        batches.flatMap(\.items).first { $0.id == id }
    }

    func batch(id: UUID) -> ReadyBatch? {
        batchesByID[id]
    }

    /// Batches these files belong to. Matched on path because the URLs come back
    /// from AppKit drags and pasteboards, which do not promise to hand back the
    /// very `URL` value they were given.
    func batchIDs(for urls: [URL]) -> Set<UUID> {
        Set(items(for: urls).map(\.batchID))
    }

    struct CollectionConversation {
        let name: String
        var urls: [URL]
    }

    /// Keep the collection's original order while grouping its files by the
    /// saved conversation name. Missing context must never silently merge chats.
    func collectionConversations(for urls: [URL]) -> [CollectionConversation]? {
        guard !urls.isEmpty else { return nil }
        let sources = Dictionary(items(for: urls).map { ($0.url.standardizedFileURL.path, $0.batchID) },
                                 uniquingKeysWith: { first, _ in first })
        var conversations: [CollectionConversation] = []
        for url in urls {
            guard let id = sources[url.standardizedFileURL.path],
                  let name = batchConversation(id) else { return nil }
            if let index = conversations.firstIndex(where: { $0.name == name }) {
                conversations[index].urls.append(url)
            } else {
                conversations.append(CollectionConversation(name: name, urls: [url]))
            }
        }
        return conversations
    }

    private func items(for urls: [URL]) -> [ReadyItem] {
        let paths = Set(urls.map(\.standardizedFileURL.path))
        return batches.flatMap(\.items).filter { paths.contains($0.url.standardizedFileURL.path) }
    }

    // MARK: - History

    /// Records a completed delivery for every batch represented by these files.
    func recordDelivery(
        urls: [URL],
        action: ShareAction,
        targetName: String? = nil
    ) {
        guard let reader else { return }
        let kind: BatchOutcome.Kind = action == .clipboard ? .copied : .delivered
        for batchID in batchIDs(for: urls) {
            try? reader.recordOutcome(
                BatchOutcome(kind: kind, at: Date()),
                targetName: targetName,
                for: batchID
            )
        }
        reload()
    }

    /// Records how a forward ended. The files are on the clipboard either way,
    /// which is why a failure is a record rather than an error dialog.
    func recordFailure(_ detail: String, urls: [URL], targetName: String? = nil) {
        record(.failed, detail: detail, targetName: targetName, for: batchIDs(for: urls))
        reload()
    }

    func recordContext(
        chatName: String? = nil,
        sceneID: String? = nil,
        sceneName: String? = nil,
        urls: [URL]
    ) {
        guard let reader else { return }
        for batchID in batchIDs(for: urls) {
            try? reader.recordContext(
                chatName: chatName,
                sceneID: sceneID,
                sceneName: sceneName,
                for: batchID
            )
        }
        reload()
    }

    /// A forward that was never carried out because too long passed between the
    /// gesture and its turn — queued behind another forward, or waiting on a
    /// question nobody answered. The same record an intent found hours later
    /// gets: 未执行, nothing pasted, files still on the clipboard.
    func recordExpired(urls: [URL]) {
        record(.expired, for: batchIDs(for: urls))
        reload()
    }

    private func record(
        _ kind: BatchOutcome.Kind,
        detail: String? = nil,
        targetName: String? = nil,
        for batchIDs: Set<UUID>
    ) {
        guard let reader else { return }
        for batchID in batchIDs {
            try? reader.recordOutcome(
                BatchOutcome(kind: kind, detail: detail, at: Date()),
                targetName: targetName,
                for: batchID
            )
        }
    }

    func discard(batchID: UUID) {
        guard !collectionLedger.collections.contains(where: { $0.status == .delivering && $0.batchIDs.contains(batchID) }) else { return }
        guard let reader else { return }
        do {
            try reader.discard(batchID: batchID)
        } catch {
            inboxFailure = error.localizedDescription
        }
        reload()
    }

    func discardHistory() {
        guard let reader else { return }
        let delivering = Set(collectionLedger.collections.filter { $0.status == .delivering }.flatMap(\.batchIDs))
        for batch in batches where !delivering.contains(batch.id) {
            do {
                try reader.discard(batchID: batch.id)
            } catch {
                inboxFailure = error.localizedDescription
            }
        }
        let remaining = Set(((try? FileManager.default.contentsOfDirectory(at: reader.inbox.ready, includingPropertiesForKeys: nil)) ?? []).compactMap { UUID(uuidString: $0.lastPathComponent) })
        _ = changeCollections { ledger in
            for group in ledger.collections where group.status != .delivering && !group.batchIDs.contains(where: remaining.contains) {
                try ledger.forget(group.id)
            }
        }
        reload()
    }

    var hasDiscardableHistory: Bool {
        !batches.isEmpty && !collectionLedger.collections.contains { $0.status == .delivering }
    }

    /// Runs at launch and after every reload. Reloading again only when
    /// something was actually removed is what keeps this from recursing.
    func pruneHistory() {
        guard let reader, preferences.historyRetentionDays > 0 else { return }
        let window = TimeInterval(preferences.historyRetentionDays) * 24 * 60 * 60
        guard collectionStateAvailable else { return }
        if reader.pruneHistory(olderThan: window, excluding: collectionLedger.protectedBatchIDs) > 0 { reload() }
    }

    // MARK: - Finder

    func reveal(id: UUID) {
        guard let item = item(id: id) else { return }
        NSWorkspace.shared.activateFileViewerSelecting([item.url])
    }

    func revealInbox() {
        guard let inbox else { return }
        try? inbox.prepareDirectories()
        NSWorkspace.shared.activateFileViewerSelecting([inbox.ready])
    }
}

extension AppModel {
    private var collectionLedgerURL: URL? { inbox?.root.appendingPathComponent("collections.json") }

    @discardableResult
    func changeCollections(_ update: (inout BatchCollectionLedger) throws -> Void) -> Bool {
        guard collectionStateAvailable, let url = collectionLedgerURL else {
            inboxFailure = L10n.text("收集记录无法读取，原始文件已保留。")
            return false
        }
        do {
            var updated = collectionLedger
            try update(&updated)
            if updated != collectionLedger {
                try updated.save(to: url)
                collectionLedger = updated
            }
            return true
        } catch {
            inboxFailure = error.localizedDescription
            return false
        }
    }

    private func ingestCollections() {
        guard collectionStateAvailable else { return }
        // Manifest ISO8601 dates have second resolution. Directory birth times
        // retain the order of several shares received within the same second.
        let candidates = batches.filter { $0.action == .collect && !collectionLedger.seenBatchIDs.contains($0.id) }
        let dated: [(batch: ReadyBatch, date: Date)] = candidates.map { batch in
            let created = (try? batch.directory.resourceValues(forKeys: [.creationDateKey]).creationDate) ?? batch.createdAt
            return (batch, created)
        }
        let unseen = dated.sorted {
            $0.date == $1.date ? $0.batch.id.uuidString < $1.batch.id.uuidString : $0.date < $1.date
        }.map { $0.batch }
        var added: UUID?
        if !unseen.isEmpty, changeCollections({ ledger in
            for batch in unseen { added = ledger.append(batch.id, at: batch.createdAt) }
        }), let added {
            assignCollectionSources(unseen)
            didCollect.send(added)
        }
        let members = Set(collectionLedger.collections.flatMap(\.batchIDs))
        let pending = batches.filter { members.contains($0.id) && collectionMetadata[$0.id] == nil && !readingMetadata.contains($0.id) }
        guard !pending.isEmpty else { return }
        readingMetadata.formUnion(pending.map(\.id))
        Task { [weak self] in
            let metadata = await Task.detached(priority: .utility) {
                pending.map { ($0.id, CollectionBatchMetadata.read(urls: $0.items.map(\.url))) }
            }.value
            guard let self else { return }
            var updated = self.collectionMetadata
            for (id, value) in metadata {
                updated[id] = value
                self.readingMetadata.remove(id)
            }
            self.collectionMetadata = updated
        }
    }

    func collection(_ id: UUID) -> BatchCollection? { collectionLedger.collections.first { $0.id == id } }
    func collectionBatches(_ group: BatchCollection) -> [ReadyBatch] {
        group.batchIDs.compactMap { batchesByID[$0] }
    }
    func collectionName(_ group: BatchCollection) -> String {
        if !group.name.isEmpty { return group.name }
        let formatter = DateFormatter()
        formatter.dateFormat = L10n.text("M月d日 HH:mm")
        return L10n.format("分批收集 · %@", formatter.string(from: group.createdAt))
    }
    func collectionSummary(_ group: BatchCollection) -> String {
        let count = group.batchIDs.count
        let known = group.batchIDs.compactMap { collectionMetadata[$0]?.count }
        let bytes = collectionBatches(group).reduce(Int64(0)) { $0 + $1.byteCount }
        let messages: String
        if known.isEmpty { messages = L10n.text("条数暂不可用") }
        else if known.count == count { messages = L10n.format("约 %d 条", known.reduce(0, +)) }
        else { messages = L10n.format("已识别约 %d 条 · %d 批条数未知", known.reduce(0, +), count - known.count) }
        return L10n.format("%d 批 · %@ · %@", count, messages, ByteFormat.string(bytes))
    }
    func collectionRange(_ group: BatchCollection) -> String? {
        group.batchIDs.last.flatMap(batchRange)
    }
    func batchRange(_ id: UUID) -> String? {
        guard let metadata = collectionMetadata[id],
              let first = metadata.first, let last = metadata.last else { return nil }
        let formatter = DateFormatter()
        formatter.dateFormat = L10n.text("M月d日 HH:mm")
        return formatter.string(from: first.date) + " — " + formatter.string(from: last.date)
    }
    func freezeCollection(_ id: UUID) -> [URL]? {
        guard let group = collection(id) else { return nil }
        guard group.batchIDs.allSatisfy({ batchConversation($0) != nil }) else {
            inboxFailure = L10n.text("请先补充每批的群名或聊天人。")
            return nil
        }
        let members = collectionBatches(group)
        guard members.count == group.batchIDs.count,
              members.allSatisfy({ !$0.items.isEmpty && $0.items.allSatisfy { FileManager.default.fileExists(atPath: $0.url.path) } }) else {
            inboxFailure = BatchCollectionLedger.Failure.missing.localizedDescription
            return nil
        }
        guard changeCollections({ _ = try $0.beginDelivery(id) }) else { return nil }
        return members.flatMap { $0.items.map(\.url) }
    }
    func finishCollection(_ id: UUID, succeeded: Bool, target: String, detail: String, scene: String? = nil) {
        _ = changeCollections { try $0.finishDelivery(id, succeeded: succeeded, target: target, detail: detail, scene: scene) }
    }
    func parkCollection() { _ = changeCollections { $0.parkCurrent() } }
    func resumeCollection(_ id: UUID) {
        if changeCollections({ try $0.resume(id) }) { didCollect.send(id) }
    }
    func discardCollection(_ id: UUID) {
        guard let group = collection(id), group.status != .delivering, let reader else { return }
        // Keep the ledger until all files have actually reached the Trash.
        do {
            for batchID in group.batchIDs where batch(id: batchID) != nil { try reader.discard(batchID: batchID) }
            _ = changeCollections { try $0.forget(id) }
        } catch { inboxFailure = error.localizedDescription }
        reload()
    }
}

extension AppModel {
    func batchConversation(_ id: UUID) -> String? {
        guard let name = batch(id: id)?.chatName?.trimmingCharacters(in: .whitespacesAndNewlines), !name.isEmpty else { return nil }
        return name
    }

    @discardableResult
    func setBatchConversation(_ id: UUID, name: String, defaultFor collectionID: UUID? = nil) -> Bool {
        let clean = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !clean.isEmpty, clean.count <= 200 else {
            inboxFailure = L10n.text("请输入群名或聊天人，最多 200 字。")
            return false
        }
        guard let reader, batch(id: id) != nil,
              !collectionLedger.collections.contains(where: { $0.status == .delivering && $0.batchIDs.contains(id) }) else { return false }
        var ids = [id]
        if let collectionID, let group = collection(collectionID) {
            guard group.batchIDs.contains(id), changeCollections({ try $0.setDefaultChatName(collectionID, name: clean) }) else { return false }
            ids += group.batchIDs.filter { $0 != id && batchConversation($0) == nil }
        }
        do {
            for batchID in ids { try reader.recordContext(chatName: clean, for: batchID) }
            reload()
            return true
        } catch { inboxFailure = error.localizedDescription; return false }
    }

    private func assignCollectionSources(_ arrivals: [ReadyBatch]) {
        let eligible = capturesCollectionSources && arrivals.count == 1
            && Date().timeIntervalSince(arrivals[0].createdAt) < BatchIntent.freshnessWindow
        for batch in arrivals where batchConversation(batch.id) == nil {
            if let snapshot = importSourceSnapshots.removeValue(forKey: batch.id) {
                recognizeBatchConversation(batch.id, snapshot: snapshot)
            } else if eligible {
                recognizeBatchConversation(batch.id)
            } else {
                Task { [weak self] in
                    guard let self, self.batchConversation(batch.id) == nil else { return }
                    if let name = self.collectionLedger.collections.first(where: { $0.batchIDs.contains(batch.id) })?.defaultChatName {
                        _ = self.setBatchConversation(batch.id, name: name)
                    }
                }
            }
        }
    }

    /// An explicit retry can name an older batch from the current WeChat chat;
    /// automatic capture is limited to the share or its import-start snapshot.
    func recognizeBatchConversation(_ id: UUID, snapshot: Task<String?, Never>? = nil) {
        guard batch(id: id) != nil, batchConversation(id) == nil, !recognizingConversations.contains(id) else { return }
        recognizingConversations.insert(id)
        let reader = readCollectionSource
        Task { [weak self] in
            let name = await (snapshot ?? Task.detached(priority: .userInitiated) { reader() }).value
            guard let self else { return }
            defer { self.recognizingConversations.remove(id) }
            guard self.batchConversation(id) == nil, self.batch(id: id) != nil else { return }
            let fallback = self.collectionLedger.collections.first { $0.batchIDs.contains(id) }?.defaultChatName
            if let name = name ?? fallback { _ = self.setBatchConversation(id, name: name) }
        }
    }

    @discardableResult
    func deleteCollectionBatch(_ batchID: UUID, from collectionID: UUID) -> Bool {
        guard let reader, let group = collection(collectionID), group.status != .delivering else { return false }
        var position = 0
        guard changeCollections({ position = try $0.remove(batchID, from: collectionID) }) else { return false }
        do {
            let trashURL = try reader.trashRecoverably(batchID: batchID)
            deletedCollectionBatch = DeletedCollectionBatch(batchID: batchID, collectionID: collectionID, position: position, trashURL: trashURL, wasCollecting: group.status == .collecting)
        } catch {
            _ = changeCollections {
                try $0.restore(batchID, to: collectionID, at: position)
                if group.status == .collecting && $0.current == nil { try $0.resume(collectionID) }
            }
            inboxFailure = error.localizedDescription
            reload()
            return false
        }
        reload()
        return true
    }

    @discardableResult
    func undoDeletedCollectionBatch() -> Bool {
        guard let removed = deletedCollectionBatch, let reader else { return false }
        guard changeCollections({ try $0.restore(removed.batchID, to: removed.collectionID, at: removed.position) }) else { return false }
        do {
            try reader.restoreTrashedBatch(removed.batchID, from: removed.trashURL)
            if removed.wasCollecting && collectionLedger.current == nil {
                _ = changeCollections { try $0.resume(removed.collectionID) }
            }
            deletedCollectionBatch = nil
            reload()
            return true
        } catch {
            _ = changeCollections { _ = try $0.remove(removed.batchID, from: removed.collectionID) }
            inboxFailure = error.localizedDescription
            reload()
            return false
        }
    }
}
