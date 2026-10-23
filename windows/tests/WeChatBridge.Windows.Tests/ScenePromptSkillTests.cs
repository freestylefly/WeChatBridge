using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Services;

namespace WeChatBridge.Windows.Tests;

/// <summary>Rendering {{skill:id}} references for a destination, and resolving them.</summary>
public sealed class ScenePromptSkillTests : IDisposable
{
    private readonly TempInbox _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static SkillRenderContext Context(
        AgentId? agent, SkillRenderMode mode, string? file = null) =>
        new(agent, id => new SkillResolution(id, $"名称-{id}", mode, file));

    private static WeChatScene Scene(string instruction, string output = "", params string[] declared) =>
        new() { Instruction = instruction, OutputSpec = output, RequiredSkillIDs = [.. declared] };

    // --- ScenePrompt.Render ------------------------------------------------------

    [Fact]
    public void NativeModeNamesTheSkill()
    {
        var prompt = ScenePrompt.Render(Scene("用 {{skill:a}} 提取"), null,
            skills: Context(AgentId.ChatGptCodex, SkillRenderMode.Native));
        Assert.StartsWith("用 「名称-a」技能（a） 提取", prompt);
        Assert.DoesNotContain("{{skill:", prompt);
    }

    [Fact]
    public void PathModePointsAtTheSkillFileEvenWithSpaces()
    {
        const string file = @"C:\Users\Some One\AppData\Local\WeChatBridge\Skills\a\SKILL.md";
        var prompt = ScenePrompt.Render(Scene("用 {{skill:a}} 提取"), null,
            skills: Context(AgentId.ChatGptCodex, SkillRenderMode.Path, file));
        Assert.Contains($"`{file}`", prompt);
        Assert.Contains("请先阅读并严格按其执行", prompt);
    }

    [Fact]
    public void MissingModeNamesTheSkillAndAsksTheAgentToProceed()
    {
        var prompt = ScenePrompt.Render(Scene("用 {{skill:a}} 提取"), null,
            skills: Context(AgentId.Claude, SkillRenderMode.Missing));
        Assert.Contains("「名称-a」技能（技能文件不可用，请直接完成）", prompt);
    }

    [Fact]
    public void MissingModeReadsTheSameWithoutAnAgent()
    {
        var prompt = ScenePrompt.Render(Scene("用 {{skill:a}} 提取"), null,
            skills: Context(null, SkillRenderMode.Missing));
        Assert.Contains("「名称-a」技能（技能文件不可用，请直接完成）", prompt);
    }

    [Fact]
    public void UnknownModeKeepsTheId()
    {
        var prompt = ScenePrompt.Render(Scene("用 {{skill:ghost}} 提取"), null,
            skills: Context(AgentId.Claude, SkillRenderMode.Unknown));
        Assert.Contains("「ghost」技能（未找到该技能）", prompt);
    }

    [Fact]
    public void EveryOccurrenceIsReplaced()
    {
        var prompt = ScenePrompt.Render(Scene("先 {{skill:a}}，后 {{skill:a}}", "附 {{skill:b}}"), null,
            skills: Context(AgentId.ChatGptCodex, SkillRenderMode.Native))!;
        Assert.Equal(2, prompt.Split("「名称-a」技能（a）").Length - 1);
        Assert.Contains("输出规范：\n附 「名称-b」技能（b）", prompt);
    }

    [Fact]
    public void DeclaredSkillsAreListedOnceAndInlineOnesAreNot()
    {
        var prompt = ScenePrompt.Render(Scene("用 {{skill:a}}", "", "a", "b", "b"), null,
            skills: Context(AgentId.ChatGptCodex, SkillRenderMode.Native))!;
        var section = prompt[prompt.IndexOf("技能要求：", StringComparison.Ordinal)..];
        Assert.Contains("- 使用「名称-b」技能（b）", section);
        Assert.DoesNotContain("名称-a", section);
        Assert.Equal(1, section.Split("名称-b").Length - 1);
    }

    [Fact]
    public void InlineOnlyScenesEmitNoSkillSection()
    {
        // Regression: an all-inline scene used to still get a bare
        // "技能要求：" header followed by boilerplate filler.
        var prompt = ScenePrompt.Render(Scene("用 {{skill:a}}", "", "a"), null,
            skills: Context(AgentId.ChatGptCodex, SkillRenderMode.Native))!;
        Assert.DoesNotContain("技能要求", prompt);
    }

    [Fact]
    public void ScenesWithoutSkillsRenderExactlyAsBefore()
    {
        var prompt = ScenePrompt.Render(Scene("整理", "输出"), null,
            skills: Context(AgentId.Claude, SkillRenderMode.Missing));
        Assert.Equal("整理\n\n输出规范：\n输出", prompt);
        Assert.Equal(prompt, ScenePrompt.Render(Scene("整理", "输出"), null));
    }

    [Fact]
    public void WithoutAContextReferencesFallBackToIds()
    {
        var prompt = ScenePrompt.Render(Scene("用 {{skill:a}}"), null);
        Assert.StartsWith("用 「a」技能", prompt);
    }

