import Foundation

/// The small amount of structure scenes need before anything is pasted: who
/// spoke, and when the exported range ended.
public struct WeChatBatchInsights: Equatable, Sendable {
    public var senders: Set<String>
    public var end: Date?

    public init(senders: Set<String> = [], end: Date? = nil) {
        self.senders = senders
        self.end = end
    }

    public mutating func merge(_ other: WeChatBatchInsights) {
        senders.formUnion(other.senders)
        if let otherEnd = other.end {
            end = max(end ?? otherEnd, otherEnd)
        }
    }
}

public enum WeChatBatchInsightsReader {
    public static func read(urls: [URL], checkCancellation: () throws -> Void = {}) throws -> WeChatBatchInsights {
        var result = WeChatBatchInsights()
        for url in urls where url.pathExtension.lowercased() == "zip" {
            try checkCancellation()
            let data = try Data(contentsOf: url, options: .mappedIfSafe)
            guard let transcript = try? WeChatNativeArchive.transcript(data, checkCancellation: checkCancellation) else { continue }
            if let records = transcript.records {
                result.senders.formUnion(records.map(\.sender).map(GroupName.normalizeSender).filter { !$0.isEmpty })
            }
            if let end = transcript.end {
                result.end = max(result.end ?? end, end)
            }
        }
        return result
    }
}
