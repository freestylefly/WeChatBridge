import Foundation

/// One locally stored scene. The package-only metadata stays on the scene so
/// exporting it is lossless, while `enabled` remains a local decision.
public struct WeChatScene: Codable, Hashable, Identifiable, Sendable {
    public var id: String
    public var name: String
    public var summary: String
    public var instruction: String
    public var outputSpec: String
    public var keywords: [String]
    public var enabled: Bool
    public var packageVersion: String
    public var author: String
    public var applicability: String
    public var requiredSkillIDs: [String]
    public var compatibleAgents: [AgentID]
    public var isOfficial: Bool

    public init(
        id: String = UUID().uuidString,
        name: String,
        summary: String = "",
        instruction: String,
        outputSpec: String,
        keywords: [String] = [],
        enabled: Bool = false,
        packageVersion: String = "1.0.0",
        author: String = "",
        applicability: String = "",
        requiredSkillIDs: [String] = [],
        compatibleAgents: [AgentID] = AgentID.allCases,
        isOfficial: Bool = false
    ) {
        self.id = id
        self.name = name
        self.summary = summary
        self.instruction = instruction
        self.outputSpec = outputSpec
        self.keywords = keywords
        self.enabled = enabled
        self.packageVersion = packageVersion
        self.author = author
        self.applicability = applicability
        self.requiredSkillIDs = requiredSkillIDs
        self.compatibleAgents = compatibleAgents
        self.isOfficial = isOfficial
    }

    public init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(String.self, forKey: .id)
        name = try container.decode(String.self, forKey: .name)
        summary = try container.decodeIfPresent(String.self, forKey: .summary) ?? ""
        instruction = try container.decode(String.self, forKey: .instruction)
        outputSpec = try container.decode(String.self, forKey: .outputSpec)
        keywords = try container.decodeIfPresent([String].self, forKey: .keywords) ?? []
        enabled = try container.decodeIfPresent(Bool.self, forKey: .enabled) ?? false
        packageVersion = try container.decodeIfPresent(String.self, forKey: .packageVersion) ?? "1.0.0"
        author = try container.decodeIfPresent(String.self, forKey: .author) ?? ""
        applicability = try container.decodeIfPresent(String.self, forKey: .applicability) ?? ""
        requiredSkillIDs = try container.decodeIfPresent([String].self, forKey: .requiredSkillIDs) ?? []
        compatibleAgents = try container.decodeIfPresent([AgentID].self, forKey: .compatibleAgents)
            ?? AgentID.allCases
        isOfficial = try container.decodeIfPresent(Bool.self, forKey: .isOfficial)
            ?? id.hasPrefix("wechatflow.")
    }

    /// Every skill the scene depends on: inline `{{skill:id}}` references in
    /// the prompt first, then declared ids not referenced inline.
    public var effectiveSkillIDs: [String] {
        var seen = Set<String>()
        return (SkillReference.parse(instruction)
            + SkillReference.parse(outputSpec)
            + requiredSkillIDs)
            .filter { seen.insert($0).inserted }
    }

    private enum CodingKeys: String, CodingKey {
        case id
        case name
        case summary
        case instruction
        case outputSpec
        case keywords
        case enabled
        case packageVersion
        case author
        case applicability
        case requiredSkillIDs
        case compatibleAgents
        case isOfficial
    }
}

/// The public, article-distributable form. It deliberately excludes local
/// switches and group bindings so importing a newer package cannot overwrite
/// the user's choices.
public struct ScenePackage: Codable, Hashable, Sendable {
    /// 3 adds inline `{{skill:id}}` references inside the prompt text.
    public static let currentSchemaVersion = 3

    public let schemaVersion: Int
    public let id: String
    public let name: String
    public let version: String
    public let author: String
    public let applicability: String
    public let keywords: [String]
    public let instruction: String
    public let outputSpec: String
    public let requiredSkillIDs: [String]
    public let compatibleAgents: [AgentID]
    public let isOfficial: Bool

