import Foundation

/// A payload-free "there is something new in Ready" ping.
///
/// The inbox on disk is the source of truth; this only shortens the latency for
/// an app that is already running. A sandboxed process may only post a
/// distributed notification with a nil object and nil userInfo, which suits a
/// signal that deliberately carries no data.
public enum InboxSignal {
    public static let didChange = Notification.Name("com.xiangming.wechatbridge.inbox.didChange")

    /// iOS has no distributed notifications; the Darwin notify center is the
    /// payload-free equivalent there, and it reaches across the app group too.
    public static func post() {
        #if os(macOS)
        DistributedNotificationCenter.default().postNotificationName(
            didChange,
            object: nil,
            userInfo: nil,
            deliverImmediately: true
        )
        #else
        CFNotificationCenterPostNotification(
            CFNotificationCenterGetDarwinNotifyCenter(),
            CFNotificationName(didChange.rawValue as CFString),
            nil,
            nil,
            true
        )
        #endif
    }
}
