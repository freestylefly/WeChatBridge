import AppKit
import WeChatBridgeCore

/// Fixed global shortcuts for the first nine enabled scenes.
///
/// They choose the scene for the next share instead of running the whole export
/// themselves, so the same gesture works whether the files arrive through the
/// Share menu or a later WeChat automation path.
@MainActor
final class SceneShortcutController {
    private static let keyCodes: [UInt16] = [18, 19, 20, 21, 23, 22, 26, 28, 25]
    private let preferences: Preferences
    private let toast = ToastPresenter()
    private var monitor: Any?

    init(preferences: Preferences) {
        self.preferences = preferences
        monitor = NSEvent.addGlobalMonitorForEvents(matching: .keyDown) { [weak self] event in
            Task { @MainActor in self?.handle(event) }
        }
    }

    private func handle(_ event: NSEvent) {
        guard !event.isARepeat else { return }
        let modifiers = event.modifierFlags
            .intersection(.deviceIndependentFlagsMask)
            .subtracting(.capsLock)
        guard modifiers == [.control, .option],
              let index = Self.keyCodes.firstIndex(of: event.keyCode) else { return }
        let scenes = preferences.scenes.enabledScenes
        guard scenes.indices.contains(index) else { return }

        let scene = scenes[index]
        preferences.selectSceneForNextForward(id: scene.id)
        toast.show(
            L10n.format("下次转发使用场景「%@」", scene.name),
            symbol: "command",
            tone: .neutral
        )
    }
}
