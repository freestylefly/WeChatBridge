// Validates the Simplified Chinese and English resources against each other and
// against the source.
//
// Source copy is Chinese and doubles as the key, which makes drift silent: a
// reworded button keeps compiling and simply stops being translated. Three
// checks catch that — the two languages must declare the same keys, every
// Chinese literal in the source must be declared, and no declared key may be
// unreferenced. The fourth check is the one that crashes at runtime rather than
// merely reading badly: format specifiers must match across languages.
//
//   swift Scripts/check-localizations.swift   (from macos/, or any cwd —
//   paths resolve against the script location, not the working directory)
//
import Foundation

let scriptURL = URL(fileURLWithPath: CommandLine.arguments[0]).standardized
let root = scriptURL.deletingLastPathComponent().deletingLastPathComponent()
let localizations = root.appendingPathComponent("Resources/Localizations")
let shareLocalizations = root.appendingPathComponent("Resources/ShareLocalizations")
let sources = root.appendingPathComponent("Sources")
let languages = ["zh-Hans", "en"]

var failures: [String] = []
func fail(_ message: String) { failures.append(message) }

func table(_ directory: URL, _ language: String, _ name: String) -> [String: String]? {
    let url = directory
        .appendingPathComponent("\(language).lproj")
        .appendingPathComponent("\(name).strings")
    guard let data = try? Data(contentsOf: url) else {
        fail("missing \(url.path)")
        return nil
    }
    guard let parsed = try? PropertyListSerialization.propertyList(
        from: data, options: [], format: nil
    ) as? [String: String] else {
        fail("cannot parse \(url.path)")
        return nil
    }
    return parsed
}

/// `%@`, `%d`, `%1$@` … in the order they appear. Order matters because a
/// translation that swaps two arguments must say so positionally.
func specifiers(in value: String) -> [String] {
    let pattern = try! NSRegularExpression(pattern: "%(?:\\d+\\$)?[@a-zA-Z]")
    let range = NSRange(value.startIndex..., in: value)
    return pattern.matches(in: value, range: range).compactMap {
        Range($0.range, in: value).map { String(value[$0]) }
    }
}

// MARK: - Table parity

// Each Share-menu entry is its own bundle with its own display name, so each
// slot directory is checked as a separate pair of tables.
var groups: [(URL, String, [String])] = [
    (localizations, "Resources/Localizations", ["Localizable", "InfoPlist"]),
]
let slots = (try? FileManager.default.contentsOfDirectory(
    at: shareLocalizations,
    includingPropertiesForKeys: [.isDirectoryKey],
    options: [.skipsHiddenFiles]
)) ?? []
if slots.isEmpty { fail("no share slots under \(shareLocalizations.path)") }
for slot in slots.sorted(by: { $0.lastPathComponent < $1.lastPathComponent }) {
    groups.append((slot, "Resources/ShareLocalizations/\(slot.lastPathComponent)", ["InfoPlist"]))
}

for (directory, label, tables) in groups {
    for name in tables {
        let loaded = languages.compactMap { language in table(directory, language, name).map { (language, $0) } }
        guard loaded.count == languages.count else { continue }
        let keySets = loaded.map { Set($0.1.keys) }
        let union = keySets.reduce(into: Set<String>()) { $0.formUnion($1) }
        for (index, (language, _)) in loaded.enumerated() {
            let missing = union.subtracting(keySets[index]).sorted()
            for key in missing {
                fail("\(label)/\(language).lproj/\(name).strings is missing \"\(key)\"")
            }
        }

        guard let reference = loaded.first else { continue }
        for (key, referenceValue) in reference.1 {
            let expected = specifiers(in: referenceValue)
            for (language, values) in loaded.dropFirst() {
                guard let value = values[key] else { continue }
                let actual = specifiers(in: value)
                if actual != expected {
                    fail("""
                    \(label)/\(name) \"\(key)\": \(reference.0) has \(expected), \
                    \(language) has \(actual)
                    """)
                }
            }
        }
    }
}

// MARK: - Source coverage

/// Every place a literal becomes a lookup key: explicit `L10n` calls, and the
/// SwiftUI views that take a `LocalizedStringKey`.
let keyPatterns = [
    "L10n\\.text\\(\\s*\"((?:\\\\.|[^\"\\\\])*)\"",
    "L10n\\.format\\(\\s*\"((?:\\\\.|[^\"\\\\])*)\"",
    "Text\\(\\s*\"((?:\\\\.|[^\"\\\\])*)\"",
    "Button\\(\\s*\"((?:\\\\.|[^\"\\\\])*)\"",
    "Toggle\\(\\s*\"((?:\\\\.|[^\"\\\\])*)\"",
    "Label\\(\\s*\"((?:\\\\.|[^\"\\\\])*)\"",
].map { try! NSRegularExpression(pattern: $0) }

var referenced: Set<String> = []
let enumerator = FileManager.default.enumerator(at: sources, includingPropertiesForKeys: nil)!
for case let url as URL in enumerator where url.pathExtension == "swift" {
    guard let text = try? String(contentsOf: url, encoding: .utf8) else { continue }
    let range = NSRange(text.startIndex..., in: text)
    for pattern in keyPatterns {
        for match in pattern.matches(in: text, range: range) {
            guard let captured = Range(match.range(at: 1), in: text) else { continue }
            let literal = String(text[captured])
            let decoded = try? JSONDecoder().decode(String.self, from: Data(("\"" + literal + "\"").utf8))
            referenced.insert(decoded ?? literal)
        }
    }
}

func containsChinese(_ value: String) -> Bool {
    value.unicodeScalars.contains { (0x4E00...0x9FFF).contains($0.value) }
}

if let zh = table(localizations, "zh-Hans", "Localizable") {
    let declared = Set(zh.keys)
    for key in referenced.filter(containsChinese).sorted() where !declared.contains(key) {
        fail("used in Sources but not declared in Localizable.strings: \"\(key)\"")
    }
    for key in declared.subtracting(referenced).sorted() {
        fail("declared in Localizable.strings but never used: \"\(key)\"")
    }
}

// MARK: - Result

if failures.isEmpty {
    print("Localization validation passed: \(languages.joined(separator: ", ")).")
    exit(0)
}
for failure in failures { FileHandle.standardError.write(Data("\(failure)\n".utf8)) }
exit(1)
