import Foundation

/// A payload-free "there is something new in Ready" ping.
///
/// The inbox on disk is the source of truth; this only shortens the latency for
/// an app that is already running. A sandboxed process may only post a
/// distributed notification with a nil object and nil userInfo, which suits a
/// signal that deliberately carries no data.
public enum InboxSignal {
    public static let didChange = Notification.Name("com.xiangming.wechatbridge.inbox.didChange")

    public static func post() {
        DistributedNotificationCenter.default().postNotificationName(
            didChange,
            object: nil,
            userInfo: nil,
            deliverImmediately: true
        )
    }
}
