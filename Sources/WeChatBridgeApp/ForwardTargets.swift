import AppKit
import WeChatBridgeCore
import SwiftUI
import UniformTypeIdentifiers

/// What is actually installed for one bundle identifier.
///
/// Resolving costs a Launch Services round trip, an `Info.plist` read and an
/// icon fetch; the settings pane asks for every row on every frame, so the
/// answer is remembered. Main-actor only, which is why the cache needs no lock.
@MainActor
struct InstalledApp {
    let bundleIdentifier: String
    /// Nil when the app is not on this Mac — the row then says 未安装 rather
    /// than pretending the target still works.
    let name: String?
    let icon: NSImage

    private nonisolated(unsafe) static var cache: [String: InstalledApp] = [:]

    var isInstalled: Bool { name != nil }

    static func lookup(_ bundleIdentifier: String) -> InstalledApp {
        if let hit = cache[bundleIdentifier] { return hit }
        let resolved = resolve(bundleIdentifier)
        // A miss is not cached: the user may be adding a target *because* they
        // are about to install the app, and a permanently remembered "missing"
        // would keep the row wrong for the rest of the session.
        if resolved.isInstalled { cache[bundleIdentifier] = resolved }
        return resolved
    }

    private static func resolve(_ bundleIdentifier: String) -> InstalledApp {
        guard let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleIdentifier) else {
            return InstalledApp(
                bundleIdentifier: bundleIdentifier,
                name: nil,
                icon: NSWorkspace.shared.icon(for: .applicationBundle)
            )
        }
        return InstalledApp(
            bundleIdentifier: bundleIdentifier,
            name: displayName(at: url),
            icon: NSWorkspace.shared.icon(forFile: url.path)
        )
    }

    /// The display name wins: a localised app shows one thing in Finder and
    /// another in `CFBundleName`, and Finder's is what the user recognises.
    static func displayName(at url: URL) -> String {
        let bundle = Bundle(url: url)
        return (bundle?.object(forInfoDictionaryKey: "CFBundleDisplayName") as? String)
            ?? (bundle?.object(forInfoDictionaryKey: "CFBundleName") as? String)
            ?? url.deletingPathExtension().lastPathComponent
    }
}

/// One entry of the 添加应用 ▸ 运行中的应用程序 submenu.
struct RunningApp: Identifiable {
    let id: String
    let name: String
    let icon: NSImage

    @MainActor
    static func current(excluding excluded: Set<String>) -> [RunningApp] {
        var seen = Set<String>()
        var result: [RunningApp] = []
        for application in NSWorkspace.shared.runningApplications {
            // Everything below `.regular` is a background agent or a menu bar
            // tool: it has no window to paste into, and listing them would turn
            // this menu into a process table.
            guard application.activationPolicy == .regular,
                  let identifier = application.bundleIdentifier,
                  !excluded.contains(identifier),
                  seen.insert(identifier).inserted
            else { continue }
            result.append(
                RunningApp(
                    id: identifier,
                    name: application.localizedName ?? identifier,
                    icon: menuIcon(for: application)
                )
            )
        }
        return result.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    /// A copy is resized, not the original: `NSRunningApplication.icon` hands
    /// back a shared instance, and setting its size reaches everywhere else that
    /// image is drawn.
    @MainActor
    private static func menuIcon(for application: NSRunningApplication) -> NSImage {
        let source = application.icon ?? NSWorkspace.shared.icon(for: .applicationBundle)
        guard let copy = source.copy() as? NSImage else { return source }
        copy.size = NSSize(width: 16, height: 16)
        return copy
    }
}

/// One row of a 发给 ▸ menu: a built-in entry or one of the user's own apps.
///
/// The two are the same gesture with a different destination, so both 发给 ▸
/// menus — 记录's SwiftUI one — build themselves from
/// this list instead of each deciding separately what belongs in it.
struct ForwardDestination: Identifiable {
    let action: ShareAction
    /// Nil for Codex and Claude, whose app the action already names.
    let target: ForwardTarget?

    var id: String { target?.bundleIdentifier ?? action.rawValue }
    /// The app, not the entry: inside a menu already titled 发给, 「发给 Codex」
    /// would say it twice.
    var title: String { target?.displayName ?? action.targetDisplayName }
    var isBuiltIn: Bool { target == nil }
}

/// The user's own list of forward destinations, and the only thing that writes
/// it.
///
/// A published copy of `ForwardTargetStore` for the settings pane, the target
/// picker and both 发给 ▸ menus. Icons are looked up live through
/// `InstalledApp`: nothing outside this process draws the list any more, so
/// nothing is pre-rendered into the group container.
@MainActor
final class ForwardTargets: ObservableObject {
    @Published private(set) var targets: [ForwardTarget] = []
    /// A row the user tried to add twice. Adding it again silently would look
    /// like the click did nothing; this makes the existing row answer instead.
    @Published private(set) var highlighted: String?

    private let store = ForwardTargetStore()
    private var highlightTask: Task<Void, Never>?

    init() {
        targets = store.load()
    }

    var isEmpty: Bool { targets.isEmpty }

    /// Built-ins first, then the user's own in the order they arranged them.
    /// The one-key entries are not special beyond being first: they are simply
    /// the two destinations WeChatBridge ships knowing about.
    var destinations: [ForwardDestination] {
        [
            ForwardDestination(action: .codex, target: nil),
            ForwardDestination(action: .claude, target: nil),
            ForwardDestination(action: .doubao, target: nil),
            ForwardDestination(action: .qwen, target: nil),
            ForwardDestination(action: .workBuddy, target: nil),
            ForwardDestination(action: .weSight, target: nil),
            ForwardDestination(action: .deepSeekHarness, target: nil),
            ForwardDestination(action: .obsidian, target: nil),
        ] + targets.map { ForwardDestination(action: .custom, target: $0) }
    }

