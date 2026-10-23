using WeChatBridge.Windows.Core;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeChatBridge.Windows.Core;

/// <summary>
/// A target that can carry an Agent Skill. Ported from AgentID.swift; raw values
/// are identical on both platforms because scenes and catalog JSON name them.
/// </summary>
[JsonConverter(typeof(AgentIdConverter))]
public enum AgentId
{
    ChatGptCodex,
    Claude,
    Doubao,
    QwenWork,
    WorkBuddy,
    WeSight,
    DeepSeekHarness,
}

public static class AgentIds
{
    public static string RawValue(this AgentId id) => id switch
    {
        AgentId.ChatGptCodex => "chatGPTCodex",
        AgentId.Claude => "claude",
        AgentId.Doubao => "doubao",
        AgentId.QwenWork => "qwenWork",
        AgentId.WorkBuddy => "workBuddy",
        AgentId.DeepSeekHarness => "deepSeekHarness",
        AgentId.WeSight => "weSight",
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    public static AgentId? FromRawValue(string raw) => raw switch
    {
        "chatGPTCodex" => AgentId.ChatGptCodex,
        "claude" => AgentId.Claude,
        "doubao" => AgentId.Doubao,
        "qwenWork" => AgentId.QwenWork,
        "workBuddy" => AgentId.WorkBuddy,
        "deepSeekHarness" => AgentId.DeepSeekHarness,
        "weSight" => AgentId.WeSight,
        _ => null,
    };

    public static string DisplayName(this AgentId id) => id switch
    {
        AgentId.ChatGptCodex => "ChatGPT / Codex",
        AgentId.Claude => "Claude",
        AgentId.Doubao => L10n.Text("豆包"),
        AgentId.QwenWork => L10n.Text("千问"),
        AgentId.WorkBuddy => "WorkBuddy",
        AgentId.DeepSeekHarness => "DeepSeek Harness",
        AgentId.WeSight => "WeSight",
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    public static AgentId? Matching(ShareAction action) => action switch
    {
        ShareAction.Codex => AgentId.ChatGptCodex,
        ShareAction.Claude => AgentId.Claude,
        ShareAction.Doubao => AgentId.Doubao,
        ShareAction.Qwen => AgentId.QwenWork,
        ShareAction.WorkBuddy => AgentId.WorkBuddy,
        ShareAction.DeepSeekHarness => AgentId.DeepSeekHarness,
        ShareAction.WeSight => AgentId.WeSight,
        _ => null,
    };

    /// <summary>
    /// The <c>AgentID.matching(bundleIdentifier:)</c> port. Windows stores an
    /// AUMID, an AppID or an exe name where macOS kept a bundle identifier, so
    /// custom targets match on the known registrations rather than equality.
    /// </summary>
    public static AgentId? MatchingBundleId(string? bundleId) => bundleId switch
    {
        var id when id is not null && id.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase) => AgentId.DeepSeekHarness,
        null => null,
        var id when id.StartsWith("OpenAI.Codex_", StringComparison.Ordinal) => AgentId.ChatGptCodex,
        var id when id.StartsWith("Claude_", StringComparison.Ordinal) => AgentId.Claude,
        "Doubao.ChatApp" => AgentId.Doubao,
        "WorkBuddy.WorkBuddy" => AgentId.WorkBuddy,
        var id when id.Contains("WeSight", StringComparison.OrdinalIgnoreCase) => AgentId.WeSight,
        var id when id.Contains("Qianwen", StringComparison.OrdinalIgnoreCase)
            || id.Contains("QwenWork", StringComparison.OrdinalIgnoreCase) => AgentId.QwenWork,
        _ => null,
    };

    public static IReadOnlyList<AgentId> All { get; } = Enum.GetValues<AgentId>();
}

public sealed class AgentIdConverter : JsonConverter<AgentId>
{
    public override AgentId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        return raw is not null && AgentIds.FromRawValue(raw) is { } id
            ? id
            : throw new JsonException($"Unknown agent id: {raw}");
    }

    public override void Write(Utf8JsonWriter writer, AgentId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.RawValue());
}

/// <summary>One official skill package shipped with the app.</summary>
public sealed record OfficialSkill(
    string Id,
    string Name,
    string Summary,
    string Version,
    /// <summary>Directory under Resources/Skills; null = not shipped yet.</summary>
    string? Package,
    IReadOnlyList<AgentId> SupportedAgents);

public sealed record OfficialSkillCatalog(int SchemaVersion, IReadOnlyList<OfficialSkill> Skills)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// catalog.json is authored snake_case (schema_version, supported_agents);
    /// macOS decodes with .convertFromSnakeCase, so this loader must not reuse
    /// the camelCase inbox options.
    /// </summary>
    private static readonly JsonSerializerOptions CatalogJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new AgentIdConverter() },
    };

    public static OfficialSkillCatalog LoadFrom(string resourcesRoot)
    {
        var path = Path.Combine(resourcesRoot, "Skills", "catalog.json");
        var catalog = JsonSerializer.Deserialize<OfficialSkillCatalog>(
            File.ReadAllText(path), CatalogJsonOptions)
            ?? throw new SkillException(L10n.Text("技能清单内容无效。"));
        if (catalog.SchemaVersion != CurrentSchemaVersion)
            throw new SkillException(L10n.Text("技能清单版本不受支持。"));
        if (catalog.Skills.Select(s => s.Id).Distinct().Count() != catalog.Skills.Count
            || catalog.Skills.Any(s => string.IsNullOrEmpty(s.Id)))
            throw new SkillException(L10n.Text("技能清单内容无效。"));
        if (catalog.Skills.FirstOrDefault(s => !SkillId.IsValid(s.Id)) is { } invalid)
            throw new SkillException(
                L10n.Format($"技能 ID「{invalid.Id}」不符合规范：只能包含小写字母、数字和连字符。"));
        return catalog;
    }
}

public sealed class SkillException(string message) : Exception(message);
