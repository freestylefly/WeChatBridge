import Foundation
import WeChatBridgeCore

/// Everything the user chooses, in the App Group's defaults so the extension
/// could read it later without a migration. Scene and group-memory keys are
/// the Mac's own names, so an exported store reads the same on both.
@MainActor
final class Preferences: ObservableObject {
    enum Key {
        static let scenes = "com.xiangming.wechatbridge.scenes.v3"
        static let groupMemory = "com.xiangming.wechatbridge.groupMemory.v1"
        static let historyRetentionDays = "com.xiangming.wechatbridge.historyRetentionDays"
        static let obsidianSubfolder = "com.xiangming.wechatbridge.obsidianSubfolder"
        static let folderSubfolder = "com.xiangming.wechatbridge.folderDeliverySubfolder"
        static let obsidianBookmark = "com.xiangming.wechatbridge.ios.obsidianBookmark"
        static let folderBookmark = "com.xiangming.wechatbridge.ios.folderBookmark"
        static let targetFormatOverrides = "com.xiangming.wechatbridge.ios.targetFormatOverrides"
        static let hiddenTargets = "com.xiangming.wechatbridge.ios.hiddenTargets"
        static let targetOrder = "com.xiangming.wechatbridge.ios.targetOrder"
        static let lastTarget = "com.xiangming.wechatbridge.ios.lastTarget"
        static let onboardingDone = "com.xiangming.wechatbridge.ios.onboardingDone"
        static let liveActivity = "com.xiangming.wechatbridge.ios.liveActivity"
        static let imagesInPDF = "com.xiangming.wechatbridge.ios.imagesInPDF"
    }

    static let defaultRetentionDays = 7

    let defaults: UserDefaults

    @Published var scenes: SceneSettings { didSet { save(scenes, Key.scenes) } }
    @Published var groupMemory: [String: GroupMemory] { didSet { save(groupMemory, Key.groupMemory) } }
    /// 0 keeps everything.
    @Published var retentionDays: Int { didSet { defaults.set(retentionDays, forKey: Key.historyRetentionDays) } }
    @Published var obsidianSubfolder: String { didSet { defaults.set(obsidianSubfolder, forKey: Key.obsidianSubfolder) } }
    @Published var folderSubfolder: String { didSet { defaults.set(folderSubfolder, forKey: Key.folderSubfolder) } }
    @Published var obsidianBookmark: Data? { didSet { defaults.set(obsidianBookmark, forKey: Key.obsidianBookmark) } }
    @Published var folderBookmark: Data? { didSet { defaults.set(folderBookmark, forKey: Key.folderBookmark) } }
    @Published var formatOverrides: [String: String] { didSet { defaults.set(formatOverrides, forKey: Key.targetFormatOverrides) } }
    @Published var hiddenTargets: [String] { didSet { defaults.set(hiddenTargets, forKey: Key.hiddenTargets); publishQuickTargets() } }
    @Published var targetOrder: [String] { didSet { defaults.set(targetOrder, forKey: Key.targetOrder); publishQuickTargets() } }
    @Published var lastTarget: String? { didSet { defaults.set(lastTarget, forKey: Key.lastTarget); publishQuickTargets() } }
    /// 「分享后提醒我投递」; the extension reads the same key.
    @Published var quickDeliver: Bool { didSet { defaults.set(quickDeliver, forKey: QuickDeliver.enabledKey) } }
    /// 「投递时显示在灵动岛」.
    @Published var liveActivity: Bool { didSet { defaults.set(liveActivity, forKey: Key.liveActivity) } }
    /// TXT targets get one PDF with the images inline; off, the TXT alone and
    /// the images go to Photos. 千问 takes images from neither a TXT share nor
    /// its file picker, so the PDF is the only one-step way (2026-10-24).
    @Published var imagesInPDF: Bool { didSet { defaults.set(imagesInPDF, forKey: Key.imagesInPDF) } }
    @Published var onboardingDone: Bool { didSet { defaults.set(onboardingDone, forKey: Key.onboardingDone) } }

    init(defaults: UserDefaults = Preferences.sharedDefaults) {
        self.defaults = defaults
        var scenes = Self.load(SceneSettings.self, defaults, Key.scenes) ?? SceneSettings.makeDefault()
        scenes.installOfficialScenes()
        self.scenes = scenes
        groupMemory = Self.load([String: GroupMemory].self, defaults, Key.groupMemory) ?? [:]
        retentionDays = defaults.object(forKey: Key.historyRetentionDays) as? Int ?? Self.defaultRetentionDays
        obsidianSubfolder = defaults.string(forKey: Key.obsidianSubfolder) ?? "微信流"
        folderSubfolder = defaults.string(forKey: Key.folderSubfolder) ?? "微信流"
        obsidianBookmark = defaults.data(forKey: Key.obsidianBookmark)
        folderBookmark = defaults.data(forKey: Key.folderBookmark)
        formatOverrides = defaults.dictionary(forKey: Key.targetFormatOverrides) as? [String: String] ?? [:]
        hiddenTargets = defaults.stringArray(forKey: Key.hiddenTargets) ?? []
        targetOrder = defaults.stringArray(forKey: Key.targetOrder) ?? []
        lastTarget = defaults.string(forKey: Key.lastTarget)
        onboardingDone = defaults.bool(forKey: Key.onboardingDone)
        quickDeliver = QuickDeliver.isEnabled(defaults)
        liveActivity = defaults.object(forKey: Key.liveActivity) as? Bool ?? true
        imagesInPDF = defaults.object(forKey: Key.imagesInPDF) as? Bool ?? true
        publishQuickTargets()
    }

    /// The notification's buttons, for the extension to read.
    private func publishQuickTargets() {
        QuickDeliver.store(targets.map { QuickDeliver.Target(id: $0.id, name: $0.displayName) }, in: defaults)
    }

    /// Falls back to the app's own defaults only when the group is missing,
    /// which on iOS means a build without the entitlement — never silently in
    /// a signed build, where the extension would then disagree.
    nonisolated static var sharedDefaults: UserDefaults {
        UserDefaults(suiteName: AppGroup.identifier) ?? .standard
    }

    /// The configured targets after the user's order, visibility and format
    /// choices, last-used first.
    var targets: [TargetProfile] {
        TargetProfile.arranged(
            TargetProfile.builtIn,
            order: targetOrder,
            hidden: Set(hiddenTargets),
            overrides: formatOverrides,
            lastUsed: lastTarget
        )
    }

    func rememberGroup(displayName: String, sceneID: String, senders: Set<String>, end: Date?) {
        let key = GroupName.normalize(displayName)
        guard !key.isEmpty else { return }
        groupMemory[key] = GroupMemory.advancing(
            groupMemory[key],
            displayName: displayName,
            sceneID: sceneID,
            senders: senders,
            end: end
        )
    }

    func bind(displayName: String, sceneIDs: [String], senders: Set<String>) {
        let key = GroupName.normalize(displayName)
        guard !key.isEmpty else { return }
        var memory = groupMemory[key] ?? GroupMemory(displayName: displayName)
        memory.boundSceneIDs = sceneIDs
        memory.senders.formUnion(senders)
        memory.updatedAt = Date()
        groupMemory[key] = memory
    }

    private func save<T: Encodable>(_ value: T, _ key: String) {
        guard let data = try? JSONEncoder().encode(value) else { return }
        defaults.set(data, forKey: key)
    }

    private static func load<T: Decodable>(_ type: T.Type, _ defaults: UserDefaults, _ key: String) -> T? {
        guard let data = defaults.data(forKey: key) else { return nil }
        return try? JSONDecoder().decode(type, from: data)
    }
}
