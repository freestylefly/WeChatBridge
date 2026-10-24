import QuickLook
import SwiftUI
import WeChatBridgeCore

struct TranscriptView: View {
    let transcript: LoadedTranscript
    @State private var viewing: URL?
    @State private var previewURL: URL?

    var body: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 10) {
                ForEach(Array(transcript.records.enumerated()), id: \.offset) { index, record in
                    let previous = index > 0 ? transcript.records[index - 1] : nil
                    if previous.map({ !Calendar.current.isDate($0.date, inSameDayAs: record.date) }) ?? true {
                        DateDivider(date: record.date)
                    }
                    MessageRow(
                        record: record,
                        showsSender: previous?.sender != record.sender,
                        transcript: transcript,
                        openImage: { viewing = $0 },
                        openFile: { previewURL = $0 }
                    )
                }
            }
            .padding()
        }
        .fullScreenCover(item: $viewing) { ImageViewer(url: $0) }
        .quickLookPreview($previewURL)
    }
}

extension URL: @retroactive Identifiable {
    public var id: String { absoluteString }
}

private struct DateDivider: View {
    let date: Date

    var body: some View {
        Text(date, format: .dateTime.year().month().day().weekday())
            .font(.caption)
            .foregroundStyle(.secondary)
            .frame(maxWidth: .infinity)
            .padding(.vertical, 4)
    }
}

private struct MessageRow: View {
    let record: WeChatTranscriptRecord
    let showsSender: Bool
    let transcript: LoadedTranscript
    let openImage: (URL) -> Void
    let openFile: (URL) -> Void

    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            Avatar(name: record.sender).opacity(showsSender ? 1 : 0)
            VStack(alignment: .leading, spacing: 4) {
                if showsSender {
                    HStack(spacing: 6) {
                        Text(record.sender).font(.caption.weight(.semibold)).foregroundStyle(Avatar.color(for: record.sender))
                        Text(record.date, format: .dateTime.hour().minute()).font(.caption2).foregroundStyle(.secondary)
                    }
                }
                ForEach(Array(MessageToken.tokenize(record.text).enumerated()), id: \.offset) { _, token in
                    TokenView(token: token, transcript: transcript, openImage: openImage, openFile: openFile)
                }
            }
            Spacer(minLength: 24)
        }
    }
}

private struct Avatar: View {
    let name: String

    var body: some View {
        Text(String(name.prefix(1)))
            .font(.footnote.weight(.semibold))
            .foregroundStyle(.white)
            .frame(width: 32, height: 32)
            .background(Self.color(for: name), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
    }

    /// Stable per name across launches (`hashValue` is reseeded each run), so
    /// a speaker keeps one colour for avatar and name.
    static func color(for name: String) -> Color {
        let seed = name.unicodeScalars.reduce(UInt32(5381)) { ($0 &* 33) &+ $1.value }
        return Color(hue: Double(seed % 360) / 360, saturation: 0.5, brightness: 0.72)
    }
}

private struct TokenView: View {
    let token: MessageToken
    let transcript: LoadedTranscript
    let openImage: (URL) -> Void
    let openFile: (URL) -> Void

    var body: some View {
        switch token {
        case .text(let text):
            Text(text)
                .textSelection(.enabled)
                .padding(10)
                .background(Color(.secondarySystemBackground), in: RoundedRectangle(cornerRadius: 12, style: .continuous))
        case .image(let name):
            if let url = transcript.url(for: name), let image = Thumbnail.load(url, maxPixel: 600) {
                let size = Self.displaySize(image.size)
                Button { openImage(url) } label: {
                    Image(uiImage: image)
                        .resizable()
                        .scaledToFit()
                        .frame(width: size.width, height: size.height)
                        // A tile behind, so a tiny or transparent image is
                        // still a visible, tappable thing.
                        .frame(minWidth: 64, minHeight: 64)
                        .background(Color(.tertiarySystemFill))
                        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
                        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(Color.primary.opacity(0.08)))
                }
                .accessibilityLabel("图片 \(name)")
            } else {
                Attachment(symbol: "photo", title: name, subtitle: "图片未随附", action: nil)
            }
        case .video(let name):
            Attachment(symbol: "video", title: name, subtitle: "视频", action: transcript.url(for: name).map { url in { openFile(url) } })
        case .file(let name):
            Attachment(symbol: "doc", title: name, subtitle: "文件", action: transcript.url(for: name).map { url in { openFile(url) } })
        case .link(let title, let url):
            Link(destination: url) { Attachment.label(symbol: "link", title: title, subtitle: url.host ?? url.absoluteString) }
                .buttonStyle(.plain)
        case .channels(let title, let url):
            Link(destination: url) { Attachment.label(symbol: "play.rectangle", title: title, subtitle: "视频号") }
                .buttonStyle(.plain)
        }
    }
}

extension TokenView {
    /// Fits in 220×260 pt and never blows a small image up past one point per pixel.
    static func displaySize(_ pixels: CGSize) -> CGSize {
        guard pixels.width > 0, pixels.height > 0 else { return CGSize(width: 64, height: 64) }
        let scale = min(220 / pixels.width, 260 / pixels.height, 1)
        return CGSize(width: pixels.width * scale, height: pixels.height * scale)
    }
}

private struct Attachment: View {
    let symbol: String
    let title: String
    let subtitle: String
    let action: (() -> Void)?

    var body: some View {
        if let action {
            Button(action: action) { Self.label(symbol: symbol, title: title, subtitle: subtitle) }
                .buttonStyle(.plain)
        } else {
            Self.label(symbol: symbol, title: title, subtitle: subtitle)
        }
    }

    static func label(symbol: String, title: String, subtitle: String) -> some View {
        HStack(spacing: 10) {
            Image(systemName: symbol).font(.title3).foregroundStyle(Color.accentColor).frame(width: 28)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.subheadline).lineLimit(2).multilineTextAlignment(.leading)
                Text(subtitle).font(.caption).foregroundStyle(.secondary)
            }
        }
        .foregroundStyle(.primary)
        .padding(10)
        .frame(maxWidth: 260, alignment: .leading)
        .background(Color(.secondarySystemBackground), in: RoundedRectangle(cornerRadius: 12, style: .continuous))
    }
}

/// A text that is not a chat record, shown as written with its links live.
struct PlainTextView: View {
    let text: String

    var body: some View {
        ScrollView {
            Text(Self.linked(text))
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding()
        }
    }

    static func linked(_ text: String) -> AttributedString {
        var attributed = AttributedString(text)
        guard let detector = try? NSDataDetector(types: NSTextCheckingResult.CheckingType.link.rawValue) else { return attributed }
        for match in detector.matches(in: text, range: NSRange(text.startIndex..., in: text)) {
            guard let url = match.url,
                  let range = Range(match.range, in: text),
                  let lower = AttributedString.Index(range.lowerBound, within: attributed),
                  let upper = AttributedString.Index(range.upperBound, within: attributed)
            else { continue }
            attributed[lower..<upper].link = url
        }
        return attributed
    }
}
