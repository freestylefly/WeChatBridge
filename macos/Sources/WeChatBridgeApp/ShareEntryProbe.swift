import WeChatBridgeCore
import Foundation

/// Whether one Share-menu entry is switched on.
enum ShareEntryState: Sendable, Equatable {
    case enabled
    case disabled
    /// `pluginkit` has no record of this identifier, or could not be asked at
    /// all. The two collapse into one case on purpose: the user-visible remedy
    /// is the same sentence either way — install the app where Launch Services
    /// indexes it — and splitting them would only add a state the pane has
    /// nothing different to say about.
    case unregistered
}

/// Reads and writes the user's choice for each of WeChatBridge's Share-menu entries.
///
/// There is no API for this. The state lives in `pkd`, and `/usr/bin/pluginkit`
/// is the only public way to reach it: `-m -i <id>` prints the entry flagged
/// `+` (on) or `-`/`!` (off), and `-e use|ignore -i <id>` is exactly what the
/// switch in System Settings → General → Login Items & Extensions → Sharing
/// writes.
///
/// This is why the app ships unsandboxed. Measured on the signed
/// `dist/WeChatBridge.app` (2026-09-05, macOS 15.7): with `com.apple.security.app-sandbox`
/// every call — reads included — exits 1 with an empty stdout and
/// `match: unauthorized discovery flag (PKDiscoverAll)` on stderr, because pkd
/// refuses a sandboxed client. Without the entitlement the same calls exit 0,
/// `-m` prints `+    com.xiangming.wechatbridge.ShareCodex(0.1.0)`, and an `-e ignore`
/// flips the next read to `-`. See ADR-0006's 取舍 section.
@MainActor
final class ShareEntryProbe: ObservableObject {
    /// Long enough for a cold `pkd` to answer — a warm one replies in a few
    /// milliseconds — and short enough that a wedged child cannot leave a row
    /// spinning for the rest of the session.
    private nonisolated static let timeout: TimeInterval = 3

    /// Absent means "not read yet", which the pane draws as a spinner. An entry
    /// that has never been probed must not borrow the appearance of one that
    /// answered.
    @Published private(set) var states: [ShareAction: ShareEntryState] = [:]
    /// Entries whose election is still in flight.
    @Published private(set) var switching: Set<ShareAction> = []
    /// What the switch shows while its election is in flight: the value the
    /// user just asked for. The read-back in `setEnabled` replaces it with
    /// what pkd actually did, so a refused election animates straight back.
    @Published private(set) var requested: [ShareAction: Bool] = [:]
    private var isRefreshing = false
    @Published var enablingFailure: String?
    private let isAppInstalled: @MainActor (String) -> Bool
    private let customTargets: () -> [ForwardTarget]

    init(
        isAppInstalled: @escaping @MainActor (String) -> Bool = { InstalledApp.isInstalledNow($0) },
        customTargets: @escaping () -> [ForwardTarget] = { ForwardTargetStore().load() }
    ) {
        self.isAppInstalled = isAppInstalled
        self.customTargets = customTargets
    }

    func state(of action: ShareAction) -> ShareEntryState? { states[action] }

    func isSwitching(_ action: ShareAction) -> Bool { switching.contains(action) }

    /// The position the switch should be drawn in right now.
    func isOn(_ action: ShareAction) -> Bool {
        requested[action] ?? (states[action] == .enabled)
    }

    var hasUnregisteredEntry: Bool { states.values.contains(.unregistered) }

    /// Re-reads every entry. Called when the pane appears and again on every
    /// activation: the entries can still be changed in System Settings, and the
    /// user comes straight back afterwards.
    func refresh() {
        // Overwriting a row mid-election would show the value the switch is
        // being moved away from.
        guard !isRefreshing, switching.isEmpty else { return }
        guard let base = Bundle.main.bundleIdentifier else {
            // No bundle identifier means no bundle: this is a `swift run`
            // binary, which installs no appex for pkd to have heard of.
            states = Dictionary(uniqueKeysWithValues: ShareAction.allCases.map { ($0, .unregistered) })
            return
        }
        isRefreshing = true
        let entries = ShareAction.allCases.map { ($0, "\(base).\($0.bundleIdentifierSuffix)") }
        Self.queue.async {
            var probed: [ShareAction: ShareEntryState] = [:]
            for (action, identifier) in entries {
                probed[action] = Self.probe(identifier)
            }
            Task { @MainActor [probed] in
                self.states = probed
                self.isRefreshing = false
            }
        }
    }

    /// Switches one entry on or off, then reads back what actually happened.
    ///
    /// The switch moves the moment it is clicked; a second click before the
    /// read-back lands is ignored here rather than shown as busy. `pluginkit
    /// -e` exits 0 whether or not pkd had anything to elect, so the fresh `-m`
    /// is still the only answer that counts; the first build hid the switch
    /// behind a spinner until then, which threw away the one animation a
    /// switch has — the user noticed (2026-09-06).
    func setEnabled(_ enabled: Bool, for action: ShareAction) {
        guard !switching.contains(action) else { return }
        enablingFailure = nil
        if enabled, let failure = installationFailure(for: action) {
            enablingFailure = failure
            return
        }
        guard !switching.contains(action), let base = Bundle.main.bundleIdentifier else { return }
        let identifier = "\(base).\(action.bundleIdentifierSuffix)"
        switching.insert(action)
        requested[action] = enabled
        Self.queue.async {
            _ = Self.run(["-e", enabled ? "use" : "ignore", "-i", identifier])
            let state = Self.probe(identifier)
            Task { @MainActor in
                self.states[action] = state
                self.requested[action] = nil
                self.switching.remove(action)
            }
        }
    }

