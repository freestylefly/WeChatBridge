import Foundation

/// Turns an `NSItemProvider.suggestedName` into something safe to write to disk.
///
/// A suggested name is host-supplied text, not a path: it may contain
/// separators, be empty, be `..`, or be long enough to exceed the 255-byte
/// filename limit. Storage safety does not depend on this — every file lands in
/// its own UUID directory — but the sanitised name is what the user sees and
/// what the receiving app gets on drop, so it still has to be well formed.
public enum DisplayName {
    public static let fallbackBaseName = "共享文件"

    /// APFS allows almost anything except NUL and `/`, but Finder renders a
    /// stored `:` as `/`, which makes a name look like a path it is not.
    private static let forbidden = CharacterSet(charactersIn: "/:\0")

    public static func sanitize(_ raw: String?, fallbackExtension: String? = nil) -> String {
        var name = (raw as NSString?)?.lastPathComponent ?? ""
        name = String(name.unicodeScalars.map { scalar -> Character in
            if forbidden.contains(scalar) { return "-" }
            // Control characters (including newlines a host might smuggle in)
            // have no place in a filename shown in a list.
            if scalar.properties.generalCategory == .control { return " " }
            return Character(scalar)
        })
        name = name.trimmingCharacters(in: .whitespacesAndNewlines)

        // "." and ".." are directory entries, and a leading dot hides the file
        // from Finder — none of which the sharer asked for.
        if name.isEmpty || name == "." || name == ".." || name.hasPrefix(".") {
            name = fallbackBaseName + name
        }

        if (name as NSString).pathExtension.isEmpty,
           let fallbackExtension, !fallbackExtension.isEmpty {
            name = "\(name).\(fallbackExtension)"
        }
        return truncate(name)
    }

    /// A subfolder path, each component sanitized as a name and rejoined with
    /// "/" so a multi-level value such as 「参考/微信流」 keeps its hierarchy.
    /// Empty input or input with no usable components yields "".
    public static func subfolderPath(_ raw: String?) -> String {
        let components = (raw ?? "")
            .split(separator: "/", omittingEmptySubsequences: true)
            .map { sanitize(String($0)) }
            .filter { !$0.isEmpty }
        return components.joined(separator: "/")
    }

    /// Keeps the extension attached: a 260-character Chinese name truncated
    /// naively becomes an extensionless file that no app knows how to open.
    static func truncate(_ name: String, limit: Int = 200) -> String {
        guard name.utf8.count > limit else { return name }
        let ext = (name as NSString).pathExtension
        let suffix = ext.isEmpty ? "" : ".\(ext)"
        var base = (name as NSString).deletingPathExtension
        let budget = max(1, limit - suffix.utf8.count)
        while base.utf8.count > budget, !base.isEmpty {
            base.removeLast()
        }
        if base.isEmpty { base = fallbackBaseName }
        return base + suffix
    }
}
