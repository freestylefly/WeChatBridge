using System.Text.Json;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

public sealed class SceneStoreTests
{
    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "WeChatBridgeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void MissingFileLoadsStarterScenes()
    {
        var store = new SceneStore(TempDirectory());
        var settings = store.Load();
        Assert.Equal(SceneSettings.StarterScenes().Count, settings.Scenes.Count);
        Assert.Empty(settings.EnabledScenes);
        Assert.Null(settings.DefaultSceneID);
    }

    [Fact]
    public void SaveThenLoadRoundTripsTheLibrary()
    {
        var directory = TempDirectory();
        var store = new SceneStore(directory);
        var settings = new SceneSettings
        {
            Scenes =
            [
                new WeChatScene
                {
                    Id = "own.scene",
                    Name = "客户复盘",
                    Instruction = "整理",
                    OutputSpec = "输出",
                    Enabled = true,
                },
            ],
            AttachToForwards = true,
        };
        settings.DefaultSceneID = "own.scene";
        store.Save(settings);

        var loaded = store.Load();
        var scene = loaded.Scene("own.scene");
        Assert.NotNull(scene);
        Assert.Equal("客户复盘", scene.Name);
        Assert.True(loaded.AttachToForwards);
        Assert.Equal("own.scene", loaded.DefaultSceneID);
        // Decode installs the official starters like the macOS decoder does.
        Assert.Equal(1 + SceneSettings.StarterScenes().Count, loaded.Scenes.Count);
    }

    [Fact]
    public void CorruptFileLoadsStarterScenes()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, SceneStore.FileName), "{ not json");
        var settings = new SceneStore(directory).Load();
        Assert.Equal(SceneSettings.StarterScenes().Count, settings.Scenes.Count);
    }

    /// <summary>Ported from testLegacyPromptBlobMigratesAndKeepsForwardSwitch.</summary>
    [Fact]
    public void LegacyPromptBlobMigratesAndKeepsForwardSwitch()
    {
        var directory = TempDirectory();
        File.WriteAllText(Path.Combine(directory, SceneStore.FileName), """
            {
              "prompts": [
                {"id":"11111111-1111-1111-1111-111111111111","text":"请总结"},
                {"id":"22222222-2222-2222-2222-222222222222","text":"请翻译"}
              ],
              "selectedID":"22222222-2222-2222-2222-222222222222",
              "attachToForwards":true
            }
            """);
        var settings = new SceneStore(directory).Load();
        Assert.Equal(7, settings.Scenes.Count);
        Assert.Equal("22222222-2222-2222-2222-222222222222", settings.DefaultSceneID);
        Assert.True(settings.AttachToForwards);
        Assert.Equal("请翻译", settings.Scene(settings.DefaultSceneID)?.Instruction);
    }

    [Fact]
    public void GroupMemoryStoreRoundTrips()
    {
        var directory = TempDirectory();
        var store = new GroupMemoryStore(directory);
        var memories = new Dictionary<string, GroupMemory>(StringComparer.Ordinal)
        {
            ["华东大客户群".ToLowerInvariant()] = new GroupMemory
            {
                DisplayName = "华东大客户群",
                BoundSceneIDs = ["a", "b"],
                LastSceneID = "b",
                LastSummaryAt = new DateTimeOffset(2026, 9, 17, 8, 30, 0, TimeSpan.Zero),
                Senders = new HashSet<string>(StringComparer.Ordinal) { "张三" },
                UpdatedAt = new DateTimeOffset(2026, 9, 17, 9, 0, 0, TimeSpan.Zero),
            },
        };
        store.Save(memories);

        var loaded = store.Load();
        var memory = loaded["华东大客户群".ToLowerInvariant()];
        Assert.Equal("华东大客户群", memory.DisplayName);
        Assert.Equal(["a", "b"], memory.BoundSceneIDs);
        Assert.Equal("b", memory.LastSceneID);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 17, 8, 30, 0, TimeSpan.Zero),
            memory.LastSummaryAt);
        Assert.Contains("张三", memory.Senders);
    }

    [Fact]
    public void GroupMemoryDecodesLegacySingleBindingAndNumberDates()
    {
        // macOS shape: boundSceneID (singular) + deferredToDate epoch seconds.
        const string legacy = """{"displayName":"旧群","boundSceneID":"first","senders":[],"updatedAt":0}""";
        var memory = JsonSerializer.Deserialize<GroupMemory>(legacy, SceneConfigJson.Options);
        Assert.NotNull(memory);
        Assert.Equal(["first"], memory.BoundSceneIDs);
        Assert.Equal(new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero), memory.UpdatedAt);
    }

    [Fact]
    public void PendingSceneSelectionIsConsumedWithinSixtySeconds()
    {
        var directory = TempDirectory();
        var now = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        var store = new NextForwardSceneStore(directory, () => now);
        store.SelectForNextForward("scene.a");

        // Another store instance sees the selection — the hotkey and the share
        // may not run in the same object graph.
        var reader = new NextForwardSceneStore(directory, () => now.AddSeconds(30));
        Assert.Equal("scene.a", reader.ConsumePendingSceneId());
        Assert.Null(reader.ConsumePendingSceneId());
    }

    [Fact]
    public void PendingSceneSelectionOlderThanSixtySecondsIsIgnored()
    {
        var directory = TempDirectory();
        var selected = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        var store = new NextForwardSceneStore(directory, () => selected.AddSeconds(61));
        store.SelectForNextForward("scene.a", selected);

        Assert.Null(store.ConsumePendingSceneId());
        // Stale or not, consumed is consumed — a second share must not see it.
        Assert.False(File.Exists(Path.Combine(directory, NextForwardSceneStore.FileName)));
    }

    [Fact]
    public void PendingSceneSelectionExactlyAtTheBoundaryStillCounts()
    {
        var directory = TempDirectory();
        var selected = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        var store = new NextForwardSceneStore(directory, () => selected.AddSeconds(60));
        store.SelectForNextForward("scene.a", selected);
        Assert.Equal("scene.a", store.ConsumePendingSceneId());
    }
}
