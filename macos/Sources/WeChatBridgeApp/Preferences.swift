import WeChatBridgeCore
import Foundation

/// A scene chosen by a global shortcut, waiting for the next share to consume it.
struct PendingSceneSelection: Codable, Sendable, Hashable {
    var sceneID: String
    var selectedAt: Date
}

/// One Dock switch and one retention window.
/// Everything else about WeChatBridge's state lives in the shared inbox on disk, where
/// both processes can see it.
@MainActor
final class Preferences: ObservableObject {
    private enum Key {
        static let onboardingCompleted = "com.xiangming.wechatbridge.onboardingCompleted"
        /// The one key here without the `com.xiangming.wechatbridge.` prefix. It is the name the
        /// reference implementation uses and the name the acceptance check for
        /// §11.2 reads, and a progress counter is worth less than the confusion
        /// of two spellings of it.
        static let onboardingStep = "onboardingStep"
        static let historyRetentionDays = "com.xiangming.wechatbridge.historyRetentionDays"
        static let showInDock = "com.xiangming.wechatbridge.showInDock"
        /// Kept only as the migration source for the old attached-prompt blob.
        static let legacyPrompt = "com.xiangming.wechatbridge.attachedPrompt"
        static let scenes = "com.xiangming.wechatbridge.scenes.v3"
        static let scenesV2 = "com.xiangming.wechatbridge.scenes.v2"
        static let askOnFirstScene = "com.xiangming.wechatbridge.askOnFirstScene"
        static let groupMemory = "com.xiangming.wechatbridge.groupMemory.v1"
        static let pendingScene = "com.xiangming.wechatbridge.pendingScene.v1"
        static let obsidianVaultPath = "com.xiangming.wechatbridge.obsidianVaultPath"
        static let obsidianSubfolder = "com.xiangming.wechatbridge.obsidianSubfolder"
        static let folderDeliveryPath = "com.xiangming.wechatbridge.folderDeliveryPath"
        static let folderDeliverySubfolder = "com.xiangming.wechatbridge.folderDeliverySubfolder"
    }

    /// A week: long enough that last Friday's chat export is still there on
    /// Monday, short enough that the group container does not quietly become the
    /// user's archive of every file they ever forwarded.
    static let defaultHistoryRetentionDays = 7