    public init(scene: WeChatScene, schemaVersion: Int = currentSchemaVersion) {
        self.schemaVersion = schemaVersion
        id = scene.id
        name = scene.name
        version = scene.packageVersion
        author = scene.author
        applicability = scene.applicability
        keywords = scene.keywords
        instruction = scene.instruction
        outputSpec = scene.outputSpec
        requiredSkillIDs = scene.effectiveSkillIDs
        compatibleAgents = scene.compatibleAgents
        isOfficial = scene.isOfficial
    }

    public var scene: WeChatScene {
        WeChatScene(
            id: id,
            name: name,
            summary: applicability,
            instruction: instruction,
            outputSpec: outputSpec,
            keywords: keywords,
            enabled: false,
            packageVersion: version,
            author: author,
            applicability: applicability,
            requiredSkillIDs: requiredSkillIDs,
            compatibleAgents: compatibleAgents,
            isOfficial: isOfficial
        )
    }

    public init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        schemaVersion = try container.decodeIfPresent(Int.self, forKey: .schemaVersion) ?? 1
        id = try container.decode(String.self, forKey: .id)
        name = try container.decode(String.self, forKey: .name)
        version = try container.decode(String.self, forKey: .version)
        author = try container.decodeIfPresent(String.self, forKey: .author) ?? ""
        applicability = try container.decodeIfPresent(String.self, forKey: .applicability) ?? ""
        keywords = try container.decodeIfPresent([String].self, forKey: .keywords) ?? []
        instruction = try container.decode(String.self, forKey: .instruction)
        outputSpec = try container.decodeIfPresent(String.self, forKey: .outputSpec) ?? ""
        requiredSkillIDs = try container.decodeIfPresent([String].self, forKey: .requiredSkillIDs) ?? []
        compatibleAgents = try container.decodeIfPresent([AgentID].self, forKey: .compatibleAgents)
            ?? AgentID.allCases
        isOfficial = try container.decodeIfPresent(Bool.self, forKey: .isOfficial) ?? false
    }

    private enum CodingKeys: String, CodingKey {
        case schemaVersion
        case id
        case name
        case version
        case author
        case applicability
        case keywords
        case instruction
        case outputSpec
        case requiredSkillIDs
        case compatibleAgents
        case isOfficial
    }

    public static func decoder() -> JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return decoder
    }

    public static func encoder() -> JSONEncoder {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        return encoder
    }
}

/// Dotted numeric versions are enough for editorial packages and give a
/// deterministic update rule without taking a SemVer dependency.
public struct SceneVersion: Comparable, Hashable, Sendable {
    public let components: [Int]

    public init?(_ raw: String) {
        let parts = raw.split(separator: ".", omittingEmptySubsequences: false)
        guard !parts.isEmpty else { return nil }
        var values: [Int] = []
        for part in parts {
            guard !part.isEmpty,
                  part.allSatisfy(\.isNumber),
                  let value = Int(part)
            else { return nil }
            values.append(value)
        }
        while values.count > 1, values.last == 0 { values.removeLast() }
        components = values
    }

    public static func < (lhs: SceneVersion, rhs: SceneVersion) -> Bool {
        let count = max(lhs.components.count, rhs.components.count)
        for index in 0..<count {
            let left = index < lhs.components.count ? lhs.components[index] : 0
            let right = index < rhs.components.count ? rhs.components[index] : 0
            if left != right { return left < right }
        }
        return false
    }
}

/// The scene library and its forward attachment switch.
public struct SceneSettings: Codable, Hashable, Sendable {
    public var scenes: [WeChatScene]
    public var defaultSceneID: String?
    public var attachToForwards: Bool

    public init(
        scenes: [WeChatScene],
        defaultSceneID: String? = nil,
        attachToForwards: Bool = false
    ) {
        self.scenes = scenes
        self.defaultSceneID = defaultSceneID
        self.attachToForwards = attachToForwards
        normalize()
    }

    public static func makeDefault() -> SceneSettings {
        let scenes = starterScenes
        return SceneSettings(scenes: scenes)
    }

