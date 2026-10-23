import CryptoKit
import Foundation
import zlib

/// Skill errors carry user-facing text — the pane and the import path display
/// them as-is, so the message is written at the throw site, not decoded later.
public struct SkillError: LocalizedError, Sendable {
    public let errorDescription: String?

    public init(_ message: String) {
        errorDescription = message
    }
}

/// The skill id rule shared by the catalog, the skill library directory name,
/// the SKILL.md frontmatter `name` and `{{skill:id}}` references: lowercase
/// letters, digits and single hyphens, at most 64 characters — the Agent
/// Skills naming spec, so one id works as a native install everywhere.
public enum SkillId {
    public static let maxLength = 64

    private static let pattern = try? NSRegularExpression(
        pattern: "^[a-z0-9]+(-[a-z0-9]+)*$"
    )

    /// Pre-spec ids (dotted) mapped to their spec-compliant replacements.
    private static let legacy: [String: String] = [
        "wechatbridge.wechat-article-extract": "wechat-article-extract",
        "wechatbridge.video-information-reading": "video-information-reading",
    ]

    public static func isValid(_ id: String?) -> Bool {
        guard let id, !id.isEmpty, id.count <= maxLength else { return false }
        let range = NSRange(id.startIndex..<id.endIndex, in: id)
        return pattern?.firstMatch(in: id, range: range) != nil
    }

    /// The current id for `id`; unknown ids pass through unchanged.
    public static func migrate(_ id: String) -> String {
        legacy[id] ?? id
    }

    /// Rewrites legacy ids in a scene's declared skills and inline references.
    /// Returns true when anything changed so callers only persist real edits.
    @discardableResult
    public static func migrate(_ scene: inout WeChatScene) -> Bool {
        var changed = false
        var seen = Set<String>()
        var ids: [String] = []
        for id in scene.requiredSkillIDs {
            let migrated = migrate(id)
            changed = changed || migrated != id
            if seen.insert(migrated).inserted {
                ids.append(migrated)
            } else {
                changed = true
            }
        }
        scene.requiredSkillIDs = ids

        let instruction = SkillReference.replace(scene.instruction) {
            SkillReference.token(migrate($0))
        }
        let output = SkillReference.replace(scene.outputSpec) {
            SkillReference.token(migrate($0))
        }
        changed = changed || instruction != scene.instruction || output != scene.outputSpec
        scene.instruction = instruction
        scene.outputSpec = output
        return changed
    }
}

/// Inline skill references inside scene prompts: `{{skill:id}}`. The id is
/// stored instead of a display name so renaming or translating a skill never
/// breaks a scene; `ScenePrompt` swaps each token for text the destination
/// agent can act on.
public enum SkillReference {
    /// Anything without whitespace or a closing brace is captured, so malformed
    /// ids surface through `invalid` instead of silently vanishing.
    private static let tokenPattern = try? NSRegularExpression(
        pattern: #"\{\{skill:([^\s{}]+)\}\}"#
    )

    public static func token(_ id: String) -> String {
        "{{skill:\(id)}}"
    }

    /// Valid referenced ids in first-appearance order, without duplicates.
    public static func parse(_ text: String?) -> [String] {
        var seen = Set<String>()
        return captures(text)
            .filter { SkillId.isValid($0) }
            .filter { seen.insert($0).inserted }
    }

    /// Referenced ids that break the `SkillId` rule.
    public static func invalid(_ text: String?) -> [String] {
        var seen = Set<String>()
        return captures(text)
            .filter { !SkillId.isValid($0) }
            .filter { seen.insert($0).inserted }
    }

    /// Replaces every token with `render`'s text. Tokens whose id is invalid
    /// are left verbatim — the editor warns about them instead. Legacy dotted
    /// ids are handed over too, so migration can rewrite them.
    public static func replace(_ text: String?, render: (String) -> String) -> String {
        guard let text, !text.isEmpty else { return text ?? "" }
        let matches = tokenPattern?.matches(
            in: text,
            range: NSRange(text.startIndex..<text.endIndex, in: text)
        ) ?? []
        guard !matches.isEmpty else { return text }

        var result = ""
        var cursor = text.startIndex
        for match in matches {
            guard let whole = Range(match.range, in: text),
                  let capture = Range(match.range(at: 1), in: text)
            else { continue }
            result += text[cursor..<whole.lowerBound]
            let id = String(text[capture])
            result += SkillId.isValid(id) || SkillId.migrate(id) != id
                ? render(id)
                : String(text[whole])
            cursor = whole.upperBound
        }
        result += text[cursor...]
        return result
    }

    private static func captures(_ text: String?) -> [String] {
        guard let text, !text.isEmpty else { return [] }
        let matches = tokenPattern?.matches(
            in: text,
            range: NSRange(text.startIndex..<text.endIndex, in: text)
        ) ?? []
        return matches.compactMap { match in
            Range(match.range(at: 1), in: text).map { String(text[$0]) }
        }
    }
}

/// How one referenced skill reaches the destination agent.
public enum SkillRenderMode: Sendable {
    /// Installed (or confirmed) in the agent's own skill system — name it.
    case native
    /// The library copy exists — the prompt points at its SKILL.md.
    case path
    /// Known skill whose library copy is absent — the agent proceeds without it.
    case missing
    /// Neither the catalog nor the library knows this id.
    case unknown
}

