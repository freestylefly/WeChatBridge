import ActivityKit
import AppIntents
import Foundation
import UIKit

/// The Live Activity shown while a delivery is out in another app: the user
/// picks 豆包 in the share sheet, lands in 豆包, and the Dynamic Island keeps
/// a way back to the chat and its prompt.
///
/// Compiled into the app and the widget extension. The widget only draws it;
/// the app starts, updates and ends it, and runs its intents.
struct DeliveryActivityAttributes: ActivityAttributes {
    struct ContentState: Codable, Hashable {
        enum Phase: String, Codable {
            /// The share sheet is up.
            case choosing
            /// The share sheet reported the hand-off.
            case sent
        }

        var phase: Phase
        var hasPrompt: Bool
    }

    var batchID: String
    var title: String
    /// 「4 条消息 · 1 个附件」, or empty.
    var detail: String
    var targetID: String
    var targetName: String
}

extension DeliveryActivityAttributes {
    /// Opens the batch's detail page in the app.
    var detailURL: URL {
        URL(string: "wechatbridge://relay/\(batchID)")!
    }

    /// `Logo-<id>` in the shared asset catalog, when the target has one.
    var targetLogo: String? {
        ["chatgpt", "doubao", "qwen", "workbuddy", "claude", "obsidian"].contains(targetID) ? "Logo-\(targetID)" : nil
    }
}

extension DeliveryActivityAttributes.ContentState {
    var status: String {
        switch phase {
        case .choosing: hasPrompt ? "提示词已复制 · 在分享面板选择" : "在分享面板选择"
        case .sent: hasPrompt ? "已交给对方 · 提示词在剪贴板" : "已交给对方"
        }
    }
}

/// The prompt the island's 「复制提示词」 puts back, in case the clipboard
/// was overwritten in the other app. Intents run in the app's process, so
/// the app's own defaults are the right place.
enum DeliveryActivityPrompt {
    static let key = "com.xiangming.wechatbridge.ios.activityPrompt"
}

struct CopyDeliveryPromptIntent: LiveActivityIntent {
    static var title: LocalizedStringResource = "复制提示词"
    static var isDiscoverable = false

    func perform() async throws -> some IntentResult {
        if let prompt = UserDefaults.standard.string(forKey: DeliveryActivityPrompt.key), !prompt.isEmpty {
            await MainActor.run { UIPasteboard.general.string = prompt }
        }
        return .result()
    }
}

struct EndDeliveryActivityIntent: LiveActivityIntent {
    static var title: LocalizedStringResource = "结束投递"
    static var isDiscoverable = false

    func perform() async throws -> some IntentResult {
        for activity in Activity<DeliveryActivityAttributes>.activities {
            await activity.end(nil, dismissalPolicy: .immediate)
        }
        UserDefaults.standard.removeObject(forKey: DeliveryActivityPrompt.key)
        return .result()
    }
}
