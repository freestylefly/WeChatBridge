import Foundation

/// What the user asked for when they picked an entry in the system Share menu.
///
/// macOS builds that menu from signed extension bundles, so one entry means one
/// `.appex`. All of WeChatBridge's extensions run the same code and tell themselves
/// apart by this value, read from their own `Info.plist`. Which entries are
/// live is the one thing the platform lets the user change, and WeChatBridge's own
/// 入口 pane writes it through `pluginkit` rather than sending them to System
/// Settings.
public enum ShareAction: String, Codable, Sendable, CaseIterable {
    /// Paste into ChatGPT's Codex.
    case codex
    /// Paste into Claude.
    case claude
    /// Paste into Doubao.
    case doubao
    /// Paste into QwenWork.
    case qwen
    /// Paste into WorkBuddy.
    case workBuddy
    /// Paste into WeSight.
    case weSight
    /// Paste into DeepSeek Harness.
    case deepSeekHarness
    /// Write a Markdown note and its source archive into the configured vault.
    case obsidian
    /// Write a Markdown note and its attachments into the configured folder,
    /// no app involved.
    case folder
    /// Retain this batch for a collection instead of forwarding it immediately.
    case collect
    /// Put the files on the clipboard and stop there.
    case clipboard
    /// Ask which app, every time. The entry itself names no destination and
    /// neither does the intent the extension writes for it: the extension has no
    /// interface left to ask in, so WeChatBridge answers the question in-process —
    /// straight through for one app, `TargetPickerPanel` for several. The Share
    /// menu is built from signed bundles and cannot grow a row per app the user
    /// installs, which is why one entry stands for all of them.
    case custom

    public static let infoDictionaryKey = "DKShareAction"

    /// The extension's own declaration. Falling back to `.clipboard` keeps a
    /// mis-built bundle useful without inventing a destination.
    public static var declared: ShareAction {
        let raw = Bundle.main.object(forInfoDictionaryKey: infoDictionaryKey) as? String
        return raw.flatMap(ShareAction.init(rawValue:)) ?? .clipboard
    }

    /// Decode the retired `shelf` value as a clipboard copy so batches written
    /// before this entry was removed remain readable.
    public init(from decoder: any Decoder) throws {
        let container = try decoder.singleValueContainer()
        let raw = try container.decode(String.self)
        if raw == "shelf" {
            self = .clipboard
            return
        }
        guard let action = ShareAction(rawValue: raw) else {
            throw DecodingError.dataCorruptedError(
                in: container,
                debugDescription: "Unknown share action: \(raw)"
            )
        }
        self = action
    }

    public func encode(to encoder: any Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(rawValue)
    }

    /// The app this action drives, if any. Resolved by bundle identifier rather
    /// than by name so a renamed or relocated app still works.
    public var targetBundleIdentifier: String? {
        switch self {
        case .codex: return "com.openai.codex"
        case .claude: return "com.anthropic.claudefordesktop"
        case .doubao: return "com.bot.pc.doubao"
        case .qwen: return "com.alibaba.qwenwork"
        case .workBuddy: return "com.tencent.workbuddy.mac"
        case .weSight: return "ai.wesight.app"
        case .deepSeekHarness: return "com.deepseek.dsh"
        case .obsidian: return "md.obsidian"
        // `.custom` has no fixed destination and no destination in its intent
        // either. `ActionRunner` resolves one from the user's own list, or takes
        // the one a 发给 ▸ menu inside WeChatBridge named.
        // `.folder` delivers to a path, not a process — the app writes the
        // note itself and no app is ever launched.
        case .collect, .clipboard, .custom, .folder: return nil
        }
    }

    /// Whether the app still has something to do once the files are durable.
    ///
    /// 「复制到剪贴板」 is finished by the extension itself, which writes the
    /// pasteboard before it exits and does not even launch the app. An intent
    /// could only ever be found "too late": a copy made while WeChatBridge was
    /// closed read 未执行 on the next launch, for a ⌘V that had worked all along.
    public var needsIntent: Bool {
        switch self {
        case .collect, .clipboard: return false
        case .codex, .claude, .doubao, .qwen, .workBuddy, .weSight, .deepSeekHarness, .obsidian, .folder, .custom: return true
        }
    }

    /// Shown by the extension while it works, and by the app when it reports a
    /// failure. Deliberately names the destination: "已保存" tells the user
    /// nothing about whether the thing they asked for happened.
    public var targetDisplayName: String {
        switch self {
        case .codex: return L10n.text("Codex")
        case .claude: return L10n.text("Claude")
        case .doubao: return L10n.text("豆包")
        case .qwen: return L10n.text("千问办公")
        case .workBuddy: return "WorkBuddy"
        case .weSight: return L10n.text("WeSight")
        case .deepSeekHarness: return "DeepSeek Harness"
        case .obsidian: return L10n.text("Obsidian")
        case .collect: return L10n.text("分批收集")
        case .folder: return L10n.text("文件夹")
        case .clipboard: return L10n.text("剪贴板")
        // Only ever reached when the chosen target is missing — a failure
        // message has to name something, and this build has nothing better.
        case .custom: return L10n.text("所选应用")
        }
    }

