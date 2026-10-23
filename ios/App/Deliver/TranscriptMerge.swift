import Foundation
import WeChatBridgeCore

/// Writes transcripts back in WeChat's own export format, so a merged
/// `聊天记录.txt` reads exactly like one WeChat produced — and parses with the
/// same `WeChatTranscriptRecord.parse` as the originals.
enum TranscriptMerge {
    static func render(_ records: [WeChatTranscriptRecord], timeZone: TimeZone = .current) -> String {
        let formatter = DateFormatter()
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = timeZone
        formatter.dateFormat = "yyyy年M月d日 HH:mm"
        return records
            .map { "·\($0.sender)\n\(formatter.string(from: $0.date))\n\($0.text)\n" }
            .joined(separator: "\n")
    }

    /// Time order, stable for equal minutes (WeChat stamps to the minute, so
    /// ties are common and their original order is the only order there is).
    /// A record appearing in two exports — overlapping forwards of the same
    /// chat — is kept once.
    static func merge(_ bodies: [String], timeZone: TimeZone = .current) throws -> String {
        try render(mergedRecords(bodies, timeZone: timeZone), timeZone: timeZone)
    }

    static func mergedRecords(_ bodies: [String], timeZone: TimeZone = .current) throws -> [WeChatTranscriptRecord] {
        let all = try bodies.flatMap { try WeChatTranscriptRecord.parse($0, timeZone: timeZone) }
        var seen = Set<String>()
        let unique = all.filter { seen.insert("\($0.sender)\u{1}\($0.date.timeIntervalSince1970)\u{1}\($0.text)").inserted }
        return unique.enumerated()
            .sorted { $0.element.date == $1.element.date ? $0.offset < $1.offset : $0.element.date < $1.element.date }
            .map(\.element)
    }
}
