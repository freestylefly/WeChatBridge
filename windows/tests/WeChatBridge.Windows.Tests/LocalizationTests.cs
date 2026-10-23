using System.Text.Json;
using System.Text.RegularExpressions;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void TranslationsPreserveEveryFormatArgumentAndUseValidCompositeFormats()
    {
        using var stream = typeof(L10n).Assembly.GetManifestResourceStream("WeChatBridge.Windows.Core.Localization.en.json")!;
        var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        var placeholder = new Regex(@"(?<!\{)\{(\d+)(?:[^{}]*)\}");
        foreach (var (key, translation) in translations)
        {
            var arguments = placeholder.Matches(key).Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order().ToArray();
            Assert.Equal(arguments, placeholder.Matches(translation).Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order().ToArray());
            if (arguments.Length > 0)
            {
                var values = Enumerable.Range(0, arguments.Max() + 1).Select(_ => (object)DateTime.UnixEpoch).ToArray();
                var error = Record.Exception(() => string.Format(translation, values));
                Assert.True(error is null, $"Invalid translation format: {key} -> {translation}: {error}");
            }
        }
        Assert.Equal("Save to Folder", translations["沉淀到文件夹"]);
        Assert.Equal("Batch collection", translations["分批收集"]);
    }
}