    [Fact]
    public void ContinuationStillFollowsTheSkillSection()
    {
        var start = new DateTimeOffset(2026, 9, 17, 8, 30, 0, TimeSpan.Zero);
        var prompt = ScenePrompt.Render(Scene("用 {{skill:a}}", "", "b"), start, start.AddHours(1),
            skills: Context(AgentId.ChatGptCodex, SkillRenderMode.Native),
            culture: System.Globalization.CultureInfo.InvariantCulture)!;
        Assert.True(prompt.IndexOf("技能要求：", StringComparison.Ordinal)
            < prompt.IndexOf("续聊要求：", StringComparison.Ordinal));
    }

    // --- SkillService.Resolve ----------------------------------------------------

    private SkillService MakeService(string? package = "pkg")
    {
        var resources = Path.Combine(_fixture.Root, "Resources");
        var skills = Path.Combine(resources, "Skills");
        Directory.CreateDirectory(skills);
        var packageJson = package is null ? "null" : $"\"{package}\"";
        File.WriteAllText(Path.Combine(skills, "catalog.json"),
            $$"""{"schema_version":1,"skills":[{"id":"demo-skill","name":"演示技能","summary":"","version":"1.0.0","package":{{packageJson}},"supported_agents":["chatGPTCodex","claude"]}]}""");
        if (package is not null)
        {
            Directory.CreateDirectory(Path.Combine(skills, package));
            File.WriteAllText(Path.Combine(skills, package, "SKILL.md"), "# demo\n");
        }
        return new SkillService(resources,
            Path.Combine(_fixture.Root, "State"),
            () => []);
    }

    [Fact]
    public void ResolvePicksTheModeForEachDestination()
    {
        var service = MakeService();
        var libraryFile = Path.Combine(_fixture.Root, "State", "Skills", "demo-skill", "SKILL.md");
        Assert.True(File.Exists(libraryFile));

        var codex = service.Resolve("demo-skill", AgentId.ChatGptCodex);
        Assert.Equal(SkillRenderMode.Path, codex.Mode);
        Assert.Equal(libraryFile, codex.SkillFile);
        Assert.Equal("演示技能", codex.DisplayName);

        // Every destination gets the library path now — readable or not.
        Assert.Equal(SkillRenderMode.Path, service.Resolve("demo-skill", AgentId.Claude).Mode);
        Assert.Equal(SkillRenderMode.Path, service.Resolve("demo-skill", null).Mode);
        Assert.Equal(SkillRenderMode.Unknown, service.Resolve("ghost", AgentId.ChatGptCodex).Mode);
    }

    [Fact]
    public void UnshippedPackageIsMissingNotUnknown()
    {
        var service = MakeService(package: null);
        Assert.Equal(SkillRenderMode.Missing, service.Resolve("demo-skill", AgentId.ChatGptCodex).Mode);
    }

    [Fact]
    public void RowsCountInlineReferencesAndReportTheLibrary()
    {
        MakeService();
        var service = new SkillService(
            Path.Combine(_fixture.Root, "Resources"),
            Path.Combine(_fixture.Root, "State"),
            () =>
            [
                new WeChatScene { Instruction = "用 {{skill:demo-skill}}" },
                new WeChatScene { RequiredSkillIDs = ["demo-skill"] },
                new WeChatScene { Instruction = "无关" },
            ]);

        var row = service.Rows.Single();
        Assert.Equal(2, row.SceneCount);
        Assert.True(row.HasScenes);
        Assert.Equal(SkillLibraryState.Ready, row.LibraryState);
        Assert.Equal("技能库已就绪", row.LibraryText);
        Assert.Null(service.LibraryIssue);
        Assert.Equal([("demo-skill", "演示技能")], service.ReferenceableSkills());
    }

    [Fact]
    public void EditedLibraryCopySurfacesAsAnIssue()
    {
        MakeService();
        File.WriteAllText(Path.Combine(_fixture.Root, "State", "Skills", "demo-skill", "SKILL.md"), "# mine\n");
        var catalog = Path.Combine(_fixture.Root, "Resources", "Skills", "catalog.json");
        File.WriteAllText(catalog, File.ReadAllText(catalog).Replace("\"1.0.0\"", "\"1.1.0\""));

        var service = new SkillService(
            Path.Combine(_fixture.Root, "Resources"),
            Path.Combine(_fixture.Root, "State"),
            () => []);

        Assert.Contains("demo-skill", service.LibraryIssue);
        Assert.True(service.Rows.Single().LibraryConflict);
        Assert.Equal(SkillRenderMode.Missing, service.Resolve("demo-skill", AgentId.ChatGptCodex).Mode);
    }

    [Fact]
    public void PromptContextRendersTheOfficialScene()
    {
        var service = MakeService();
        var scene = new WeChatScene { Instruction = "用 {{skill:demo-skill}} 处理" };

        var prompt = ScenePrompt.Render(scene, null, skills: service.PromptContext(AgentId.ChatGptCodex));

        Assert.Contains("SKILL.md`", prompt);
        Assert.Contains("「演示技能」技能", prompt);
    }
}
