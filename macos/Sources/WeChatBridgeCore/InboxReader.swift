import Foundation

public struct ReadyItem: Sendable, Hashable, Identifiable {
    public let id: UUID
    public let batchID: UUID
    public let displayName: String
    public let url: URL
    public let byteCount: Int64
    public let contentType: String?
    public let createdAt: Date
    /// Where the batch this item belongs to came from.
    public let action: ShareAction

    public init(
        id: UUID,
        batchID: UUID,
        displayName: String,
        url: URL,
        byteCount: Int64,
        contentType: String?,
        createdAt: Date,
        action: ShareAction
    ) {
        self.id = id
        self.batchID = batchID
        self.displayName = displayName
        self.url = url
        self.byteCount = byteCount
        self.contentType = contentType
        self.createdAt = createdAt
        self.action = action
    }
}

public struct ReadyBatch: Sendable, Hashable, Identifiable {
    public let id: UUID
    public let directory: URL
    public let createdAt: Date
    public let action: ShareAction
    public let items: [ReadyItem]
    public let outcome: BatchOutcome?
    /// The app a 「发送到自定义」 batch was pointed at, as it was named on screen
    /// when the user picked it. Nil for every other entry.
    public let targetName: String?
    public let chatName: String?
    public let sceneID: String?
    public let sceneName: String?
    /// True when this very load had to write `state.json`, which is the one
    /// durable signal that no run of the app has ever processed this batch.
    /// A batch that is merely new to *this process* — every batch, after a
    /// relaunch — is not an arrival and must not replay a forward.
    public let isFirstSeen: Bool

    public init(
        id: UUID,
        directory: URL,
        createdAt: Date,
        action: ShareAction,
        items: [ReadyItem],
        outcome: BatchOutcome?,
        targetName: String? = nil,
        chatName: String? = nil,
        sceneID: String? = nil,
        sceneName: String? = nil,
        isFirstSeen: Bool
    ) {
        self.id = id
        self.directory = directory
        self.createdAt = createdAt
        self.action = action
        self.items = items
        self.outcome = outcome
        self.targetName = targetName
        self.chatName = chatName
        self.sceneID = sceneID
        self.sceneName = sceneName
        self.isFirstSeen = isFirstSeen
    }

    public var byteCount: Int64 { items.reduce(0) { $0 + $1.byteCount } }
}

/// The app's half of the inbox protocol: read `Ready`, never touch `Staging`.
///
/// The app is the only process that writes into `Ready` after the extension's
/// commit rename, so rewriting a manifest to drop one item, or writing
/// `state.json`, needs no cross-process coordination — the extension only ever
/// creates new batches under new UUIDs.
public struct InboxReader {
    /// How a removed batch leaves the container.
    public enum Removal: Sendable {
        /// The product behaviour: recoverable from Finder.
        case trash
        /// Used by tests, which must not deposit fixtures in the user's Trash.
        case delete
    }

    public let inbox: Inbox
    private let fileManager: FileManager
    private let removal: Removal

    public init(inbox: Inbox, fileManager: FileManager = .default, removal: Removal = .trash) {
        self.inbox = inbox
        self.fileManager = fileManager
        self.removal = removal
    }

    /// Newest batch first. A batch whose manifest is missing, unreadable or
    /// written by a newer schema is skipped rather than partially shown: a
    /// half-listed share is worse than a share the user can still find on disk.
    ///
    /// Reading is also what initialises a batch: a batch without `state.json`
    /// has never been processed, so one is written here from the manifest's
    /// action. Doing it during the read keeps "the app has seen this" and "the
    /// app knows about this" from ever disagreeing after a crash.
    public func loadBatches() -> [ReadyBatch] {
        batches(initializing: true)
    }

    /// `isFirstSeen` is spent the moment `state.json` lands on disk, and it is
    /// the only durable signal that a forward has never been carried out. So
    /// anything that merely counts or sweeps batches reads with
    /// `initializing: false`: were it to initialise, the `reload()` that
    /// follows would find nothing to announce and the user's forward would be
    /// silently dropped.
    private func batches(initializing: Bool) -> [ReadyBatch] {
        let directories = (try? fileManager.contentsOfDirectory(
            at: inbox.ready,
            includingPropertiesForKeys: [.isDirectoryKey],
            options: [.skipsHiddenFiles]
        )) ?? []

        return directories
            .compactMap { batch(at: $0, initializing: initializing) }
            .sorted { $0.createdAt > $1.createdAt }
    }

    public func batch(at directory: URL) -> ReadyBatch? {
        batch(at: directory, initializing: true)
    }

