import Foundation
import WeChatBridgeCore

/// 「分批收集到微信流」: the action extension drops this file into its batch
/// before commit, and the app adds marked batches to the open collection on
/// its next reload. The ledger keeps one writer, the app; the marker rides
/// inside the atomic commit, so a batch is never half marked.
///
/// The ordinary share extension cannot use `ShareAction.collect` for this:
/// it writes that action for every save.
enum CollectMarker {
    static let fileName = "collection-member"
    /// Set in the action extension's Info.plist only.
    static let infoKey = "DKJoinsCollection"

    static var declared: Bool {
        Bundle.main.object(forInfoDictionaryKey: infoKey) as? Bool ?? false
    }

    static func mark(_ directory: URL) throws {
        try Data().write(to: directory.appendingPathComponent(fileName), options: .atomic)
    }

    static func isMarked(_ directory: URL) -> Bool {
        FileManager.default.fileExists(atPath: directory.appendingPathComponent(fileName).path)
    }

    /// Marked batches the ledger has not taken yet, in the order they landed.
    /// Directory birth times keep two shares within one second apart, as on the Mac.
    static func unseen(_ directories: [URL], seen: Set<UUID>) -> [UUID] {
        let dated: [(id: UUID, date: Date)] = directories.compactMap { directory in
            guard let id = UUID(uuidString: directory.lastPathComponent), !seen.contains(id), isMarked(directory) else { return nil }
            let created = (try? directory.resourceValues(forKeys: [.creationDateKey]).creationDate) ?? .distantPast
            return (id, created)
        }
        return dated.sorted { $0.date == $1.date ? $0.id.uuidString < $1.id.uuidString : $0.date < $1.date }.map(\.id)
    }

    static func ledgerURL(in inbox: Inbox) -> URL {
        inbox.root.appendingPathComponent("collections.json")
    }

    /// Which batch of the open collection the one just committed is: what the
    /// ledger holds plus every marked batch the app has not taken yet, this
    /// one included. Read only; a missing ledger counts as empty.
    static func batchNumber(in inbox: Inbox) -> Int {
        let ledger = (try? BatchCollectionLedger.load(from: ledgerURL(in: inbox))) ?? BatchCollectionLedger()
        let ready = (try? FileManager.default.contentsOfDirectory(at: inbox.ready, includingPropertiesForKeys: [.creationDateKey])) ?? []
        return (ledger.current?.batchIDs.count ?? 0) + unseen(ready, seen: ledger.seenBatchIDs).count
    }
}

/// WeChat forwards at most 100 messages at a time. The next batch starts right
/// after this one's last message, so its words go to the clipboard: pasted
/// into WeChat's chat search, they land the user where to select again.
enum SearchAnchor {
    static let forwardLimit = 100

    /// The last message with words of its own, first line, short enough for
    /// WeChat's search box. Images, files and other `[标签]` lines are skipped:
    /// search finds only what was typed.
    static func keyword(_ records: [WeChatTranscriptRecord]) -> String? {
        anchor(records)?.keyword
    }

    /// The message the keyword comes from, which is shown beside it.
    static func anchor(_ records: [WeChatTranscriptRecord]) -> (record: WeChatTranscriptRecord, keyword: String)? {
        for record in records.reversed() {
            let line = record.text.split(whereSeparator: \.isNewline).first.map(String.init)?
                .trimmingCharacters(in: .whitespaces) ?? ""
            guard !line.isEmpty, !line.hasPrefix("[") else { continue }
            return (record, String(line.prefix(30)))
        }
        return nil
    }
}
