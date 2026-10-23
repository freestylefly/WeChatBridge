using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WeChatBridge.Windows.Core;

/// <summary>
/// One locally stored scene. Ported from WeChatScene in Scene.swift; the JSON
/// shape is identical on both platforms so a scene package exported on macOS
/// imports on Windows unchanged.
/// </summary>
public sealed class WeChatScene : IJsonOnDeserialized
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Instruction { get; set; } = "";
    public string OutputSpec { get; set; } = "";
    public List<string> Keywords { get; set; } = [];
    public bool Enabled { get; set; }
    public string PackageVersion { get; set; } = "1.0.0";
    public string Author { get; set; } = "";
    public string Applicability { get; set; } = "";
    public List<string> RequiredSkillIDs { get; set; } = [];
    public List<AgentId> CompatibleAgents { get; set; } = new(AgentIds.All);
    public bool IsOfficial { get; set; }

    /// <summary>
    /// Every skill the scene depends on: inline <c>{{skill:id}}</c> references
    /// in the prompt first, then declared ids not referenced inline.
    /// </summary>
    public IReadOnlyList<string> EffectiveSkillIDs() =>
        SkillReference.Parse(Instruction)
            .Concat(SkillReference.Parse(OutputSpec))
            .Concat(RequiredSkillIDs)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    void IJsonOnDeserialized.OnDeserialized()
    {
        // macOS decodes a missing isOfficial from the official id prefix.
        if (!IsOfficial && Id.StartsWith("wechatflow.", StringComparison.Ordinal))
            IsOfficial = true;
    }
}

/// <summary>
/// The public, article-distributable form. Excludes local switches and group
/// bindings so importing a newer package cannot overwrite the user's choices.
/// </summary>
public sealed class ScenePackage
{
    /// <summary>3 adds inline <c>{{skill:id}}</c> references inside the prompt text.</summary>
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Version { get; set; }
    public string Author { get; set; } = "";
    public string Applicability { get; set; } = "";
    public List<string> Keywords { get; set; } = [];
    public required string Instruction { get; set; }
    public string OutputSpec { get; set; } = "";
    public List<string> RequiredSkillIDs { get; set; } = [];
    public List<AgentId> CompatibleAgents { get; set; } = new(AgentIds.All);
    public bool IsOfficial { get; set; }

    public static ScenePackage FromScene(WeChatScene scene) => new()
    {
        Id = scene.Id,
        Name = scene.Name,
        Version = scene.PackageVersion,
        Author = scene.Author,
        Applicability = scene.Applicability,
        Keywords = new(scene.Keywords),
        Instruction = scene.Instruction,
        OutputSpec = scene.OutputSpec,
        RequiredSkillIDs = new(scene.EffectiveSkillIDs()),
        CompatibleAgents = new(scene.CompatibleAgents),
        IsOfficial = scene.IsOfficial,
    };

    public WeChatScene ToScene() => new()
    {
        Id = Id,
        Name = Name,
        Summary = Applicability,
        Instruction = Instruction,
        OutputSpec = OutputSpec,
        Keywords = new(Keywords),
        Enabled = false,
        PackageVersion = Version,
        Author = Author,
        Applicability = Applicability,
        RequiredSkillIDs = new(RequiredSkillIDs),
        CompatibleAgents = new(CompatibleAgents),
        IsOfficial = IsOfficial,
    };
}

