import Foundation
import UserNotifications

/// 「分享后提醒我投递」: the share extension posts a notification for the batch
/// it just saved, and tapping it opens the app straight into delivery. This is
/// as close to one tap as iOS allows: an extension cannot open other apps,
/// start a Live Activity, or present a share sheet that lists them.
///
/// Compiled into both the app and the extension; they meet only through the
/// App Group defaults and the notification's userInfo.
enum QuickDeliver {
    static let enabledKey = "com.xiangming.wechatbridge.ios.quickDeliver"
    /// `[[id, name]]`, the first few targets as the app arranges them,
    /// written by the app because the extension has no target table.
    static let targetsKey = "com.xiangming.wechatbridge.ios.quickTargets"
    static let category = "relay.saved"
    static let batchKey = "batchID"
    static let targetKey = "targetID"
    /// An action's identifier is this plus a target id.
    static let actionPrefix = "deliver."
    /// Notification buttons, and the card menu's quick entries.
    static let maxTargets = 3

    struct Target: Equatable {
        let id: String
        let name: String
    }

    /// On unless the user turned it off.
    static func isEnabled(_ defaults: UserDefaults) -> Bool {
        defaults.object(forKey: enabledKey) as? Bool ?? true
    }

    static func targets(_ defaults: UserDefaults) -> [Target] {
        (defaults.array(forKey: targetsKey) as? [[String]] ?? []).compactMap { pair in
            pair.count == 2 ? Target(id: pair[0], name: pair[1]) : nil
        }
    }

    static func store(_ targets: [Target], in defaults: UserDefaults) {
        defaults.set(targets.prefix(maxTargets).map { [$0.id, $0.name] }, forKey: targetsKey)
    }

    static func content(batchID: UUID, head: String, targets: [Target]) -> UNMutableNotificationContent {
        let content = UNMutableNotificationContent()
        content.title = "已存入中转站"
        content.body = targets.first.map { "\(head) · 点一下投递到「\($0.name)」，长按换目标" } ?? "\(head) · 点一下去投递"
        content.categoryIdentifier = category
        content.userInfo = [batchKey: batchID.uuidString, targetKey: targets.first?.id ?? ""]
        return content
    }

    /// One action per recent target; each opens the app, which then shows
    /// the share sheet.
    static func makeCategory(targets: [Target]) -> UNNotificationCategory {
        UNNotificationCategory(
            identifier: category,
            actions: targets.map {
                UNNotificationAction(identifier: actionPrefix + $0.id, title: "投递到「\($0.name)」", options: .foreground)
            },
            intentIdentifiers: []
        )
    }

    /// Fire and forget; without permission the share has still succeeded.
    static func post(batchID: UUID, head: String, defaults: UserDefaults) async {
        guard isEnabled(defaults) else { return }
        let center = UNUserNotificationCenter.current()
        guard await center.notificationSettings().authorizationStatus == .authorized else { return }
        let targets = targets(defaults)
        center.setNotificationCategories([makeCategory(targets: targets)])
        let request = UNNotificationRequest(
            identifier: batchID.uuidString,
            content: content(batchID: batchID, head: head, targets: targets),
            trigger: nil
        )
        try? await center.add(request)
    }

    /// What a tap asks for: the batch, and the target (nil: open the panel).
    static func request(from response: UNNotificationResponse) -> (batchID: UUID, targetID: String?)? {
        let info = response.notification.request.content.userInfo
        guard let raw = info[batchKey] as? String, let batchID = UUID(uuidString: raw) else { return nil }
        if response.actionIdentifier.hasPrefix(actionPrefix) {
            return (batchID, String(response.actionIdentifier.dropFirst(actionPrefix.count)))
        }
        let target = info[targetKey] as? String
        return (batchID, target?.isEmpty == false ? target : nil)
    }
}
