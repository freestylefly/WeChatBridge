import SwiftUI
import WeChatBridgeCore

@MainActor
final class ReceiptModel: ObservableObject {
    enum State: Equatable {
        case saving
        case saved(names: [String], byteCount: Int64)
        case failed(String)
    }

    @Published var state: State = .saving
}

/// One small card over the host app. Message counts are deliberately absent:
/// they would mean reading the whole archive in a process with a tight memory
/// ceiling, and the relay shows them a moment later anyway.
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
        case .failed:
            Image(systemName: "exclamationmark.triangle.fill").font(.system(size: 40)).foregroundStyle(.orange)
        }
    }

    private var title: String {
        switch model.state {
        case .saving: "正在存入…"
        case .saved: "已存入中转站"
        case .failed: "没有存入"
        }
    }

    private var detail: String? {
        switch model.state {
        case .saving:
            return nil
        case .saved(let names, let byteCount):
            let head = names.count == 1 ? names[0] : "\(names.count) 个文件"
            return "\(head) · \(ByteFormat.string(byteCount))"
        case .failed(let message):
            return message
        }
    }
}