    /// The order 「发送到自定义」 offers them in: last pick first, then the
    /// order the user arranged. The panel's top row is the one Return takes, so
    /// this is the whole of "the app you used last is one keystroke away".
    var orderedTargets: [ForwardTarget] {
        ForwardTargetStore.ordered(targets, lastUsed: store.lastUsedBundleIdentifier())
    }

    /// Never a target: WeChatBridge would paste into itself, and Finder has no text
    /// field to paste into.
    static let excludedBundleIdentifiers: Set<String> = [
        "com.apple.finder",
    ]

    /// Apps that are terminals, added with 只粘贴文件路径 already on: a
    /// terminal can never take a pasted file, so the checkbox would be the
    /// first thing the user had to find after adding one. A guess is only a
    /// default — the row's checkbox still decides.
    static let terminalBundleIdentifiers: Set<String> = [
        "com.apple.Terminal",
        "com.googlecode.iterm2",
        "dev.warp.Warp-Stable",
        "com.mitchellh.ghostty",
        "net.kovidgoyal.kitty",
        "org.alacritty",
        "com.github.wez.wezterm",
        "co.zeit.hyper",
        "org.tabby",
        "com.stablyai.orca",
    ]

    private var excluded: Set<String> {
        var identifiers = Self.excludedBundleIdentifiers
        if let own = Bundle.main.bundleIdentifier { identifiers.insert(own) }
        return identifiers
    }

    func runningApplications() -> [RunningApp] {
        RunningApp.current(excluding: excluded)
    }

    func contains(_ bundleIdentifier: String) -> Bool {
        targets.contains { $0.bundleIdentifier == bundleIdentifier }
    }

    // MARK: - Editing

    /// Appends, because the stored order is the order the panel and the menus
    /// show — a new app belongs at the end of the list the user arranged, not in
    /// the middle of it.
    func add(bundleIdentifier: String, displayName: String) {
        guard !excluded.contains(bundleIdentifier) else { return }
        guard !contains(bundleIdentifier) else {
            highlight(bundleIdentifier)
            return
        }
        targets.append(
            ForwardTarget(
                bundleIdentifier: bundleIdentifier,
                displayName: displayName,
                addedAt: Date(),
                pastesPathOnly: Self.terminalBundleIdentifiers.contains(bundleIdentifier)
            )
        )
        persist()
    }

    /// 只粘贴文件路径, flipped from the row's checkbox. The value is replaced
    /// whole because a target is a value; the identifier, name and date it was
    /// added carry over untouched.
    func setPastesPathOnly(_ pastesPathOnly: Bool, for target: ForwardTarget) {
        guard let index = targets.firstIndex(where: { $0.bundleIdentifier == target.bundleIdentifier }),
              targets[index].pastesPathOnly != pastesPathOnly
        else { return }
        let current = targets[index]
        targets[index] = ForwardTarget(
            bundleIdentifier: current.bundleIdentifier,
            displayName: current.displayName,
            addedAt: current.addedAt,
            pastesPathOnly: pastesPathOnly
        )
        persist()
    }

    /// The list's current answer for an app, or nil when the app is not on
    /// it. `ActionRunner` asks this at forward time rather than reading the
    /// arrival: the setting belongs to the row in 设置, not to the request,
    /// and an intent stamped by an older extension names the app without it.
    func pastesPathOnly(for bundleIdentifier: String) -> Bool? {
        targets.first { $0.bundleIdentifier == bundleIdentifier }?.pastesPathOnly
    }

    /// From an `.app` the user picked in Finder. A bundle with no identifier is
    /// not an app WeChatBridge can activate, so it is dropped rather than added as a
    /// row that could never work.
    func add(applicationAt url: URL) {
        guard let identifier = Bundle(url: url)?.bundleIdentifier else { return }
        add(bundleIdentifier: identifier, displayName: InstalledApp.displayName(at: url))
    }

    func remove(_ target: ForwardTarget) {
        targets.removeAll { $0.bundleIdentifier == target.bundleIdentifier }
        persist()
    }

    /// The stored order is the order everything shows, so a drag in the settings
    /// list reorders the share panel and both 发给 ▸ menus at once.
    func move(fromOffsets source: IndexSet, toOffset destination: Int) {
        targets.move(fromOffsets: source, toOffset: destination)
        persist()
    }

    private func persist() {
        store.save(targets)
    }

    private func highlight(_ bundleIdentifier: String) {
        highlightTask?.cancel()
        highlighted = bundleIdentifier
        highlightTask = Task { [weak self] in
            // Long enough to notice the row answer, short enough that it is not
            // still lit when the user looks back.
            try? await Task.sleep(nanoseconds: 1_200_000_000)
            guard !Task.isCancelled else { return }
            self?.highlighted = nil
        }
    }

    // MARK: - Use

    /// Remembered so the share panel opens on the app the user picked last, and
    /// Return takes it. A forward started from a WeChatBridge menu counts too: it is
    /// the same decision, made from a different surface.
    func recordUse(_ target: ForwardTarget) {
        store.recordLastUsed(target.bundleIdentifier)
    }
}
