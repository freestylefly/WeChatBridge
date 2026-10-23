import Foundation
import WeChatBridgeCore

/// Hears `InboxSignal` from the share extension while the app is running.
///
/// A Darwin notification carries no payload and is only delivered to a live
/// process, so this is an optimisation: the app also reloads whenever it
/// becomes active, which is what catches shares made while it was suspended.
final class InboxObserver {
    private let onChange: @MainActor () -> Void

    init(onChange: @escaping @MainActor () -> Void) {
        self.onChange = onChange
        CFNotificationCenterAddObserver(
            CFNotificationCenterGetDarwinNotifyCenter(),
            Unmanaged.passUnretained(self).toOpaque(),
            { _, observer, _, _, _ in
                guard let observer else { return }
                let me = Unmanaged<InboxObserver>.fromOpaque(observer).takeUnretainedValue()
                Task { @MainActor in me.onChange() }
            },
            InboxSignal.didChange.rawValue as CFString,
            nil,
            .deliverImmediately
        )
    }

    deinit {
        CFNotificationCenterRemoveEveryObserver(
            CFNotificationCenterGetDarwinNotifyCenter(),
            Unmanaged.passUnretained(self).toOpaque()
        )
    }
}
