import CryptoKit
import Foundation

/// A target that can carry an Agent Skill.
public enum AgentID: String, Codable, CaseIterable, Hashable, Identifiable, Sendable {
    case chatGPTCodex
    case claude
    case doubao
    case qwenWork
    case workBuddy
    case weSight

    public var id: String { rawValue }

    public var displayName: String {
        switch self {
        case .chatGPTCodex: return "ChatGPT / Codex"
        case .claude: return "Claude"
        case .doubao: return L10n.text("豆包")
        case .qwenWork: return L10n.text("千问办公")
        case .workBuddy: return "WorkBuddy"
        case .weSight: return "WeSight"
        }
    }

    public var bundleIdentifier: String {
        switch self {
        case .chatGPTCodex: return "com.openai.codex"
        case .claude: return "com.anthropic.claudefordesktop"
        case .doubao: return "com.bot.pc.doubao"
        case .qwenWork: return "com.alibaba.qwenwork"
        case .workBuddy: return "com.tencent.workbuddy.mac"
        case .weSight: return "ai.wesight.app"
        }
    }

    public var logoSuffix: String {
        switch self {
        case .chatGPTCodex: return "chatgpt"
        case .claude: return "claude"
        case .doubao: return "doubao"
        case .qwenWork: return "qwen"
        case .workBuddy: return "workbuddy"
        case .weSight: return "wesight"
        }
    }

    public static func matching(bundleIdentifier: String) -> AgentID? {
        allCases.first { $0.bundleIdentifier == bundleIdentifier }
    }

    public static func matching(_ action: ShareAction) -> AgentID? {
        action.targetBundleIdentifier.flatMap(matching(bundleIdentifier:))
    }
}

/// One official skill package shipped with WeChatBridge.
public struct OfficialSkill: Codable, Hashable, Identifiable, Sendable {
    public let id: String
    public let name: String
    public let summary: String
    public let version: String
    /// Directory under `Resources/Skills`. Nil means the package has not been
    /// supplied yet, but scenes may still reference its stable ID.
    public let package: String?
    public let supportedAgents: [AgentID]

    public init(
        id: String,
        name: String,
        summary: String,
        version: String,
        package: String?,
        supportedAgents: [AgentID]
    ) {
        self.id = id
        self.name = name
        self.summary = summary
        self.version = version
        self.package = package
        self.supportedAgents = supportedAgents
    }
}

public struct OfficialSkillCatalog: Codable, Hashable, Sendable {
    public let schemaVersion: Int
    public let skills: [OfficialSkill]

    public init(schemaVersion: Int = 1, skills: [OfficialSkill]) {
        self.schemaVersion = schemaVersion
        self.skills = skills
    }

    public static func decoder() -> JSONDecoder {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        return decoder
    }

    public static func load(from resourcesRoot: URL) throws -> OfficialSkillCatalog {
        let url = resourcesRoot
            .appendingPathComponent("Skills", isDirectory: true)
            .appendingPathComponent("catalog.json")
        let data = try Data(contentsOf: url)
        let catalog = try decoder().decode(OfficialSkillCatalog.self, from: data)
        guard catalog.schemaVersion == 1 else {
            throw SkillError(L10n.text("技能清单版本不受支持。"))
        }
        var seen = Set<String>()
        for skill in catalog.skills {
            guard !skill.id.isEmpty, seen.insert(skill.id).inserted else {
                throw SkillError(L10n.text("技能清单内容无效。"))
            }
            guard SkillId.isValid(skill.id) else {
                throw SkillError(L10n.format(
                    "技能 ID「%@」不符合规范：只能包含小写字母、数字和连字符。",
                    skill.id
                ))
            }
        }
        return catalog
    }
}