    public static var starterScenes: [WeChatScene] {
        [
            WeChatScene(
                id: "wechatflow.starter.customer-review",
                name: L10n.text("客户复盘"),
                summary: L10n.text("提取客户群里的需求、承诺、风险和下一步。"),
                instruction: L10n.text("阅读附件里的聊天记录，聚焦客户需求、业务结论、承诺和下一步推进。"),
                outputSpec: standardOutputSpec,
                keywords: ["客户", "甲方"],
                enabled: false,
                packageVersion: "1.0.0",
                author: "微信流",
                applicability: L10n.text("适合客户群、售后群和甲方沟通群。"),
                isOfficial: true
            ),
            WeChatScene(
                id: "wechatflow.starter.project-sync",
                name: L10n.text("项目周会"),
                summary: L10n.text("整理项目进展、阻塞、负责人和截止时间。"),
                instruction: L10n.text("阅读附件里的聊天记录，整理项目进展、决策、阻塞和待办。"),
                outputSpec: standardOutputSpec,
                keywords: ["项目", "周会"],
                enabled: false,
                packageVersion: "1.0.0",
                author: "微信流",
                applicability: L10n.text("适合项目群、跨团队协作群和固定周会群。"),
                isOfficial: true
            ),
            WeChatScene(
                id: "wechatflow.starter.daily-summary",
                name: L10n.text("日常摘要"),
                summary: L10n.text("按时间线总结一段群聊，保留关键事实和待办。"),
                instruction: L10n.text("阅读附件里的微信聊天记录，按时间线总结重要信息，不要逐条复述。"),
                outputSpec: standardOutputSpec,
                keywords: [],
                enabled: false,
                packageVersion: "1.0.0",
                author: "微信流",
                applicability: L10n.text("通用群聊摘要场景。"),
                isOfficial: true
            ),
            WeChatScene(
                id: "wechatflow.official.article-extract",
                name: L10n.text("公众号文章提取"),
                summary: L10n.text("从聊天记录中找出公众号文章，提取正文并整理成 Markdown。"),
                instruction: L10n.text("读取附件中的聊天记录，找出公众号文章链接或分享卡片，用 {{skill:wechat-article-extract}} 提取标题、公众号、发布时间、正文和图片，并保留原文链接。"),
                outputSpec: L10n.text("按文章逐篇输出 Markdown：标题、公众号、发布时间、核心摘要、正文、图片、原文链接。无法访问的文章明确标记。"),
                keywords: ["公众号", "文章"],
                enabled: false,
                packageVersion: "1.0.0",
                author: "微信流",
                applicability: L10n.text("适合包含公众号文章分享的群聊和收藏群。"),
                requiredSkillIDs: ["wechat-article-extract"],
                isOfficial: true
            ),
            WeChatScene(
                id: "wechatflow.official.video-reading",
                name: L10n.text("视频信息读取"),
                summary: L10n.text("读取聊天里的视频链接或文件，提炼逐字稿、摘要和关键时间点。"),
                instruction: L10n.text("读取附件中的聊天记录，找出视频链接或本地视频文件，用 {{skill:video-information-reading}} 提取可获得的逐字稿、摘要、关键结论和时间点，并保留来源。"),
                outputSpec: L10n.text("输出来源、时长、逐字稿或摘要、关键结论、关键时间点和无法读取的部分。"),
                keywords: ["视频", "抖音", "B站"],
                enabled: false,
                packageVersion: "1.0.0",
                author: "微信流",
                applicability: L10n.text("适合经常分享视频链接或视频文件的群聊。"),
                requiredSkillIDs: ["video-information-reading"],
                isOfficial: true
            ),
        ]
    }

    public static var standardOutputSpec: String {
        L10n.text("输出以下六项：1. 要点；2. 结论；3. 待办；4. 风险；5. 负责人；6. 截止时间。没有信息的项目明确写“无”。")
    }

