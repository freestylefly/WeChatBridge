import Combine
import Foundation
import Sparkle

/// Owns update checking for the status menu and the 关于 pane.
///
/// Two channels share this front. A distribution build carries `SUFeedURL`
/// and Sparkle runs — its own scheduler, its own UI. A build without a feed
/// falls back to `LanzouUpdateChecker`, the 蓝奏云 share folder the project
/// actually ships through today; its results surface in the 关于 pane, which
/// `checkForUpdates()` opens on the way.
///
/// `swift run WeChatBridge` has no assembled Info.plist and therefore no
/// `SUFeedURL` either, so the 蓝奏云 fallback is what a development build
/// exercises.
///
/// Automatic checks are switched on in Info.plist rather than asked for: the
/// share extension starts this app in the background over WeChat, and a
/// permission prompt would otherwise be the first window a new user ever saw
/// from WeChatBridge. The fallback keeps that contract — its daily check is
/// silent too.
@MainActor
final class AppUpdater: ObservableObject {
    @Published private(set) var canCheckForUpdates = false

    private let controller: SPUStandardUpdaterController
    private let isConfigured: Bool
    private var canCheckObserver: AnyCancellable?
    private var stateObserver: AnyCancellable?

    /// The 蓝奏云 folder check. Always present; only consulted when this build
    /// carries no Sparkle feed.
    let lanzou: LanzouUpdateChecker

    /// Where a manual fallback check shows its progress and result: the 关于
    /// pane. The app delegate points this at `openMainWindow(.about)`.
    var showUpdatePane: (() -> Void)?

    private let defaults: UserDefaults
    static let automaticCheckKey = "UpdateCheckAutomatically"
    static let lastAutomaticCheckKey = "UpdateLastCheckAt"

    init(
        bundle: Bundle = .main,
        defaults: UserDefaults = .standard,
        lanzou: LanzouUpdateChecker? = nil
    ) {
        self.defaults = defaults
        self.lanzou = lanzou ?? LanzouUpdateChecker()
        isConfigured = bundle.object(forInfoDictionaryKey: "SUFeedURL") != nil
        controller = SPUStandardUpdaterController(
            startingUpdater: isConfigured,
            updaterDelegate: nil,
            userDriverDelegate: nil
        )
        defaults.register(defaults: [Self.automaticCheckKey: true])

        if isConfigured {
            canCheckForUpdates = controller.updater.canCheckForUpdates
            canCheckObserver = controller.updater
                .publisher(for: \.canCheckForUpdates)
                .receive(on: RunLoop.main)
                .removeDuplicates()
                .sink { [weak self] canCheck in
                    self?.canCheckForUpdates = canCheck
                }
        } else {
            canCheckForUpdates = true
            stateObserver = self.lanzou.$state
                .receive(on: RunLoop.main)
                .sink { [weak self] state in
                    self?.canCheckForUpdates = state != .checking
                    self?.objectWillChange.send()
                }
        }
    }

    /// Sparkle shows its own dialogs; the fallback's status lives in the 关于
    /// pane, which reads this.
    var usesSparkleFeed: Bool { isConfigured }
    var updateState: LanzouUpdateChecker.State { lanzou.state }
    var availableUpdate: LanzouUpdateChecker.Release? {
        isConfigured ? nil : lanzou.availableUpdate
    }

    /// The daily check, as the 通用 pane's switch. Reads false and ignores
    /// writes in an unconfigured build, which is the honest state of a
    /// `swift run` binary with no feed to check. The fallback persists the
    /// same switch for its own once-a-day pass.
    var automaticallyChecksForUpdates: Bool {
        get {
            guard isConfigured else {
                return defaults.bool(forKey: Self.automaticCheckKey)
            }
            return controller.updater.automaticallyChecksForUpdates
        }
        set {
            guard isConfigured else {
                defaults.set(newValue, forKey: Self.automaticCheckKey)
                objectWillChange.send()
                return
            }
            // Sparkle owns the value; the view only needs to know it moved.
            objectWillChange.send()
            controller.updater.automaticallyChecksForUpdates = newValue
        }
    }

    func checkForUpdates() {
        guard isConfigured else {
            showUpdatePane?()
            Task { await lanzou.check() }
            return
        }
        controller.checkForUpdates(nil)
    }

    /// The 关于 pane asks once per run — an idle state means this launch has
    /// not checked yet, while a settled state simply displays again.
    func checkForUpdatesIfIdle() {
        guard !isConfigured, lanzou.state == .idle else { return }
        Task { await lanzou.check() }
    }

    /// The once-a-day quiet check, called at launch. Sparkle schedules its own
    /// when a feed exists; the 蓝奏云 channel needs this loop instead. A hit
    /// stays silent — the status menu's 检查更新 item turns into a download
    /// link, but no window appears over WeChat uninvited.
    func checkForUpdatesInBackgroundIfDue() {
        guard !isConfigured, automaticallyChecksForUpdates, !lanzou.isChecking else { return }
        let lastCheck = defaults.object(forKey: Self.lastAutomaticCheckKey) as? Date ?? .distantPast
        guard Date().timeIntervalSince(lastCheck) >= 24 * 60 * 60 else { return }
        defaults.set(Date(), forKey: Self.lastAutomaticCheckKey)
        Task { await lanzou.check() }
    }
}