    private func batch(at directory: URL, initializing: Bool) -> ReadyBatch? {
        guard let manifest = manifest(at: directory) else { return nil }

        let stored = state(at: directory)
        // A batch written before the manifest carried `action` can recover its
        // request from `intent.json`, which is still on disk at this point.
        let requested = stored == nil ? peekIntent(at: directory) : nil
        let state = stored ?? BatchState.initial(
            for: manifest,
            requestedAction: requested?.action,
            targetName: requested?.targetDisplayName
        )
        // Best effort: a container that refuses the write still shows the user
        // their files, it just re-derives the same state on the next read.
        if stored == nil, initializing { try? write(state, at: directory) }

        // The state's own copy outlives `intent.json`, so the history keeps
        // saying where a legacy batch came from long after the request is gone.
        let action = state.action ?? manifest.action
        let items = manifest.items.compactMap { item -> ReadyItem? in
            let url = directory.appendingPathComponent(item.relativePath)
            // A manifest entry without its file is debris from a manual delete
            // in Finder, not a batch to advertise.
            guard fileManager.fileExists(atPath: url.path) else { return nil }
            return ReadyItem(
                id: item.id,
                batchID: manifest.batchID,
                displayName: item.displayName,
                url: url,
                byteCount: item.byteCount,
                contentType: item.contentType,
                createdAt: manifest.createdAt,
                action: action
            )
        }
        guard !items.isEmpty else { return nil }
        return ReadyBatch(
            id: manifest.batchID,
            directory: directory,
            createdAt: manifest.createdAt,
            action: action,
            items: items,
            outcome: state.outcome,
            targetName: state.targetName,
            chatName: state.chatName,
            sceneID: state.sceneID,
            sceneName: state.sceneName,
            isFirstSeen: stored == nil && initializing
        )
    }

    /// Every batch still on disk, in bytes. What the settings window reports as
    /// "占用", so it counts the payload the user could get back by clearing the
    /// history — not the manifests and state files around it.
    public func totalByteCount() -> Int64 {
        batches(initializing: false).reduce(0) { $0 + $1.byteCount }
    }

    /// `targetName` is only ever written, never cleared: a forward may name its
    /// destination, and every other caller leaves the existing value alone.
    public func recordOutcome(
        _ outcome: BatchOutcome,
        targetName: String? = nil,
        for batchID: UUID
    ) throws {
        try mutateState(batchID: batchID) { $0.withOutcome(outcome, targetName: targetName) }
    }

    /// Writes the scene/group snapshot independently of delivery outcome, so a
    /// failed forward still tells the user which group and scene it concerned.
    public func recordContext(
        chatName: String? = nil,
        sceneID: String? = nil,
        sceneName: String? = nil,
        for batchID: UUID
    ) throws {
        try mutateState(batchID: batchID) {
            $0.withContext(chatName: chatName, sceneID: sceneID, sceneName: sceneName)
        }
    }

    public func state(forBatch batchID: UUID) -> BatchState? {
        state(at: directory(for: batchID))
    }

    // MARK: - Removal

    /// Moves a whole batch to the user's Trash.
    ///
    /// Trash rather than `removeItem` on purpose: "移到废纸篓" is a routine
    /// gesture, and the archive may be the only copy of a chat export the user
    /// has. Finder's own restore is a better undo than anything WeChatBridge would
    /// build, and it costs no extra lifecycle in the group container.
    public func discard(batchID: UUID) throws {
        try trashOrRemove(directory(for: batchID))
    }

