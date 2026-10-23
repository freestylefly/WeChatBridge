using System.Text.Json;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Services;

namespace WeChatBridge.Windows.Tests;

/// <summary>Skill id rule, legacy id migration and inline {{skill:id}} references.</summary>
public sealed class SkillReferenceTests
{
    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "WeChatBridgeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    // --- SkillId ---------------------------------------------------------------

    [Theory]
    [InlineData("wechat-article-extract")]
    [InlineData("video-information-reading")]
    [InlineData("a")]
    [InlineData("skill2")]
    [InlineData("a-b-c-1")]
    public void AcceptsSpecCompliantIds(string id) => Assert.True(SkillId.IsValid(id));

    [Theory]
    [InlineData("")]
    [InlineData("wechatbridge.wechat-article-extract")]
    [InlineData("Upper-Case")]
    [InlineData("中文技能")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("double--hyphen")]
    [InlineData("with space")]
    [InlineData("under_score")]
    public void RejectsNonCompliantIds(string id) => Assert.False(SkillId.IsValid(id));

    [Fact]
    public void RejectsIdsLongerThanSixtyFourCharacters()
    {
        Assert.True(SkillId.IsValid(new string('a', 64)));
        Assert.False(SkillId.IsValid(new string('a', 65)));
    }

    [Fact]
    public void MigratesLegacyIds()
    {
        Assert.Equal("wechat-article-extract", SkillId.Migrate("wechatbridge.wechat-article-extract"));
        Assert.Equal("video-information-reading", SkillId.Migrate("wechatbridge.video-information-reading"));
        Assert.Equal("custom-skill", SkillId.Migrate("custom-skill"));
    }

    [Fact]
    public void MigratesASceneInPlace()
    {
        var scene = new WeChatScene
        {
            Instruction = "用 {{skill:wechatbridge.wechat-article-extract}} 提取",
            OutputSpec = "无",
            RequiredSkillIDs = ["wechatbridge.wechat-article-extract", "wechat-article-extract", "other"],
        };

        Assert.True(SkillId.Migrate(scene));
        Assert.Equal("用 {{skill:wechat-article-extract}} 提取", scene.Instruction);
        Assert.Equal(["wechat-article-extract", "other"], scene.RequiredSkillIDs);
        Assert.False(SkillId.Migrate(scene));
    }

