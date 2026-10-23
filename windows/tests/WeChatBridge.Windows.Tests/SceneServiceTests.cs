using System.Text.Json;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Services;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// SceneService driven entirely on fakes — temp Config directory, stub picker
/// answers, injected title/insights readers. No WeChat window is ever read.
/// </summary>
public sealed class SceneServiceTests
{
    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "WeChatBridgeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static WeChatScene Scene(string id, string name = "场景", bool enabled = true,
        bool official = false, string[]? keywords = null,
        List<AgentId>? agents = null) =>
        new()
        {
            Id = id,
            Name = name,
            Summary = $"说明:{id}",
            Instruction = "整理",
            OutputSpec = "输出",
            Keywords = keywords is null ? [] : new List<string>(keywords),
            Enabled = enabled,
            IsOfficial = official,
            CompatibleAgents = agents ?? new List<AgentId>(AgentIds.All),
        };

    private sealed class StubPicker
    {
        public List<IReadOnlyList<WeChatScene>> Asked { get; } = [];
        public Queue<ScenePickerAnswer> Answers { get; } = new();

        public Task<ScenePickerAnswer> Choose(
            IReadOnlyList<WeChatScene> scenes, CancellationToken cancellationToken)
        {
            Asked.Add(scenes);
            return Task.FromResult(
                Answers.Count > 0 ? Answers.Dequeue() : ScenePickerAnswer.Cancelled);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(params WeChatScene[] scenes)
        {
            Directory = TempDirectory();
            Picker = new StubPicker();
            Service = new SceneService(
                configDirectory: Directory,
                picker: Picker.Choose,
                titleReader: _ => Task.FromResult(Title),
                insightsReader: (_, _) => Task.FromResult(Insights),
                notify: Notices.Add);
            if (scenes.Length > 0)
            {
                var store = new SceneStore(Directory);
                store.Save(new SceneSettings { Scenes = new List<WeChatScene>(scenes) });
            }
        }

        public string Directory { get; }
        public SceneService Service { get; }
        public StubPicker Picker { get; }
        public List<string> Notices { get; } = [];
        public GroupTitleParser.Title? Title { get; set; }
        public WeChatBatchInsights Insights { get; set; } = WeChatBatchInsights.Empty;

        public void SaveMemories(Dictionary<string, GroupMemory> memories) =>
            new GroupMemoryStore(Directory).Save(memories);

        public Dictionary<string, GroupMemory> LoadMemories() =>
            new GroupMemoryStore(Directory).Load();

        public void Dispose() => Service.Dispose();
    }

    // --- ResolveForShareAsync -------------------------------------------------

    [Fact]
    public async Task PendingShortcutSceneShortCircuitsResolution()
    {
        using var fixture = new Fixture(Scene("a", "甲"), Scene("b", "乙"));
        fixture.Service.Pending.SelectForNextForward("b");

        var choice = await fixture.Service.ResolveForShareAsync(captureTitle: false);

        Assert.NotNull(choice);
        Assert.Equal("b", choice!.Scene!.Id);
        Assert.Empty(fixture.Picker.Asked);
    }

    [Fact]
    public async Task WindowTitleSuppliesTheGroupName()
    {
        using var fixture = new Fixture(Scene("a"));
        fixture.Title = new GroupTitleParser.Title("华东客户群", 12);

        var choice = await fixture.Service.ResolveForShareAsync();

        Assert.Equal("华东客户群", choice!.GroupName);
        // Unbound named group → the picker was asked, 直接转发 leaves no scene.
        Assert.Single(fixture.Picker.Asked);
        Assert.Null(choice.Scene);
    }

    [Fact]
    public async Task SingleBoundSceneGoesStraightThrough()
    {
        using var fixture = new Fixture(Scene("a"));
        fixture.SaveMemories(new Dictionary<string, GroupMemory>
        {
            [GroupName.Normalize("群")] = new GroupMemory
            {
                DisplayName = "群",
                BoundSceneIDs = ["a"],
            },
        });

        var choice = await fixture.Service.ResolveForShareAsync(groupName: "群", captureTitle: false);

        Assert.Equal("a", choice!.Scene!.Id);
        Assert.Empty(fixture.Picker.Asked);
    }

    [Fact]
    public async Task UnboundGroupPickIsRememberedAsBinding()
    {
        using var fixture = new Fixture(Scene("a", "甲"), Scene("b", "乙"));
        var picked = fixture.Service.LoadSettings().Scene("b")!;
        fixture.Picker.Answers.Enqueue(ScenePickerAnswer.Picked(picked));

        var choice = await fixture.Service.ResolveForShareAsync(groupName: "新群", captureTitle: false);

        Assert.Equal("b", choice!.Scene!.Id);
        var memory = fixture.LoadMemories()[GroupName.Normalize("新群")];
        Assert.Equal(["b"], memory.BoundSceneIDs);
    }

    [Fact]
    public async Task PickerExpiryPropagatesAsNullChoice()
    {
        using var fixture = new Fixture(Scene("a"), Scene("b"));
        fixture.SaveMemories(new Dictionary<string, GroupMemory>
        {
            [GroupName.Normalize("群")] = new GroupMemory
            {
                DisplayName = "群",
                BoundSceneIDs = ["a", "b"],
            },
        });
        fixture.Picker.Answers.Enqueue(ScenePickerAnswer.Expired);

        Assert.Null(await fixture.Service.ResolveForShareAsync(groupName: "群"));
    }

    [Fact]
    public async Task NoEnabledScenesSkipsTheWholePipeline()
    {
        using var fixture = new Fixture(Scene("a", enabled: false));
        fixture.Title = new GroupTitleParser.Title("群名", null);

        var choice = await fixture.Service.ResolveForShareAsync();

        Assert.NotNull(choice);
        Assert.Null(choice!.Scene);
        Assert.Equal("群名", choice.GroupName); // the title snapshot still lands
        Assert.Empty(fixture.Picker.Asked);
    }

    [Fact]
    public async Task HeadlessServiceAnswersPickerAsDirectForward()
    {
        var directory = TempDirectory();
        new SceneStore(directory).Save(new SceneSettings { Scenes = [Scene("a"), Scene("b")] });
        using var service = new SceneService(configDirectory: directory);

        var choice = await service.ResolveForShareAsync(groupName: "群", captureTitle: false);

        Assert.NotNull(choice);
        Assert.Null(choice!.Scene);
        Assert.Empty(new GroupMemoryStore(directory).Load());
    }

    // --- RenderPrompt / CompleteForward ----------------------------------------

    [Fact]
    public async Task RenderPromptGatesByCompatibleAgent()
    {
        using var fixture = new Fixture(Scene("a", agents: [AgentId.Claude]));
        fixture.SaveMemories(new Dictionary<string, GroupMemory>
        {
            [GroupName.Normalize("群")] = new GroupMemory
            {
                DisplayName = "群",
                BoundSceneIDs = ["a"],
            },
        });
        var choice = await fixture.Service.ResolveForShareAsync(groupName: "群", captureTitle: false);
        Assert.Equal("a", choice!.Scene!.Id);

        Assert.NotNull(fixture.Service.RenderPrompt(choice, AgentId.Claude));
        Assert.Null(fixture.Service.RenderPrompt(choice, AgentId.Doubao));
        // A destination with no matching agent (clipboard, custom) attaches as-is.
        Assert.NotNull(fixture.Service.RenderPrompt(choice, null));
    }

    [Fact]
    public async Task CompleteForwardWritesWatermarkAndSenders()
    {
        using var fixture = new Fixture(Scene("a"));
        fixture.SaveMemories(new Dictionary<string, GroupMemory>
        {
            [GroupName.Normalize("群")] = new GroupMemory
            {
                DisplayName = "群",
                BoundSceneIDs = ["a"],
            },
        });
        var end = new DateTimeOffset(2026, 9, 25, 13, 0, 0, TimeSpan.Zero);
        fixture.Insights = new WeChatBatchInsights
        {
            Senders = new HashSet<string>(StringComparer.Ordinal) { "张三", "李四" },
            End = end,
        };
        // filePaths drives the insights read — without it the batch is bare,
        // matching macOS where prepare always receives the share URLs.
        var choice = await fixture.Service.ResolveForShareAsync(
            filePaths: ["chat.zip"], groupName: "群", captureTitle: false);
        Assert.Equal("a", choice!.Scene!.Id);

        fixture.Service.CompleteForward(choice!);

        var memory = fixture.LoadMemories()[GroupName.Normalize("群")];
        Assert.Equal("a", memory.LastSceneID);
        Assert.Equal(end, memory.LastSummaryAt);
        Assert.True(memory.Senders.SetEquals(new[] { "张三", "李四" }));
    }

    // --- Library management -----------------------------------------------------

    [Fact]
    public void AddSceneCreatesAnEnabledUserScene()
    {
        using var fixture = new Fixture();
        var scene = fixture.Service.AddScene();
        Assert.Equal("新场景", scene.Name);
        Assert.True(scene.Enabled);
        Assert.False(scene.IsOfficial);
        Assert.Contains(fixture.Service.LoadSettings().Scenes, s => s.Id == scene.Id);
    }

    [Fact]
    public void DuplicateSceneCopiesOfficialIntoEditable()
    {
        using var fixture = new Fixture(Scene("official", "官方", official: true));
        var copy = fixture.Service.DuplicateScene("official");
        Assert.NotNull(copy);
        Assert.False(copy!.IsOfficial);
        Assert.True(copy.Enabled);
        Assert.Equal("官方 副本", copy.Name);
    }

    [Fact]
    public void UpdateScenePreservesTheStoredEnabledSwitch()
    {
        using var fixture = new Fixture(Scene("a", enabled: true));
        var edited = fixture.Service.LoadSettings().Scene("a")!;
        edited.Name = "改名";
        edited.Enabled = false; // must not take effect through UpdateScene
        fixture.Service.UpdateScene(edited);

        var stored = fixture.Service.LoadSettings().Scenes.First(s => s.Id == "a");
        Assert.Equal("改名", stored.Name);
        Assert.True(stored.Enabled);
    }

    [Fact]
    public void DisablingSceneClearsDefaultAndBindings()
    {
        using var fixture = new Fixture(Scene("a"), Scene("b"));
        var settings = fixture.Service.LoadSettings();
        settings.DefaultSceneID = "a";
        new SceneStore(fixture.Directory).Save(settings);
        fixture.SaveMemories(new Dictionary<string, GroupMemory>
        {
            ["g1"] = new() { DisplayName = "群一", BoundSceneIDs = ["a", "b"] },
            ["g2"] = new() { DisplayName = "群二", BoundSceneIDs = ["a"] },
        });

        fixture.Service.SetSceneEnabled("a", enabled: false);

        var loaded = fixture.Service.LoadSettings();
        // Scene(id) filters on Enabled — look the disabled row up directly.
        Assert.False(loaded.Scenes.First(s => s.Id == "a").Enabled);
        Assert.Null(loaded.DefaultSceneID);
        var memories = fixture.LoadMemories();
        Assert.Equal(["b"], memories["g1"].BoundSceneIDs);
        Assert.Empty(memories["g2"].BoundSceneIDs);
    }

    [Fact]
    public void RemoveSceneCleansBindingsAndLastScene()
    {
        using var fixture = new Fixture(Scene("a"), Scene("b"));
        fixture.SaveMemories(new Dictionary<string, GroupMemory>
        {
            ["g"] = new()
            {
                DisplayName = "群",
                BoundSceneIDs = ["a", "b"],
                LastSceneID = "a",
            },
        });

        fixture.Service.RemoveScene("a");

        Assert.Null(fixture.Service.LoadSettings().Scenes.FirstOrDefault(s => s.Id == "a"));
        var memory = fixture.LoadMemories()["g"];
        Assert.Equal(["b"], memory.BoundSceneIDs);
        Assert.Null(memory.LastSceneID);
    }

    [Fact]
    public void MoveSceneSwapsNeighbours()
    {
        using var fixture = new Fixture(Scene("a"), Scene("b"), Scene("c"));
        fixture.Service.MoveScene("b", -1);
        // LoadSettings installs the disabled official starters after user scenes.
        Assert.Equal(
            ["b", "a", "c"],
            fixture.Service.LoadSettings().Scenes.Take(3).Select(s => s.Id).ToArray());
        // Edges are no-ops.
        fixture.Service.MoveScene("b", -1);
        Assert.Equal("b", fixture.Service.LoadSettings().Scenes[0].Id);
    }

    [Fact]
    public void ToggleGroupBindingKeepsEnabledLibraryOrder()
    {
        using var fixture = new Fixture(
            Scene("a"), Scene("b"), Scene("off", enabled: false));
        fixture.SaveMemories(new Dictionary<string, GroupMemory>
        {
            ["g"] = new() { DisplayName = "群", BoundSceneIDs = ["b"] },
        });

        // Adding a disabled scene is a no-op after the library-order rewrite.
        fixture.Service.ToggleGroupBinding("g", "off");
        Assert.Equal(["b"], fixture.LoadMemories()["g"].BoundSceneIDs);

        fixture.Service.ToggleGroupBinding("g", "a");
        Assert.Equal(["a", "b"], fixture.LoadMemories()["g"].BoundSceneIDs);

        fixture.Service.ClearGroupBinding("g");
        Assert.Empty(fixture.LoadMemories()["g"].BoundSceneIDs);
    }

    [Fact]
    public void SetDefaultSceneRejectsDisabledAndMissingIds()
    {
        using var fixture = new Fixture(Scene("a"), Scene("off", enabled: false));
        fixture.Service.SetDefaultScene("a");
        Assert.Equal("a", fixture.Service.LoadSettings().DefaultSceneID);
        fixture.Service.SetDefaultScene("off");
        Assert.Equal("a", fixture.Service.LoadSettings().DefaultSceneID);
        fixture.Service.SetDefaultScene(null);
        Assert.Null(fixture.Service.LoadSettings().DefaultSceneID);
    }

    [Fact]
    public void ShortcutHintCoversOnlyTheFirstNineEnabled()
    {
        var scenes = Enumerable.Range(1, 11)
            .Select(i => Scene($"s{i}", $"场景{i}", enabled: i != 2))
            .ToArray();
        using var fixture = new Fixture(scenes);
        var loaded = fixture.Service.LoadSettings();

        Assert.Equal("Ctrl+Alt+1", fixture.Service.ShortcutHint(loaded.Scene("s1")!));
        Assert.Null(fixture.Service.ShortcutHint(
            loaded.Scenes.First(s => s.Id == "s2"))); // disabled
        Assert.Equal("Ctrl+Alt+9", fixture.Service.ShortcutHint(loaded.Scene("s10")!));
        Assert.Null(fixture.Service.ShortcutHint(loaded.Scene("s11")!));
    }

    [Fact]
    public async Task HotkeyDigitSelectsTheSceneForTheNextShare()
    {
        using var fixture = new Fixture(Scene("a", "甲"), Scene("b", "乙"));
        fixture.Service.Shortcuts.HandleSceneDigit(2);

        Assert.Single(fixture.Notices);
        Assert.Contains("乙", fixture.Notices[0]);

        var choice = await fixture.Service.ResolveForShareAsync(captureTitle: false);
        Assert.Equal("b", choice!.Scene!.Id);
    }

    [Fact]
    public void ChangedFiresOnLibraryMutations()
    {
        using var fixture = new Fixture(Scene("a"));
        var fired = 0;
        fixture.Service.Changed += () => fired++;

        fixture.Service.AddScene();
        fixture.Service.SetSceneEnabled("a", false);

        Assert.True(fired >= 2);
    }

    // --- Scene packages -----------------------------------------------------------

    [Fact]
    public void ExportedPackageRoundTripsThroughTheImport()
    {
        using var fixture = new Fixture();
        var scene = fixture.Service.AddScene();
        scene.Instruction = "提取要点";
        scene.PackageVersion = "1.2.0";
        var json = fixture.Service.ExportPackageJson(scene);
        var path = Path.Combine(fixture.Directory, "scene.json");
        File.WriteAllText(path, json);

        var report = fixture.Service.ImportScenePackages([path]);
        Assert.Equal(1, report.Imported);
        Assert.Empty(report.Errors);

        var decoded = JsonSerializer.Deserialize<ScenePackage>(json, BatchManifest.JsonOptions)!;
        Assert.Equal(scene.Id, decoded.Id);
        Assert.Equal("1.2.0", decoded.Version);
    }

    [Fact]
    public void ExportRejectsANonNumericVersion()
    {
        using var fixture = new Fixture(Scene("a"));
        var scene = fixture.Service.LoadSettings().Scenes.First(s => s.Id == "a");
        scene.PackageVersion = "beta";
        Assert.Throws<SceneService.ScenePackageException>(
            () => fixture.Service.ExportPackageJson(scene));
    }

    [Fact]
    public void ImportAppliesNewerSkipsOlderAndConfirmsSame()
    {
        using var fixture = new Fixture(Scene("a"));
        var local = fixture.Service.LoadSettings().Scenes.First(s => s.Id == "a");
        local.PackageVersion = "2.0.0";
        fixture.Service.UpdateScene(local);

        var older = WritePackage(fixture.Directory, "older.json", "a", "1.0.0");
        var same = WritePackage(fixture.Directory, "same.json", "a", "2.0.0");
        var newer = WritePackage(fixture.Directory, "newer.json", "a", "3.0.0");

        // Decline the overwrite prompt: same-version is skipped, newer applies.
        var report = fixture.Service.ImportScenePackages(
            [older, same, newer], confirmOverwrite: _ => false);
        Assert.Equal(1, report.Imported);
        Assert.Equal(1, report.SkippedOlder);
        Assert.Equal(1, report.Declined);
        Assert.Equal("3.0.0",
            fixture.Service.LoadSettings().Scenes.First(s => s.Id == "a").PackageVersion);
    }

    [Fact]
    public void ImportOverwritesSameVersionKeepingLocalSwitch()
    {
        using var fixture = new Fixture(Scene("a", enabled: true));
        var local = fixture.Service.LoadSettings().Scenes.First(s => s.Id == "a");
        local.PackageVersion = "1.0.0";
        fixture.Service.UpdateScene(local);

        var same = WritePackage(fixture.Directory, "same.json", "a", "1.0.0");
        var report = fixture.Service.ImportScenePackages([same], confirmOverwrite: _ => true);

        Assert.Equal(1, report.Imported);
        var stored = fixture.Service.LoadSettings().Scenes.First(s => s.Id == "a");
        Assert.True(stored.Enabled); // 本地的启用状态保留
        Assert.Equal("来自包", stored.Instruction);
    }

    [Fact]
    public void ImportRejectsUnsupportedSchemaAndMissingFields()
    {
        using var fixture = new Fixture();
        var badSchema = Path.Combine(fixture.Directory, "bad.json");
        File.WriteAllText(badSchema, """
            {"schemaVersion":99,"id":"x","name":"x","version":"1.0.0","instruction":"x"}
            """);
        var report = fixture.Service.ImportScenePackages([badSchema]);
        Assert.Equal(0, report.Imported);
        Assert.Single(report.Errors);
        Assert.Contains("更新版本", report.Errors[0]);

        var missing = Path.Combine(fixture.Directory, "missing.json");
        File.WriteAllText(missing, """
            {"schemaVersion":2,"id":"x","name":" ","version":"1.0.0","instruction":"x"}
            """);
        report = fixture.Service.ImportScenePackages([missing]);
        Assert.Equal(0, report.Imported);
        Assert.Single(report.Errors);
        Assert.Contains("缺少", report.Errors[0]);
    }

    [Fact]
    public void ImportWithNoJsonFilesReportsNothingFound()
    {
        using var fixture = new Fixture();
        var text = Path.Combine(fixture.Directory, "note.txt");
        File.WriteAllText(text, "not a package");
        var report = fixture.Service.ImportScenePackages([text]);
        Assert.Equal(0, report.Imported);
        Assert.Single(report.Errors);
        Assert.Contains("没有找到可导入的场景包", report.Errors[0]);
    }

    private static string WritePackage(
        string directory, string fileName, string id, string version)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": 2,
              "id": "{{id}}",
              "name": "包场景",
              "version": "{{version}}",
              "instruction": "来自包",
              "compatibleAgents": ["claude"]
            }
            """);
        return path;
    }
}
