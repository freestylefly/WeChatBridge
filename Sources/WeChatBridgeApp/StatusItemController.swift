import AppKit
import WeChatBridgeCore

/// The menu bar item: the always-visible entry point to WeChatBridge.
///
/// Left click opens the main window. Right click and ⌃click open the menu.
@MainActor
final class StatusItemController: NSObject, NSMenuDelegate {
    /// Ten batches is about a screen of menu. Beyond that the submenu stops
    /// being a shortcut and 全部记录… is the better answer.
    private static let recentLimit = 10

    private let statusItem: NSStatusItem
    private let model: AppModel
    private let updater: AppUpdater
    private let openSettings: (SettingsTab) -> Void
    private let menu = NSMenu()

    init(
        model: AppModel,
        updater: AppUpdater,
        openSettings: @escaping (SettingsTab) -> Void
    ) {
        self.model = model
        self.updater = updater
        self.openSettings = openSettings
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        super.init()

        // Without this AppKit re-enables every item through the responder chain.
        menu.autoenablesItems = false
        menu.delegate = self
        statusItem.button?.target = self
        statusItem.button?.action = #selector(statusItemClicked)
        statusItem.button?.sendAction(on: [.leftMouseUp, .rightMouseUp])

        statusItem.button?.toolTip = L10n.text("微信流")
        let image = StatusGlyph.image
        image.accessibilityDescription = L10n.text("微信流")
        statusItem.button?.image = image
        statusItem.button?.setAccessibilityLabel(L10n.text("微信流"))
    }

    @objc private func statusItemClicked(_ sender: NSStatusBarButton) {
        let event = NSApp.currentEvent
        let opensMenu = event?.type == .rightMouseUp
            || event?.modifierFlags.contains(.control) == true

        if opensMenu {
            statusItem.menu = menu
            sender.performClick(nil)
            statusItem.menu = nil
        } else {
            openSettings(.general)
        }
    }

    // MARK: - Menu

    /// Rebuilt on every open rather than kept in sync: the history and the
    /// retention sweep change underneath it, and a menu that is only ever seen
    /// for a second is the cheapest thing in the app to rebuild.
    func menuNeedsUpdate(_ menu: NSMenu) {
        actionTargets.removeAll()
        menu.removeAllItems()

        if let collection = model.collectionLedger.current {
            menu.addItem(item(L10n.format("继续收集 · %d 批", collection.batchIDs.count)) { [weak self] in
                self?.model.didCollect.send(collection.id)
            })
            menu.addItem(.separator())
        }

        let recent = NSMenuItem(title: L10n.text("最近记录"), action: nil, keyEquivalent: "")
        recent.submenu = recentMenu()
        menu.addItem(recent)
        menu.addItem(.separator())

        menu.addItem(item(L10n.text("设置…"), key: ",") { [weak self] in self?.openSettings(.general) })
        menu.addItem(item(L10n.text("关于 WeChatBridge…")) { [weak self] in self?.openSettings(.about) })
        // Where a regular app keeps it: under the About item. A menu bar app
        // has no application menu, so the status menu is the only one it has.
        if let release = updater.availableUpdate {
            menu.addItem(item(L10n.format("下载新版本 %@…", release.versionLabel)) {
                AppLinks.open(release.pageURL.absoluteString)
            })
        } else {
            menu.addItem(item(L10n.text("检查更新…"), enabled: updater.canCheckForUpdates) { [weak self] in
                self?.updater.checkForUpdates()
            })
        }
        menu.addItem(.separator())
        menu.addItem(item(L10n.text("退出 WeChatBridge"), key: "q") { NSApp.terminate(nil) })
    }

    private func recentMenu() -> NSMenu {
        let submenu = NSMenu()
        submenu.autoenablesItems = false

        guard !model.batches.isEmpty else {
            submenu.addItem(item(L10n.text("暂无记录"), enabled: false) {})
            return submenu
        }

        let now = Date()
        for batch in model.batches.prefix(Self.recentLimit) {
            let entry = item(HistoryLabel.menuTitle(for: batch, now: now)) { [weak self] in
                self?.openSettings(.history)
            }
            submenu.addItem(entry)
        }

        submenu.addItem(.separator())
        submenu.addItem(item(L10n.text("全部记录…")) { [weak self] in self?.openSettings(.history) })
        submenu.addItem(item(L10n.text("清空记录"), enabled: model.hasDiscardableHistory) { [weak self] in
            self?.model.discardHistory()
        })
        return submenu
    }

    private func item(
        _ title: String,
        key: String = "",
        enabled: Bool = true,
        action: @escaping () -> Void
    ) -> NSMenuItem {
        let target = ActionTarget(action)
        actionTargets.append(target)
        let menuItem = NSMenuItem(title: title, action: #selector(ActionTarget.invoke), keyEquivalent: key)
        menuItem.keyEquivalentModifierMask = key.isEmpty ? [] : .command
        menuItem.target = target
        menuItem.isEnabled = enabled
        return menuItem
    }

    /// `NSMenuItem` does not retain its target, and the menu outlives the call
    /// that built it.
    private var actionTargets: [ActionTarget] = []

    private final class ActionTarget: NSObject {
        private let action: () -> Void
        init(_ action: @escaping () -> Void) { self.action = action }
        @objc func invoke() { action() }
    }
}
