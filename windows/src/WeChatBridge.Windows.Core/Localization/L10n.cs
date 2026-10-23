using System.Globalization;
using System.Text.Json;

namespace WeChatBridge.Windows.Core;

/// <summary>Shared UI translations. Chinese remains the source key, matching the macOS tables.</summary>
public static class L10n
{
    // Headless Core consumers keep the original Chinese default; UI entry points load the preference.
    public static string Language { get; set; } = "zh-Hans";
    private static bool English => Language == "en" || (Language == "system"
        && !CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(English ? "en-US" : "zh-CN");
    private static readonly Lazy<Dictionary<string, string>> Translations = new(() =>
    {
        using var stream = typeof(L10n).Assembly.GetManifestResourceStream(
            "WeChatBridge.Windows.Core.Localization.en.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    });
    public static string Text(string key) => English && Translations.Value.TryGetValue(key, out var translated)
        ? translated : key;
    public static string Format(FormattableString value) => string.Format(
        Culture,
        Text(value.Format), value.GetArguments());
}
