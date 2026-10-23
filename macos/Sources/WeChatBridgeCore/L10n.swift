import Foundation

/// Localization lookup for strings produced outside SwiftUI.
///
/// Source copy is Simplified Chinese and doubles as the key, so a missing
/// translation degrades to readable Chinese instead of a raw identifier. The
/// extension and the app both load `Localizable.strings` from their own
/// `Bundle.main`; `Bundle.module` would point back into the build directory and
/// not survive into a distributed `.app`.
public enum L10n {
    private static let lock = NSLock()
    nonisolated(unsafe) private static var cache: [String: String] = [:]

    public static func text(_ key: String, bundle: Bundle = .main) -> String {
        guard bundle === Bundle.main else {
            return bundle.localizedString(forKey: key, value: key, table: nil)
        }
        lock.lock()
        if let hit = cache[key] {
            lock.unlock()
            return hit
        }
        lock.unlock()

        // Resolved outside our lock: `Bundle` takes its own, and holding both
        // would serialise UI lookups behind background ones.
        let resolved = bundle.localizedString(forKey: key, value: key, table: nil)
        lock.lock()
        cache[key] = resolved
        lock.unlock()
        return resolved
    }

    public static func format(_ key: String, _ arguments: CVarArg..., bundle: Bundle = .main) -> String {
        String(format: text(key, bundle: bundle), locale: locale(for: bundle), arguments: arguments)
    }

    /// The app can carry a per-app language that differs from the Mac's region,
    /// so the bundle's own preference wins over `Locale.current`.
    public static func locale(for bundle: Bundle = .main) -> Locale {
        guard let identifier = bundle.preferredLocalizations.first, !identifier.isEmpty else {
            return .current
        }
        return Locale(identifier: identifier)
    }
}