/// One skill resolved for one destination.
public struct SkillResolution: Sendable {
    public let id: String
    public let displayName: String
    public let mode: SkillRenderMode
    public let skillFile: String?

    public init(id: String, displayName: String, mode: SkillRenderMode, skillFile: String? = nil) {
        self.id = id
        self.displayName = displayName
        self.mode = mode
        self.skillFile = skillFile
    }
}

/// Everything `ScenePrompt` needs to turn `{{skill:id}}` into agent-facing
/// text: the destination (nil = clipboard / custom app with no known agent)
/// and a resolver over the catalog and the app-owned library.
public struct SkillRenderContext: Sendable {
    public let agent: AgentID?
    private let resolver: @Sendable (String) -> SkillResolution

    public init(agent: AgentID?, resolve: @escaping @Sendable (String) -> SkillResolution) {
        self.agent = agent
        resolver = resolve
    }

    public func resolve(_ id: String) -> SkillResolution {
        resolver(id)
    }

    /// The rendered phrase. It reads naturally after a verb ("用 …提取"), so
    /// the same text works inline and as a bullet in the 技能要求 section.
    public func phrase(_ id: String) -> String {
        let skill = resolve(id)
        switch skill.mode {
        case .native:
            return L10n.format("「%@」技能（%@）", skill.displayName, skill.id)
        case .path:
            return L10n.format(
                "「%@」技能（技能说明：`%@`，请先阅读并严格按其执行）",
                skill.displayName,
                skill.skillFile ?? ""
            )
        case .missing:
            return L10n.format("「%@」技能（技能文件不可用，请直接完成）", skill.displayName)
        case .unknown:
            return L10n.format("「%@」技能（未找到该技能）", skill.id)
        }
    }
}

/// Where one skill stands inside the app-owned library.
public enum SkillLibraryState: Sendable {
    /// Not in the library (no package shipped, or never synced).
    case missing
    /// Present and byte-identical to what the library last wrote.
    case ready
    /// Present but edited outside the app, or placed there by hand — never overwritten.
    case conflict
}

/// One library record: what was written, from where, and its digest.
public struct SkillLibraryEntry: Codable, Equatable, Sendable {
    public let id: String
    public let version: String
    public let digest: String
    public let source: String
    public var syncedAt: Date

    public init(id: String, version: String, digest: String, source: String, syncedAt: Date) {
        self.id = id
        self.version = version
        self.digest = digest
        self.source = source
        self.syncedAt = syncedAt
    }
}

/// What one `SkillStore.syncOfficial` pass changed.
public struct SkillSyncReport: Sendable {
    public let added: Int
    public let updated: Int
    public let conflicts: [String]

    public init(added: Int, updated: Int, conflicts: [String]) {
        self.added = added
        self.updated = updated
        self.conflicts = conflicts
    }

    public static let empty = SkillSyncReport(added: 0, updated: 0, conflicts: [])
}

/// The app-owned skill library — the single source of truth for every skill
/// WeChatBridge manages. Packages live under
/// `Application Support/WeChatBridge/Skills/<id>/` (the directory name is the
/// skill id, which is also the SKILL.md `name`), the registry in
/// `skill-library.json`, and replaced versions in `SkillBackups/`. Scene
/// prompts point agents at these files; agent skill directories are only
/// deployment targets.
///
/// Safety mirrors the installer: a directory the registry does not know, or
/// whose digest no longer matches, is reported as a conflict and left
/// untouched.
public struct SkillStore: Sendable {
    public static let registryFileName = "skill-library.json"
    public static let officialSource = "official"
    /// Registry source for user-imported packages — not touched by official sync.
    public static let importedSource = "import"
    public static let maxBackups = 20

    private static let backupStampFormat = "yyyyMMddHHmmssSSS"

    public let root: URL
    public let backupRoot: URL
    private let configDirectory: URL
    private let clock: @Sendable () -> Date

    /// - Parameters:
    ///   - root: Library directory; nil means Application Support/WeChatBridge/Skills.
    ///   - configDirectory: Where the registry lives; nil means the same Application Support folder.
    ///   - backupRoot: Backup directory; nil means a SkillBackups sibling of `root`.
    ///   - clock: Timestamp source for registry entries and backup names.
    public init(
        root: URL? = nil,
        configDirectory: URL? = nil,
        backupRoot: URL? = nil,
        clock: (@Sendable () -> Date)? = nil
    ) {
        let stateDirectory = FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("WeChatBridge", isDirectory: true)
        let resolvedRoot = (root ?? stateDirectory.appendingPathComponent("Skills", isDirectory: true))
            .standardizedFileURL
        self.root = resolvedRoot
        self.backupRoot = (backupRoot
            ?? resolvedRoot.deletingLastPathComponent().appendingPathComponent("SkillBackups", isDirectory: true))
            .standardizedFileURL
        self.configDirectory = configDirectory ?? stateDirectory
        self.clock = clock ?? { Date() }
    }

