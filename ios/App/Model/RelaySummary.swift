import Foundation
import UniformTypeIdentifiers
import WeChatBridgeCore

/// What a relay card shows, worked out once per batch and cached next to it.
struct RelaySummary: Codable, Hashable {
    /// What the batch's archive turned out to hold.
    enum Kind: String, Codable {
        /// WeChat's chat-record export: `·sender`, timestamp, text.
        case chat
        /// A text file in another layout, such as a single long message or a
        /// note exported on its own. Read and delivered as the text it is.
        case text
        /// No readable text at all: delivered only as the original files.
        case files
    }

    static let currentSchemaVersion = 3
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
    var kind: Kind
    /// No text in any of the batch's files: a plain file someone shared.
    /// Still deliverable, as itself.
    var parseFailed: Bool { kind == .files }

    /// The archive the summary was read from, by item id; nil when none parsed.
    var archiveItemID: UUID?

    /// The chat's last message with searchable words (see `SearchAnchor`):
    /// where the next batch of 「分批收集」 picks up.
    struct Last: Codable, Hashable {
        let sender: String
        let date: Date
        let text: String
    }
    var last: Last?
    /// What to search in WeChat to find `last`.
    var anchor: String?

    /// WeChat stopped the selection here, so the chat likely goes on.
    var hitsForwardLimit: Bool { messageCount == SearchAnchor.forwardLimit }

    static func make(for batch: ReadyBatch) -> RelaySummary {
        // A chat export anywhere in the batch wins over a plain text file.
        // Only the chosen archive is unpacked: the batch has one unpack folder.
        var text: (item: ReadyItem, data: Data, transcript: WeChatNativeArchive.Transcript)?
        for item in batch.items where item.url.pathExtension.lowercased() == "zip" {
            guard let data = try? Data(contentsOf: item.url, options: .mappedIfSafe),
                  let transcript = try? WeChatNativeArchive.transcript(data)
            else { continue }
            guard let records = transcript.records else {
                if text == nil { text = (item, data, transcript) }
                continue
            }
            return make(
                records: records,
                transcript: transcript,
                chatName: batch.chatName,
                archiveName: item.displayName,
                mediaCount: mediaCount(data, transcript: transcript, batch: batch),
                archiveItemID: item.id
            )
        }
        if let (item, data, transcript) = text {
            return make(text: transcript.body, archiveName: item.displayName,
                        mediaCount: mediaCount(data, transcript: transcript, batch: batch), archiveItemID: item.id)
        }
        return RelaySummary(
            title: batch.items.count == 1 ? batch.items[0].displayName : "\(batch.items.count) 个文件",
            messageCount: nil,
            senders: [],
            start: nil,
            end: nil,
            preview: batch.items.prefix(2).map(\.displayName),
            mediaCount: batch.items.count,
            kind: .files,
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
        let anchor = SearchAnchor.anchor(records)
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
            kind: .chat,
            archiveItemID: archiveItemID,
            last: anchor.map { Last(sender: $0.record.sender, date: $0.record.date, text: String($0.record.text.prefix(200))) },
            anchor: anchor?.keyword
        )
    }

    /// Files in the archive besides its text, plus the batch's other files.
    private static func mediaCount(_ data: Data, transcript: WeChatNativeArchive.Transcript, batch: ReadyBatch) -> Int {
        let media = (try? RelayFiles.extract(data, batchID: batch.id))?
            .names.filter { $0 != transcript.path && !$0.hasSuffix("/") }.count ?? 0
        return media + batch.items.count - 1
    }

    /// The first line names the text (WeChat starts a single exported message
    /// with `·`); the next lines preview it.
    static func make(text: String, archiveName: String, mediaCount: Int, archiveItemID: UUID?) -> RelaySummary {
        let lines = text.components(separatedBy: .newlines)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
        let heading = lines.first.map { String($0.drop { $0 == "·" }).trimmingCharacters(in: .whitespaces) } ?? ""
        return RelaySummary(
            title: heading.isEmpty ? archiveName : String(heading.prefix(40)),
            messageCount: nil,
            senders: [],
            start: nil,
            end: nil,
            preview: lines.dropFirst().prefix(2).map { String($0.prefix(40)) },
            mediaCount: mediaCount,
            kind: .text,
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
