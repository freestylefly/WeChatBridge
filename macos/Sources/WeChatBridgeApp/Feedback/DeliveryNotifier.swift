import AppKit
import UserNotifications
import WeChatBridgeCore

/// The registration surface lets startup wiring be checked without asking the
/// system notification service from an unbundled test runner.
protocol DeliveryNotificationCenter: AnyObject {
    var delegate: UNUserNotificationCenterDelegate? { get set }
    func setNotificationCategories(_ categories: Set<UNNotificationCategory>)
    func requestAuthorization(options: UNAuthorizationOptions) async throws -> Bool
    func add(_ request: UNNotificationRequest) async throws
}

extension UNUserNotificationCenter: DeliveryNotificationCenter {}

/// The success report for the knowledge destinations: a system notification,
/// not a corner capsule.
///
/// A knowledge delivery always happens with WeChatBridge in the background —
/// the share extension triggered it — and macOS will not render a background
/// app's own windows: the capsule's `show` ran, its frame was right,
/// `orderFrontRegardless` and a hard `NSApp.activate(true)` were tried, and
/// nothing appeared. The notification centre is a separate process and shows
/// the banner whatever owns the foreground; its action buttons carry the jump
/// to what just landed — 「在访达中显示」 for 「沉淀到文件夹」, 「打开笔记」 for
/// 「沉淀到 Obsidian」.
///
/// Obsidian used to open the note itself the moment it landed. That reached
/// for the foreground on every delivery whether the user wanted it or not,
/// and it raced Obsidian's own indexing of the brand-new file — the
/// 「找不到文件」 of Issue #17. Both knowledge destinations now behave alike:
/// quiet on success, with the one-tap way in sitting on the notification.
///
/// Failures keep the toast (`ToastPresenter`): they usually arrive while the
/// user is working in WeChatBridge's own settings window, where a self-drawn
/// capsule does appear.
@MainActor
final class DeliveryNotifier: NSObject, UNUserNotificationCenterDelegate {
    nonisolated static let folderCategoryIdentifier = "folderDelivery"
    nonisolated static let revealActionIdentifier = "revealInFinder"
    nonisolated static let obsidianCategoryIdentifier = "obsidianDelivery"
    nonisolated static let openNoteActionIdentifier = "openInObsidian"

    /// The userInfo keys that carry what an action replays. Plain strings, not
    /// anything richer: userInfo crosses into the notification centre's own
    /// storage and back, and strings make that round trip lossless. A folder
    /// notification carries absolute note paths for Finder; an Obsidian one
    /// carries the vault name and vault-relative paths — the shape the
    /// `obsidian://open` scheme asks for.
    private nonisolated static let revealPathsKey = "revealPaths"
    private nonisolated static let obsidianVaultKey = "obsidianVault"
    private nonisolated static let obsidianFilesKey = "obsidianFiles"

    private var isConfigured = false
    private let providedCenter: (any DeliveryNotificationCenter)?
    private var center: any DeliveryNotificationCenter {
        providedCenter ?? UNUserNotificationCenter.current()
    }

    init(center: (any DeliveryNotificationCenter)? = nil) {
        providedCenter = center
        super.init()
    }

    /// The folder delivery's notice: `folderName` lands in the title, `notes`
    /// come back when 「在访达中显示」 is clicked.
    func notify(savedTo folderName: String, revealing notes: [URL]) async {
        await add(
            title: L10n.format("已保存到 %@", folderName),
            category: Self.folderCategoryIdentifier,
            userInfo: Self.userInfo(forRevealing: notes)
        )
    }

    /// The Obsidian delivery's notice. The vault-relative paths are worked out
    /// here, at the only moment the vault path is at hand, so that 「打开笔记」
    /// can rebuild its URL however much later it is clicked without reading
    /// any preferences. A note that somehow landed outside the vault has no
    /// obsidian URL to open, and a notification whose button does nothing is
    /// worse than silence, so such a delivery stays quiet — the old auto-open
    /// skipped those notes the same way.
    func notify(savedToVault vaultPath: String, notes: [URL]) async {
        let vault = URL(fileURLWithPath: vaultPath, isDirectory: true).standardizedFileURL
        let prefix = vault.path.hasSuffix("/") ? vault.path : vault.path + "/"
        let files = notes.compactMap { note -> String? in
            let path = note.standardizedFileURL.path
            guard path.hasPrefix(prefix) else { return nil }
            return String(path.dropFirst(prefix.count))
        }
        guard !files.isEmpty else { return }
        await add(
            title: L10n.format("已保存到 %@", vault.lastPathComponent),
            category: Self.obsidianCategoryIdentifier,
            userInfo: Self.userInfo(forOpeningIn: vault.lastPathComponent, files: files)
        )
    }

    // MARK: - Adding a notice

