using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

/// <summary>The app-owned skill library, driven entirely in temp directories.</summary>
public sealed class SkillStoreTests
{
    private readonly string _root;
    private readonly string _resources;
    private DateTimeOffset _now = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);

    public SkillStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "WeChatBridgeTests", Guid.NewGuid().ToString("N"));
        _resources = Path.Combine(_root, "resources");
        Directory.CreateDirectory(Path.Combine(_resources, "Skills"));
    }

    private SkillStore Store() => new(
        root: Path.Combine(_root, "Skills"),
        configDirectory: Path.Combine(_root, "Config"),
        backupRoot: Path.Combine(_root, "SkillBackups"),
        clock: () => _now);

    private static OfficialSkillCatalog Catalog(string version = "1.0.0", string? package = "pkg") =>
        new(1, [new OfficialSkill("demo-skill", "演示", "", version, package, [AgentId.ChatGptCodex])]);

    private void WritePackage(string body = "# demo\n")
    {
        var directory = Path.Combine(_resources, "Skills", "pkg");
        Directory.CreateDirectory(Path.Combine(directory, "references"));
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), body);
        File.WriteAllText(Path.Combine(directory, "references", "note.md"), "note");
    }

    [Fact]
    public void FirstSyncCopiesThePackageIntoTheLibrary()
    {
        WritePackage();
        var store = Store();

        var report = store.SyncOfficial(Catalog(), _resources);

        Assert.Equal(1, report.Added);
        Assert.Empty(report.Conflicts);
        Assert.Equal(SkillLibraryState.Ready, store.State("demo-skill"));
        var file = store.SkillFile("demo-skill");
        Assert.NotNull(file);
        Assert.Equal(Path.Combine(_root, "Skills", "demo-skill", "SKILL.md"), file);
        Assert.Equal("# demo\n", File.ReadAllText(file!));
        Assert.True(File.Exists(Path.Combine(_root, "Skills", "demo-skill", "references", "note.md")));
        var entry = store.Entries()["demo-skill"];
        Assert.Equal("1.0.0", entry.Version);
        Assert.Equal(SkillStore.OfficialSource, entry.Source);
    }

    [Fact]
    public void SameVersionResyncChangesNothing()
    {
        WritePackage();
        var store = Store();
        store.SyncOfficial(Catalog(), _resources);

        var report = store.SyncOfficial(Catalog(), _resources);

        Assert.Equal(SkillSyncReport.Empty with { Conflicts = report.Conflicts }, report);
        Assert.Empty(report.Conflicts);
        Assert.False(Directory.Exists(Path.Combine(_root, "SkillBackups")));
    }

    [Fact]
    public void NewerBundledVersionBacksUpThenReplaces()
    {
        WritePackage("# v1\n");
        var store = Store();
        store.SyncOfficial(Catalog("1.0.0"), _resources);

        WritePackage("# v2\n");
        _now = _now.AddMinutes(5);
        var report = store.SyncOfficial(Catalog("1.1.0"), _resources);

        Assert.Equal(1, report.Updated);
        Assert.Equal("# v2\n", File.ReadAllText(store.SkillFile("demo-skill")!));
        Assert.Equal("1.1.0", store.Entries()["demo-skill"].Version);
        var backup = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "SkillBackups")));
        Assert.StartsWith("demo-skill-1.0.0-", Path.GetFileName(backup));
        Assert.Equal("# v1\n", File.ReadAllText(Path.Combine(backup, "SKILL.md")));
    }

    [Fact]
    public void ExternallyEditedCopyIsAConflictAndIsNotOverwritten()
    {
        WritePackage("# v1\n");
        var store = Store();
        store.SyncOfficial(Catalog("1.0.0"), _resources);
        var file = Path.Combine(_root, "Skills", "demo-skill", "SKILL.md");
        File.WriteAllText(file, "# my edits\n");

        WritePackage("# v2\n");
        var report = store.SyncOfficial(Catalog("1.1.0"), _resources);

        Assert.Equal(["demo-skill"], report.Conflicts);
        Assert.Equal(0, report.Updated);
        Assert.Equal("# my edits\n", File.ReadAllText(file));
        Assert.Equal(SkillLibraryState.Conflict, store.State("demo-skill"));
        Assert.Null(store.SkillFile("demo-skill"));
    }

    [Fact]
    public void UnregisteredDirectoryIsAConflict()
    {
        WritePackage();
        var foreign = Path.Combine(_root, "Skills", "demo-skill");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "SKILL.md"), "# someone else\n");
        var store = Store();

        var report = store.SyncOfficial(Catalog(), _resources);

        Assert.Equal(["demo-skill"], report.Conflicts);
        Assert.Equal("# someone else\n", File.ReadAllText(Path.Combine(foreign, "SKILL.md")));
    }

    [Fact]
    public void SkillsWithoutAPackageAreSkipped()
    {
        var store = Store();

        var report = store.SyncOfficial(Catalog(package: null), _resources);

        Assert.Equal(0, report.Added);
        Assert.Equal(SkillLibraryState.Missing, store.State("demo-skill"));
        Assert.Null(store.SkillFile("demo-skill"));
    }

    [Fact]
    public void BackupsArePrunedToTheNewestTwenty()
    {
        WritePackage("# v0\n");
        var store = Store();
        store.SyncOfficial(Catalog("1.0"), _resources);
        for (var i = 1; i <= SkillStore.MaxBackups + 3; i++)
        {
            _now = _now.AddSeconds(1);
            WritePackage($"# v{i}\n");
            store.SyncOfficial(Catalog($"1.{i}"), _resources);
        }

        var backups = Directory.GetDirectories(Path.Combine(_root, "SkillBackups"))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Equal(SkillStore.MaxBackups, backups.Count);
        Assert.DoesNotContain(backups, name => name!.StartsWith("demo-skill-1.0-", StringComparison.Ordinal));
        Assert.Contains(backups, name => name!.StartsWith($"demo-skill-1.{SkillStore.MaxBackups + 2}-", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidIdsNeverResolve() =>
        Assert.Equal(SkillLibraryState.Missing, Store().State("..\\escape"));
}