    public init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        if let scenes = try container.decodeIfPresent([WeChatScene].self, forKey: .scenes) {
            self.scenes = scenes
            defaultSceneID = try container.decodeIfPresent(String.self, forKey: .defaultSceneID)
            attachToForwards = try container.decodeIfPresent(Bool.self, forKey: .attachToForwards) ?? false
            installOfficialScenes()
            normalize()
            return
        }

        // The old attached-prompt blob had no `scenes` key. Convert each prompt
        // into a scene and keep the surface switches and selection.
        let legacy = try decoder.container(keyedBy: LegacyCodingKeys.self)
        let prompts = try legacy.decodeIfPresent([LegacyPrompt].self, forKey: .prompts) ?? []
        let converted = prompts.enumerated().map { index, prompt in
            WeChatScene(
                id: prompt.id.uuidString,
                name: prompt.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                    ? L10n.format("原 Prompt %d", index + 1)
                    : L10n.format("原 Prompt %d", index + 1),
                summary: L10n.text("由旧版附加 Prompt 迁移。"),
                instruction: prompt.text,
                outputSpec: Self.standardOutputSpec,
                enabled: true,
                packageVersion: "1.0.0",
                author: "",
                applicability: ""
            )
        }
        if converted.isEmpty {
            self = Self.makeDefault()
            return
        }
        scenes = Self.starterScenes + converted
        let selected = try legacy.decodeIfPresent(UUID.self, forKey: .selectedID)?.uuidString
        defaultSceneID = scenes.contains(where: { $0.id == selected }) ? selected : converted.first?.id
        attachToForwards = try container.decodeIfPresent(Bool.self, forKey: .attachToForwards) ?? false
        normalize()
    }

    public mutating func installOfficialScenes() {
        let existing = Set(scenes.map(\.id))
        scenes.append(contentsOf: Self.starterScenes.filter { !existing.contains($0.id) })
        for starter in Self.starterScenes {
            guard let index = scenes.firstIndex(where: { $0.id == starter.id && $0.isOfficial }) else { continue }
            // Official scenes are read-only templates, so their prompt and skill
            // wiring follow the shipped version rather than a stale stored copy.
            scenes[index].compatibleAgents = starter.compatibleAgents
            scenes[index].instruction = starter.instruction
            scenes[index].outputSpec = starter.outputSpec
            scenes[index].requiredSkillIDs = starter.requiredSkillIDs
        }
        for index in scenes.indices {
            SkillId.migrate(&scenes[index])
        }
    }

    public var enabledScenes: [WeChatScene] {
        scenes.filter(\.enabled)
    }

    public func scene(id: String?) -> WeChatScene? {
        guard let id else { return nil }
        return scenes.first { $0.id == id && $0.enabled }
    }

    /// Returns enabled scenes in library order, regardless of binding order.
    public func scenes(ids: [String]) -> [WeChatScene] {
        let ids = Set(ids)
        return scenes.filter { $0.enabled && ids.contains($0.id) }
    }

    public func defaultScene() -> WeChatScene? {
        scene(id: defaultSceneID)
    }

    public mutating func add(_ scene: WeChatScene) {
        scenes.append(scene)
        normalize()
    }

    public mutating func remove(id: String) {
        scenes.removeAll { $0.id == id }
        normalize()
    }

    public mutating func replace(_ scene: WeChatScene) {
        guard let index = scenes.firstIndex(where: { $0.id == scene.id }) else {
            add(scene)
            return
        }
        let enabled = scenes[index].enabled
        var updated = scene
        updated.enabled = enabled
        scenes[index] = updated
        normalize()
    }

    public func copiedAsUserTask(_ scene: WeChatScene) -> WeChatScene {
        WeChatScene(
            name: L10n.format("%@ 副本", scene.name),
            summary: scene.summary,
            instruction: scene.instruction,
            outputSpec: scene.outputSpec,
            keywords: scene.keywords,
            enabled: true,
            packageVersion: scene.packageVersion,
            author: "",
            applicability: scene.applicability,
            requiredSkillIDs: scene.requiredSkillIDs,
            compatibleAgents: scene.compatibleAgents,
            isOfficial: false
        )
    }

    private mutating func normalize() {
        var seen = Set<String>()
        scenes = scenes.filter { !$0.id.isEmpty && seen.insert($0.id).inserted }
        if !scenes.contains(where: { $0.id == defaultSceneID && $0.enabled }) {
            defaultSceneID = nil
        }
    }

    private struct LegacyPrompt: Decodable {
        let id: UUID
        let text: String
    }

    private enum CodingKeys: String, CodingKey {
        case scenes
        case defaultSceneID
        case attachToForwards
    }

    private enum LegacyCodingKeys: String, CodingKey {
        case prompts
        case selectedID
    }
}