    /// Quick deletion must remain reversible; unlike ordinary cleanup this
    /// never falls back to permanent removal when Trash is unavailable.
    public func trashRecoverably(batchID: UUID) throws -> URL {
        let source = directory(for: batchID)
        guard manifest(at: source)?.batchID == batchID else { throw InboxError.manifestUnreadable(reason: BatchStaging.manifestFileName) }
        if removal == .delete {
            // Tests keep recoverable removals inside their temporary inbox.
            let folder = inbox.root.appendingPathComponent("TestTrash", isDirectory: true)
            try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)
            let target = folder.appendingPathComponent(UUID().uuidString, isDirectory: true)
            try fileManager.moveItem(at: source, to: target)
            return target
        }
        var result: NSURL?
        try fileManager.trashItem(at: source, resultingItemURL: &result)
        guard let result else { throw CocoaError(.fileWriteUnknown) }
        return result as URL
    }

    public func restoreTrashedBatch(_ batchID: UUID, from url: URL) throws {
        guard url.isFileURL, manifest(at: url)?.batchID == batchID else { throw InboxError.manifestUnreadable(reason: BatchStaging.manifestFileName) }
        try fileManager.moveItem(at: url, to: directory(for: batchID))
    }

    /// Removes one item and rewrites the batch manifest; the batch itself goes
    /// away once its last item does.
    public func discard(item: ReadyItem) throws {
        let directory = directory(for: item.batchID)
        guard let manifest = manifest(at: directory) else {
            throw InboxError.manifestUnreadable(reason: BatchStaging.manifestFileName)
        }

        let remaining = manifest.items.filter { $0.id != item.id }
        guard !remaining.isEmpty else {
            try trashOrRemove(directory)
            return
        }

        try trashOrRemove(item.url.deletingLastPathComponent())
        let updated = BatchManifest(
            batchID: manifest.batchID,
            createdAt: manifest.createdAt,
            items: remaining,
            action: manifest.action,
            schemaVersion: manifest.schemaVersion
        )
        try BatchManifest.encoder().encode(updated).write(
            to: directory.appendingPathComponent(BatchStaging.manifestFileName),
            options: .atomic
        )
    }

    public func discardAll() throws {
        for batch in batches(initializing: false) {
            try discard(batchID: batch.id)
        }
    }

    /// Ages out history the user has finished with.
    ///
    /// Returns how many batches were removed.
    @discardableResult
    public func pruneHistory(olderThan interval: TimeInterval, now: Date = Date(), excluding protected: Set<UUID> = []) -> Int {
        // A non-positive window means "keep forever", not "delete everything":
        // the preference's 0 is the "从不" option.
        guard interval > 0 else { return 0 }
        var removed = 0
        for batch in batches(initializing: false) {
            guard !protected.contains(batch.id), !(batch.action == .collect && batch.outcome == nil) else { continue }
            guard now.timeIntervalSince(batch.createdAt) > interval else { continue }
            if (try? discard(batchID: batch.id)) != nil { removed += 1 }
        }
        return removed + pruneUnreadable(olderThan: interval, now: now)
    }

    /// Debris no `ReadyBatch` can be built from: a manifest truncated by a
    /// crash mid-write, or a batch whose files were deleted by hand in Finder.
    /// It is invisible in 记录 and counts for nothing in 占用, so without this
    /// it would sit in the group container forever.
    ///
    /// A manifest written by a *newer* schema is the one thing left alone: a
    /// future build understands it, and this one has no business trashing what
    /// it merely cannot read yet.
    private func pruneUnreadable(olderThan interval: TimeInterval, now: Date) -> Int {
        let directories = (try? fileManager.contentsOfDirectory(
            at: inbox.ready,
            includingPropertiesForKeys: [.creationDateKey, .isDirectoryKey],
            options: [.skipsHiddenFiles]
        )) ?? []

        var removed = 0
        for directory in directories {
            guard batch(at: directory, initializing: false) == nil else { continue }
            let data = try? Data(contentsOf: directory.appendingPathComponent(BatchStaging.manifestFileName))
            let decoded = data.flatMap { try? BatchManifest.decoder().decode(BatchManifest.self, from: $0) }
            if let decoded, decoded.schemaVersion > BatchManifest.currentSchemaVersion { continue }
            let values = try? directory.resourceValues(forKeys: [.creationDateKey, .isDirectoryKey])
            guard values?.isDirectory != false else { continue }
            // An unparseable manifest still leaves the directory's own creation
            // date. Unknown age counts as "just arrived", because trashing on a
            // guess is the one mistake with no way back.
            let created = decoded?.createdAt ?? values?.creationDate ?? now
            guard now.timeIntervalSince(created) > interval else { continue }
            if (try? trashOrRemove(directory)) != nil { removed += 1 }
        }
        return removed
    }

    // MARK: - Failures

    /// Every message the extension left behind, oldest first, read and deleted
    /// in one gesture.
    ///
    /// Deleting as it reads is the whole protocol: there is no state to say a
    /// failure has been shown, so the file's existence *is* that state. A
    /// report the app cannot decode is deleted too — a stale byte sequence
    /// nothing will ever be able to read would otherwise be re-examined on
    /// every scan for the life of the container.
    public func consumeFailures() -> [ShareFailure] {
        let files = (try? fileManager.contentsOfDirectory(
            at: inbox.failures,
            includingPropertiesForKeys: nil,
            options: [.skipsHiddenFiles]
        )) ?? []

        var failures: [ShareFailure] = []
        for file in files where file.pathExtension == "json" {
            let data = try? Data(contentsOf: file)
            try? fileManager.removeItem(at: file)
            guard let data,
                  let failure = try? BatchManifest.decoder().decode(ShareFailure.self, from: data)
            else { continue }
            failures.append(failure)
        }
        // Oldest first, so a burst of them ends with the most recent message on
        // screen: the toast replaces its own content rather than queueing.
        return failures.sorted { $0.at < $1.at }
    }

    // MARK: - Files

    private func directory(for batchID: UUID) -> URL {
        inbox.ready.appendingPathComponent(batchID.uuidString, isDirectory: true)
    }

    private func manifest(at directory: URL) -> BatchManifest? {
        let url = directory.appendingPathComponent(BatchStaging.manifestFileName)
        guard let data = try? Data(contentsOf: url),
              let manifest = try? BatchManifest.decoder().decode(BatchManifest.self, from: data),
              manifest.schemaVersion <= BatchManifest.currentSchemaVersion
        else { return nil }
        return manifest
    }

    private func state(at directory: URL) -> BatchState? {
        let url = directory.appendingPathComponent(BatchState.fileName)
        guard let data = try? Data(contentsOf: url),
              let state = try? BatchManifest.decoder().decode(BatchState.self, from: data),
              state.schemaVersion <= BatchState.currentSchemaVersion
        else { return nil }
        return state
    }

    private func write(_ state: BatchState, at directory: URL) throws {
        try BatchManifest.encoder().encode(state).write(
            to: directory.appendingPathComponent(BatchState.fileName),
            options: .atomic
        )
    }

    /// Read-modify-write against the file rather than against a `ReadyBatch` the
    /// caller is holding, so concurrent outcome updates cannot lose one another.
    private func mutateState(batchID: UUID, _ transform: (BatchState) -> BatchState) throws {
        let directory = directory(for: batchID)
        guard let manifest = manifest(at: directory) else {
            throw InboxError.manifestUnreadable(reason: BatchStaging.manifestFileName)
        }
        let current = state(at: directory) ?? BatchState.initial(for: manifest)
        try write(transform(current), at: directory)
    }

    /// A volume without a trash (or a sandbox refusal) must not leave the user
    /// unable to clear history.
    private func trashOrRemove(_ url: URL) throws {
        guard removal == .trash else {
            try fileManager.removeItem(at: url)
            return
        }
        do {
            try fileManager.trashItem(at: url, resultingItemURL: nil)
        } catch {
            try fileManager.removeItem(at: url)
        }
    }
}

