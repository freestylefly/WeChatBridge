import AppKit
import Combine
import WeChatBridgeCore
import SwiftUI

/// A menu bar app with one on-demand window.
///
/// By default `LSUIElement` keeps WeChatBridge out of the Dock: it is a resident
/// receiver, and the share extension launches it in the background where a
/// bouncing Dock icon would be noise. Users can opt into a Dock icon in General.
@main
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    /// A plain AppKit entry. Every window is owned by an AppKit controller
    /// (`SettingsWindowController`, `OnboardingWindowController`), so the app
    /// has no SwiftUI scene to host: the former `App` shell kept a `Settings {
    /// EmptyView() }` placeholder only to satisfy `body`, and on ad-hoc
    /// self-made builds macOS state restoration resurrected that placeholder as
    /// a blank window on cold launch. With no scene there is nothing to
    /// restore. `app.run()` does not return before termination, so the local
    /// `delegate` keeps the app's `weak` delegate alive for the whole run.
    static func main() {
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.mainMenu = makeMainMenu()
        app.run()
    }

    /// The SwiftUI App runtime synthesized a default menu bar as a side effect
    /// of hosting scenes. A plain AppKit entry owns none, and without one key
    /// equivalents have nowhere to route: the settings window's text fields
    /// lose 撤销/拷贝/粘贴/全选, 关闭 stops closing windows, and 退出 loses ⌘Q
    /// (the status menu's ⌘Q only fires while that menu is open). This rebuilds
    /// the parts of that default the app actually relies on. Every item targets
    /// `nil`, so the responder chain decides what they do, and autoenabling
    /// keeps them dimmed when nothing answers — an accessory app's menu is
    /// consulted for key equivalents even though it never owns the menu bar.
    private static func makeMainMenu() -> NSMenu {
        let mainMenu = NSMenu()

        let appName = (Bundle.main.object(forInfoDictionaryKey: "CFBundleName") as? String) ?? "WeChatBridge"
        mainMenu.addItem(submenu(appName, items: [
            item(L10n.text("隐藏 WeChatBridge"), #selector(NSApplication.hide(_:)), "h"),
            item(L10n.text("隐藏其他"), #selector(NSApplication.hideOtherApplications(_:)), "h", modifiers: [.command, .option]),
            item(L10n.text("显示全部"), #selector(NSApplication.unhideAllApplications(_:)), ""),
            .separator(),
            item(L10n.text("退出 WeChatBridge"), #selector(NSApplication.terminate(_:)), "q")
        ]))
        mainMenu.addItem(submenu(L10n.text("编辑"), items: [
            // The edit actions are AppKit's standard first-responder selectors:
            // the responder chain, not this menu, decides who answers them.
            // `undo:` and `redo:` are declared on no public class, hence the raw
            // selectors; the rest anchor on `NSText` only to spell the selector,
            // because the importer hides `NSResponder.copy(_:)` behind
            // `NSObject.copy()`.
            item(L10n.text("撤销"), Selector(("undo:")), "z"),
            item(L10n.text("重做"), Selector(("redo:")), "Z"),
            .separator(),
            item(L10n.text("剪切"), #selector(NSText.cut(_:)), "x"),
            item(L10n.text("拷贝"), #selector(NSText.copy(_:)), "c"),
            item(L10n.text("粘贴"), #selector(NSText.paste(_:)), "v"),
            item(L10n.text("全选"), #selector(NSText.selectAll(_:)), "a")
        ]))
        mainMenu.addItem(submenu(L10n.text("窗口"), items: [
            item(L10n.text("最小化"), #selector(NSWindow.performMiniaturize(_:)), "m"),
            item(L10n.text("缩放"), #selector(NSWindow.performZoom(_:)), ""),
            item(L10n.text("关闭"), #selector(NSWindow.performClose(_:)), "w")
        ]))
        return mainMenu
    }

    private static func submenu(_ title: String, items: [NSMenuItem]) -> NSMenuItem {
        let menu = NSMenu(title: title)
        for entry in items { menu.addItem(entry) }
        let menuItem = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        menuItem.submenu = menu
        return menuItem
    }

    private static func item(
        _ title: String,
        _ action: Selector,
        _ key: String,
        modifiers: NSEvent.ModifierFlags = .command
    ) -> NSMenuItem {
        let menuItem = NSMenuItem(title: title, action: action, keyEquivalent: key)
        menuItem.keyEquivalentModifierMask = modifiers
        return menuItem
    }

    let preferences: Preferences
    let model: AppModel
    let loginItem = LoginItem()
    let authorization = AccessibilityAuthorization()
    let screenRecording = ScreenRecordingAuthorization()
    let settingsRouter = SettingsRouter()
    let forwardTargets = ForwardTargets()
    let skills = SkillLibrary()
    /// Started only in a real bundle — see `AppUpdater`.
    let updater = AppUpdater()
    private var actionRunner: ActionRunner?
    private var sceneShortcuts: SceneShortcutController?
    private var collectionCoordinator: CollectionCoordinator?
    private var statusItem: StatusItemController?
    private var cancellables = Set<AnyCancellable>()
    private var settingsWindow: SettingsWindowController?
    private var pendingSettingsTab: SettingsTab?
    private var onboardingWindow: OnboardingWindowController?

    /// The model reads the retention window straight off the preferences, so the
    /// two are built together rather than wired up later — a prune that ran
    /// before the preference arrived would use the wrong window exactly once,
    /// on the launch where it does the most damage.
    override init() {
        let preferences = Preferences()
        self.preferences = preferences
        model = AppModel(preferences: preferences)
        super.init()
    }

    /// Handed to the settings window so a pane can reach the action runner
    /// without holding it.
    var settingsActions: SettingsActions {
        SettingsActions(
            perform: { [weak self] action, target, urls in
                self?.forward(ArrivedBatch(action: action, target: target, urls: urls))
            },
            showEntries: { [weak self] in self?.openMainWindow(.entries) },
            // The counter is reset here, not only on 完成: a guide that was
            // re-run and then closed at 权限 leaves it at 2, and the next
            // 重新运行 would resume rather than run.
            openCollection: { [weak self] id, delivery in
                self?.collectionCoordinator?.show(id, delivery: delivery)
            },
            restartOnboarding: { [weak self] in
                self?.preferences.onboardingStep = 0
                self?.onboardingWindow?.show()
            }
        )
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        // Two copies would race for the same inbox. The one already running wins.
        let others = NSRunningApplication.runningApplications(
            withBundleIdentifier: Bundle.main.bundleIdentifier ?? ""
        ).filter { $0 != .current }
        if !others.isEmpty {
            NSApp.terminate(nil)
            return
        }

        applyActivationPolicy()
        preferences.$showInDock
            .dropFirst()
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.applyActivationPolicy() }
            .store(in: &cancellables)

        let sceneCoordinator = SceneCoordinator(preferences: preferences)
        let runner = ActionRunner(
            model: model,
            authorization: authorization,
            targets: forwardTargets,
            preferences: preferences,
            sceneCoordinator: sceneCoordinator,
            skills: skills
        )
        runner.openEntries = { [weak self] in self?.openMainWindow(.entries) }
        runner.openSkills = { [weak self] in self?.openMainWindow(.skills) }
        actionRunner = runner
        collectionCoordinator = CollectionCoordinator(model: model, targets: forwardTargets, preferences: preferences, runner: runner)
        runner.configureNotifications()
        sceneShortcuts = SceneShortcutController(preferences: preferences)

        // One place decides what an arriving batch means.
        model.didArrive
            .receive(on: RunLoop.main)
            .sink { [weak self] arrival in self?.forward(arrival) }
            .store(in: &cancellables)

        // The share extension has no screen of its own (ADR-0005). What it
        // could not do is said here, in the app, on the same capsule every
        // other failure uses.
        model.didFailToReceive
            .receive(on: RunLoop.main)
            .sink { [weak self, weak runner] failure in
                if failure.action == .collect { self?.collectionCoordinator?.importFailed(failure) }
                else { runner?.report(failure) }
            }
            .store(in: &cancellables)

        settingsWindow = SettingsWindowController(router: settingsRouter) { [unowned self] in
            SettingsView(
                model: model,
                preferences: preferences,
                loginItem: loginItem,
                authorization: authorization,
                screenRecording: screenRecording,
                router: settingsRouter,
                forwardTargets: forwardTargets,
                skills: skills,
                updater: updater,
                actions: settingsActions
            )
        }

        onboardingWindow = OnboardingWindowController { [unowned self] in
            OnboardingFlow(
                preferences: preferences,
                authorization: authorization,
                finish: { [weak self] in self?.finishOnboarding() }
            )
        }

        statusItem = StatusItemController(
            model: model, updater: updater,
            openSettings: { [weak self] tab in self?.openMainWindow(tab) }
        )
        updater.showUpdatePane = { [weak self] in self?.openMainWindow(.about) }
        updater.checkForUpdatesInBackgroundIfDue()
        model.start()
        model.reload()
        if let tab = pendingSettingsTab {
            pendingSettingsTab = nil
            openMainWindow(tab)
        } else if !preferences.onboardingCompleted, !Self.launchedInBackground {
            // A person's first launch — Finder, Launchpad, Spotlight — gets the
            // guide at once. Nothing else would happen with the default
            // accessory policy, so without this a fresh install put a menu bar
            // icon on screen and nothing that said what to do with it.
            onboardingWindow?.show()
        }
    }

    /// The share extension says so when it is the one starting the app
    /// (`LaunchArgument.background`): the user is in WeChat at that moment, and
    /// a window over it would undo the point of an extension with no screen.
    private static let launchedInBackground = CommandLine.arguments.contains(LaunchArgument.background)

    private func applyActivationPolicy() {
        NSApp.setActivationPolicy(preferences.showInDock ? .regular : .accessory)
    }

    /// First-run guidance waits for the user to actually bring WeChatBridge forward.
    /// The share extension launches this app in the background on purpose, so it
    /// must not throw a window over WeChat. `openMainWindow` carries the same
    /// first-run rule for the menu bar path.
    func applicationDidBecomeActive(_ notification: Notification) {
        loginItem.refresh()
        guard !preferences.onboardingCompleted else { return }
        onboardingWindow?.show()
    }

    /// Only finishing the guide counts as having done it. Closing its window at
    /// step 3 is not an answer, so it comes back on the next activation — and
    /// the step counter goes back to the start, because 重新运行设置向导 means
    /// run it, not resume it.
    ///
    /// 设置 opens as the guide closes. The guide only switched on entries and
    /// asked for one permission; a new user's next questions — scenes,
    /// Obsidian, history — all live there, and a window that simply vanished
    /// left them with a menu bar icon they had not yet learned to look for.
    private func finishOnboarding() {
        preferences.onboardingCompleted = true
        preferences.onboardingStep = 0
        onboardingWindow?.close()
        openMainWindow(.general)
    }

    /// One door for every forward, wherever it was asked for: an arriving batch
    /// or 记录. Remembering the target here rather than in `ActionRunner` keeps
    /// the runner ignorant of the user's list — it only ever needs the one app
    /// it is about to activate.
    private func forward(_ arrival: ArrivedBatch) {
        if let target = arrival.target { forwardTargets.recordUse(target) }
        actionRunner?.handle(arrival)
    }

    /// A `wechatbridge://` URL. Nothing in this tree sends one any more — the share
    /// panel that did is gone — but the scheme stays registered and routed as
    /// the app's front door onto a settings pane. `AppLink` explains why.
    func application(_ application: NSApplication, open urls: [URL]) {
        for url in urls where url.scheme == AppLink.scheme {
            // An unknown path still opens the window: the user asked for WeChatBridge,
            // and silence would look like the button did nothing.
            openMainWindow(AppLink.settingsPath(of: url).flatMap(SettingsTab.init(rawValue:)))
        }
    }

    /// Closing the window must not quit a menu bar app.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    /// A reopen only counts when a person performed it.
    ///
    /// Measured 2026-09-05 on the signed build: the share extension starts the
    /// app with `NSWorkspace.openApplication(activates: false)`, and AppKit
    /// delivers that to an *already running* app as a reopen — so every share
    /// into a running WeChatBridge put the 780 × 560 settings window on screen over
    /// WeChat. Window count went 1 → 2 on a bare `open -g`. The extension's
    /// reopen leaves the app inactive; a double click in Finder or a click on
    /// the Dock icon activates it first, which is the difference this reads.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows: Bool) -> Bool {
        if sender.isActive, !hasVisibleWindows { openMainWindow() }
        return true
    }

    /// Until the guide has been finished it is the app's front door, whatever
    /// pane was asked for.
    ///
    /// Not a preference for the guide over the settings window so much as a
    /// refusal to show both: `SettingsWindowController.show` activates the app,
    /// which fires `applicationDidBecomeActive` and opens the guide on a first
    /// run. Letting a named pane through would therefore put two windows on
    /// screen for one click, not one.
    func openMainWindow(_ tab: SettingsTab? = nil) {
        guard settingsWindow != nil else {
            pendingSettingsTab = tab ?? .general
            return
        }
        guard preferences.onboardingCompleted else {
            onboardingWindow?.show()
            return
        }
        settingsWindow?.show(tab)
    }
}
