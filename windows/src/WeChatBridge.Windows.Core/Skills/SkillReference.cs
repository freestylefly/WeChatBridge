using System.Text.RegularExpressions;

namespace WeChatBridge.Windows.Core;

/// <summary>
/// Inline skill references inside scene prompts: <c>{{skill:id}}</c>. The id is
/// stored instead of a display name so renaming or translating a skill never
/// breaks a scene; <see cref="ScenePrompt"/> swaps each token for text the
/// destination agent can act on.
/// </summary>
public static class SkillReference
{
    /// <summary>
    /// Anything without whitespace or a closing brace is captured, so malformed
    /// ids surface through <see cref="Invalid"/> instead of silently vanishing.
    /// </summary>
    private static readonly Regex TokenPattern =
        new(@"\{\{skill:([^\s{}]+)\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Token(string id) => $"{{{{skill:{id}}}}}";

    /// <summary>Valid referenced ids in first-appearance order, without duplicates.</summary>
    public static IReadOnlyList<string> Parse(string? text) =>
        Captures(text).Where(SkillId.IsValid).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Referenced ids that break the <see cref="SkillId"/> rule.</summary>
    public static IReadOnlyList<string> Invalid(string? text) =>
        Captures(text).Where(id => !SkillId.IsValid(id)).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Replaces every token with <paramref name="render"/>'s text. Tokens whose
    /// id is invalid are left verbatim — the editor warns about them instead.
    /// Legacy dotted ids are handed over too, so migration can rewrite them.
    /// </summary>
    public static string Replace(string? text, Func<string, string> render)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";
        return TokenPattern.Replace(text, match =>
        {
            var id = match.Groups[1].Value;
            return SkillId.IsValid(id) || SkillId.Migrate(id) != id ? render(id) : match.Value;
        });
    }

    private static IEnumerable<string> Captures(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : TokenPattern.Matches(text).Select(m => m.Groups[1].Value);
}
