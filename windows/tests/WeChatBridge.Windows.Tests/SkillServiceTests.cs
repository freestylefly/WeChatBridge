using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Services;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// The pane-facing half of the skill system: <c>Services/SkillService</c> is
/// compile-linked from the WPF project (kept WPF-free on purpose, like
/// <c>SceneService</c>), so these tests drive the real facade. The agent
/// install layer was dropped — the service manages the app-owned library and
/// resolves <c>{{skill:id}}</c> references, nothing more. State and resources
/// all live under a throwaway tree — nothing touches the real profile.
/// </summary>
public sealed class SkillServiceTests : IDisposable
{
    private readonly TempInbox _fixture = new();
    private readonly string _resources;
    private readonly string _state;

    public SkillServiceTests()
    {
        _resources = Path.Combine(_fixture.Root, "Resources");
        _state = Path.Combine(_fixture.Root, "State");
        Directory.CreateDirectory(_resources);
    }

    public void Dispose() => _fixture.Dispose();

    private SkillService MakeService(IReadOnlyList<WeChatScene>? scenes = null) =>
        new(_resources, _state, scenes is null ? null : () => scenes);

    private static OfficialSkill Skill(
        string id, string? package, AgentId[] agents, string version = "1.0.0") =>
        new(id, $"技能 {id}", "测试用技能", version, package, agents);

    private void WriteCatalog(params OfficialSkill[] skills)
    {
        var directory = Path.Combine(_resources, "Skills");
        Directory.CreateDirectory(directory);
        var entries = skills.Select(SkillJson);
        File.WriteAllText(
            Path.Combine(directory, "catalog.json"),
            $$"""{"schema_version":1,"skills":[{{string.Join(",", entries)}}]}""");
    }

    private static string SkillJson(OfficialSkill skill) =>
        $$"""{"id":"{{skill.Id}}","name":"{{skill.Name}}","summary":"{{skill.Summary}}","version":"{{skill.Version}}","package":{{(skill.Package is null ? "null" : $"\"{skill.Package}\"")}},"supported_agents":[{{string.Join(",", skill.SupportedAgents.Select(a => $"\"{a.RawValue()}\""))}}]}""";

    private void WritePackage(string package, string content = "# Test Skill\n") =>
        File.WriteAllText(
            Path.Combine(Directory.CreateDirectory(
                Path.Combine(_resources, "Skills", package)).FullName, "SKILL.md"),
            content);

    // MARK: - Catalog loading

    [Fact]
    public void MissingCatalogReadsAsLoadError()
    {
        var service = MakeService();
        Assert.NotNull(service.LoadError);
        Assert.Empty(service.Rows);
        Assert.Equal(0, service.SkillCount);
        Assert.Equal(0, service.ImportedCount);
    }

    [Fact]
    public void InvalidCatalogReadsAsLoadError()
    {
        var directory = Path.Combine(_resources, "Skills");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "catalog.json"),
            """
            {
              "schema_version": 1,
              "skills": [
                {"id":"same","name":"A","summary":"","version":"1.0.0","package":null,"supported_agents":["doubao"]},
                {"id":"same","name":"B","summary":"","version":"1.0.0","package":null,"supported_agents":["claude"]}
              ]
            }
            """);
        var service = MakeService();
        Assert.Equal("技能清单内容无效。", service.LoadError);
        Assert.Empty(service.Rows);
    }

    [Fact]
    public void CatalogProducesOneRowPerSkillWithLibraryState()
    {
        WriteCatalog(
            Skill("wechatbridge-packaged", "pkg",
                [AgentId.ChatGptCodex, AgentId.Doubao, AgentId.WorkBuddy]),
            Skill("wechatbridge-unshipped", null, [AgentId.ChatGptCodex, AgentId.Claude]));
        WritePackage("pkg");
        var service = MakeService(scenes:
            [new WeChatScene { RequiredSkillIDs = ["wechatbridge-packaged"] }]);

        Assert.Equal(2, service.Rows.Count);
        Assert.Equal(2, service.SkillCount);
        Assert.Equal(0, service.ImportedCount);
        Assert.Equal(1, service.ReferencedCount);

        var packaged = service.Rows[0];
        Assert.Equal(1, packaged.SceneCount);
        Assert.True(packaged.HasScenes);
        Assert.False(packaged.IsUserSkill);
        Assert.Equal("官方技能", packaged.OriginText);
        Assert.Equal(SkillLibraryState.Ready, packaged.LibraryState);
        Assert.Equal("技能库已就绪", packaged.LibraryText);
        Assert.Equal("已就绪", packaged.StatusTitle);
        Assert.Equal("{{skill:wechatbridge-packaged}}", packaged.ReferenceToken);
        Assert.Equal(
            Path.Combine(_state, "Skills", "wechatbridge-packaged", "SKILL.md"),
            packaged.LibraryFile);

        var unshipped = service.Rows[1];
        Assert.Equal(SkillLibraryState.Missing, unshipped.LibraryState);
        Assert.Equal("缺技能包", unshipped.StatusTitle);
        Assert.True(unshipped.PackageMissing);
        Assert.Null(unshipped.LibraryFile);
    }

    // MARK: - Resource root detection

    [Fact]
    public void FindResourcesRootWalksUpAndAcceptsTheRepoLayout()
    {
        var start = Path.Combine(_fixture.Root, "app", "bin", "debug");
        Directory.CreateDirectory(start);
        Assert.Null(SkillService.FindResourcesRoot(start));

        // Repo layout: <root>/Resources/Skills/catalog.json.
        WriteCatalog(Skill("a", null, [AgentId.Doubao]));
        Assert.Equal(
            Path.GetFullPath(_resources),
            Path.GetFullPath(SkillService.FindResourcesRoot(start)!));

        // Flat layout wins at the same level: <root>/Skills/catalog.json.
        var flat = Path.Combine(_fixture.Root, "Skills");
        Directory.CreateDirectory(flat);
        File.WriteAllText(Path.Combine(flat, "catalog.json"), "{}");
        Assert.Equal(
            Path.GetFullPath(_fixture.Root),
            Path.GetFullPath(SkillService.FindResourcesRoot(start)!));
    }

    [Fact]
    public void SceneBadgeCountsOnlyMatchingScenes()
    {
        WriteCatalog(Skill("wechatbridge-test", null, [AgentId.Doubao]));
        var service = MakeService(scenes:
        [
            new WeChatScene { RequiredSkillIDs = ["wechatbridge-test"] },
            new WeChatScene { RequiredSkillIDs = ["other", "wechatbridge-test"] },
            new WeChatScene { RequiredSkillIDs = ["other"] },
        ]);
        Assert.Equal(2, Assert.Single(service.Rows).SceneCount);
    }
}
