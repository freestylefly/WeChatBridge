import Foundation
import UniformTypeIdentifiers
import WeChatBridgeCore

/// A link or text a host shared instead of a file: X, Safari, 抖音 and most
/// apps hand the share sheet a URL, not a document. The share extension keeps
/// it beside the batch (and as a TXT item, which is what gets delivered); the
/// app reads it back for the card and to tell which app it came from.
struct ShareOrigin: Codable, Hashable {
    static let fileName = "share-origin.json"
    /// `contentType` of the TXT item the extension writes for the link.
    static let noteContentType = UTType.url.identifier

    var url: URL?
    var title: String?
    var text: String?

    var isEmpty: Bool { url == nil && (text ?? "").isEmpty }

    /// The host's title, else the text's first line, else the link's domain.
    var heading: String {
        if let title = title?.trimmingCharacters(in: .whitespacesAndNewlines), !title.isEmpty { return String(title.prefix(60)) }
        if let line = text?.split(whereSeparator: \.isNewline).first?.trimmingCharacters(in: .whitespaces), !line.isEmpty {
            return String(line.prefix(40))
        }
        return url.flatMap(ShareSource.displayHost) ?? "分享的文字"
    }

    /// What the delivered TXT holds: title, link, then the text the host sent.
    var note: String {
        var lines: [String] = []
        if let title, !title.isEmpty { lines.append(title) }
        if let url { lines.append(url.absoluteString) }
        if let text, !text.isEmpty {
            if !lines.isEmpty { lines.append("") }
            lines.append(text)
        }
        return lines.joined(separator: "\n") + "\n"
    }

    var noteFileName: String {
        let base = heading.replacingOccurrences(of: "/", with: "-")
        return DisplayName.sanitize("\(base).txt", fallbackExtension: "txt")
    }

    func write(to directory: URL) throws {
        try BatchManifest.encoder().encode(self).write(to: directory.appendingPathComponent(Self.fileName), options: .atomic)
    }

    static func read(in directory: URL) -> ShareOrigin? {
        guard let data = try? Data(contentsOf: directory.appendingPathComponent(fileName)) else { return nil }
        return try? BatchManifest.decoder().decode(ShareOrigin.self, from: data)
    }

    /// Text that is only the link itself adds nothing; a link buried in the
    /// text (抖音's 「复制打开抖音」 blurb) becomes the link.
    static func make(url: URL?, title: String?, texts: [String]) -> ShareOrigin? {
        var url = url
        var parts: [String] = []
        for raw in texts {
            let text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty, text != url?.absoluteString, !parts.contains(text) else { continue }
            parts.append(text)
        }
        if url == nil { url = parts.lazy.compactMap(firstWebURL).first }
        let title = title?.trimmingCharacters(in: .whitespacesAndNewlines)
        // A host that sends the tweet as both the title and the text.
        parts.removeAll { $0 == title }
        let origin = ShareOrigin(url: url, title: title?.isEmpty == false ? title : nil, text: parts.isEmpty ? nil : parts.joined(separator: "\n\n"))
        return origin.isEmpty ? nil : origin
    }

    static func isWeb(_ url: URL) -> Bool {
        ["http", "https"].contains(url.scheme?.lowercased() ?? "")
    }

    private static func firstWebURL(in text: String) -> URL? {
        guard let detector = try? NSDataDetector(types: NSTextCheckingResult.CheckingType.link.rawValue) else { return nil }
        let range = NSRange(text.startIndex..., in: text)
        return detector.matches(in: text, range: range).lazy.compactMap(\.url).first(where: isWeb)
    }
}

/// Which app a batch came from. iOS never tells a share extension its host
/// app, so this reads it off what was shared: the link's domain, or a
/// WeChat chat export. Anything else is named by what it is.
enum ShareSource {
    static let wechat = "微信"
    static let web = "网页"
    static let text = "文字"
    static let images = "图片"
    static let files = "文件"

    /// Domain suffix → app, most specific first.
    private static let apps: [(suffix: String, name: String)] = [
        ("mp.weixin.qq.com", wechat), ("weixin.qq.com", wechat),
        ("x.com", "X"), ("twitter.com", "X"), ("t.co", "X"),
        ("douyin.com", "抖音"), ("iesdouyin.com", "抖音"),
        ("xiaohongshu.com", "小红书"), ("xhslink.com", "小红书"),
        ("bilibili.com", "哔哩哔哩"), ("b23.tv", "哔哩哔哩"),
        ("zhihu.com", "知乎"),
        ("weibo.com", "微博"), ("weibo.cn", "微博"),
        ("kuaishou.com", "快手"), ("chenzhongtech.com", "快手"),
        ("toutiao.com", "今日头条"),
        ("youtube.com", "YouTube"), ("youtu.be", "YouTube"),
        ("github.com", "GitHub"),
        ("instagram.com", "Instagram"),
        ("threads.net", "Threads"), ("threads.com", "Threads"),
        ("reddit.com", "Reddit"),
        ("feishu.cn", "飞书"), ("larksuite.com", "飞书"),
        ("yuque.com", "语雀"),
        ("jike.city", "即刻"), ("okjike.com", "即刻"),
        ("douban.com", "豆瓣"),
        ("apple.com", "Apple"),
    ]

    static func app(for url: URL) -> String? {
        guard let host = url.host?.lowercased() else { return nil }
        return apps.first { host == $0.suffix || host.hasSuffix("." + $0.suffix) }?.name
    }

    static func displayHost(_ url: URL) -> String? {
        guard let host = url.host else { return nil }
        return host.hasPrefix("www.") ? String(host.dropFirst(4)) : host
    }
}
