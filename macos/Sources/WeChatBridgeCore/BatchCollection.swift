import Foundation

public struct BatchCollection: Codable, Sendable, Identifiable, Equatable {
    public enum Status: String, Codable, Sendable {
        case collecting, draft, delivering, delivered, retry
    }
    public let id: UUID
    public let createdAt: Date
    public var name: String
    public var batchIDs: [UUID]
    public var status: Status
    public var targetName: String?
    public var detail: String?
    public var sceneName: String?
    public var defaultChatName: String?

    public init(id: UUID = UUID(), createdAt: Date = Date(), name: String = "", batchIDs: [UUID] = [], status: Status = .collecting) {
        self.id = id
        self.createdAt = createdAt
        self.name = name
        self.batchIDs = batchIDs
        self.status = status
    }
}

/// Only the app writes this ledger. Original batches stay in Ready, with no
/// duplicate payloads. Seen IDs survive detaching a member so reload cannot
/// unexpectedly collect it again after a crash or restart.
public struct BatchCollectionLedger: Codable, Sendable, Equatable {
    public let schemaVersion: Int
    public private(set) var collections: [BatchCollection]
    public private(set) var seenBatchIDs: Set<UUID>

    public init() {
        schemaVersion = 1
        collections = []
        seenBatchIDs = []
    }
    public var current: BatchCollection? { collections.first { $0.status == .collecting } }
    public var protectedBatchIDs: Set<UUID> {
        Set(collections.filter { $0.status != .delivered }.flatMap(\.batchIDs))
    }

    @discardableResult
    public mutating func append(_ batchID: UUID, at date: Date = Date()) -> UUID {
        if seenBatchIDs.contains(batchID) {
            return collections.first { $0.batchIDs.contains(batchID) }?.id ?? batchID
        }
        if current == nil { collections.append(BatchCollection(createdAt: date)) }
        let index = collections.firstIndex { $0.status == .collecting }!
        collections[index].batchIDs.append(batchID)
        seenBatchIDs.insert(batchID)
        return collections[index].id
    }

    public mutating func resume(_ id: UUID) throws {
        let index = try editableIndex(id)
        guard !collections[index].batchIDs.isEmpty else { throw Failure.empty }
        for i in collections.indices where collections[i].status == .collecting {
            collections[i].status = .draft
        }
        collections[index].status = .collecting
        collections[index].detail = nil
    }

    public mutating func parkCurrent() {
        for i in collections.indices where collections[i].status == .collecting {
            collections[i].status = .draft
        }
    }

    public mutating func rename(_ id: UUID, to name: String) throws {
        collections[try editableIndex(id)].name = String(name.trimmingCharacters(in: .whitespacesAndNewlines).prefix(120))
    }

    public mutating func setDefaultChatName(_ id: UUID, name: String) throws {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { throw Failure.invalid }
        collections[try editableIndex(id)].defaultChatName = trimmed
    }

    @discardableResult
    public mutating func remove(_ batchID: UUID, from id: UUID) throws -> Int {
        let index = try editableIndex(id)
        guard let position = collections[index].batchIDs.firstIndex(of: batchID) else { throw Failure.missing }
        collections[index].batchIDs.remove(at: position)
        if collections[index].batchIDs.isEmpty { collections[index].status = .draft }
        return position
    }

    public mutating func restore(_ batchID: UUID, to id: UUID, at position: Int) throws {
        let index = try editableIndex(id)
        guard !collections.contains(where: { $0.batchIDs.contains(batchID) }) else { throw Failure.busy }
        collections[index].batchIDs.insert(batchID, at: min(max(0, position), collections[index].batchIDs.count))
    }

    /// Freeze before enqueueing any paste. Later shares cannot enter this group.
    public mutating func beginDelivery(_ id: UUID) throws -> [UUID] {
        let index = try editableIndex(id)
        guard !collections[index].batchIDs.isEmpty else { throw Failure.empty }
        collections[index].status = .delivering
        collections[index].detail = nil
        return collections[index].batchIDs
    }

    public mutating func finishDelivery(_ id: UUID, succeeded: Bool, target: String, detail: String, scene: String? = nil) throws {
        guard let index = collections.firstIndex(where: { $0.id == id }), collections[index].status == .delivering else { throw Failure.missing }
        collections[index].status = succeeded ? .delivered : .retry
        collections[index].targetName = target
        collections[index].detail = detail
        collections[index].sceneName = scene
    }

    public mutating func recoverInterruptedDeliveries() {
        for i in collections.indices where collections[i].status == .delivering {
            collections[i].status = .retry
            collections[i].detail = L10n.text("交付被中断，原始文件已保留。请确认目标附件后再重试。")
        }
    }

    public mutating func forget(_ id: UUID) throws {
        collections.remove(at: try editableIndex(id))
    }

    private func editableIndex(_ id: UUID) throws -> Int {
        guard let index = collections.firstIndex(where: { $0.id == id }) else { throw Failure.missing }
        guard collections[index].status != .delivering else { throw Failure.busy }
        return index
    }

    public enum Failure: Error, LocalizedError {
        case missing, busy, empty, invalid
        public var errorDescription: String? {
            switch self {
            case .missing: L10n.text("部分收集文件已不存在，请查看批次。")
            case .busy: L10n.text("正在交付，请稍后操作。")
            case .empty: L10n.text("这组收集没有文件。")
            case .invalid: L10n.text("收集记录无法读取，原始文件已保留。")
            }
        }
    }

    public static func load(from url: URL) throws -> Self {
        guard FileManager.default.fileExists(atPath: url.path) else { return Self() }
        let ledger = try BatchManifest.decoder().decode(Self.self, from: Data(contentsOf: url))
        let members = ledger.collections.flatMap(\.batchIDs)
        guard ledger.schemaVersion == 1,
              Set(ledger.collections.map(\.id)).count == ledger.collections.count,
              Set(members).count == members.count,
              Set(members).isSubset(of: ledger.seenBatchIDs),
              ledger.collections.filter({ $0.status == .collecting }).count <= 1 else { throw Failure.invalid }
        return ledger
    }

    public func save(to url: URL) throws {
        try BatchManifest.encoder().encode(self).write(to: url, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }
}

public struct CollectionBatchMetadata: Sendable {
    public let count: Int?
    public let first: WeChatTranscriptRecord?
    public let last: WeChatTranscriptRecord?

    public static func read(urls: [URL]) -> Self {
        let transcripts = urls.filter { $0.pathExtension.lowercased() == "zip" }.map { url -> [WeChatTranscriptRecord]? in
            guard let data = try? Data(contentsOf: url, options: .mappedIfSafe),
                  let transcript = try? WeChatNativeArchive.transcript(data) else { return nil }
            return transcript.records
        }
        let records = transcripts.compactMap { $0 }.flatMap { $0 }
        // Preserve original order among equal minute timestamps.
        let ordered = records.enumerated().sorted {
            $0.element.date == $1.element.date ? $0.offset < $1.offset : $0.element.date < $1.element.date
        }.map(\.element)
        return Self(count: !transcripts.isEmpty && transcripts.allSatisfy({ $0 != nil }) ? records.count : nil, first: ordered.first, last: ordered.last)
    }
}
