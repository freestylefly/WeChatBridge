import Foundation
import UniformTypeIdentifiers
import WeChatBridgeCore

/// What a relay card shows, worked out once per batch and cached next to it.
struct RelaySummary: Codable, Hashable {
    static let currentSchemaVersion = 1
    static let cacheFileName = "relay-cache.json"
    static let mediaFolder = "聊天记录内的图片、视频和文件/"

    var schemaVersion = Self.currentSchemaVersion
    var title: String
    var messageCount: Int?
    /// First-appearance order, which is the order a reader meets them in.
    var senders: [String]
    var start: Date?
    var end: Date?
    var preview: [String]
    var mediaCount: Int
    /// No WeChat transcript in any of the batch's files: a plain file someone
    /// shared. Still deliverable, as itself.
    var parseFailed: Bool

    /// The archive the summary was read from, by item id; nil when none parsed.
    var archiveItemID: UUID?

    static func make(for batch: ReadyBatch) -> RelaySummary {
        for item in batch.items where item.url.pathExtension.lowercased() == "zip" {
            guard let data = try? Data(contentsOf: item.url, options: .mappedIfSafe),
                  let transcript = try? WeChatNativeArchive.transcript(data),
                  let records = transcript.records
            else { continue }
            let media = (try? RelayFiles.extract(data, batchID: batch.id))?
                .names.filter { $0 != transcript.path && !$0.hasSuffix("/") }.count ?? 0
            return make(
                records: records,
                transcript: transcript,
                chatName: batch.chatName,
                archiveName: item.displayName,
                mediaCount: media + batch.items.count - 1,
                archiveItemID: item.id
            )
        }
        return RelaySummary(
            title: batch.items.count == 1 ? batch.items[0].displayName : "\(batch.items.count) 个文件",
            messageCount: nil,
            senders: [],
            start: nil,
            end: nil,
            preview: batch.items.prefix(2).map(\.displayName),
            mediaCount: batch.items.count,
            parseFailed: true,
            archiveItemID: nil
        )
    }

    static func make(
        records: [WeChatTranscriptRecord],
        transcript: WeChatNativeArchive.Transcript,
        chatName: String?,
        archiveName: String,
        mediaCount: Int,
        archiveItemID: UUID?
    ) -> RelaySummary {
        var seen = Set<String>()
        let senders = records.map(\.sender).filter { seen.insert($0).inserted }
        let preview = records.prefix(2).map { record -> String in
            let line = record.text.components(separatedBy: .newlines).first ?? ""
            return String("\(record.sender)：\(line)".prefix(40))
        }
        return RelaySummary(
            title: ObsidianNote.title(chatName: chatName, transcript: transcript, archiveName: archiveName),
            messageCount: records.count,
            senders: senders,
            start: transcript.start,
            end: transcript.end,
            preview: preview,
            mediaCount: mediaCount,
            parseFailed: false,
            archiveItemID: archiveItemID
        )
    }

    // MARK: - Cache

    static func cached(in directory: URL) -> RelaySummary? {
        let url = directory.appendingPathComponent(cacheFileName)
        guard let data = try? Data(contentsOf: url),
              let summary = try? BatchManifest.decoder().decode(RelaySummary.self, from: data),
              summary.schemaVersion == currentSchemaVersion
        else { return nil }
        return summary
    }

    func store(in directory: URL) {
        guard let data = try? BatchManifest.encoder().encode(self) else { return }
        try? data.write(to: directory.appendingPathComponent(Self.cacheFileName), options: .atomic)
    }
}

/// Where an archive is unpacked for reading. Caches, not the group container:
/// the system may purge it, and every reader re-extracts when it is missing.
enum RelayFiles {
    struct Extracted {
        let directory: URL
        let names: [String]
    }

    static var root: URL {
        FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Relay", isDirectory: true)
    }

    static func directory(for batchID: UUID) -> URL {
        root.appendingPathComponent(batchID.uuidString, isDirectory: true)
    }

    /// Idempotent: a complete earlier extraction (marked by `.done`) is reused.
    static func extract(_ data: Data, batchID: UUID) throws -> Extracted {
        let directory = directory(for: batchID)
        let marker = directory.appendingPathComponent(".done")
        let fm = FileManager.default
        if let listed = try? String(contentsOf: marker, encoding: .utf8) {
            return Extracted(directory: directory, names: listed.split(separator: "\n").map(String.init))
        }
        try? fm.removeItem(at: directory)
        try fm.createDirectory(at: directory, withIntermediateDirectories: true)
        let names = try WeChatNativeArchive.extract(data, to: directory)
        try names.joined(separator: "\n").write(to: marker, atomically: true, encoding: .utf8)
        return Extracted(directory: directory, names: names)
    }

    static func remove(batchID: UUID) {
        try? FileManager.default.removeItem(at: directory(for: batchID))
    }
}