    /// The entry as it is worded in the system Share menu.
    ///
    /// The history says what the user picked, not what WeChatBridge did with it, so it
    /// reuses the very words that were on screen when they picked it — the
    /// defaults in `Scripts/share-slots.sh`.
    public var entryTitle: String {
        switch self {
        case .codex, .claude: return L10n.format("发给 %@", targetDisplayName)
        case .doubao: return L10n.text("发给豆包")
        case .qwen: return L10n.text("发给千问办公")
        case .workBuddy: return L10n.text("发给 WorkBuddy")
        case .weSight: return L10n.text("发给 WeSight")
        case .deepSeekHarness: return L10n.text("发给 DeepSeek Harness")
        case .obsidian: return L10n.text("沉淀到 Obsidian")
        case .collect: return L10n.text("分批收集到微信流")
        case .folder: return L10n.text("沉淀到文件夹")
        case .clipboard: return L10n.text("复制到剪贴板")
        case .custom: return L10n.text("发送到自定义")
        }
    }

    /// The extension bundle that carries this entry, as a suffix on the app's
    /// own identifier. The other half of this table is
    /// `Scripts/share-slots.sh`, which is what `make-app.sh` stamps into each
    /// appex; the two have to be read together.
    public var bundleIdentifierSuffix: String {
        switch self {
        case .codex: return "ShareCodex"
        case .claude: return "ShareClaude"
        case .doubao: return "ShareDoubao"
        case .qwen: return "ShareQwenWork"
        case .workBuddy: return "ShareWorkBuddy"
        case .weSight: return "ShareWeSight"
        case .deepSeekHarness: return "ShareDeepSeekHarness"
        case .obsidian: return "ShareObsidian"
        case .collect: return "ShareCollection"
        case .folder: return "ShareFolder"
        case .clipboard: return "ShareClipboard"
        case .custom: return "ShareCustom"
        }
    }
}

/// A one-shot request attached to a committed batch.
///
/// Kept out of `manifest.json` on purpose: the manifest describes what the batch
/// *is*, and survives for the life of the files. This describes what should
/// happen *once*, and the app deletes it the moment it acts — which is what
/// stops a forward from firing again on the next rescan or relaunch.
public struct BatchIntent: Codable, Sendable, Hashable {
    public static let fileName = "intent.json"
    public static let currentSchemaVersion = 1

    /// A forward is an immediate gesture. If WeChatBridge was not running and takes a
    /// while to start that is fine, but an intent found hours later — because
    /// the Mac was asleep, or the app never launched — must not suddenly paste
    /// into whatever the user happens to have open. Those are recorded as
    /// `expired` and nothing is done with the files, which is why the extension
    /// also puts them on the clipboard before it exits.
    public static let freshnessWindow: TimeInterval = 90

    public let schemaVersion: Int
    public let action: ShareAction
    public let requestedAt: Date
    /// Which app 「发送到自定义」 was pointed at. Nil for every other entry, and
    /// nil in every intent written before this field existed — which is why the
    /// schema version stays 1: an older app reading one of these simply ignores
    /// a field it has no use for, and a newer app reading an older intent gets
    /// exactly what that intent meant.
    public let targetBundleIdentifier: String?
    /// Stored beside the identifier so a failure can name the app even when it
    /// has since been uninstalled and Launch Services can no longer resolve it.
    public let targetDisplayName: String?

    public init(
        action: ShareAction,
        requestedAt: Date,
        target: ForwardTarget? = nil,
        schemaVersion: Int = currentSchemaVersion
    ) {
        self.schemaVersion = schemaVersion
        self.action = action
        self.requestedAt = requestedAt
        targetBundleIdentifier = target?.bundleIdentifier
        targetDisplayName = target?.displayName
    }

    /// The app to forward to, rebuilt from the two flat fields.
    ///
    /// `addedAt` is the request time rather than the moment the user added the
    /// app to their list: nothing downstream sorts by it, and the intent is the
    /// only record here.
    public var target: ForwardTarget? {
        guard let targetBundleIdentifier else { return nil }
        return ForwardTarget(
            bundleIdentifier: targetBundleIdentifier,
            displayName: targetDisplayName ?? targetBundleIdentifier,
            addedAt: requestedAt
        )
    }

    public func isFresh(now: Date = Date()) -> Bool {
        now.timeIntervalSince(requestedAt) < Self.freshnessWindow
    }
}
