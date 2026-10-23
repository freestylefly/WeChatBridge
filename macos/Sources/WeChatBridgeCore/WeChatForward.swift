import Foundation

public enum WeChatReadError: Error, LocalizedError {
    case invalidTranscript, transcriptMismatch
    public var errorDescription: String? {
        switch self {
        case .invalidTranscript: L10n.text("微信导出的文件为空或无法完整读取，文件已保留。")
        case .transcriptMismatch: L10n.text("未能继续定位微信消息，已收到的文件会保留在记录中。")
        }
    }
}

public struct WeChatTranscriptRecord: Equatable, Sendable {
    public let sender: String
    public let date: Date
    public let text: String

    public static func parse(_ body: String, timeZone: TimeZone = .current) throws -> [Self] {
        let body = body.replacingOccurrences(of: "\r\n", with: "\n").trimmingCharacters(in: CharacterSet(charactersIn: "\u{feff}"))
        let regex = try NSRegularExpression(pattern: #"(?m)^·([^\n]+)\n(\d{4}年\d{1,2}月\d{1,2}日 \d{2}:\d{2})\n"#)
        let matches = regex.matches(in: body, range: NSRange(body.startIndex..., in: body))
        guard matches.first?.range.location == 0 else { throw WeChatReadError.invalidTranscript }
        let formatter = DateFormatter()
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = timeZone
        formatter.dateFormat = "yyyy年M月d日 HH:mm"
        let source = body as NSString
        return try matches.enumerated().map { index, match in
            guard let date = formatter.date(from: source.substring(with: match.range(at: 2))) else { throw WeChatReadError.invalidTranscript }
            let start = NSMaxRange(match.range)
            let end = index + 1 < matches.count ? matches[index + 1].range.location : source.length
            return Self(sender: source.substring(with: match.range(at: 1)), date: date,
                        text: source.substring(with: NSRange(location: start, length: end - start)).trimmingCharacters(in: .whitespacesAndNewlines))
        }
    }

}
