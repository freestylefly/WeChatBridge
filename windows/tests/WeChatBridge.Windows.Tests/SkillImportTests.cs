using System.IO.Compression;
using System.Text;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Services;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// User-supplied skill zips: <see cref="SkillArchive"/> extraction safety and
/// frontmatter parsing, <see cref="SkillStore.Import"/> bookkeeping, and the
/// <c>SkillService</c> path that turns an import into a card a scene can
/// reference.
/// </summary>
public sealed class SkillImportTests : IDisposable
{
    private readonly TempInbox _fixture = new();
    private DateTimeOffset _now = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

    public void Dispose() => _fixture.Dispose();

    private SkillStore Store() => new(
        root: Path.Combine(_fixture.Root, "Skills"),
        configDirectory: Path.Combine(_fixture.Root, "Config"),
        backupRoot: Path.Combine(_fixture.Root, "SkillBackups"),
        clock: () => _now);

    private static string SkillMd(string name, string description = "演示技能",
        string? extra = null) =>
        $"---\nname: {name}\ndescription: {description}\n{extra ?? ""}---\n\n# 演示\n\nDo things.\n";

    /// <summary>Writes a zip whose entries are verbatim relative paths.</summary>
    private string WriteZip(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(_fixture.Root, $"pkg-{Guid.NewGuid():N}.zip");
        using (var stream = new FileStream(path, FileMode.CreateNew))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }
        return path;
    }

    private (string Directory, SkillPackageInfo Info) Extract(string zipPath) =>
        SkillArchive.ExtractToStaging(zipPath, Path.Combine(_fixture.Root, "staging"));

    // MARK: - Archive extraction

    [Fact]
    public void WrappedArchiveExtractsAndParsesFrontmatter()
    {
        var zip = WriteZip(
            ("my-skill/SKILL.md", SkillMd("my-skill", "读取剪贴板", "metadata:\n  version: 1.2.0\n")),
            ("my-skill/references/guide.md", "guide"),
            ("my-skill/scripts/run.py", "print(1)"));

        var (dir, info) = Extract(zip);
        try
        {
            Assert.Equal("my-skill", info.Id);
            Assert.Equal("演示", info.DisplayName); // the first "# " heading
            Assert.Equal("读取剪贴板", info.Summary);
            Assert.Equal("1.2.0", info.Version);
            Assert.True(File.Exists(Path.Combine(dir, "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(dir, "references", "guide.md")));
            Assert.True(File.Exists(Path.Combine(dir, "scripts", "run.py")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RootLevelSkillMdIsAcceptedWithFallbackId()
    {
        // Flat archive: no wrapping directory, so the frontmatter name rules.
        var zip = WriteZip(("SKILL.md", SkillMd("flat-skill")), ("notes.txt", "x"));

        var (dir, info) = Extract(zip);
        try
        {
            Assert.Equal("flat-skill", info.Id);
            Assert.Equal("1.0.0", info.Version); // no version field → default
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MissingFrontmatterNameFallsBackToWrappingDirectory()
    {
        var zip = WriteZip(("dir-name/SKILL.md", "---\ndescription: 没有 name\n---\n# T\n"));

        var (_, info) = Extract(zip);
        Assert.Equal("dir-name", info.Id);
    }

    [Fact]
    public void MissingSkillMdIsRejected()
    {
        var zip = WriteZip(("a/readme.md", "x"));
        var error = Assert.Throws<SkillException>(() => Extract(zip));
        Assert.Contains("SKILL.md", error.Message);
    }

    [Fact]
    public void TwoSkillMdFilesAreAmbiguous()
    {
        var zip = WriteZip(
            ("a/SKILL.md", SkillMd("a")), ("b/SKILL.md", SkillMd("b")));
        Assert.Throws<SkillException>(() => Extract(zip));
    }

    [Fact]
    public void TraversalAndAbsolutePathsAreRejected()
    {
        Assert.Throws<SkillException>(() =>
            Extract(WriteZip(("a/SKILL.md", SkillMd("a")), ("../evil.txt", "x"))));
        Assert.Throws<SkillException>(() =>
            Extract(WriteZip(("a/SKILL.md", SkillMd("a")), ("/abs/x.txt", "x"))));
        Assert.Throws<SkillException>(() =>
            Extract(WriteZip(("a/SKILL.md", SkillMd("a")), ("C:/evil.txt", "x"))));
        Assert.Throws<SkillException>(() =>
            Extract(WriteZip(("a/SKILL.md", SkillMd("a")), ("a/..\\evil.txt", "x"))));
    }

    [Fact]
    public void InvalidFrontmatterNameIsRejected()
    {
        var zip = WriteZip(("x/SKILL.md", SkillMd("Bad Name")));
        var error = Assert.Throws<SkillException>(() => Extract(zip));
        Assert.Contains("Bad Name", error.Message);
    }

    [Fact]
    public void CorruptZipReadsAsUnreadable()
    {
        var path = Path.Combine(_fixture.Root, "broken.zip");
        File.WriteAllText(path, "not a zip");
        Assert.Throws<SkillException>(() => Extract(path));
    }

    // MARK: - Library import and removal

    private string Stage(string id, string content) =>
        Extract(WriteZip(($"{id}/SKILL.md", content))).Directory;

    [Fact]
    public void ImportRegistersAnImportedReadyCopy()
    {
        var dir = Stage("my-skill", SkillMd("my-skill"));
        var store = Store();

        var entry = store.Import(dir, "my-skill", "1.0.0");

        Assert.Equal(SkillStore.ImportedSource, entry.Source);
        Assert.Equal(SkillLibraryState.Ready, store.State("my-skill"));
        Assert.NotNull(store.SkillFile("my-skill"));
    }

    [Fact]
    public void IdenticalReimportIsANoOp()
    {
        var content = SkillMd("my-skill");
        var store = Store();
        store.Import(Stage("my-skill", content), "my-skill", "1.0.0");

        _now = _now.AddMinutes(1);
        store.Import(Stage("my-skill", content), "my-skill", "1.0.0");

        Assert.False(Directory.Exists(Path.Combine(_fixture.Root, "SkillBackups")));
        Assert.Equal(SkillLibraryState.Ready, store.State("my-skill"));
    }

    [Fact]
    public void ChangedReimportBacksUpThePreviousCopy()
    {
        var store = Store();
        store.Import(Stage("my-skill", SkillMd("my-skill", "v1")), "my-skill", "1.0.0");

        _now = _now.AddMinutes(1);
        store.Import(Stage("my-skill", SkillMd("my-skill", "v2")), "my-skill", "1.1.0");

        var backup = Assert.Single(
            Directory.GetDirectories(Path.Combine(_fixture.Root, "SkillBackups")));
        Assert.StartsWith("my-skill-1.0.0-", Path.GetFileName(backup));
        Assert.Equal("1.1.0", store.Entries()["my-skill"].Version);
        Assert.Contains("v2", File.ReadAllText(store.SkillFile("my-skill")!));
    }

    [Fact]
    public void ImportOverAForeignDirectoryKeepsItInBackups()
    {
        var foreign = Path.Combine(_fixture.Root, "Skills", "my-skill");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "SKILL.md"), "someone else's");
        var store = Store();

        store.Import(Stage("my-skill", SkillMd("my-skill")), "my-skill", "1.0.0");

        Assert.Equal(SkillLibraryState.Ready, store.State("my-skill"));
        var backup = Assert.Single(
            Directory.GetDirectories(Path.Combine(_fixture.Root, "SkillBackups")));
        Assert.StartsWith("my-skill-replaced-", Path.GetFileName(backup));
        Assert.Equal("someone else's",
            File.ReadAllText(Path.Combine(backup, "SKILL.md")));
    }

    [Fact]
    public void RemoveMovesTheCopyIntoBackupsAndDropsTheRegistryEntry()
    {
        var store = Store();
        store.Import(Stage("my-skill", SkillMd("my-skill")), "my-skill", "1.0.0");

        Assert.True(store.Remove("my-skill"));

        Assert.Equal(SkillLibraryState.Missing, store.State("my-skill"));
        Assert.False(store.Entries().ContainsKey("my-skill"));
        Assert.Single(Directory.GetDirectories(Path.Combine(_fixture.Root, "SkillBackups")));
        Assert.False(store.Remove("my-skill"));
    }

    // MARK: - Service wiring

    private SkillService MakeService() =>
        new(resourcesRoot: Path.Combine(_fixture.Root, "Resources"),
            stateDirectory: Path.Combine(_fixture.Root, "State"),
            scenes: () => []);

    [Fact]
    public async Task ImportedSkillGetsACardAndPathResolution()
    {
        var zip = WriteZip(("my-skill/SKILL.md", SkillMd("my-skill", "导入测试")));
        var service = MakeService();

        var info = await service.ImportArchiveAsync(zip);

        Assert.Equal("my-skill", info.Id);
        var row = Assert.Single(service.Rows);
        Assert.True(row.IsUserSkill);
        Assert.Equal("导入的技能", row.OriginText);
        Assert.Equal("演示", row.Skill.Name);
        Assert.Equal("导入测试", row.Skill.Summary);
        Assert.Equal("已就绪", row.StatusTitle);
        Assert.Equal("{{skill:my-skill}}", row.ReferenceToken);

        var resolution = service.Resolve("my-skill", AgentId.ChatGptCodex);
        Assert.Equal(SkillRenderMode.Path, resolution.Mode);
        Assert.EndsWith("SKILL.md", resolution.SkillFile);
        Assert.Contains(service.ReferenceableSkills(), s => s.Id == "my-skill");

        // A scene that references it finds the library copy in the prompt.
        var prompt = ScenePrompt.Render(
            new WeChatScene { Instruction = "摘要如下 {{skill:my-skill}}" },
            null, null, service.PromptContext(AgentId.ChatGptCodex));
        Assert.Contains(resolution.SkillFile!, prompt);
    }

    [Fact]
    public async Task ImportCollidingWithAnOfficialIdIsRefused()
    {
        var resources = Path.Combine(_fixture.Root, "Resources", "Skills");
        Directory.CreateDirectory(resources);
        File.WriteAllText(Path.Combine(resources, "catalog.json"),
            """{"schema_version":1,"skills":[{"id":"official-one","name":"官方","summary":"","version":"1.0.0","package":null,"supported_agents":["claude"]}]}""");
        var service = MakeService();
        var zip = WriteZip(("official-one/SKILL.md", SkillMd("official-one")));

        await Assert.ThrowsAsync<SkillException>(() => service.ImportArchiveAsync(zip));
        Assert.DoesNotContain(service.Rows, r => r.IsUserSkill);
    }

    [Fact]
    public async Task RemoveSkillDropsTheCard()
    {
        var zip = WriteZip(("my-skill/SKILL.md", SkillMd("my-skill")));
        var service = MakeService();
        await service.ImportArchiveAsync(zip);
        var row = Assert.Single(service.Rows);

        await service.RemoveSkillAsync(row);

        Assert.Empty(service.Rows);
        Assert.Equal(SkillRenderMode.Unknown,
            service.Resolve("my-skill", AgentId.ChatGptCodex).Mode);
    }

    [Fact]
    public async Task RemoveRejectsOfficialSkills()
    {
        var resources = Path.Combine(_fixture.Root, "Resources", "Skills");
        Directory.CreateDirectory(resources);
        File.WriteAllText(Path.Combine(resources, "catalog.json"),
            """{"schema_version":1,"skills":[{"id":"official-one","name":"官方","summary":"","version":"1.0.0","package":null,"supported_agents":["claude"]}]}""");
        var service = MakeService();

        await Assert.ThrowsAsync<SkillException>(
            () => service.RemoveSkillAsync(Assert.Single(service.Rows)));
    }
}