    [Fact]
    public void ShippedCatalogUsesSpecCompliantIds()
    {
        var root = SkillService.FindResourcesRoot(AppContext.BaseDirectory)
            ?? SkillService.FindResourcesRoot(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "shared"));
        Assert.NotNull(root);
        var catalog = OfficialSkillCatalog.LoadFrom(root!);
        Assert.All(catalog.Skills, skill => Assert.True(SkillId.IsValid(skill.Id), skill.Id));
    }

    [Fact]
    public void CatalogRejectsNonCompliantIds()
    {
        var root = TempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "Skills"));
        File.WriteAllText(Path.Combine(root, "Skills", "catalog.json"),
            """{"schema_version":1,"skills":[{"id":"bad.id","name":"A","summary":"","version":"1.0.0","package":null,"supported_agents":["doubao"]}]}""");
        var error = Assert.Throws<SkillException>(() => OfficialSkillCatalog.LoadFrom(root));
        Assert.Contains("bad.id", error.Message);
    }

    // --- SkillReference ---------------------------------------------------------

    [Fact]
    public void ParsesReferencesInOrderWithoutDuplicates()
    {
        const string text = "{{skill:b-skill}}开头，中间（{{skill:a-skill}}），再次{{skill:b-skill}}。结尾{{skill:c}}";
        Assert.Equal(["b-skill", "a-skill", "c"], SkillReference.Parse(text));
    }

    [Theory]
    [InlineData("{{skill:}}")]
    [InlineData("{{ skill:x }}")]
    [InlineData("{skill:x}")]
    [InlineData("{{Skill:x}}")]
    [InlineData("没有引用")]
    public void IgnoresMalformedTokens(string text) => Assert.Empty(SkillReference.Parse(text));

    [Fact]
    public void ReportsInvalidIds()
    {
        const string text = "{{skill:Bad.Id}} 和 {{skill:good}}";
        Assert.Equal(["Bad.Id"], SkillReference.Invalid(text));
        Assert.Equal(["good"], SkillReference.Parse(text));
    }

    [Fact]
    public void ReplaceOnlyTouchesValidTokens()
    {
        const string text = "先 {{skill:a}}，再 {{skill:Bad.Id}}，最后 {{skill:a}}。";
        var result = SkillReference.Replace(text, id => $"[{id}]");
        Assert.Equal("先 [a]，再 {{skill:Bad.Id}}，最后 [a]。", result);
        Assert.Equal("", SkillReference.Replace(null, id => id));
    }

    [Fact]
    public void TokenRoundTripsThroughParse() =>
        Assert.Equal(["wechat-article-extract"], SkillReference.Parse(SkillReference.Token("wechat-article-extract")));

    // --- Scene integration ------------------------------------------------------

    [Fact]
    public void EffectiveSkillIdsPutInlineReferencesFirst()
    {
        var scene = new WeChatScene
        {
            Instruction = "用 {{skill:b}} 处理",
            OutputSpec = "附上 {{skill:c}}",
            RequiredSkillIDs = ["a", "b"],
        };
        Assert.Equal(["b", "c", "a"], scene.EffectiveSkillIDs());
    }

    [Fact]
    public void OfficialScenesReferenceTheirSkillsInline()
    {
        foreach (var scene in SceneSettings.StarterScenes().Where(s => s.RequiredSkillIDs.Count > 0))
        {
            Assert.All(scene.RequiredSkillIDs, id => Assert.True(SkillId.IsValid(id), id));
            Assert.Equal(scene.RequiredSkillIDs, SkillReference.Parse(scene.Instruction));
        }
    }

    [Fact]
    public void StoreLoadMigratesLegacySkillIds()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, SceneStore.FileName), """
            {
              "scenes": [
                {
                  "id": "own",
                  "name": "我的",
                  "instruction": "用 {{skill:wechatbridge.video-information-reading}} 读",
                  "outputSpec": "",
                  "requiredSkillIDs": ["wechatbridge.video-information-reading"],
                  "enabled": true
                },
                {
                  "id": "wechatflow.official.article-extract",
                  "name": "旧官方",
                  "instruction": "旧的提示词",
                  "outputSpec": "",
                  "requiredSkillIDs": ["wechatbridge.wechat-article-extract"],
                  "isOfficial": true
                }
              ]
            }
            """);

        var settings = new SceneStore(directory).Load();

        var own = settings.Scenes.Single(s => s.Id == "own");
        Assert.Equal("用 {{skill:video-information-reading}} 读", own.Instruction);
        Assert.Equal(["video-information-reading"], own.RequiredSkillIDs);

        var official = settings.Scenes.Single(s => s.Id == "wechatflow.official.article-extract");
        Assert.Equal(["wechat-article-extract"], official.RequiredSkillIDs);
        Assert.Contains("{{skill:wechat-article-extract}}", official.Instruction);
    }

    [Fact]
    public void UpdateSceneDerivesRequiredSkillsFromReferences()
    {
        var directory = TempDirectory();
        using var service = new SceneService(configDirectory: directory);
        var scene = service.AddScene();

        scene.Instruction = "先用 {{skill:a}}，再用 {{skill:b}}";
        scene.RequiredSkillIDs = ["stale"];
        service.UpdateScene(scene);
        Assert.Equal(["a", "b"],
            service.LoadSettings().Scenes.Single(s => s.Id == scene.Id).RequiredSkillIDs);

        scene.Instruction = "只用 {{skill:b}}";
        service.UpdateScene(scene);
        Assert.Equal(["b"],
            service.LoadSettings().Scenes.Single(s => s.Id == scene.Id).RequiredSkillIDs);
    }

    [Fact]
    public void PackageV3RoundTripsAndOlderPackagesStillImport()
    {
        var directory = TempDirectory();
        using var service = new SceneService(configDirectory: directory);
        var scene = new WeChatScene
        {
            Id = "pkg.scene",
            Name = "包",
            Instruction = "用 {{skill:wechat-article-extract}}",
            PackageVersion = "1.0.0",
        };

        var json = service.ExportPackageJson(scene);
        using (var document = JsonDocument.Parse(json))
        {
            Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("wechat-article-extract",
                document.RootElement.GetProperty("requiredSkillIDs")[0].GetString());
        }

        var v3 = Path.Combine(directory, "v3.json");
        File.WriteAllText(v3, json);
        var v2 = Path.Combine(directory, "v2.json");
        File.WriteAllText(v2, """
            {
              "schemaVersion": 2,
              "id": "legacy.scene",
              "name": "旧包",
              "version": "1.0.0",
              "instruction": "整理",
              "requiredSkillIDs": ["wechatbridge.wechat-article-extract"]
            }
            """);

        var report = service.ImportScenePackages([v3, v2]);

        Assert.Empty(report.Errors);
        Assert.Equal(2, report.Imported);
        var scenes = service.LoadSettings().Scenes;
        Assert.Equal("用 {{skill:wechat-article-extract}}", scenes.Single(s => s.Id == "pkg.scene").Instruction);
        Assert.Equal(["wechat-article-extract"], scenes.Single(s => s.Id == "legacy.scene").RequiredSkillIDs);
    }

}