    public func skillDirectory(_ id: String) -> URL {
        root.appendingPathComponent(id, isDirectory: true)
    }

    public func entries() -> [String: SkillLibraryEntry] {
        registry()
    }

    public func state(_ id: String) -> SkillLibraryState {
        guard SkillId.isValid(id) else { return .missing }
        let directory = skillDirectory(id)
        guard pathExists(directory) else { return .missing }
        guard let entry = registry()[id] else { return .conflict }
        do {
            let skillFile = directory.appendingPathComponent("SKILL.md")
            guard FileManager.default.fileExists(atPath: skillFile.path),
                  try SkillPackage.packageDigest(at: directory) == entry.digest
            else { return .conflict }
            return .ready
        } catch {
            return .conflict
        }
    }

    /// Absolute path of the skill's SKILL.md when the library copy is ready; otherwise nil.
    public func skillFile(_ id: String) -> String? {
        state(id) == .ready ? skillDirectory(id).appendingPathComponent("SKILL.md").path : nil
    }

    /// Brings every shipped official package into the library: missing ones
    /// are copied in, a newer bundled version replaces an untouched library
    /// copy (after a backup), and anything edited outside the app is reported,
    /// not overwritten. Skills without a package are skipped.
    @discardableResult
    public func syncOfficial(catalog: OfficialSkillCatalog, resourcesRoot: URL) throws -> SkillSyncReport {
        var registry = registry()
        var added = 0
        var updated = 0
        var conflicts: [String] = []

        for skill in catalog.skills {
            guard let package = skill.package, SkillId.isValid(skill.id) else { continue }
            let source = resourcesRoot
                .appendingPathComponent("Skills", isDirectory: true)
                .appendingPathComponent(package, isDirectory: true)
            guard FileManager.default.fileExists(
                atPath: source.appendingPathComponent("SKILL.md").path
            ) else { continue }

            let sourceDigest = try SkillPackage.validatePackage(at: source)
            let target = skillDirectory(skill.id)
            let exists = pathExists(target)

            if !exists {
                try write(source, to: target, backupAs: nil)
                registry[skill.id] = entry(for: skill, digest: sourceDigest)
                added += 1
                continue
            }

            guard state(skill.id) == .ready, let stored = registry[skill.id] else {
                conflicts.append(skill.id)
                continue
            }

            if let current = SceneVersion(stored.version),
               let bundled = SceneVersion(skill.version),
               bundled > current
            {
                try write(source, to: target, backupAs: "\(skill.id)-\(stored.version)")
                registry[skill.id] = entry(for: skill, digest: sourceDigest)
                updated += 1
            }
        }

        if added + updated > 0 {
            saveRegistry(registry)
        }
        if updated > 0 {
            pruneBackups()
        }
        return SkillSyncReport(added: added, updated: updated, conflicts: conflicts)
    }

    /// Registers a user-supplied package directory (already extracted and
    /// validated by `SkillArchive`). Whatever currently occupies the slot — a
    /// ready copy, a conflicted one or a foreign directory — is moved into the
    /// backups first, so an explicit import never destroys bytes; a
    /// byte-identical re-import is a no-op that refreshes the stamp.
    @discardableResult
    public func `import`(source: URL, id: String, version: String) throws -> SkillLibraryEntry {
        let digest = try SkillPackage.validatePackage(at: source)
        var registry = registry()
        let target = skillDirectory(id)
        let exists = pathExists(target)

        if exists,
           let current = registry[id],
           state(id) == .ready,
           current.digest == digest
        {
            var refreshed = current
            refreshed.syncedAt = clock()
            registry[id] = refreshed
            saveRegistry(registry)
            return refreshed
        }

        try write(
            source,
            to: target,
            backupAs: exists ? "\(id)-\(registry[id]?.version ?? "replaced")" : nil
        )
        let entry = SkillLibraryEntry(
            id: id,
            version: version,
            digest: digest,
            source: Self.importedSource,
            syncedAt: clock()
        )
        registry[id] = entry
        saveRegistry(registry)
        if exists {
            pruneBackups()
        }
        return entry
    }

    /// Removes a library copy: the directory moves into the backups (still
    /// recoverable) and the registry entry is dropped. Returns false when the
    /// id was neither on disk nor registered.
    @discardableResult
    public func remove(_ id: String) throws -> Bool {
        let target = skillDirectory(id)
        let found = pathExists(target)
        if found {
            try FileManager.default.createDirectory(at: backupRoot, withIntermediateDirectories: true)
            try moveAny(target, to: backupRoot.appendingPathComponent(
                "\(id)-removed-\(backupStamp())",
                isDirectory: true
            ))
            pruneBackups()
        }
        var registry = registry()
        let removed = registry.removeValue(forKey: id) != nil
        if removed {
            saveRegistry(registry)
        }
        return found || removed
    }

    private func entry(for skill: OfficialSkill, digest: String) -> SkillLibraryEntry {
        SkillLibraryEntry(
            id: skill.id,
            version: skill.version,
            digest: digest,
            source: Self.officialSource,
            syncedAt: clock()
        )
    }