public struct GroupMemory: Codable, Hashable, Sendable {
    public var displayName: String
    public var boundSceneIDs: [String]
    public var lastSceneID: String?
    public var lastSummaryAt: Date?
    public var senders: Set<String>
    public var updatedAt: Date

    public init(
        displayName: String,
        boundSceneIDs: [String] = [],
        lastSceneID: String? = nil,
        lastSummaryAt: Date? = nil,
        senders: Set<String> = [],
        updatedAt: Date = Date()
    ) {
        self.displayName = displayName
        var seen = Set<String>()
        self.boundSceneIDs = boundSceneIDs.filter { !$0.isEmpty && seen.insert($0).inserted }
        self.lastSceneID = lastSceneID
        self.lastSummaryAt = lastSummaryAt
        self.senders = senders
        self.updatedAt = updatedAt
    }

    public init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        displayName = try container.decode(String.self, forKey: .displayName)
        if let ids = try container.decodeIfPresent([String].self, forKey: .boundSceneIDs) {
            var seen = Set<String>()
            boundSceneIDs = ids.filter { !$0.isEmpty && seen.insert($0).inserted }
        } else if let id = try container.decodeIfPresent(String.self, forKey: .boundSceneID) {
            boundSceneIDs = [id]
        } else {
            boundSceneIDs = []
        }
        lastSceneID = try container.decodeIfPresent(String.self, forKey: .lastSceneID)
        lastSummaryAt = try container.decodeIfPresent(Date.self, forKey: .lastSummaryAt)
        senders = try container.decodeIfPresent(Set<String>.self, forKey: .senders) ?? []
        updatedAt = try container.decodeIfPresent(Date.self, forKey: .updatedAt) ?? Date()
    }

    public func encode(to encoder: any Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(displayName, forKey: .displayName)
        try container.encode(boundSceneIDs, forKey: .boundSceneIDs)
        try container.encodeIfPresent(lastSceneID, forKey: .lastSceneID)
        try container.encodeIfPresent(lastSummaryAt, forKey: .lastSummaryAt)
        try container.encode(senders, forKey: .senders)
        try container.encode(updatedAt, forKey: .updatedAt)
    }

    private enum CodingKeys: String, CodingKey {
        case displayName
        case boundSceneIDs
        case boundSceneID
        case lastSceneID
        case lastSummaryAt
        case senders
        case updatedAt
    }

    public static func advancing(
        _ existing: GroupMemory?,
        displayName: String,
        sceneID: String,
        senders: Set<String>,
        end: Date?,
        at: Date = Date()
    ) -> GroupMemory {
        var memory = existing ?? GroupMemory(displayName: displayName)
        memory.lastSceneID = sceneID
        if let end, memory.lastSummaryAt == nil || end > memory.lastSummaryAt! {
            memory.lastSummaryAt = end
        }
        memory.senders.formUnion(senders)
        if memory.senders.count > 200 {
            memory.senders = Set(memory.senders.prefix(200))
        }
        memory.updatedAt = at
        return memory
    }
}

public enum GroupName {
    public static func normalize(_ raw: String) -> String {
        raw.trimmingCharacters(in: .whitespacesAndNewlines)
            .folding(options: [.caseInsensitive, .diacriticInsensitive], locale: .current)
    }

    public static func normalizeSender(_ raw: String) -> String {
        normalize(raw)
    }
}

public enum GroupTitleParser {
    public struct Title: Equatable, Sendable {
        public let name: String
        public let memberCount: Int?

