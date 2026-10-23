import AppKit
import Combine
import Foundation
import SwiftUI
import WeChatBridgeCore

/// The shipped skill catalogue plus the app-owned skill library: the catalog
/// says what exists and the store keeps the authoritative copy scenes point
/// agents at through `{{skill:id}}` prompt references. Views read from here;
/// no row reaches into the file system itself.
@MainActor
final class SkillLibrary: ObservableObject {
    let resourcesRoot: URL?
    let store: SkillStore

    @Published private(set) var catalog = OfficialSkillCatalog(skills: [])
    @Published private(set) var loadError: String?
    /// Last library sync problem (a conflict or I/O failure); nil when clean.
    @Published private(set) var libraryIssue: String?
    @Published private(set) var revision = 0
    /// User-imported skills, synthesized into catalog-shaped records on every
    /// reload so imported packages get the same card treatment as official
    /// ones. Never contains catalog ids.
    @Published private(set) var userSkills: [OfficialSkill] = []

    var skills: [OfficialSkill] { catalog.skills }
    var allSkills: [OfficialSkill] { catalog.skills + userSkills }

    init(
        resourcesRoot: URL? = SkillLibrary.findResourcesRoot(),
        store: SkillStore = SkillStore()
    ) {
        self.resourcesRoot = resourcesRoot
        self.store = store
        reload()
    }

    func reload() {
        if let resourcesRoot {
            do {
                catalog = try OfficialSkillCatalog.load(from: resourcesRoot)
                loadError = nil
            } catch {
                catalog = OfficialSkillCatalog(skills: [])
                loadError = (error as? LocalizedError)?.errorDescription
                    ?? error.localizedDescription
            }
            syncLibrary(resourcesRoot)
        } else {
            catalog = OfficialSkillCatalog(skills: [])
            loadError = L10n.text("没有找到内置技能清单。")
        }
        loadUserSkills()
        revision += 1
    }

    func skill(id: String) -> OfficialSkill? {
        allSkills.first { $0.id == id }
    }

    // MARK: - The app-owned library

    func libraryState(for skill: OfficialSkill) -> SkillLibraryState {
        store.state(skill.id)
    }

    /// Absolute SKILL.md path while the library copy is ready — the exact
    /// file scene prompts point agents at.
    func libraryFile(for skill: OfficialSkill) -> String? {
        store.skillFile(skill.id)
    }

    /// Every skill a scene can reference: catalog + imported, then stray
    /// library ids.
    func referenceableSkills() -> [(id: String, name: String)] {
        let known = Set(allSkills.map(\.id))
        return allSkills.map { ($0.id, $0.name) }
            + store.entries().keys
                .filter { !known.contains($0) }
                .sorted()
                .map { ($0, $0) }
    }

    /// 导入技能 — unpacks a user-picked zip into the library and reloads so
    /// the new card and scene references pick it up. Errors (corrupt archive,
    /// unsafe paths, invalid frontmatter, an id that an official skill already
    /// owns) surface as `SkillError` for the pane to display.
    @discardableResult
    func importArchive(at url: URL) throws -> SkillPackageInfo {
        let stagingParent = FileManager.default.temporaryDirectory
            .appendingPathComponent("WeChatBridge", isDirectory: true)
        let result = try SkillArchive.extractToStaging(
            archivePath: url,
            stagingParent: stagingParent
        )
        defer { try? FileManager.default.removeItem(at: result.directory) }
        guard !catalog.skills.contains(where: { $0.id == result.info.id }) else {
            throw SkillError(L10n.format(
                "「%@」是内置技能，请修改 SKILL.md 的 name 后再导入。",
                result.info.id
            ))
        }
        try store.import(
            source: result.directory,
            id: result.info.id,
            version: result.info.version
        )
        reload()
        return result.info
    }

    /// 从技能库移除 — the package moves into the backups and the card drops
    /// out. Only user-imported skills can be removed; official ones come back
    /// on the next catalog sync.
    func removeSkill(_ skill: OfficialSkill) throws {
        guard userSkills.contains(where: { $0.id == skill.id }) else {
            throw SkillError(L10n.text("内置技能不能移除。"))
        }
        try store.remove(skill.id)
        reload()
    }

    // MARK: - Scene prompt resolution

    /// Scenes carry skills by reference only: a pointer to the library's
    /// SKILL.md whenever the copy exists, missing for a known skill with no
    /// package, unknown for an id nobody knows.
    func resolve(_ id: String, agent: AgentID?) -> SkillResolution {
        let skill = skill(id: id)
        let file = store.skillFile(id)
        if skill == nil, file == nil {
            return SkillResolution(id: id, displayName: id, mode: .unknown)
        }
        let name = skill?.name ?? id
        return file != nil
            ? SkillResolution(id: id, displayName: name, mode: .path, skillFile: file)
            : SkillResolution(id: id, displayName: name, mode: .missing)
    }

