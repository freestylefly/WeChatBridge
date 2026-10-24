import SwiftUI
import WeChatBridgeCore

struct RelayCardView: View {
    let entry: RelayEntry
    var collecting = false
    /// Offered on a chat cut off at WeChat's forward limit; nil hides it.
    var startCollecting: (() -> Void)?

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            KindBadge(kind: entry.summary?.kind)
            VStack(alignment: .leading, spacing: 5) {
                HStack(alignment: .firstTextBaseline) {
                    Text(entry.summary?.title ?? entry.batch.items.first?.displayName ?? "聊天记录")
                        .font(.headline)
                        .lineLimit(1)
                    Spacer(minLength: 4)
                    StatusTag(entry: entry, collecting: collecting)
                }
                if let summary = entry.summary {
                    ForEach(Self.previewLines(summary), id: \.self) { line in
                        Text(line).font(.subheadline).foregroundStyle(.secondary).lineLimit(1)
                    }
                    HStack(spacing: 6) {
                        Text(Self.counts(summary))
                        Spacer()
                        Text(RelayFormat.range(summary.start, summary.end) ?? "收到于 \(RelayFormat.day(entry.batch.createdAt))")
                    }
                    .font(.caption)
                    .foregroundStyle(.tertiary)
                    .padding(.top, 2)
                } else {
                    ProgressView().controlSize(.small)
                }
                if let startCollecting, !collecting, entry.summary?.hitsForwardLimit == true {
                    ForwardLimitHint(action: startCollecting).padding(.top, 4)
                }
            }
        }
        .padding(.vertical, 6)
    }
}

/// Exactly 100 messages is where WeChat stops a selection, so the chat most
/// likely goes on beyond this batch.
private struct ForwardLimitHint: View {
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Image(systemName: "exclamationmark.circle.fill").foregroundStyle(.orange)
                // Per-run colours: a button label is otherwise tinted throughout.
                (Text("正好 \(SearchAnchor.forwardLimit) 条，微信一次最多转 \(SearchAnchor.forwardLimit) 条。还有前后的消息？").foregroundColor(.primary)
                    + Text("分批收集").bold().foregroundColor(.accentColor))
                    .font(.caption)
                    .multilineTextAlignment(.leading)
            }
            .padding(8)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Color.yellow.opacity(0.18), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
        }
        // Its own tap target inside the row's navigation link.
        .buttonStyle(.borderless)
    }
}

/// Which kind of content a card holds, at a glance.
private struct KindBadge: View {
    let kind: RelaySummary.Kind?

    var body: some View {
        let (symbol, color): (String, Color) = switch kind {
        case .chat: ("bubble.left.and.bubble.right.fill", .accentColor)
        case .text: ("doc.plaintext.fill", .blue)
        case .files: ("exclamationmark.triangle.fill", .orange)
        case nil: ("tray.fill", .secondary)
        }
        Image(systemName: symbol)
            .font(.system(size: 15, weight: .semibold))
            .foregroundStyle(color)
            .frame(width: 34, height: 34)
            .background(color.opacity(0.13), in: RoundedRectangle(cornerRadius: 9, style: .continuous))
    }
}

extension RelayCardView {
    /// A text's lines as written, except that a bare link shows only its
    /// domain and at most one link is shown.
    static func previewLines(_ summary: RelaySummary) -> [String] {
        guard summary.kind == .text else { return summary.preview }
        var sawLink = false
        return summary.preview.compactMap { line in
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            guard let url = URL(string: trimmed), let scheme = url.scheme, scheme.hasPrefix("http"), let host = url.host else { return line }
            if sawLink { return nil }
            sawLink = true
            return "🔗 \(host)"
        }
    }

    /// `8 条消息 · 3 个附件`; a text file reads `文本`, and a lone count has no dot.
    static func counts(_ summary: RelaySummary) -> String {
        var parts: [String] = []
        if let count = summary.messageCount {
            parts.append("\(count) 条消息")
        } else if summary.kind == .text {
            parts.append("文本")
        }
        if summary.mediaCount > 0 { parts.append("\(summary.mediaCount) 个附件") }
        return parts.joined(separator: " · ")
    }
}

private struct StatusTag: View {
    let entry: RelayEntry
    let collecting: Bool

    var body: some View {
        let (text, color): (String, Color) = {
            if entry.summary?.parseFailed == true { return ("解析失败", .orange) }
            if collecting { return ("收集中", .purple) }
            if entry.isDelivered { return (entry.batch.targetName.map { "已投递 · \($0)" } ?? "已投递", .secondary) }
            // Undelivered is the normal state and gets no tag.
            return ("", .clear)
        }()
        if !text.isEmpty {
            tag(text, color: color)
        }
    }

    private func tag(_ text: String, color: Color) -> some View {
        Text(text)
            .font(.caption2.weight(.medium))
            .lineLimit(1)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .foregroundStyle(color)
            .background(color.opacity(0.12), in: Capsule())
    }
}

extension View {
    /// The relay's card look inside a plain List: a rounded tile on the
    /// grouped background, drawn as the row background so the disclosure
    /// chevron and edit-mode checkmarks sit inside it.
    func relayCardRow() -> some View {
        listRowBackground(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .fill(Color(.secondarySystemGroupedBackground))
                .padding(.vertical, 5)
                .padding(.horizontal, 16)
        )
        .listRowInsets(EdgeInsets(top: 10, leading: 30, bottom: 10, trailing: 30))
        .listRowSeparator(.hidden)
    }
}


struct CollectionCardView: View {
    @EnvironmentObject private var store: RelayStore
    let collection: BatchCollection
    let continueCollecting: () -> Void
    let deliver: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            header
            // Styled buttons keep their own tap targets inside the row's link.
            HStack(spacing: 10) {
                Button(action: continueCollecting) {
                    Label("继续收集", systemImage: "plus.square.on.square").frame(maxWidth: .infinity)
                }
                .buttonStyle(.bordered)
                Button(action: deliver) {
                    Label("完成并投递", systemImage: "paperplane.fill").frame(maxWidth: .infinity)
                }
                .buttonStyle(.borderedProminent)
            }
            .controlSize(.small)
            .font(.subheadline.weight(.medium))
        }
        .padding(.vertical, 4)
    }

    private var header: some View {
        HStack(spacing: 12) {
            Image(systemName: "square.stack.3d.up.fill")
                .font(.system(size: 15, weight: .semibold))
                .foregroundStyle(.purple)
                .frame(width: 34, height: 34)
                .background(Color.purple.opacity(0.13), in: RoundedRectangle(cornerRadius: 9, style: .continuous))
            VStack(alignment: .leading, spacing: 4) {
                Text(collection.name.isEmpty ? "未命名收集" : collection.name).font(.headline)
                Text(line)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            Image(systemName: "pin.fill").font(.caption).foregroundStyle(.secondary)
        }
    }

    /// `3 批 · 约 280 条 · 上一批 09:00–09:58`. 「约」: overlapping batches are
    /// deduplicated only when merged.
    private var line: String {
        let count = store.entries(collection.batchIDs).compactMap { $0.summary?.messageCount }.reduce(0, +)
        var parts = ["\(collection.batchIDs.count) 批", "约 \(count) 条"]
        if let last = store.lastBatch(of: collection)?.summary, let range = RelayFormat.range(last.start, last.end) {
            parts.append("上一批 \(range)")
        }
        return parts.joined(separator: " · ")
    }
}