/// <summary>
/// Dotted numeric versions give a deterministic update rule without a SemVer
/// dependency. Ported from SceneVersion.
/// </summary>
public readonly struct SceneVersion : IComparable<SceneVersion>
{
    public IReadOnlyList<int> Components { get; }

    private SceneVersion(List<int> components) => Components = components;

    public static SceneVersion? Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return null;
        var parts = raw.Split('.');
        var values = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (part.Length == 0 || !part.All(char.IsDigit) || !int.TryParse(part, out var value))
                return null;
            values.Add(value);
        }
        while (values.Count > 1 && values[^1] == 0)
            values.RemoveAt(values.Count - 1);
        return new SceneVersion(values);
    }

    public int CompareTo(SceneVersion other)
    {
        var count = Math.Max(Components.Count, other.Components.Count);
        for (var i = 0; i < count; i++)
        {
            var left = i < Components.Count ? Components[i] : 0;
            var right = i < other.Components.Count ? other.Components[i] : 0;
            if (left != right)
                return left.CompareTo(right);
        }
        return 0;
    }

    public static bool operator <(SceneVersion a, SceneVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(SceneVersion a, SceneVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(SceneVersion a, SceneVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SceneVersion a, SceneVersion b) => a.CompareTo(b) >= 0;
}

/// <summary>The scene library plus its forward-attachment switch.</summary>
public sealed class SceneSettings
{
    public List<WeChatScene> Scenes { get; set; } = [];
    public string? DefaultSceneID { get; set; }
    public bool AttachToForwards { get; set; }

    public static SceneSettings MakeDefault() => new() { Scenes = StarterScenes() };

    public static List<WeChatScene> StarterScenes() =>
    [
        new WeChatScene
        {
            Id = "wechatflow.starter.customer-review",
            Name = "客户复盘",
            Summary = "提取客户群里的需求、承诺、风险和下一步。",
            Instruction = "阅读附件里的聊天记录，聚焦客户需求、业务结论、承诺和下一步推进。",
            OutputSpec = StandardOutputSpec,
            Keywords = ["客户", "甲方"],
            Author = "微信流",
            Applicability = "适合客户群、售后群和甲方沟通群。",
            IsOfficial = true,
        },
        new WeChatScene
        {
            Id = "wechatflow.starter.project-sync",
            Name = "项目周会",
            Summary = "整理项目进展、阻塞、负责人和截止时间。",
            Instruction = "阅读附件里的聊天记录，整理项目进展、决策、阻塞和待办。",
            OutputSpec = StandardOutputSpec,
            Keywords = ["项目", "周会"],
            Author = "微信流",
            Applicability = "适合项目群、跨团队协作群和固定周会群。",
            IsOfficial = true,
        },
        new WeChatScene
        {
            Id = "wechatflow.starter.daily-summary",
            Name = "日常摘要",
            Summary = "按时间线总结一段群聊，保留关键事实和待办。",
            Instruction = "阅读附件里的微信聊天记录，按时间线总结重要信息，不要逐条复述。",
            OutputSpec = StandardOutputSpec,
            Author = "微信流",
            Applicability = "通用群聊摘要场景。",
            IsOfficial = true,
        },
        new WeChatScene
        {
            Id = "wechatflow.official.article-extract",
            Name = "公众号文章提取",
            Summary = "从聊天记录中找出公众号文章，提取正文并整理成 Markdown。",
            Instruction = "读取附件中的聊天记录，找出公众号文章链接或分享卡片，用 {{skill:wechat-article-extract}} 提取标题、公众号、发布时间、正文和图片，并保留原文链接。",
            OutputSpec = "按文章逐篇输出 Markdown：标题、公众号、发布时间、核心摘要、正文、图片、原文链接。无法访问的文章明确标记。",
            Keywords = ["公众号", "文章"],
            Author = "微信流",
            Applicability = "适合包含公众号文章分享的群聊和收藏群。",
            RequiredSkillIDs = ["wechat-article-extract"],
            IsOfficial = true,
        },
        new WeChatScene
        {
            Id = "wechatflow.official.video-reading",
            Name = "视频信息读取",
            Summary = "读取聊天里的视频链接或文件，提炼逐字稿、摘要和关键时间点。",
            Instruction = "读取附件中的聊天记录，找出视频链接或本地视频文件，用 {{skill:video-information-reading}} 提取可获得的逐字稿、摘要、关键结论和时间点，并保留来源。",
            OutputSpec = "输出来源、时长、逐字稿或摘要、关键结论、关键时间点和无法读取的部分。",
            Keywords = ["视频", "抖音", "B站"],
            Author = "微信流",
            Applicability = "适合经常分享视频链接或视频文件的群聊。",
            RequiredSkillIDs = ["video-information-reading"],
            IsOfficial = true,
        },
    ];

    public const string StandardOutputSpec =
        "输出以下六项：1. 要点；2. 结论；3. 待办；4. 风险；5. 负责人；6. 截止时间。没有信息的项目明确写“无”。";

    public void InstallOfficialScenes()
    {
        var existing = Scenes.Select(s => s.Id).ToHashSet();
        Scenes.AddRange(StarterScenes().Where(s => !existing.Contains(s.Id)));
        foreach (var starter in StarterScenes())
        {
            var index = Scenes.FindIndex(s => s.Id == starter.Id && s.IsOfficial);
            if (index < 0)
                continue;
            // Official scenes are read-only templates, so their prompt and skill
            // wiring follow the shipped version rather than a stale stored copy.
            Scenes[index].CompatibleAgents = new(starter.CompatibleAgents);
            Scenes[index].Instruction = starter.Instruction;
            Scenes[index].OutputSpec = starter.OutputSpec;
            Scenes[index].RequiredSkillIDs = new(starter.RequiredSkillIDs);
        }
        foreach (var scene in Scenes)
            SkillId.Migrate(scene);
        Normalize();
    }

    public List<WeChatScene> EnabledScenes => Scenes.Where(s => s.Enabled).ToList();

    public WeChatScene? Scene(string? id) =>
        id is null ? null : Scenes.FirstOrDefault(s => s.Id == id && s.Enabled);

    /// <summary>Enabled scenes in library order, regardless of binding order.</summary>
    public List<WeChatScene> ScenesFor(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        return Scenes.Where(s => s.Enabled && set.Contains(s.Id)).ToList();
    }

    public WeChatScene? DefaultScene() => Scene(DefaultSceneID);

    public void Add(WeChatScene scene)
    {
        Scenes.Add(scene);
        Normalize();
    }

    public void Remove(string id)
    {
        Scenes.RemoveAll(s => s.Id == id);
        Normalize();
    }

    /// <summary>Replace by id, preserving the stored enabled switch.</summary>
    public void Replace(WeChatScene scene)
    {
        var index = Scenes.FindIndex(s => s.Id == scene.Id);
        if (index < 0)
        {
            Add(scene);
            return;
        }
        scene.Enabled = Scenes[index].Enabled;
        Scenes[index] = scene;
        Normalize();
    }

    public WeChatScene CopiedAsUserTask(WeChatScene scene) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Name = $"{scene.Name} 副本",
        Summary = scene.Summary,
        Instruction = scene.Instruction,
        OutputSpec = scene.OutputSpec,
        Keywords = new(scene.Keywords),
        Enabled = true,
        PackageVersion = scene.PackageVersion,
        Applicability = scene.Applicability,
        RequiredSkillIDs = new(scene.RequiredSkillIDs),
        CompatibleAgents = new(scene.CompatibleAgents),
        IsOfficial = false,
    };

    private void Normalize()
    {
        var seen = new HashSet<string>();
        Scenes = Scenes.Where(s => s.Id.Length > 0 && seen.Add(s.Id)).ToList();
        if (!Scenes.Any(s => s.Id == DefaultSceneID && s.Enabled))
            DefaultSceneID = null;
    }
}

