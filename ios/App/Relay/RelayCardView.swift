import SwiftUI
import WeChatBridgeCore

struct RelayCardView: View {
    let entry: RelayEntry
    var collecting = false

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline) {
                Text(entry.summary?.title ?? entry.batch.items.first?.displayName ?? "聊天记录")
                    .font(.headline)
                    .lineLimit(1)
                Spacer()
                StatusTag(entry: entry, collecting: collecting)
            }
            if let summary = entry.summary {
                ForEach(summary.preview, id: \.self) { line in
                    Text(line).font(.subheadline).foregroundStyle(.secondary).lineLimit(1)
                }
                HStack(spacing: 6) {
                    Text(Self.counts(summary))
                    Spacer()
                    if let range = RelayFormat.range(summary.start, summary.end) {
                        Text(range)
                    }
                }
                .font(.caption)
                .foregroundStyle(.secondary)
            } else {
                ProgressView().controlSize(.small)
            }
        }
        .padding(.vertical, 4)
    }
}

extension RelayCardView {
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
            if entry.isDelivered { return ("已投递到 \(entry.batch.targetName ?? "")", .green) }
            return ("未投递", .blue)
        }()
        Text(text)
            .font(.caption2.weight(.medium))
            .lineLimit(1)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .foregroundStyle(color)
            .background(color.opacity(0.12), in: Capsule())
    }
}

enum RelayFormat {
    /// `10月20日 09:00–09:07`; both ends dated when the range crosses a day.
    static func range(_ start: Date?, _ end: Date?, calendar: Calendar = .current) -> String? {
        guard let start else { return nil }
        let day = DateFormatter()
        day.locale = Locale(identifier: "zh_CN")
        day.dateFormat = "M月d日 HH:mm"
        let time = DateFormatter()
        time.dateFormat = "HH:mm"
        guard let end, end != start else { return day.string(from: start) }
        let tail = calendar.isDate(start, inSameDayAs: end) ? time.string(from: end) : day.string(from: end)
        return "\(day.string(from: start))–\(tail)"
    }
}

struct CollectionCardView: View {
    @EnvironmentObject private var store: RelayStore
    let collection: BatchCollection

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: "square.stack.3d.up.fill")
                .font(.title2)
                .foregroundStyle(.purple)
            VStack(alignment: .leading, spacing: 4) {
                Text(collection.name.isEmpty ? "未命名收集" : collection.name).font(.headline)
                Text("\(collection.batchIDs.count) 批 · \(messageCount) 条消息")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            Image(systemName: "pin.fill").font(.caption).foregroundStyle(.secondary)
        }
        .padding(.vertical, 4)
    }

    private var messageCount: Int {
        store.entries(collection.batchIDs).compactMap { $0.summary?.messageCount }.reduce(0, +)
    }
}
