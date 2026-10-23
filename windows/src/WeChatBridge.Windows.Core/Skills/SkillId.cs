using System.Text.RegularExpressions;

namespace WeChatBridge.Windows.Core;

/// <summary>
/// The skill id rule shared by the catalog, the skill library directory name,
/// the SKILL.md frontmatter <c>name</c> and <c>{{skill:id}}</c> references:
/// lowercase letters, digits and single hyphens, at most 64 characters — the
/// Agent Skills naming spec, so one id works as a native install everywhere.
/// </summary>
public static class SkillId
{
    public const int MaxLength = 64;

    private static readonly Regex Pattern =
        new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Pre-spec ids (dotted) mapped to their spec-compliant replacements.</summary>
    private static readonly IReadOnlyDictionary<string, string> Legacy =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["wechatbridge.wechat-article-extract"] = "wechat-article-extract",
            ["wechatbridge.video-information-reading"] = "video-information-reading",
        };

    public static bool IsValid(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= MaxLength && Pattern.IsMatch(id);

    /// <summary>The current id for <paramref name="id"/>; unknown ids pass through unchanged.</summary>
    public static string Migrate(string id) =>
        Legacy.TryGetValue(id, out var current) ? current : id;

    /// <summary>
    /// Rewrites legacy ids in a scene's declared skills and inline references.
    /// Returns true when anything changed so callers only persist real edits.
    /// </summary>
    public static bool Migrate(WeChatScene scene)
    {
        var changed = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ids = new List<string>(scene.RequiredSkillIDs.Count);
        foreach (var id in scene.RequiredSkillIDs)
        {
            var migrated = Migrate(id);
            changed |= migrated != id;
            if (seen.Add(migrated))
                ids.Add(migrated);
            else
                changed = true;
        }
        scene.RequiredSkillIDs = ids;

        var instruction = SkillReference.Replace(scene.Instruction, id => SkillReference.Token(Migrate(id)));
        var output = SkillReference.Replace(scene.OutputSpec, id => SkillReference.Token(Migrate(id)));
        changed |= instruction != scene.Instruction || output != scene.OutputSpec;
        scene.Instruction = instruction;
        scene.OutputSpec = output;
        return changed;
    }
}