    func installationFailure(for action: ShareAction) -> String? {
        if let identifier = action.targetBundleIdentifier, !isAppInstalled(identifier) {
            return L10n.format("请先安装 %@，再开启此入口。", action.targetDisplayName)
        }
        if action == .custom && !customTargets().contains(where: { isAppInstalled($0.bundleIdentifier) }) {
            return L10n.text("请先添加至少一个已安装的应用，再开启自定义入口。")
        }
        return nil
    }

    // MARK: - The tool

    /// A real thread, not the cooperative pool.
    ///
    /// `run` blocks on a `DispatchSemaphore`, and a blocked cooperative thread
    /// is gone until it unblocks — the runtime cannot reclaim it. `refresh`
    /// probes nine entries in a row, so one cold `pkd` could park a pool thread
    /// for 15 s, and flipping several switches quickly parks more. On a
    /// four-core Mac that is most of the pool, and the `Task.sleep` loop
    /// `AutoPaste` uses to wait for the target app to come frontmost has a
    /// wall-clock deadline: it would expire waiting for a thread and turn a good
    /// forward into a 「没有切到前台」 failure. Concurrent, because a switch
    /// flipped during a refresh must not queue behind nine reads.
    private nonisolated static let queue = DispatchQueue(
        label: "com.xiangming.wechatbridge.share-entry-probe",
        qos: .userInitiated,
        attributes: .concurrent
    )

    /// Blocking; called only from `queue`.
    private nonisolated static func probe(_ identifier: String) -> ShareEntryState {
        guard let result = run(["-m", "-i", identifier]), result.status == 0 else {
            return .unregistered
        }
        return state(fromOutput: result.text, identifier: identifier)
    }

    private struct ToolResult {
        let status: Int32
        let text: String
    }

    private nonisolated static func run(_ arguments: [String]) -> ToolResult? {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/pluginkit")
        process.arguments = arguments
        let pipe = Pipe()
        process.standardOutput = pipe
        // pluginkit's stderr is diagnostics for a human at a terminal; the exit
        // status is what this code acts on.
        process.standardError = FileHandle.nullDevice

        let finished = DispatchSemaphore(value: 0)
        process.terminationHandler = { _ in finished.signal() }
        do {
            try process.run()
        } catch {
            return nil
        }

        // Waiting before reading would deadlock on a child that fills the 64 KB
        // pipe buffer; `-i` narrows the output to one line, and the timeout is
        // the backstop if that ever stops being true.
        guard finished.wait(timeout: .now() + timeout) == .success else {
            process.terminate()
            return nil
        }
        let data = (try? pipe.fileHandleForReading.readToEnd()) ?? Data()
        return ToolResult(status: process.terminationStatus, text: String(decoding: data, as: UTF8.self))
    }

    /// `pluginkit` prints one line per match, flagged in the first column:
    /// `+` elected on, `-` elected off, `!` disabled by the system, `?` unknown
    /// — and **blank**, which is what a freshly installed extension reads as:
    /// registered, never elected either way.
    ///
    /// Blank is off, and that was measured rather than assumed. 2026-09-05, on
    /// the signed build with the new ShareCustom appex installed: a real
    /// `NSSharingServicePicker` over a .zip listed exactly the entries pkd
    /// flagged `+` — Freeform, Simulator, Shortcuts, Dropover, LocalSend,
    /// 发给 Codex. Every blank-flagged extension on this Mac was
    /// absent from that sheet, ShareCustom included, alongside WeChat's own and
    /// Telegram's. `pluginkit -e use` on a blank entry is what puts it there.
    ///
    /// But blank is *not* `.unregistered`: pkd knows this extension and the
    /// switch works on it. Reading it as unregistered — which this did until the
    /// first entry made it visible — showed 未注册 and a dead switch beside an
    /// entry one election away from working, and told the user to reinstall an
    /// app that was installed correctly.
    ///
    /// Only a line naming this identifier counts. No such line means pkd has
    /// never heard of the extension — a `swift run` binary, or an app that is
    /// not installed where Launch Services looks.
    nonisolated static func state(fromOutput text: String, identifier: String) -> ShareEntryState {
        for line in text.split(whereSeparator: \.isNewline) where line.contains(identifier) {
            // Only `+` is in the sheet. `-` elected off, `!` disabled by the
            // system, `?` unknown and a blank column for never elected are one
            // answer as far as the user is concerned: not there, and one
            // `-e use` away from being there.
            return line.first == "+" ? .enabled : .disabled
        }
        return .unregistered
    }
}