    /// The render context the forward path and the scene preview hand to
    /// `ScenePrompt`. The resolver works off value snapshots so `ScenePrompt`
    /// can render synchronously off the main actor.
    func promptContext(agent: AgentID?) -> SkillRenderContext {
        let byID = Dictionary(allSkills.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        let store = self.store
        return SkillRenderContext(agent: agent) { id in
            let skill = byID[id]
            let file = store.skillFile(id)
            if skill == nil, file == nil {
                return SkillResolution(id: id, displayName: id, mode: .unknown)
            }
            let name = skill?.name ?? id
            return file != nil
                ? SkillResolution(id: id, displayName: name, mode: .path, skillFile: file)
                : SkillResolution(id: id, displayName: name, mode: .missing)
        }
    }

    // MARK: - Internals

    /// Copies shipped official packages into the library. A failure here must
    /// not take the pane down — it is surfaced through `libraryIssue`.
    private func syncLibrary(_ resourcesRoot: URL) {
        do {
            let report = try store.syncOfficial(catalog: catalog, resourcesRoot: resourcesRoot)
            libraryIssue = report.conflicts.isEmpty
                ? nil
                : L10n.format(
                    "技能库中的 %@ 被外部修改，未自动更新。",
                    report.conflicts.joined(separator: "、")
                )
        } catch {
            libraryIssue = (error as? LocalizedError)?.errorDescription
                ?? error.localizedDescription
        }
    }

    /// Rebuilds `userSkills` from the library registry: every imported id that
    /// still has a package on disk becomes a catalog-shaped record. Ids that a
    /// shipped catalog later claims stay official — the catalog wins.
    private func loadUserSkills() {
        let catalogIDs = Set(catalog.skills.map(\.id))
        userSkills = store.entries().values
            .filter {
                $0.source == SkillStore.importedSource
                    && !catalogIDs.contains($0.id)
                    && store.state($0.id) != .missing
            }
            .map { entry in
                let info = try? SkillArchive.readInfo(
                    packageDirectory: store.skillDirectory(entry.id)
                )
                return OfficialSkill(
                    id: entry.id,
                    name: info?.displayName ?? entry.id,
                    summary: info?.summary ?? "",
                    version: entry.version,
                    package: nil,
                    supportedAgents: AgentID.allCases
                )
            }
            .sorted { $0.id < $1.id }
    }

    nonisolated private static func findResourcesRoot() -> URL? {
        let fileManager = FileManager.default
        var starts = [Bundle.main.resourceURL, URL(fileURLWithPath: fileManager.currentDirectoryPath)]
            .compactMap { $0 }
        if starts.isEmpty {
            starts = [Bundle.main.bundleURL]
        }
        for start in starts {
            var candidate = start.standardizedFileURL
            for _ in 0..<8 {
                let catalog = candidate
                    .appendingPathComponent("Skills", isDirectory: true)
                    .appendingPathComponent("catalog.json")
                if fileManager.fileExists(atPath: catalog.path) {
                    return candidate
                }
                let parent = candidate.deletingLastPathComponent()
                if parent == candidate { break }
                candidate = parent
            }
        }
        return nil
    }
}

struct AgentLogo: View {
    let agent: AgentID
    let resourcesRoot: URL?
    var size: CGFloat = 18

    var body: some View {
        Group {
            if let image = AgentLogoCache.image(for: agent, resourcesRoot: resourcesRoot) {
                Image(nsImage: image)
                    .resizable()
                    .interpolation(.high)
                    .aspectRatio(contentMode: .fit)
            } else {
                Image(systemName: "sparkles")
                    .font(.system(size: size * 0.72, weight: .medium))
                    .foregroundStyle(Theme.accent)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .background(Theme.accentSoft)
            }
        }
        .frame(width: size, height: size)
        .clipShape(RoundedRectangle(cornerRadius: size * 0.22, style: .continuous))
        .accessibilityLabel(Text(agent.displayName))
    }
}

@MainActor
private enum AgentLogoCache {
    private static var cache: [String: NSImage?] = [:]

    static func image(for agent: AgentID, resourcesRoot: URL?) -> NSImage? {
        guard let resourcesRoot else { return nil }
        let key = "\(resourcesRoot.path)|\(agent.logoSuffix)"
        if let hit = cache[key] { return hit }
        let directory = resourcesRoot.appendingPathComponent("AppLogos", isDirectory: true)
        let files = (try? FileManager.default.contentsOfDirectory(
            at: directory,
            includingPropertiesForKeys: nil
        )) ?? []
        let match = files.first {
            $0.lastPathComponent.lowercased().contains(agent.logoSuffix.lowercased())
        }
        let image = match.flatMap(NSImage.init(contentsOf:))
        cache[key] = image
        return image
    }
}