/// What `consumeIntent` found. A stale request is reported as its own case
/// rather than as "nothing": the user did ask for a forward, it did not happen,
/// and the history has to be able to say so.
public enum ConsumedIntent: Sendable, Hashable {
    /// No request attached, already consumed, or written by a schema this build
    /// cannot read.
    case none
    /// Requested recently: carry it out.
    case ready(BatchIntent)
    /// Requested too long ago to act on. Record it, do not run it.
    case expired(BatchIntent)

    public var action: ShareAction? {
        switch self {
        case .none: return nil
        case .ready(let intent), .expired(let intent): return intent.action
        }
    }
}

extension InboxReader {
    /// Reads and immediately consumes the batch's one-shot request.
    ///
    /// Consumption is the deletion: an intent that has been read is gone from
    /// disk, so a rescan, a relaunch or a second window can never replay a
    /// forward the user asked for once. A stale request is consumed too — and
    /// reported as `.expired`, because pasting into whatever app happens to be
    /// frontmost hours later is worse than doing nothing.
    public func consumeIntent(forBatch batchID: UUID, now: Date = Date()) -> ConsumedIntent {
        let url = directory(for: batchID).appendingPathComponent(BatchIntent.fileName)
        guard let data = try? Data(contentsOf: url) else { return .none }
        try? fileManager.removeItem(at: url)

        guard let intent = try? BatchManifest.decoder().decode(BatchIntent.self, from: data),
              intent.schemaVersion <= BatchIntent.currentSchemaVersion
        else { return .none }
        return intent.isFresh(now: now) ? .ready(intent) : .expired(intent)
    }

    /// The batch's request, read and left exactly where it is.
    ///
    /// `consumeIntent` deletes as it reads, on purpose — a forward must never
    /// replay. The first read has to resolve the manifest's action before that
    /// deletion, so this is the one read that leaves the file alone. The chosen
    /// app comes back with it, because 记录 has to keep saying 「发给 Cursor」
    /// long after `intent.json` is gone.
    private func peekIntent(at directory: URL) -> BatchIntent? {
        let url = directory.appendingPathComponent(BatchIntent.fileName)
        guard let data = try? Data(contentsOf: url),
              let intent = try? BatchManifest.decoder().decode(BatchIntent.self, from: data),
              intent.schemaVersion <= BatchIntent.currentSchemaVersion
        else { return nil }
        return intent
    }
}