    /// Asking for permission here — not at launch — means the prompt appears
    /// at the first delivery, when the user has a reason to answer it;
    /// `requestAuthorization` only prompts while the choice is undecided, so
    /// this is silent from the second delivery on. A refusal also returns
    /// immediately and quietly: the delivery itself succeeded, and the notice
    /// was a bonus — not something to escalate into a failure report.
    ///
    /// The authorization prompt can hold this call until the user answers it,
    /// which holds the delivery queue behind it — harmless, because the modal
    /// prompt has the user's attention anyway, and it never appears again once
    /// answered.
    private func add(title: String, category: String, userInfo: [AnyHashable: Any]) async {
        configure()

        let center = self.center
        guard (try? await center.requestAuthorization(options: [.alert])) == true else { return }

        let content = UNMutableNotificationContent()
        // No sound asked for, and only `.alert` in the authorization: this is
        // an "it worked" banner, not an alarm; its visual presence is the
        // whole message.
        content.title = title
        content.categoryIdentifier = category
        content.userInfo = userInfo
        try? await center.add(
            UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
        )
    }

    // MARK: - Action round trips

    /// The encode side of the folder notification's userInfo.
    nonisolated static func userInfo(forRevealing notes: [URL]) -> [AnyHashable: Any] {
        [revealPathsKey: notes.map(\.path)]
    }

    /// The decode side: anything unexpected — a missing key, a foreign type —
    /// decodes to nothing rather than crashing in a notification callback.
    nonisolated static func revealPaths(from userInfo: [AnyHashable: Any]) -> [URL] {
        (userInfo[revealPathsKey] as? [String])?.map { URL(fileURLWithPath: $0) } ?? []
    }

    /// The encode side of the Obsidian notification's userInfo.
    nonisolated static func userInfo(
        forOpeningIn vault: String,
        files: [String]
    ) -> [AnyHashable: Any] {
        [obsidianVaultKey: vault, obsidianFilesKey: files]
    }

    /// The decode side of the Obsidian notification's userInfo: the
    /// `obsidian://open` URLs for every note, ready to hand to the workspace.
    nonisolated static func obsidianURLs(from userInfo: [AnyHashable: Any]) -> [URL] {
        guard let vault = userInfo[obsidianVaultKey] as? String,
              let files = userInfo[obsidianFilesKey] as? [String]
        else { return [] }
        return files.compactMap { obsidianURL(vault: vault, file: $0) }
    }

    /// The `obsidian://open` URL for one vault-relative note. `URLComponents`
    /// percent-encodes the query, which is what lets vault and file names
    /// carrying spaces, CJK, or the reserved 「?」「#」「&」 survive the handoff
    /// to Obsidian.
    nonisolated static func obsidianURL(vault: String, file: String) -> URL? {
        var components = URLComponents()
        components.scheme = "obsidian"
        components.host = "open"
        components.queryItems = [
            URLQueryItem(name: "vault", value: vault),
            URLQueryItem(name: "file", value: file),
        ]
        return components.url
    }

    // MARK: - UNUserNotificationCenterDelegate

    /// Register while the application launches so actions on retained folder
    /// and Obsidian notifications work before another note is saved.
    func configure() {
        guard !isConfigured else { return }
        isConfigured = true
        let center = self.center
        center.setNotificationCategories([
            UNNotificationCategory(
                identifier: Self.folderCategoryIdentifier,
                actions: [
                    UNNotificationAction(
                        identifier: Self.revealActionIdentifier,
                        title: L10n.text("在访达中显示")
                    )
                ],
                intentIdentifiers: []
            ),
            UNNotificationCategory(
                identifier: Self.obsidianCategoryIdentifier,
                actions: [
                    UNNotificationAction(
                        identifier: Self.openNoteActionIdentifier,
                        title: L10n.text("打开笔记")
                    )
                ],
                intentIdentifiers: []
            ),
        ])
        // The centre holds its delegate weakly; `ActionRunner` owns this
        // notifier for the app's lifetime, so the callback has somewhere to
        // land however much later the button is clicked.
        center.delegate = self
    }

    /// Both the buttons and a bare click on the banner mean "take me there";
    /// a dismissal does not. Which "there" is decided by what the notification
    /// carries, not by which action fired — the decode sides answer that, and
    /// anything unexpected decodes to nothing. The callback can arrive off the
    /// main thread.
    nonisolated func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        didReceive response: UNNotificationResponse,
        withCompletionHandler completionHandler: @escaping () -> Void
    ) {
        guard response.actionIdentifier == Self.revealActionIdentifier
            || response.actionIdentifier == Self.openNoteActionIdentifier
            || response.actionIdentifier == UNNotificationDefaultActionIdentifier
        else {
            completionHandler()
            return
        }
        let userInfo = response.notification.request.content.userInfo
        Task { @MainActor in
            let notes = Self.revealPaths(from: userInfo)
            if !notes.isEmpty {
                NSWorkspace.shared.activateFileViewerSelecting(notes)
            } else {
                for url in Self.obsidianURLs(from: userInfo) {
                    NSWorkspace.shared.open(url)
                }
            }
            completionHandler()
        }
    }

    /// A foreground app gets no banner unless it opts in — and a delivery can
    /// be replayed from WeChatBridge's own 记录 window while it is frontmost.
    nonisolated func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        willPresent notification: UNNotification,
        withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void
    ) {
        completionHandler([.banner, .list])
    }
}
