import Foundation

/// The `wechatbridge://` URLs the app answers to.
///
/// Written for the 「发送到自定义」 share panel, which opened
/// `wechatbridge://settings/entries` when the user's target list was empty. That panel
/// is gone — the extension has no interface at all now (ADR-0005), and the app
/// makes the same offer on its own failure capsule, in-process. So nothing in
/// this tree opens one of these any more; the scheme stays registered and
/// routed because it is the app's front door for anything that wants to point
/// at a settings pane, and because dropping a shipped URL scheme is a decision
/// of its own.
///
/// Declared here because two places have to agree on the spelling: the app
/// delegate that routes it and `Resources/Info.plist`'s `CFBundleURLTypes` —
/// which is asserted by `Scripts/check-release-config.sh`, since a scheme that
/// is not registered fails by doing nothing at all.
public enum AppLink {
    public static let scheme = "wechatbridge"

    /// Opens the settings window on 入口, where the forward targets are managed.
    public static let settingsEntries = URL(string: "\(scheme)://settings/entries")!

    /// Which settings pane a `wechatbridge://` URL asks for, or nil if it asks for
    /// something this build does not know about — in which case the app opens
    /// its window rather than ignoring the user entirely.
    public static func settingsPath(of url: URL) -> String? {
        guard url.scheme == scheme, url.host == "settings" else { return nil }
        let path = url.path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        return path.isEmpty ? nil : path
    }
}

/// What the share extension passes when it starts the app: not a person
/// opening WeChatBridge, so a first run keeps its guide for when one does.
///
/// Declared beside `AppLink` for the same reason: the extension that sends it
/// and the app delegate that reads it have to agree on the spelling.
public enum LaunchArgument {
    public static let background = "--background"
}