/// <summary>What is remembered about one chat group.</summary>
public sealed class GroupMemory : IJsonOnDeserialized
{
    public string DisplayName { get; set; } = "";
    public List<string> BoundSceneIDs { get; set; } = [];
    public string? LastSceneID { get; set; }
    public DateTimeOffset? LastSummaryAt { get; set; }
    public HashSet<string> Senders { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
        // macOS compatibility: the legacy single-binding key upgrades to a list.
        if (BoundSceneIDs.Count == 0
            && ExtensionData?.TryGetValue("boundSceneID", out var legacy) == true
            && legacy.ValueKind == JsonValueKind.String)
        {
            BoundSceneIDs = [legacy.GetString()!];
        }
        var seen = new HashSet<string>();
        BoundSceneIDs = BoundSceneIDs.Where(id => id.Length > 0 && seen.Add(id)).ToList();
    }

    public static GroupMemory Advancing(
        GroupMemory? existing,
        string displayName,
        string sceneId,
        IReadOnlySet<string> senders,
        DateTimeOffset? end,
        DateTimeOffset? at = null)
    {
        var memory = existing ?? new GroupMemory { DisplayName = displayName };
        memory.LastSceneID = sceneId;
        if (end is { } e && (memory.LastSummaryAt is null || e > memory.LastSummaryAt))
            memory.LastSummaryAt = e;
        memory.Senders.UnionWith(senders);
        if (memory.Senders.Count > 200)
            memory.Senders = new HashSet<string>(memory.Senders.Take(200), StringComparer.Ordinal);
        memory.UpdatedAt = at ?? DateTimeOffset.UtcNow;
        return memory;
    }
}

