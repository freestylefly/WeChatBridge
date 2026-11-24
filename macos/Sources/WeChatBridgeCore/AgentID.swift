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

    /// The primary bundle identifier. See `bundleIdentifiers` for every
    /// identifier the same agent ships under.
    public var bundleIdentifier: String { bundleIdentifiers[0] }

    /// Every bundle identifier this agent is known to ship under, primary first.
    /// WorkBuddy has two: the WeChat-distributed build is
    /// `com.tencent.workbuddy.mac`, the public download is
    /// `com.workbuddy.workbuddy` (issue #44).
    public var bundleIdentifiers: [String] {
        switch self {
        case .chatGPTCodex: return ["com.openai.codex"]
        case .claude: return ["com.anthropic.claudefordesktop"]
        case .doubao: return ["com.bot.pc.doubao"]
        case .qwenWork: return ["com.alibaba.qwenwork"]
        case .workBuddy: return ["com.tencent.workbuddy.mac", "com.workbuddy.workbuddy"]
        case .weSight: return ["ai.wesight.app"]
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
        allCases.first { $0.bundleIdentifiers.contains(bundleIdentifier) }
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
    public let tier: SkillTier
    public let protection: SkillProtection
    public let billing: SkillBilling?
    /// Reserved for third-party authors; nil means WeChatBridge itself.
    public let author: String?
    public let releaseID: String?
    public let cloudOriginID: String?
    public let executionKind: String?

    public init(
        id: String,
        name: String,
        summary: String,
        version: String,
        package: String?,
        supportedAgents: [AgentID],
        tier: SkillTier = .free,
        protection: SkillProtection = .l0,
        billing: SkillBilling? = nil,
        author: String? = nil,
        releaseID: String? = nil,
        cloudOriginID: String? = nil,
        executionKind: String? = nil
    ) {
        self.id = id
        self.name = name
        self.summary = summary
        self.version = version
        self.package = package
        self.supportedAgents = supportedAgents
        self.tier = tier
        self.protection = protection
        self.billing = billing
        self.author = author
        self.releaseID = releaseID
        self.cloudOriginID = cloudOriginID
        self.executionKind = executionKind
    }

    private enum CodingKeys: String, CodingKey {
        case id, name, summary, version, package, supportedAgents, tier, protection, billing, author, releaseID, cloudOriginID, executionKind
    }

    /// Catalogs written before paid skills omit the monetization fields;
    /// they decode as free, unprotected, official skills.
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(String.self, forKey: .id)
        name = try container.decode(String.self, forKey: .name)
        summary = try container.decode(String.self, forKey: .summary)
        version = try container.decode(String.self, forKey: .version)
        package = try container.decodeIfPresent(String.self, forKey: .package)
        supportedAgents = try container.decode([AgentID].self, forKey: .supportedAgents)
        tier = try container.decodeIfPresent(SkillTier.self, forKey: .tier) ?? .free
        protection = try container.decodeIfPresent(SkillProtection.self, forKey: .protection) ?? .l0
        billing = try container.decodeIfPresent(SkillBilling.self, forKey: .billing)
        author = try container.decodeIfPresent(String.self, forKey: .author)
        releaseID = try container.decodeIfPresent(String.self, forKey: .releaseID)
        cloudOriginID = try container.decodeIfPresent(String.self, forKey: .cloudOriginID)
        executionKind = try container.decodeIfPresent(String.self, forKey: .executionKind)
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
            // A protected skill's body lives in the cloud; shipping it in the
            // bundle would hand out the very asset the protection guards.
            guard !skill.protection.isRemote || skill.package == nil else {
                throw SkillError(L10n.format("受保护技能「%@」不能随 App 附带技能包。", skill.id))
            }
            guard skill.tier != .metered || (skill.billing?.points ?? 0) > 0 else {
                throw SkillError(L10n.format("按量计费技能「%@」缺少有效的计费规则。", skill.id))
            }
        }
        return catalog
    }
}
