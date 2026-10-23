import Foundation

/// One line of a message body, recognised by the placeholders WeChat writes
/// into `聊天记录.txt`. Anything unrecognised stays text: a placeholder this
/// code does not know yet must still be readable, never dropped.
enum MessageToken: Equatable, Hashable {
    case text(String)
    case image(name: String)
    case video(name: String)
    case file(name: String)
    case link(title: String, url: URL)
    case channels(title: String, url: URL)

    static func tokenize(_ body: String) -> [MessageToken] {
        var tokens: [MessageToken] = []
        var pending: [String] = []

        func flush() {
            guard !pending.isEmpty else { return }
            tokens.append(.text(pending.joined(separator: "\n")))
            pending.removeAll()
        }

        for line in body.components(separatedBy: "\n") {
            if let token = token(for: line) {
                flush()
                tokens.append(token)
            } else {
                pending.append(line)
            }
        }
        flush()
        return tokens
    }

    private static func token(for line: String) -> MessageToken? {
        let trimmed = line.trimmingCharacters(in: .whitespaces)
        if let name = rest(of: trimmed, after: "[图片]") { return .image(name: name) }
        if let name = rest(of: trimmed, after: "[视频]") { return .video(name: name) }
        if let name = rest(of: trimmed, after: "[文件]") { return .file(name: name) }
        if let rest = rest(of: trimmed, after: "[链接]"), let (title, url) = titleAndURL(rest) {
            return .link(title: title, url: url)
        }
        if let rest = rest(of: trimmed, after: "[视频号]"), let (title, url) = titleAndURL(rest) {
            return .channels(title: title, url: url)
        }
        return nil
    }

    private static func rest(of line: String, after prefix: String) -> String? {
        guard line.hasPrefix(prefix + " ") else { return nil }
        let rest = line.dropFirst(prefix.count + 1).trimmingCharacters(in: .whitespaces)
        return rest.isEmpty ? nil : rest
    }

    /// The URL is the last whitespace-free run starting with http(s)://; the
    /// title is whatever precedes it, which may itself contain spaces.
    private static func titleAndURL(_ text: String) -> (String, URL)? {
        guard let range = text.range(of: "https?://\\S+", options: [.regularExpression, .backwards]),
              let url = URL(string: String(text[range]))
        else { return nil }
        let title = text[..<range.lowerBound].trimmingCharacters(in: .whitespaces)
        return (title.isEmpty ? url.host ?? url.absoluteString : title, url)
    }
}