    private func registry() -> [String: SkillLibraryEntry] {
        let url = configDirectory.appendingPathComponent(Self.registryFileName)
        guard let data = try? Data(contentsOf: url) else { return [:] }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return (try? decoder.decode([String: SkillLibraryEntry].self, from: data)) ?? [:]
    }

    private func saveRegistry(_ registry: [String: SkillLibraryEntry]) {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(registry) else { return }
        try? FileManager.default.createDirectory(at: configDirectory, withIntermediateDirectories: true)
        try? data.write(
            to: configDirectory.appendingPathComponent(Self.registryFileName),
            options: .atomic
        )
    }

    /// Staging copy, then an all-or-nothing swap: the target path only ever
    /// holds a complete package. The replaced copy moves into the backups.
    private func write(_ source: URL, to target: URL, backupAs: String?) throws {
        let fileManager = FileManager.default
        try fileManager.createDirectory(at: root, withIntermediateDirectories: true)
        let id = target.lastPathComponent
        let staging = root.appendingPathComponent(".\(id).staging-\(UUID().uuidString)", isDirectory: true)
        do {
            try SkillPackage.copyPackage(from: source, to: staging)
            guard let backupAs else {
                try fileManager.moveItem(at: staging, to: target)
                return
            }
            try fileManager.createDirectory(at: backupRoot, withIntermediateDirectories: true)
            let backup = backupRoot.appendingPathComponent(
                "\(backupAs)-\(backupStamp())",
                isDirectory: true
            )
            try moveAny(target, to: backup)
            do {
                try fileManager.moveItem(at: staging, to: target)
            } catch {
                try? moveAny(backup, to: target)
                throw error
            }
        } catch {
            try? fileManager.removeItem(at: staging)
            throw error
        }
    }

    /// Keeps the newest `maxBackups` backups, ordered by their name stamp.
    private func pruneBackups() {
        let fileManager = FileManager.default
        guard let backups = try? fileManager.contentsOfDirectory(
            at: backupRoot,
            includingPropertiesForKeys: nil
        ) else { return }
        let stale = backups
            .sorted { left, right in
                (left.lastPathComponent.split(separator: "-").last ?? "")
                    > (right.lastPathComponent.split(separator: "-").last ?? "")
            }
            .dropFirst(Self.maxBackups)
        for url in stale {
            try? fileManager.removeItem(at: url)
        }
    }

    private func backupStamp() -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = Self.backupStampFormat
        return formatter.string(from: clock())
    }

    /// File OR directory — the analogue of `fileExists(atPath:)` covering both.
    private func pathExists(_ url: URL) -> Bool {
        FileManager.default.fileExists(atPath: url.path)
    }

    private func moveAny(_ source: URL, to destination: URL) throws {
        try FileManager.default.moveItem(at: source, to: destination)
    }
}

/// Metadata read from a package's SKILL.md frontmatter.
public struct SkillPackageInfo: Sendable {
    public let id: String
    public let displayName: String
    public let summary: String
    public let version: String

    public init(id: String, displayName: String, summary: String, version: String) {
        self.id = id
        self.displayName = displayName
        self.summary = summary
        self.version = version
    }
}

/// Reads user-supplied skill zips into a staging directory. A valid archive
/// holds exactly one SKILL.md — either at the zip root or wrapped in a single
/// top-level directory, which is also the shape our own 导出 ZIP produces
/// (`{id}/SKILL.md`). Every entry name is normalized and checked for traversal
/// before anything touches disk, and the extracted tree then goes through the
/// same `SkillPackage` validation as shipped packages (no links, no non-file
/// members).
public enum SkillArchive {
    /// Guard against zip bombs — real skill packages are kilobytes.
    static let maxArchiveBytes: Int64 = 128 * 1024 * 1024

    /// Extracts `archivePath` under `stagingParent` and returns the package
    /// directory with its parsed frontmatter. On success the caller owns the
    /// staging directory; on failure it is already gone.
    public static func extractToStaging(
        archivePath: URL,
        stagingParent: URL
    ) throws -> (directory: URL, info: SkillPackageInfo) {
        let staging = stagingParent.appendingPathComponent(
            "skill-import-\(UUID().uuidString.replacingOccurrences(of: "-", with: ""))",
            isDirectory: true
        )
        try FileManager.default.createDirectory(at: staging, withIntermediateDirectories: true)
        do {
            let root = try writeEntries(archivePath: archivePath, staging: staging)
            return (staging, try readInfo(packageDirectory: staging, fallbackID: root))
        } catch {
            try? FileManager.default.removeItem(at: staging)
            throw error
        }
    }