/// <summary>Case/diacritic-insensitive name folding, matching macOS GroupName.</summary>
public static class GroupName
{
    public static string Normalize(string raw)
    {
        var trimmed = raw.Trim();
        var decomposed = trimmed.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    public static string NormalizeSender(string raw) => Normalize(raw);
}

/// <summary>Parses "群名 (123)" group titles; falls back to the longest line.</summary>
public static class GroupTitleParser
{
    public readonly record struct Title(string Name, int? MemberCount);

    private static readonly Regex MemberCount =
        new(@"[（(]\s*(\d{1,5})\s*[)）]\s*$", RegexOptions.Compiled);

    public static Title? Parse(IEnumerable<string> lines)
    {
        var candidates = lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        foreach (var line in candidates)
        {
            var match = MemberCount.Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var count))
            {
                var name = line[..match.Index].Trim();
                if (name.Length > 0)
                    return new Title(name, count);
            }
        }
        var best = candidates.OrderByDescending(l => l.Length).FirstOrDefault();
        return best is null ? null : new Title(best, null);
    }
}

public enum SceneMatchSource { Binding, Keyword, Fingerprint, LastUsed, DefaultScene, None }

public sealed record SceneResolution(
    IReadOnlyList<WeChatScene> Scenes,
    string? GroupName,
    bool UsedFingerprint,
    SceneMatchSource Source)
{
    public WeChatScene? Scene => Scenes.Count > 0 ? Scenes[0] : null;
}

/// <summary>Ported from SceneResolver: binding → keyword → fingerprint → lastUsed → default.</summary>
public static class SceneResolver
{
    public static SceneResolution Resolve(
        string? groupName,
        SceneSettings settings,
        IReadOnlyDictionary<string, GroupMemory> memories,
        IReadOnlySet<string>? senders = null,
        bool allowDefault = true)
    {
        var normalized = groupName is null ? null : GroupName.Normalize(groupName);
        if (normalized is not null && memories.TryGetValue(normalized, out var memory))
        {
            var scenes = settings.ScenesFor(memory.BoundSceneIDs);
            if (scenes.Count > 0)
                return new SceneResolution(scenes, memory.DisplayName, false, SceneMatchSource.Binding);
            if (KeywordMatch(memory.DisplayName, settings) is { } bound)
                return new SceneResolution([bound], memory.DisplayName, false, SceneMatchSource.Keyword);
        }

        if (groupName is not null && KeywordMatch(groupName, settings) is { } byName)
            return new SceneResolution([byName], groupName, false, SceneMatchSource.Keyword);

        if (senders is { Count: > 0 }
            && GroupFingerprint.Match(senders, memories) is { } key
            && memories.TryGetValue(key, out var fingerprinted))
        {
            var scenes = settings.ScenesFor(fingerprinted.BoundSceneIDs);
            if (scenes.Count > 0)
                return new SceneResolution(scenes, fingerprinted.DisplayName, true, SceneMatchSource.Fingerprint);
            if (settings.Scene(fingerprinted.LastSceneID) is { } last)
                return new SceneResolution([last], fingerprinted.DisplayName, true, SceneMatchSource.LastUsed);
            if (KeywordMatch(fingerprinted.DisplayName, settings) is { } fallback)
                return new SceneResolution([fallback], fingerprinted.DisplayName, true, SceneMatchSource.Keyword);
        }

        if (normalized is not null
            && memories.TryGetValue(normalized, out var remembered)
            && settings.Scene(remembered.LastSceneID) is { } rememberedScene)
        {
            return new SceneResolution([rememberedScene], remembered.DisplayName, false, SceneMatchSource.LastUsed);
        }

        var defaultScene = allowDefault ? settings.DefaultScene() : null;
        return new SceneResolution(
            defaultScene is null ? [] : [defaultScene],
            groupName,
            false,
            defaultScene is null ? SceneMatchSource.None : SceneMatchSource.DefaultScene);
    }