        public init(name: String, memberCount: Int?) {
            self.name = name
            self.memberCount = memberCount
        }
    }

    public static func parse(_ lines: [String]) -> Title? {
        let candidates = lines.map {
            $0.trimmingCharacters(in: .whitespacesAndNewlines)
        }.filter { !$0.isEmpty }

        // A group title usually carries a member count. Prefer that over
        // unrelated chrome OCR'd from elsewhere in the same title strip.
        for line in candidates {
            let range = NSRange(line.startIndex..<line.endIndex, in: line)
            if let match = try? NSRegularExpression(pattern: #"[（(]\s*(\d{1,5})\s*[)）]\s*$"#)
                .firstMatch(in: line, range: range),
               let full = Range(match.range(at: 0), in: line),
               let numberRange = Range(match.range(at: 1), in: line),
               let count = Int(line[numberRange]) {
                let name = String(line[..<full.lowerBound]).trimmingCharacters(in: .whitespacesAndNewlines)
                if !name.isEmpty { return Title(name: name, memberCount: count) }
            }
        }
        var best: String?
        for line in candidates where best == nil || line.count > best!.count {
            best = line
        }
        return best.map { Title(name: $0, memberCount: nil) }
    }
}

public enum SceneMatchSource: String, Equatable, Sendable {
    case binding
    case keyword
    case fingerprint
    case lastUsed
    case defaultScene
    case none
}

public struct SceneResolution: Equatable, Sendable {
    public let scenes: [WeChatScene]
    public let groupName: String?
    public let usedFingerprint: Bool
    public let source: SceneMatchSource

    public init(
        scenes: [WeChatScene],
        groupName: String?,
        usedFingerprint: Bool,
        source: SceneMatchSource
    ) {
        self.scenes = scenes
        self.groupName = groupName
        self.usedFingerprint = usedFingerprint
        self.source = source
    }

    public var scene: WeChatScene? { scenes.first }
}

public enum SceneResolver {
    public static func resolve(
        groupName: String?,
        settings: SceneSettings,
        memories: [String: GroupMemory],
        senders: Set<String> = [],
        allowDefault: Bool = true
    ) -> SceneResolution {
        let normalized = groupName.map(GroupName.normalize)
        if let normalized, let memory = memories[normalized] {
            let scenes = settings.scenes(ids: memory.boundSceneIDs)
            if !scenes.isEmpty {
                return SceneResolution(
                    scenes: scenes,
                    groupName: memory.displayName,
                    usedFingerprint: false,
                    source: .binding
                )
            }
            if let scene = keywordMatch(displayName: memory.displayName, settings: settings) {
                return SceneResolution(
                    scenes: [scene],
                    groupName: memory.displayName,
                    usedFingerprint: false,
                    source: .keyword
                )
            }
        }

        if let groupName, let scene = keywordMatch(displayName: groupName, settings: settings) {
            return SceneResolution(scenes: [scene], groupName: groupName, usedFingerprint: false, source: .keyword)
        }

        if !senders.isEmpty, let key = GroupFingerprint.match(senders: senders, memories: memories),
           let memory = memories[key] {
            let scenes = settings.scenes(ids: memory.boundSceneIDs)
            if !scenes.isEmpty {
                return SceneResolution(
                    scenes: scenes,
                    groupName: memory.displayName,
                    usedFingerprint: true,
                    source: .fingerprint
                )
            }
            if let scene = settings.scene(id: memory.lastSceneID) {
                return SceneResolution(
                    scenes: [scene],
                    groupName: memory.displayName,
                    usedFingerprint: true,
                    source: .lastUsed
                )
            }
            if let scene = keywordMatch(displayName: memory.displayName, settings: settings) {
                return SceneResolution(
                    scenes: [scene],
                    groupName: memory.displayName,
                    usedFingerprint: true,
                    source: .keyword
                )
            }
        }

        if let normalized, let memory = memories[normalized],
           let scene = settings.scene(id: memory.lastSceneID) {
            return SceneResolution(
                scenes: [scene],
                groupName: memory.displayName,
                usedFingerprint: false,
                source: .lastUsed
            )
        }

        let fallback = allowDefault ? settings.defaultScene() : nil
        return SceneResolution(
            scenes: fallback.map { [$0] } ?? [],
            groupName: groupName,
            usedFingerprint: false,
            source: fallback == nil ? .none : .defaultScene
        )
    }