    /// Parses the frontmatter of `SKILL.md` inside an already-extracted
    /// package directory. `fallbackID` is the wrapping folder name from the
    /// archive — used as the id only when frontmatter omits `name`. The
    /// display name comes from the first Markdown heading, falling back to the
    /// id itself.
    public static func readInfo(
        packageDirectory: URL,
        fallbackID: String? = nil
    ) throws -> SkillPackageInfo {
        let file = packageDirectory.appendingPathComponent("SKILL.md")
        guard let text = try? String(contentsOf: file, encoding: .utf8) else {
            throw SkillError(L10n.text("SKILL.md 无法读取。"))
        }

        let fields = frontmatter(text)
        let id = fields["name"].flatMap { $0.isEmpty ? nil : $0 } ?? fallbackID
        guard let id, !id.isEmpty else {
            throw SkillError(L10n.text("SKILL.md 的 frontmatter 中没有 name 字段。"))
        }
        guard SkillId.isValid(id) else {
            throw SkillError(L10n.format("技能 ID「%@」不符合规范（仅限小写字母、数字和连字符）。", id))
        }

        var version = fields["version"] ?? fields["metadata.version"]
        // The registry only needs an ordered version; anything unreadable
        // lands on 1.0.0 rather than failing the whole import.
        if version == nil || SceneVersion(version!) == nil {
            version = "1.0.0"
        }

        return SkillPackageInfo(
            id: id,
            displayName: heading(frontmatterBody(text)) ?? id,
            summary: fields["description"] ?? "",
            version: version ?? "1.0.0"
        )
    }

    /// Validates every entry name, picks the single package root, and writes
    /// the files under it into `staging`. Returns the wrapping directory's
    /// name, or nil for a root-level SKILL.md.
    private static func writeEntries(archivePath: URL, staging: URL) throws -> String? {
        let entries = try SkillZipReader.entries(archivePath: archivePath)

        var files: [(name: String, entry: SkillZipReader.Entry)] = []
        var skillMd: String?
        for entry in entries {
            let name = try normalizeEntryPath(entry.name)
            if name.hasSuffix("/") { continue } // directory entries are implicit
            files.append((name, entry))
            let segments = name.split(separator: "/", omittingEmptySubsequences: false)
            if segments.last == "SKILL.md", segments.count <= 2 {
                if skillMd != nil {
                    throw SkillError(L10n.text("压缩包中有多个 SKILL.md，无法确定技能目录。"))
                }
                skillMd = name
            }
        }
        guard let skillMd else {
            throw SkillError(L10n.text("压缩包中没有找到 SKILL.md。"))
        }

        // "" for a root-level package, "dir/" for a wrapped one.
        let prefix = String(skillMd.dropLast("SKILL.md".count))
        var totalBytes: Int64 = 0
        for (name, entry) in files {
            guard name.hasPrefix(prefix) else { continue }
            totalBytes += entry.uncompressedSize
            if totalBytes > maxArchiveBytes {
                throw SkillError(L10n.text("压缩包内容过大，超过 128 MB 上限。"))
            }
            let relative = String(name.dropFirst(prefix.count))
            let target = staging.appendingPathComponent(relative)
            do {
                let contents = try SkillZipReader.contents(of: entry)
                try FileManager.default.createDirectory(
                    at: target.deletingLastPathComponent(),
                    withIntermediateDirectories: true
                )
                guard !FileManager.default.fileExists(atPath: target.path),
                      FileManager.default.createFile(atPath: target.path, contents: contents)
                else {
                    throw CocoaError(.fileWriteFileExists)
                }
            } catch {
                throw SkillError(L10n.format("文件「%@」无法写入临时目录。", relative))
            }
        }
        _ = try SkillPackage.validatePackage(at: staging)
        return prefix.isEmpty ? nil : String(prefix.dropLast())
    }

    /// One archive name, '/'-normalized and rejected when it could escape the
    /// staging root: absolute paths, drive letters, parent segments.
    private static func normalizeEntryPath(_ fullName: String) throws -> String {
        let name = fullName.replacingOccurrences(of: "\\", with: "/")
        let unsafe = name.isEmpty
            || name.hasPrefix("/")
            || (name.count >= 2 && name[name.index(name.startIndex, offsetBy: 1)] == ":")
            || name.split(separator: "/", omittingEmptySubsequences: false)
                .contains { $0 == ".." || $0 == "." }
        if unsafe {
            throw SkillError(L10n.text("压缩包包含不安全的路径。"))
        }
        return name
    }

    /// The frontmatter block between the opening and closing '---' lines, as a
    /// flat key→scalar map; one nested level (e.g. `metadata:`) folds into
    /// "parent.child" keys. Values may be single- or double-quoted.
    private static func frontmatter(_ text: String) -> [String: String] {
        var fields: [String: String] = [:]
        let lines = text.replacingOccurrences(of: "\r\n", with: "\n").split(
            separator: "\n",
            omittingEmptySubsequences: false
        )
        guard let first = lines.first, first.trimmingCharacters(in: .whitespaces) == "---" else {
            return fields
        }

        var parent: String?
        for line in lines.dropFirst() {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed == "---" || trimmed == "..." { break }
            if line.hasPrefix(" ") || line.hasPrefix("\t") {
                if let parent, let pair = splitField(trimmed) {
                    fields["\(parent).\(pair.key)"] = pair.value
                }
                continue
            }
            guard let pair = splitField(String(line)) else { continue }
            if pair.value.isEmpty {
                parent = pair.key // a block mapping like "metadata:" — keep the parent name
                continue
            }
            parent = nil
            fields[pair.key] = pair.value
        }
        return fields
    }

