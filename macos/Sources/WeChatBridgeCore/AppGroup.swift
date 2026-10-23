import Darwin
import Foundation

/// The one identifier the app and its share extension must agree on.
///
/// It is read from `Info.plist` rather than hard-coded so a single build-script
/// substitution can retarget both bundles at once; a mismatch between the two
/// entitlements is otherwise invisible until a share silently lands in a
/// container the app cannot see. `Scripts/check-release-config.sh` asserts that
/// all four files carry the same string.
public enum AppGroup {
    public static let infoDictionaryKey = "DKAppGroupIdentifier"
    public static let storageModeKey = "DKStorageMode"

    /// Used by `swift run`, unit tests and previews, which have no bundle.
    /// Team-prefixed because macOS provisions app groups per team, and the same
    /// literal then works for both an ad-hoc local build and a Developer ID one.
    public static let fallbackIdentifier = "DLKMC3ZRZQ.com.xiangming.wechatbridge.shared"

    public static var identifier: String {
        let declared = Bundle.main.object(forInfoDictionaryKey: infoDictionaryKey) as? String
        guard let declared, !declared.isEmpty else { return fallbackIdentifier }
        return declared
    }

    public enum StorageMode: String, Sendable {
        case appGroup = "app-group"
        case sharedDirectory = "shared"

        /// Unsigned local builds use a plain Application Support directory:
        /// macOS treats App Group containers as TCC-protected when the code
        /// signature has no Team ID, which makes every ad-hoc build prompt.
        public static var current: StorageMode {
            guard let raw = Bundle.main.object(forInfoDictionaryKey: storageModeKey) as? String,
                  let mode = StorageMode(rawValue: raw)
            else { return .sharedDirectory }
            return mode
        }

        /// The real account home, not the sandbox container home.
        public static var sharedRoot: URL {
            guard let entry = getpwuid(getuid()), let path = entry.pointee.pw_dir else {
                return FileManager.default.homeDirectoryForCurrentUser
                    .appendingPathComponent("Library/Application Support/WeChatBridge", isDirectory: true)
            }
            return URL(fileURLWithPath: String(cString: path), isDirectory: true)
                .appendingPathComponent("Library/Application Support/WeChatBridge", isDirectory: true)
        }
    }
}