    private static func keywordMatch(displayName: String, settings: SceneSettings) -> WeChatScene? {
        let name = GroupName.normalize(displayName)
        var best: (scene: WeChatScene, length: Int)?
        for scene in settings.enabledScenes {
            for raw in scene.keywords {
                let keyword = GroupName.normalize(raw)
                guard !keyword.isEmpty, name.contains(keyword) else { continue }
                if best == nil || keyword.count > best!.length {
                    best = (scene, keyword.count)
                }
            }
        }
        return best?.scene
    }
}

public enum GroupFingerprint {
    public static let minimumSenderCount = 2
    public static let scoreThreshold = 0.7

    public static func match(senders: Set<String>, memories: [String: GroupMemory]) -> String? {
        let current = Set(senders.map(GroupName.normalizeSender).filter { !$0.isEmpty })
        guard current.count >= minimumSenderCount else { return nil }

        var best: (key: String, score: Double)?
        var tied = false
        for (key, memory) in memories {
            let known = Set(memory.senders.map(GroupName.normalizeSender).filter { !$0.isEmpty })
            guard known.count >= minimumSenderCount else { continue }
            let overlap = current.intersection(known).count
            let smaller = min(current.count, known.count)
            let score = Double(overlap) / Double(smaller)
            guard score >= scoreThreshold else { continue }
            if best == nil || score > best!.score {
                best = (key, score)
                tied = false
            } else if score == best!.score {
                tied = true
            }
        }
        return tied ? nil : best?.key
    }
}

public enum ScenePrompt {
    /// - Parameter skills: Resolves each `{{skill:id}}` for the destination.
    ///   Nil (no skill library available) still renders every reference,
    ///   naming skills by id.
    public static func render(
        scene: WeChatScene,
        previousSummaryAt: Date?,
        currentEnd: Date? = nil,
        skills: SkillRenderContext? = nil,
        locale: Locale = .current,
        timeZone: TimeZone = .current
    ) -> String? {
        let context = skills ?? SkillRenderContext(agent: nil) { id in
            SkillResolution(id: id, displayName: id, mode: .missing)
        }
        let instruction = SkillReference.replace(scene.instruction, render: context.phrase)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        let output = SkillReference.replace(scene.outputSpec, render: context.phrase)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !instruction.isEmpty || !output.isEmpty else { return nil }

        var parts: [String] = []
        if !instruction.isEmpty { parts.append(instruction) }
        if !output.isEmpty { parts.append(L10n.format("输出规范：\n%@", output)) }
        // Inline references already carry their phrase; declared-only skills
        // (official or legacy scenes) get listed under 技能要求. An all-inline
        // scene emits no section at all.
        let inline = Set(SkillReference.parse(scene.instruction) + SkillReference.parse(scene.outputSpec))
        var seen = Set<String>()
        let listed = scene.requiredSkillIDs
            .filter { !inline.contains($0) }
            .filter { seen.insert($0).inserted }
            .map { L10n.format("- 使用%@", context.phrase($0)) }
        if !listed.isEmpty {
            parts.append(([L10n.text("技能要求：")] + listed).joined(separator: "\n"))
        }
        if let previousSummaryAt, let currentEnd, currentEnd > previousSummaryAt {
            let formatter = DateFormatter()
            formatter.locale = locale
            formatter.timeZone = timeZone
            formatter.dateFormat = "yyyy-MM-dd HH:mm"
            parts.append(L10n.format("续聊要求：只处理 %@ 之后的新消息，不要重复上次已经总结过的内容。", formatter.string(from: previousSummaryAt)))
        }
        return parts.joined(separator: "\n\n")
    }
}