    /// The markdown body — everything after the closing frontmatter marker.
    private static func frontmatterBody(_ text: String) -> String {
        let lines = text.replacingOccurrences(of: "\r\n", with: "\n").split(
            separator: "\n",
            omittingEmptySubsequences: false
        )
        guard let first = lines.first, first.trimmingCharacters(in: .whitespaces) == "---" else {
            return text
        }
        for (index, line) in lines.dropFirst().enumerated() {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed == "---" || trimmed == "..." {
                return lines.dropFirst(index + 2).joined(separator: "\n")
            }
        }
        return text
    }

    private static func splitField(_ line: String) -> (key: String, value: String)? {
        guard let colon = line.firstIndex(of: ":"), colon > line.startIndex else { return nil }
        let key = line[..<colon].trimmingCharacters(in: .whitespaces)
        let value = unquote(String(line[line.index(after: colon)...]).trimmingCharacters(in: .whitespaces))
        return key.isEmpty ? nil : (key, value)
    }

    private static func unquote(_ value: String) -> String {
        guard value.count >= 2 else { return value }
        let first = value.first!, last = value.last!
        return (first == "\"" && last == "\"") || (first == "'" && last == "'")
            ? String(value.dropFirst().dropLast())
            : value
    }

    /// The first "# …" heading in the markdown body, if any.
    private static func heading(_ body: String) -> String? {
        for line in body.replacingOccurrences(of: "\r\n", with: "\n").split(separator: "\n") {
            let trimmed = line.drop(while: { $0 == " " || $0 == "\t" })
            if trimmed.hasPrefix("# "), trimmed.count > 2 {
                return String(trimmed.dropFirst(2)).trimmingCharacters(in: .whitespaces)
            }
        }
        return nil
    }
}

// MARK: - Package helpers shared by the installer, the store and the archive

/// Package-directory helpers used by `SkillStore` (the app-owned skill
/// library) and `SkillArchive` (user zip imports): validation, the SHA-256
/// digest, safe enumeration and copy. The digest matches the Windows
/// `SkillPackage` byte-for-byte so copies hash identically on both platforms.
enum SkillPackage {
    /// The install sidecar written next to agent-deployed copies by older app
    /// versions. The library never writes one, but legacy packages may carry
    /// it and the digest must skip it to stay compatible.
    static let metadataFileName = ".wechatbridge-install.json"