    private static WeChatScene? KeywordMatch(string displayName, SceneSettings settings)
    {
        var name = GroupName.Normalize(displayName);
        WeChatScene? best = null;
        var bestLength = 0;
        foreach (var scene in settings.EnabledScenes)
        {
            foreach (var raw in scene.Keywords)
            {
                var keyword = GroupName.Normalize(raw);
                if (keyword.Length == 0 || !name.Contains(keyword, StringComparison.Ordinal))
                    continue;
                if (best is null || keyword.Length > bestLength)
                {
                    best = scene;
                    bestLength = keyword.Length;
                }
            }
        }
        return best;
    }
}

/// <summary>Group identification by sender-set overlap, ported from GroupFingerprint.</summary>
public static class GroupFingerprint
{
    public const int MinimumSenderCount = 2;
    public const double ScoreThreshold = 0.7;

    public static string? Match(IReadOnlySet<string> senders, IReadOnlyDictionary<string, GroupMemory> memories)
    {
        var current = senders.Select(GroupName.NormalizeSender).Where(s => s.Length > 0).ToHashSet();
        if (current.Count < MinimumSenderCount)
            return null;

        string? best = null;
        double bestScore = 0;
        var tied = false;
        foreach (var (key, memory) in memories)
        {
            var known = memory.Senders.Select(GroupName.NormalizeSender).Where(s => s.Length > 0).ToHashSet();
            if (known.Count < MinimumSenderCount)
                continue;
            var overlap = current.Intersect(known).Count();
            var score = (double)overlap / Math.Min(current.Count, known.Count);
            if (score < ScoreThreshold)
                continue;
            if (best is null || score > bestScore)
            {
                best = key;
                bestScore = score;
                tied = false;
            }
            else if (score == bestScore)
            {
                tied = true;
            }
        }
        return tied ? null : best;
    }
}

/// <summary>Renders the prompt pasted ahead of the files, ported from ScenePrompt.</summary>
public static class ScenePrompt
{
    /// <param name="skills">
    /// Resolves each <c>{{skill:id}}</c> for the destination. Null (no skill
    /// service available) still renders every reference, naming skills by id.
    /// </param>
    public static string? Render(
        WeChatScene scene,
        DateTimeOffset? previousSummaryAt,
        DateTimeOffset? currentEnd = null,
        SkillRenderContext? skills = null,
        CultureInfo? culture = null)
    {
        skills ??= new SkillRenderContext(null, id => new SkillResolution(id, id, SkillRenderMode.Missing));
        var instruction = SkillReference.Replace(scene.Instruction, skills.Phrase).Trim();
        var output = SkillReference.Replace(scene.OutputSpec, skills.Phrase).Trim();
        if (instruction.Length == 0 && output.Length == 0)
            return null;

        var parts = new List<string>();
        if (instruction.Length > 0)
            parts.Add(instruction);
        if (output.Length > 0)
            parts.Add($"输出规范：\n{output}");
        // Inline references already carry their phrase; declared-only skills
        // (official or legacy scenes) get listed under 技能要求. An all-inline
        // scene emits no section at all.
        var inline = SkillReference.Parse(scene.Instruction)
            .Concat(SkillReference.Parse(scene.OutputSpec))
            .ToHashSet(StringComparer.Ordinal);
        var listed = scene.RequiredSkillIDs
            .Where(id => !inline.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .Select(id => $"- 使用{skills.Phrase(id)}")
            .ToList();
        if (listed.Count > 0)
            parts.Add(string.Join("\n", new[] { "技能要求：" }.Concat(listed)));
        if (previousSummaryAt is { } prev && currentEnd is { } end && end > prev)
        {
            var stamp = prev.ToString("yyyy-MM-dd HH:mm", culture ?? CultureInfo.CurrentCulture);
            parts.Add($"续聊要求：只处理 {stamp} 之后的新消息，不要重复上次已经总结过的内容。");
        }
        return string.Join("\n\n", parts);
    }
}