    /// 「沉淀到文件夹」 needs somewhere to land on a fresh install before the
    /// user has opened 设置, and ~/Downloads is the one folder every Mac has.
    static var defaultFolderDeliveryPath: String {
        FileManager.default
            .homeDirectoryForCurrentUser
            .appendingPathComponent("Downloads", isDirectory: true)
            .path
    }

    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        onboardingCompleted = defaults.bool(forKey: Key.onboardingCompleted)
        onboardingStep = defaults.object(forKey: Key.onboardingStep) as? Int ?? 0
        // `object(forKey:)` distinguishes "never set" from "set to 0"; a plain
        // `integer(forKey:)` would silently turn a fresh install into "从不".
        historyRetentionDays = defaults.object(forKey: Key.historyRetentionDays) as? Int
            ?? Self.defaultHistoryRetentionDays
        showInDock = defaults.object(forKey: Key.showInDock) as? Bool ?? false
        // New installs get the three starter scenes. Existing installs are
        // migrated from the old prompt blob once, without changing the new
        // well-known starter ids.
        scenes = defaults.data(forKey: Key.scenes)
            .flatMap { try? JSONDecoder().decode(SceneSettings.self, from: $0) }
            ?? defaults.data(forKey: Key.scenesV2)
                .flatMap { try? JSONDecoder().decode(SceneSettings.self, from: $0) }
            ?? defaults.data(forKey: Key.legacyPrompt)
                .flatMap { try? JSONDecoder().decode(SceneSettings.self, from: $0) }
            ?? SceneSettings.makeDefault()
        askOnFirstScene = defaults.object(forKey: Key.askOnFirstScene) as? Bool ?? true
        groupMemory = defaults.data(forKey: Key.groupMemory)
            .flatMap { try? JSONDecoder().decode([String: GroupMemory].self, from: $0) } ?? [:]
        pendingScene = defaults.data(forKey: Key.pendingScene)
            .flatMap { try? JSONDecoder().decode(PendingSceneSelection.self, from: $0) }
        obsidianVaultPath = defaults.string(forKey: Key.obsidianVaultPath)
        obsidianSubfolder = defaults.string(forKey: Key.obsidianSubfolder) ?? "微信流"
        folderDeliveryPath = defaults.string(forKey: Key.folderDeliveryPath)
            ?? Self.defaultFolderDeliveryPath
        folderDeliverySubfolder = defaults.string(forKey: Key.folderDeliverySubfolder) ?? ""
    }

    /// Set only by finishing the guide. Closing its window half way through is
    /// not an answer, so the guide comes back on the next activation and picks
    /// up at `onboardingStep`.
    @Published var onboardingCompleted: Bool {
        didSet { defaults.set(onboardingCompleted, forKey: Key.onboardingCompleted) }
    }

    /// Which step of the guide is on screen, written on every step change.
    ///
    /// The permission step sends the user into System Settings, and macOS is
    /// free to hand focus back — or not — whenever it likes; without this, a
    /// user who came back an hour later would start again at step one.
    @Published var onboardingStep: Int {
        didSet { defaults.set(onboardingStep, forKey: Key.onboardingStep) }
    }

    /// 0 means "从不".
    @Published var historyRetentionDays: Int {
        didSet { defaults.set(historyRetentionDays, forKey: Key.historyRetentionDays) }
    }

    /// The app is an accessory by default so a background launch from a share
    /// extension does not bounce a Dock icon. Users who want one can opt in.
    @Published var showInDock: Bool {
        didSet { defaults.set(showInDock, forKey: Key.showInDock) }
    }

    /// The scene library and the switch for each surface. Imported packages are
    /// stored here; group facts live separately because they survive deleting a
    /// scene or clearing history.
    @Published var scenes: SceneSettings {
        didSet {
            guard let data = try? JSONEncoder().encode(scenes) else { return }
            defaults.set(data, forKey: Key.scenes)
        }
    }

    /// A group with no memory asks once and keeps the answer. Turning this off
    /// uses keyword/fingerprint/default matching without interrupting a share.
    @Published var askOnFirstScene: Bool {
        didSet { defaults.set(askOnFirstScene, forKey: Key.askOnFirstScene) }
    }

    @Published var groupMemory: [String: GroupMemory] {
        didSet {
            guard let data = try? JSONEncoder().encode(groupMemory) else { return }
            defaults.set(data, forKey: Key.groupMemory)
        }
    }

    @Published private(set) var pendingScene: PendingSceneSelection? {
        didSet {
            guard let pendingScene, let data = try? JSONEncoder().encode(pendingScene) else {
                defaults.removeObject(forKey: Key.pendingScene)
                return
            }
            defaults.set(data, forKey: Key.pendingScene)
        }
    }

    @Published var obsidianVaultPath: String? {
        didSet { defaults.set(obsidianVaultPath, forKey: Key.obsidianVaultPath) }
    }

    @Published var obsidianSubfolder: String {
        didSet { defaults.set(obsidianSubfolder, forKey: Key.obsidianSubfolder) }
    }

    /// Where 「沉淀到文件夹」 lands its notes. A real path, never `~`: the
    /// folder entry ships working out of the box, and the tilde would have to
    /// be re-expanded on every read for no gain.
    @Published var folderDeliveryPath: String {
        didSet { defaults.set(folderDeliveryPath, forKey: Key.folderDeliveryPath) }
    }

    /// The subfolder of the destination folder the note lands in, allowed to
    /// name several levels with `/`. Empty means the `微信流` default — the
    /// same fallback the Obsidian entry applies inside the vault.
    @Published var folderDeliverySubfolder: String {
        didSet { defaults.set(folderDeliverySubfolder, forKey: Key.folderDeliverySubfolder) }
    }

    /// Global scene shortcuts choose for the next share, not forever. A stale
    /// choice is ignored rather than silently changing a later forward.
    func selectSceneForNextForward(id: String) {
        pendingScene = PendingSceneSelection(sceneID: id, selectedAt: Date())
    }

    func consumePendingSceneID(now: Date = Date()) -> String? {
        guard let selection = pendingScene else { return nil }
        pendingScene = nil
        guard now.timeIntervalSince(selection.selectedAt) <= 60 else { return nil }
        return selection.sceneID
    }
}