    static func validatePackage(at root: URL) throws -> String {
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: root.path, isDirectory: &isDirectory),
              isDirectory.boolValue
        else {
            throw SkillError(L10n.text("技能包目录不存在。"))
        }
        guard FileManager.default.fileExists(
            atPath: root.appendingPathComponent("SKILL.md").path
        ) else {
            throw SkillError(L10n.text("技能包缺少 SKILL.md。"))
        }
        return try packageDigest(at: root)
    }

    /// Per file: the UTF-8 '/'-joined relative path, one NUL byte, the file
    /// bytes, one NUL byte — enumerated in sorted order with the install
    /// sidecar skipped.
    static func packageDigest(at root: URL) throws -> String {
        var hasher = SHA256()
        for file in try SkillPackageFiles.enumerate(root: root) {
            if file.relativePath == metadataFileName { continue }
            hasher.update(data: Data(file.relativePath.utf8))
            hasher.update(data: [0])
            hasher.update(data: try Data(contentsOf: file.url))
            hasher.update(data: [0])
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    static func copyPackage(from source: URL, to destination: URL) throws {
        let fileManager = FileManager.default
        try fileManager.createDirectory(at: destination, withIntermediateDirectories: true)
        for file in try SkillPackageFiles.enumerate(root: source) {
            let target = destination.appendingPathComponent(file.relativePath)
            try fileManager.createDirectory(
                at: target.deletingLastPathComponent(),
                withIntermediateDirectories: true
            )
            try fileManager.copyItem(at: file.url, to: target)
        }
    }
}

/// Sorted file enumeration with the safety checks every package shares: the
/// root or any member being a symlink rejects the package, as does any member
/// that is neither file nor directory.
enum SkillPackageFiles {
    struct File {
        let url: URL
        let relativePath: String
    }

    static func enumerate(root: URL) throws -> [File] {
        let fileManager = FileManager.default
        let keys: [URLResourceKey] = [.isDirectoryKey, .isRegularFileKey, .isSymbolicLinkKey]
        let rootValues = try root.resourceValues(forKeys: Set(keys))
        if rootValues.isSymbolicLink == true {
            throw SkillError(L10n.text("技能包不能包含符号链接。"))
        }
        guard rootValues.isDirectory == true else {
            throw SkillError(L10n.text("技能包目录无效。"))
        }
        guard let enumerator = fileManager.enumerator(
            at: root,
            includingPropertiesForKeys: keys,
            options: [],
            errorHandler: { _, error in
                assertionFailure(error.localizedDescription)
                return false
            }
        ) else {
            throw SkillError(L10n.text("无法读取技能包。"))
        }

        let base = root.standardizedFileURL.path
        var files: [File] = []
        for case let url as URL in enumerator {
            let values = try url.resourceValues(forKeys: Set(keys))
            if values.isSymbolicLink == true {
                throw SkillError(L10n.text("技能包不能包含符号链接。"))
            }
            if values.isDirectory == true { continue }
            guard values.isRegularFile == true else {
                throw SkillError(L10n.text("技能包只能包含普通文件和目录。"))
            }
            let path = url.standardizedFileURL.path
            guard path.hasPrefix(base + "/") else {
                throw SkillError(L10n.text("技能包包含越界路径。"))
            }
            files.append(File(url: url, relativePath: String(path.dropFirst(base.count + 1))))
        }
        return files.sorted { $0.relativePath < $1.relativePath }
    }
}

/// The minimal deflate-free zip writer used for 导出 ZIP: stored entries only,
/// produced byte-for-byte identically on macOS and Windows.
enum SkillZip {
    static func write(directory: URL, rootName: String, to destination: URL) throws {
        let files = try SkillPackageFiles.enumerate(root: directory)
        try FileManager.default.createDirectory(
            at: destination.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        var archive = Data()
        var central = Data()

        for file in files {
            let name = "\(rootName)/\(file.relativePath)"
            let nameData = Data(name.utf8)
            let contents = try Data(contentsOf: file.url)
            let crc = CRC32.checksum(contents)
            let offset = UInt32(archive.count)
            archive.appendLE(UInt32(0x04034b50))
            archive.appendLE(UInt16(20))
            archive.appendLE(UInt16(0x0800))
            archive.appendLE(UInt16(0))
            archive.appendLE(UInt16(0))
            archive.appendLE(UInt16(0))
            archive.appendLE(crc)
            archive.appendLE(UInt32(contents.count))
            archive.appendLE(UInt32(contents.count))
            archive.appendLE(UInt16(nameData.count))
            archive.appendLE(UInt16(0))
            archive.append(nameData)
            archive.append(contents)

            central.appendLE(UInt32(0x02014b50))
            central.appendLE(UInt16(20))
            central.appendLE(UInt16(20))
            central.appendLE(UInt16(0x0800))
            central.appendLE(UInt16(0))
            central.appendLE(UInt16(0))
            central.appendLE(UInt16(0))
            central.appendLE(crc)
            central.appendLE(UInt32(contents.count))
            central.appendLE(UInt32(contents.count))
            central.appendLE(UInt16(nameData.count))
            central.appendLE(UInt16(0))
            central.appendLE(UInt16(0))
            central.appendLE(UInt16(0))
            central.appendLE(UInt16(0))
            central.appendLE(UInt32(0))
            central.appendLE(offset)
            central.append(nameData)
        }

        let centralOffset = UInt32(archive.count)
        archive.append(central)
        archive.appendLE(UInt32(0x06054b50))
        archive.appendLE(UInt16(0))
        archive.appendLE(UInt16(0))
        archive.appendLE(UInt16(files.count))
        archive.appendLE(UInt16(files.count))
        archive.appendLE(UInt32(central.count))
        archive.appendLE(centralOffset)
        archive.appendLE(UInt16(0))
        try archive.write(to: destination, options: .atomic)
    }
}

/// The read half of `SkillZip`: central-directory scan, then per-entry local
/// headers. Stored entries pass through; deflated ones go through zlib's raw
/// inflate. Anything else — encryption, zip64, a torn archive — reads as
/// "压缩包无法读取或已损坏。" rather than being half-extracted.
enum SkillZipReader {
    struct Entry {
        let name: String
        let method: UInt16
        let crc32: UInt32
        let compressedSize: Int64
        let uncompressedSize: Int64
        /// Offset of the entry's file data inside the archive.
        let dataOffset: Int
        let archive: Data
    }

    static func entries(archivePath: URL) throws -> [Entry] {
        guard let data = try? Data(contentsOf: archivePath) else {
            throw SkillError(L10n.text("压缩包无法读取或已损坏。"))
        }
        return try entries(in: data)
    }

    static func contents(of entry: Entry) throws -> Data {
        let end = entry.dataOffset + Int(entry.compressedSize)
        guard end <= entry.archive.count else {
            throw SkillError(L10n.text("压缩包无法读取或已损坏。"))
        }
        let compressed = entry.archive.subdata(in: entry.dataOffset..<end)
        let contents: Data
        switch entry.method {
        case 0:
            contents = compressed
        case 8:
            contents = try inflate(raw: compressed, uncompressedSize: entry.uncompressedSize)
        default:
            throw SkillError(L10n.text("压缩包无法读取或已损坏。"))
        }
        guard CRC32.checksum(contents) == entry.crc32 else {
            throw SkillError(L10n.text("压缩包无法读取或已损坏。"))
        }
        return contents
    }

    private static func entries(in data: Data) throws -> [Entry] {
        // End-of-central-directory: signature 0x06054b50 inside the last
        // 64 KB + 22 bytes.
        let searchFloor = max(0, data.count - 22 - 0xFFFF)
        var eocd: Int?
        var cursor = data.count - 22
        while cursor >= searchFloor {
            if data.uint32LE(cursor) == 0x06054b50 {
                eocd = cursor
                break
            }
            cursor -= 1
        }
        guard let eocd else {
            throw SkillError(L10n.text("压缩包无法读取或已损坏。"))
        }
        let entryCount = Int(data.uint16LE(eocd + 10))
        var offset = Int(data.uint32LE(eocd + 16))

        var entries: [Entry] = []
        for _ in 0..<entryCount {
            guard offset + 46 <= data.count, data.uint32LE(offset) == 0x02014b50 else {
                throw SkillError(L10n.text("压缩包无法读取或已损坏。"))
            }
            let flags = data.uint16LE(offset + 8)
            let method = data.uint16LE(offset + 10)
            let crc32 = data.uint32LE(offset + 16)
            let compressedSize = data.uint32LE(offset + 20)
            let uncompressedSize = data.uint32LE(offset + 24)
            let nameLength = Int(data.uint16LE(offset + 28))
            let extraLength = Int(data.uint16LE(offset + 30))
            let commentLength = Int(data.uint16LE(offset + 32))
            let localOffset = Int(data.uint32LE(offset + 42))
            let nameStart = offset + 46
            guard nameStart + nameLength <= data.count,
                  compressedSize != 0xFFFFFFFF, uncompressedSize != 0xFFFFFFFF,
                  flags & 0x1 == 0, // encrypted entries are out of scope
                  let name = String(
                      data: data.subdata(in: nameStart..<nameStart + nameLength),
                      encoding: .utf8
                  ) ?? String(
                      data: data.subdata(in: nameStart..<nameStart + nameLength),
                      encoding: .isoLatin1
                  )
            else {
                throw SkillError(L10n.text("压缩包无法读取或已损坏。"))
            }

            // The local header carries its own (often different) extra field,
            // so the data offset must be recomputed per entry.
            guard localOffset + 30 <= data.count,
                  data.uint32LE(localOffset) == 0x04034b50
            else {
                throw SkillError(L10n.text("压缩包无法读取或已损坏。"))
            }
            let localNameLength = Int(data.uint16LE(localOffset + 26))
            let localExtraLength = Int(data.uint16LE(localOffset + 28))
            let dataOffset = localOffset + 30 + localNameLength + localExtraLength
            guard dataOffset + Int(compressedSize) <= data.count else {
                throw SkillError(L10n.text("压缩包无法读取或已损坏。"))
            }

            entries.append(Entry(
                name: name,
                method: method,
                crc32: crc32,
                compressedSize: Int64(compressedSize),
                uncompressedSize: Int64(uncompressedSize),
                dataOffset: dataOffset,
                archive: data
            ))
            offset = nameStart + nameLength + extraLength + commentLength
        }
        return entries
    }

    /// Raw-deflate inflate through zlib (-15 window bits, no zlib header).
    private static func inflate(raw data: Data, uncompressedSize: Int64) throws -> Data {
        guard uncompressedSize >= 0, uncompressedSize <= SkillArchive.maxArchiveBytes else {
            throw SkillError(L10n.text("压缩包无法读取或已损坏。"))
        }
        var stream = z_stream()
        guard inflateInit2_(
            &stream,
            -15,
            zlibVersion(),
            Int32(MemoryLayout<z_stream>.stride)
        ) == Z_OK else {
            throw SkillError(L10n.text("压缩包无法读取或已损坏。"))
        }
        defer { inflateEnd(&stream) }

        if uncompressedSize == 0 { return Data() }
        var output = Data(count: Int(uncompressedSize))
        let status: Int32 = data.withUnsafeBytes { input in
            output.withUnsafeMutableBytes { buffer in
                stream.next_in = UnsafeMutablePointer(
                    mutating: input.baseAddress?.assumingMemoryBound(to: Bytef.self)
                )
                stream.avail_in = uInt(input.count)
                stream.next_out = buffer.baseAddress?.assumingMemoryBound(to: Bytef.self)
                stream.avail_out = uInt(buffer.count)
                return zlib.inflate(&stream, Z_FINISH)
            }
        }
        guard status == Z_STREAM_END, stream.total_out == uncompressedSize else {
            throw SkillError(L10n.text("压缩包无法读取或已损坏。"))
        }
        return output
    }
}

enum CRC32 {
    static let table: [UInt32] = (0..<256).map { value in
        var crc = UInt32(value)
        for _ in 0..<8 {
            crc = (crc & 1) == 1 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1
        }
        return crc
    }

    static func checksum(_ data: Data) -> UInt32 {
        var crc: UInt32 = 0xFFFFFFFF
        for byte in data {
            crc = table[Int((crc ^ UInt32(byte)) & 0xFF)] ^ (crc >> 8)
        }
        return crc ^ 0xFFFFFFFF
    }
}

extension Data {
    mutating func appendLE(_ value: UInt16) {
        append(UInt8(value & 0xFF))
        append(UInt8((value >> 8) & 0xFF))
    }

    mutating func appendLE(_ value: UInt32) {
        append(UInt8(value & 0xFF))
        append(UInt8((value >> 8) & 0xFF))
        append(UInt8((value >> 16) & 0xFF))
        append(UInt8((value >> 24) & 0xFF))
    }

    func uint16LE(_ offset: Int) -> UInt16 {
        UInt16(self[offset]) | (UInt16(self[offset + 1]) << 8)
    }

    func uint32LE(_ offset: Int) -> UInt32 {
        UInt32(uint16LE(offset)) | (UInt32(uint16LE(offset + 2)) << 16)
    }
}
