using System.Text.Json;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

public sealed class ContractTests
{
    private static readonly JsonSerializerOptions Json = BatchManifest.JsonOptions;

    [Fact]
    public void ShareActionRawValuesMatchMacOS()
    {
        var cases = new (ShareAction action, string raw)[]
        {
            (ShareAction.Codex, "codex"), (ShareAction.Claude, "claude"),
            (ShareAction.Doubao, "doubao"), (ShareAction.Qwen, "qwen"),
            (ShareAction.WorkBuddy, "workBuddy"), (ShareAction.WeSight, "weSight"),
            (ShareAction.Obsidian, "obsidian"), (ShareAction.Clipboard, "clipboard"),
            (ShareAction.Custom, "custom"),
        };
        foreach (var (action, raw) in cases)
            Assert.Equal($"\"{raw}\"", JsonSerializer.Serialize(action, Json));
    }

    [Fact]
    public void ShareActionDecodesRetiredShelfAsClipboard()
    {
        Assert.Equal(ShareAction.Clipboard, JsonSerializer.Deserialize<ShareAction>("\"shelf\"", Json));
        Assert.Equal(ShareAction.Clipboard, JsonSerializer.Deserialize<ShareAction>("\"clipboard\"", Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ShareAction>("\"bogus\"", Json));
    }

    [Fact]
    public void BatchOutcomeKindDecodesRetiredShelvedAsExpired() =>
        Assert.Equal(BatchOutcomeKind.Expired, JsonSerializer.Deserialize<BatchOutcomeKind>("\"shelved\"", Json));

    [Fact]
    public void BatchIntentRoundTripsWithMacOSShape()
    {
        var intent = new BatchIntent
        {
            Action = ShareAction.Custom,
            RequestedAt = new DateTimeOffset(2026, 9, 25, 4, 38, 29, TimeSpan.Zero),
            TargetBundleIdentifier = "com.example.cursor",
            TargetDisplayName = "Cursor",
        };
        var json = JsonSerializer.Serialize(intent, Json);
        var node = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        Assert.Equal("custom", node["action"].GetString());
        Assert.Equal("2026-09-25T04:38:29Z", node["requestedAt"].GetString());
        Assert.Equal("com.example.cursor", node["targetBundleIdentifier"].GetString());
        Assert.Equal("Cursor", node["targetDisplayName"].GetString());
        Assert.Equal(1, node["schemaVersion"].GetInt32());
    }

    [Fact]
    public void BatchIntentFreshnessWindowIsNinetySeconds()
    {
        var requested = new DateTimeOffset(2026, 9, 25, 4, 0, 0, TimeSpan.Zero);
        var intent = new BatchIntent { Action = ShareAction.Codex, RequestedAt = requested };
        Assert.True(intent.IsFresh(requested + TimeSpan.FromSeconds(89)));
        Assert.False(intent.IsFresh(requested + TimeSpan.FromSeconds(91)));
    }

    [Fact]
    public void BatchStateInitialMarksClipboardAsCopiedAtBatchTime()
    {
        var created = new DateTimeOffset(2026, 9, 25, 4, 0, 0, TimeSpan.Zero);
        var manifest = new BatchManifest(1, Guid.NewGuid(), created, "clipboard", []);
        var state = BatchState.Initial(manifest);
        Assert.Equal(BatchOutcomeKind.Copied, state.Outcome?.Kind);
        Assert.Equal(created, state.Outcome?.At);
        Assert.Equal(ShareAction.Clipboard, state.Action);
    }

    [Fact]
    public void BatchStateInitialLeavesForwardOutcomePending()
    {
        var manifest = new BatchManifest(1, Guid.NewGuid(), DateTimeOffset.UtcNow, "codex", []);
        var state = BatchState.Initial(manifest);
        Assert.Null(state.Outcome);
        Assert.Equal(ShareAction.Codex, state.Action);
    }

    [Fact]
    public void BatchStateWithOutcomePreservesTargetName()
    {
        var state = new BatchState { TargetName = "Cursor" };
        var updated = state.WithOutcome(new BatchOutcome(BatchOutcomeKind.Delivered, null, DateTimeOffset.UtcNow));
        Assert.Equal("Cursor", updated.TargetName);
        var renamed = state.WithOutcome(
            new BatchOutcome(BatchOutcomeKind.Failed, "未安装", DateTimeOffset.UtcNow), targetName: "VS Code");
        Assert.Equal("VS Code", renamed.TargetName);
    }

    [Fact]
    public void SceneVersionComparesDottedComponents()
    {
        Assert.True(SceneVersion.Parse("1.0.0") < SceneVersion.Parse("1.0.1"));
        Assert.Equal(0, SceneVersion.Parse("1.0")!.Value.CompareTo(SceneVersion.Parse("1.0.0")!.Value));
        Assert.Null(SceneVersion.Parse("1.x"));
        Assert.Null(SceneVersion.Parse(""));
    }

    [Fact]
    public void SceneResolverPrefersBindingThenKeywordThenFingerprintThenDefault()
    {
        var sceneA = new WeChatScene { Id = "a", Name = "A", Enabled = true };
        var sceneB = new WeChatScene { Id = "b", Name = "B", Enabled = true, Keywords = ["客户"] };
        var sceneC = new WeChatScene { Id = "c", Name = "C", Enabled = true };
        var settings = new SceneSettings { Scenes = [sceneA, sceneB, sceneC], DefaultSceneID = "c" };
        var memories = new Dictionary<string, GroupMemory>
        {
            ["测试群"] = new() { DisplayName = "测试群", BoundSceneIDs = ["a"] },
            ["指纹群"] = new() { DisplayName = "指纹群", BoundSceneIDs = ["a"], Senders = ["甲", "乙"] },
        };

        var bound = SceneResolver.Resolve("测试群", settings, memories);
        Assert.Equal("a", bound.Scene?.Id);
        Assert.Equal(SceneMatchSource.Binding, bound.Source);

        var keyword = SceneResolver.Resolve("客户服务群", settings, memories);
        Assert.Equal("b", keyword.Scene?.Id);
        Assert.Equal(SceneMatchSource.Keyword, keyword.Source);

        var fingerprint = SceneResolver.Resolve(null, settings, memories, senders: new HashSet<string> { "甲", "乙" });
        Assert.Equal("a", fingerprint.Scene?.Id);
        Assert.True(fingerprint.UsedFingerprint);

        var fallback = SceneResolver.Resolve("未知群", settings, memories);
        Assert.Equal("c", fallback.Scene?.Id);
        Assert.Equal(SceneMatchSource.DefaultScene, fallback.Source);
    }

    [Fact]
    public void GroupFingerprintRequiresThresholdOverlap()
    {
        var memories = new Dictionary<string, GroupMemory>
        {
            ["g1"] = new() { Senders = ["a", "b", "c", "d"] },
        };
        Assert.Equal("g1", GroupFingerprint.Match(new HashSet<string> { "a", "b", "c" }, memories));
        Assert.Null(GroupFingerprint.Match(new HashSet<string> { "a", "x" }, memories));
        Assert.Null(GroupFingerprint.Match(new HashSet<string> { "a" }, memories));
    }

    [Fact]
    public void GroupTitleParserReadsMemberCount()
    {
        var title = GroupTitleParser.Parse(["闲聊", "项目组（236）"]);
        Assert.Equal("项目组", title?.Name);
        Assert.Equal(236, title?.MemberCount);
    }

    [Fact]
    public void ForwardTargetStoreOrdersLastUsedFirst()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var store = new ForwardTargetStore(dir);
            var targets = new List<ForwardTarget>
            {
                new("com.a", "A", DateTimeOffset.UtcNow),
                new("com.b", "B", DateTimeOffset.UtcNow),
            };
            store.Save(targets);
            store.RecordLastUsed("com.b");
            var ordered = store.OrderedTargets();
            Assert.Equal("com.b", ordered[0].BundleIdentifier);
            Assert.Equal("com.a", ordered[1].BundleIdentifier);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void InboxReaderFirstSeenInitialisesStateOnce()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var paths = new InboxPaths(root);
            paths.EnsureCreated();
            var batchDir = Path.Combine(paths.Ready, Guid.NewGuid().ToString("D"));
            Directory.CreateDirectory(Path.Combine(batchDir, "files"));
            File.WriteAllText(Path.Combine(batchDir, "files", "a.zip"), "zip");
            var manifest = new BatchManifest(
                1, Guid.Parse(Path.GetFileName(batchDir)), DateTimeOffset.UtcNow, "clipboard",
                [new ManifestItem(Guid.NewGuid(), "a.zip", "files/a.zip", "application/zip", 3, 0, 0)]);
            File.WriteAllText(
                Path.Combine(batchDir, "manifest.json"),
                JsonSerializer.Serialize(manifest, Json));

            var reader = new InboxReader(paths, InboxReader.Removal.Delete);
            var first = reader.LoadBatches();
            Assert.Single(first);
            Assert.True(first[0].IsFirstSeen);
            Assert.True(File.Exists(Path.Combine(batchDir, "state.json")));

            var second = reader.LoadBatches();
            Assert.False(second[0].IsFirstSeen);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ConsumeIntentDeletesAndReportsExpiry()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var paths = new InboxPaths(root);
            var batchId = Guid.NewGuid();
            var batchDir = Path.Combine(paths.Ready, batchId.ToString("D"));
            Directory.CreateDirectory(batchDir);
            var intent = new BatchIntent
            {
                Action = ShareAction.Codex,
                RequestedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10),
            };
            File.WriteAllText(
                Path.Combine(batchDir, "intent.json"),
                JsonSerializer.Serialize(intent, Json));

            var reader = new InboxReader(paths, InboxReader.Removal.Delete);
            var consumed = reader.ConsumeIntent(batchId);
            Assert.Equal(ConsumedIntentKind.Expired, consumed.Kind);
            Assert.False(File.Exists(Path.Combine(batchDir, "intent.json")));
            Assert.Equal(ConsumedIntentKind.None, reader.ConsumeIntent(batchId).Kind);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ConsumeFailuresReadsAndDeletesReports()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var paths = new InboxPaths(root);
            paths.EnsureCreated();
            ShareFailure.Record(
                new ShareFailure(DateTimeOffset.UtcNow, ShareAction.Obsidian, "知识库未配置"),
                paths);
            var reader = new InboxReader(paths, InboxReader.Removal.Delete);
            var failures = reader.ConsumeFailures();
            Assert.Single(failures);
            Assert.Equal("知识库未配置", failures[0].Message);
            Assert.Empty(reader.ConsumeFailures());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
