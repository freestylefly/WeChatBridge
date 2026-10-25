import SwiftUI
import WeChatBridgeCore

@MainActor
final class ReceiptModel: ObservableObject {
    /// One batch of 「分批收集」. Counts are nil when the share was not a
    /// WeChat export the extension could read.
    struct Collected: Equatable {
        let number: Int
        let count: Int?
        let start: Date?
        let end: Date?
        /// What went to the clipboard, for WeChat's search.
        let anchor: String?
    }

    enum State: Equatable {
        case saving
        case saved(names: [String], byteCount: Int64, anchor: String?)
        case collected(Collected)
        case failed(String)
    }

    @Published var state: State = .saving
}

/// One small card over the host app. Only the TXT is read for counts, never
/// the media, so the extension stays well inside its memory ceiling.
struct ReceiptView: View {
    @ObservedObject var model: ReceiptModel
    let done: () -> Void

    var body: some View {
        VStack {
            Spacer()
            VStack(spacing: 12) {
                icon
                Text(title).font(.headline)
                if let detail {
                    Text(detail)
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                        .lineLimit(3)
                }
                if let hint {
                    Text(hint)
                        .font(.footnote)
                        .multilineTextAlignment(.center)
                        .padding(10)
                        .frame(maxWidth: .infinity)
                        .background(Color.green.opacity(0.12), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
                }
                if model.state != .saving {
                    Button("完成", action: done)
                        .buttonStyle(.borderedProminent)
                        .tint(.green)
                        .padding(.top, 4)
                }
            }
            .padding(24)
            .frame(maxWidth: 320)
            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 20, style: .continuous))
            .padding(.bottom, 48)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .animation(.easeOut(duration: 0.2), value: model.state)
    }

    @ViewBuilder private var icon: some View {
        switch model.state {
        case .saving:
            ProgressView().controlSize(.large)
        case .saved:
            Image(systemName: "checkmark.circle.fill").font(.system(size: 40)).foregroundStyle(.green)
        case .collected:
            Image(systemName: "square.stack.3d.down.forward.fill").font(.system(size: 36)).foregroundStyle(.green)
        case .failed:
            Image(systemName: "exclamationmark.triangle.fill").font(.system(size: 40)).foregroundStyle(.orange)
        }
    }

    private var title: String {
        switch model.state {
        case .saving: "正在存入…"
        case .saved: "已存入中转站"
        case .collected(let batch): "已收第 \(batch.number) 批"
        case .failed: "没有存入"
        }
    }

    private var detail: String? {
        switch model.state {
        case .saving:
            return nil
        case .saved(let names, let byteCount, _):
            let head = names.count == 1 ? names[0] : "\(names.count) 个文件"
            return "\(head) · \(ByteFormat.string(byteCount))"
        case .collected(let batch):
            // The title already says how many batches there are.
            var parts: [String] = []
            if let count = batch.count { parts.append("这批 \(count) 条") }
            if let range = RelayFormat.range(batch.start, batch.end) { parts.append(range) }
            return parts.isEmpty ? nil : parts.joined(separator: " · ")
        case .failed(let message):
            return message
        }
    }

    private var hint: String? {
        switch model.state {
        case .collected(let batch):
            let next = "回微信多选下一段，再选「分批收集到微信流」"
            guard let anchor = batch.anchor else { return next }
            return "已复制这批最后一句「\(anchor)」\n回微信搜索它定位，从下一条接着多选\n再选「分批收集到微信流」"
        case .saved(_, _, let anchor?):
            return "正好 \(SearchAnchor.forwardLimit) 条，后面可能还有。已复制最后一句「\(anchor)」，回微信搜索它接着选，用「分批收集到微信流」分批存。"
        default:
            return nil
        }
    }
}
