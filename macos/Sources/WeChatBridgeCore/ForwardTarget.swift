import Foundation

/// An app the user added to 「发送到自定义」.
///
/// Identified by bundle identifier rather than by path: an app that is updated,
/// moved into a subfolder of /Applications or reinstalled keeps working, and a
/// user with two copies of the same app does not end up with two rows that
/// behave identically. `displayName` is stored rather than resolved on every
/// read so 记录 and a failure message can still name an app that has since
/// been uninstalled.
public struct ForwardTarget: Codable, Hashable, Sendable, Identifiable {
    public let bundleIdentifier: String
    public let displayName: String
    public let addedAt: Date
    /// Paste the files' paths as text instead of the files themselves.
    ///
    /// A terminal has nothing to receive a pasted file with: ⌘V over file URLs
    /// does nothing in Terminal, iTerm or an Electron terminal such as Orca,
    /// while dropping the same file on that window types its quoted path. On,
    /// the forward produces exactly what the drop would have (2026-09-05, the
    /// user's Orca screenshot).
    public let pastesPathOnly: Bool

    public var id: String { bundleIdentifier }

    public init(
        bundleIdentifier: String,
        displayName: String,
        addedAt: Date,
        pastesPathOnly: Bool = false
    ) {
        self.bundleIdentifier = bundleIdentifier
        self.displayName = displayName
        self.addedAt = addedAt
        self.pastesPathOnly = pastesPathOnly
    }

    /// A list written before `pastesPathOnly` existed has no such key, and it
    /// meant "paste the files" — which is what the missing key decodes to. The
    /// store has no schema version, so this is the whole migration.
    public init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        bundleIdentifier = try container.decode(String.self, forKey: .bundleIdentifier)
        displayName = try container.decode(String.self, forKey: .displayName)
        addedAt = try container.decode(Date.self, forKey: .addedAt)
        pastesPathOnly = try container.decodeIfPresent(Bool.self, forKey: .pastesPathOnly) ?? false
    }
}

/// The list of custom forward targets.
///
/// Signed distribution builds keep it in the app group's `UserDefaults` suite.
/// Local ad-hoc builds use the app's own defaults, because touching a
/// TCC-protected group container without a Team ID asks for App Data access.
public struct ForwardTargetStore: Sendable {
    public static let defaultsKey = "com.xiangming.wechatbridge.forwardTargets"
    public static let lastUsedKey = "com.xiangming.wechatbridge.lastForwardTarget"

    private let appGroupIdentifier: String
    private let storageMode: AppGroup.StorageMode

    public init(
        appGroupIdentifier: String = AppGroup.identifier,
        storageMode: AppGroup.StorageMode = .current
    ) {
        self.appGroupIdentifier = appGroupIdentifier
        self.storageMode = storageMode
    }

    private var defaults: UserDefaults? {
        switch storageMode {
        case .appGroup: UserDefaults(suiteName: appGroupIdentifier)
        case .sharedDirectory: .standard
        }
    }

    // MARK: - The list

    /// Stored order, which is the order the user dragged the rows into. Not
    /// re-sorted by `addedAt`: a list the user has arranged and an app that
    /// re-sorts it are two different features, and only one of them was asked
    /// for.
    public func load() -> [ForwardTarget] {
        guard let data = defaults?.data(forKey: Self.defaultsKey) else { return [] }
        return Self.decode(data)
    }

    public func save(_ targets: [ForwardTarget]) {
        guard let defaults, let data = Self.encode(targets) else { return }
        defaults.set(data, forKey: Self.defaultsKey)
    }

    /// Last pick first, then stored order. The panel opens on the target the
    /// user chose the previous time, which is the one they are most likely to
    /// want again — and Return is bound to the first row.
    public func orderedTargets() -> [ForwardTarget] {
        Self.ordered(load(), lastUsed: lastUsedBundleIdentifier())
    }

    // MARK: - The wire format
    //
    // Split out as pure functions so the tests can exercise them without
    // creating a `UserDefaults` suite: a suite is a real preferences domain,
    // and cfprefsd leaves its plist in the user's ~/Library/Preferences after
    // the test has removed the domain — measured, eight stray files after one
    // run. The plumbing these sit behind is verified on the signed build
    // instead, which is the only place it can be verified anyway: it spans two
    // processes on opposite sides of the sandbox.

    public static func encode(_ targets: [ForwardTarget]) -> Data? {
        try? encoder().encode(targets)
    }

    /// Anything unreadable is an empty list rather than an error: a corrupt
    /// preference must not stop the share panel from opening.
    public static func decode(_ data: Data) -> [ForwardTarget] {
        (try? decoder().decode([ForwardTarget].self, from: data)) ?? []
    }

    /// A pointer at an app that is no longer in the list changes nothing.
    public static func ordered(_ targets: [ForwardTarget], lastUsed: String?) -> [ForwardTarget] {
        guard let lastUsed,
              let index = targets.firstIndex(where: { $0.bundleIdentifier == lastUsed })
        else { return targets }
        var ordered = targets
        ordered.insert(ordered.remove(at: index), at: 0)
        return ordered
    }

    public func lastUsedBundleIdentifier() -> String? {
        defaults?.string(forKey: Self.lastUsedKey)
    }

    /// Written whenever a forward is carried out, whether it came from a share
    /// or from a 发给 ▸ menu inside WeChatBridge.
    public func recordLastUsed(_ bundleIdentifier: String) {
        defaults?.set(bundleIdentifier, forKey: Self.lastUsedKey)
    }

    // MARK: - Coding

    /// ISO-8601 for the same reason the manifests use it: a field report reads
    /// the plist by hand, and a bare `Double` needs the app to interpret it.
    private static func encoder() -> JSONEncoder {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        return encoder
    }

    private static func decoder() -> JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return decoder
    }
}
