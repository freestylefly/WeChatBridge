using System.Globalization;
using System.Text.Json;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// The platform-independent cases of SceneTests.swift, plus the coordinator
/// flow driven through injected pickers/readers.
/// </summary>
public sealed class SceneDecisionTests
{
    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "WeChatBridgeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static WeChatScene Scene(string id, string name = "场景", bool enabled = true,
        string[]? keywords = null) =>
        new()
        {
            Id = id,
            Name = name,
            Instruction = "整理",
            OutputSpec = "输出",
            Keywords = keywords is null ? [] : new List<string>(keywords),
            Enabled = enabled,
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

    private sealed class Fixture
    {
        public Fixture(SceneSettings settings)
        {
            Directory = TempDirectory();
            Scenes = new SceneStore(Directory);
            Memories = new GroupMemoryStore(Directory);
            Pending = new NextForwardSceneStore(Directory, () => Now);
            Scenes.Save(settings);
        }

        public string Directory { get; }
        public SceneStore Scenes { get; }
        public GroupMemoryStore Memories { get; }
        public NextForwardSceneStore Pending { get; }
        public StubPicker Picker { get; } = new();
        public DateTimeOffset Now { get; set; } =
            new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

        public SceneCoordinator Coordinator(
            Func<CancellationToken, Task<GroupTitleParser.Title?>>? titleReader = null,
            Func<CancellationToken, Task<WeChatBatchInsights>>? insightsReader = null) =>
            new(Scenes, Memories, Pending, Picker.Choose, titleReader, insightsReader);
    }

    // --- ScenePickerDecision -------------------------------------------------

    [Fact]
    public void PickerDecisionAsksOnlyWhenThereIsARealChoice()
    {
        Assert.Equal(ScenePickerDecisionKind.NoPrompt,
            ScenePickerDecision.Decide([]).Kind);
        var single = ScenePickerDecision.Decide([Scene("a")]);
        Assert.Equal(ScenePickerDecisionKind.Single, single.Kind);
        var choose = ScenePickerDecision.Decide([Scene("a"), Scene("b")]);
        Assert.Equal(ScenePickerDecisionKind.Choose, choose.Kind);
        Assert.Equal(2, choose.Scenes.Count);
    }

    // --- Coordinator ----------------------------------------------------------

    [Fact]
    public async Task PendingShortcutSceneShortCircuitsResolution()
    {
        var fixture = new Fixture(new SceneSettings
        {
            Scenes = [Scene("a", "甲"), Scene("b", "乙")],
        });
        fixture.Pending.SelectForNextForward("b", fixture.Now);

        var answer = await fixture.Coordinator().PrepareAsync(
            enabled: true, groupName: "任何群", captureTitle: false, allowDefault: false);

        Assert.Equal(SceneCoordinator.AnswerKind.Ready, answer.Kind);
        Assert.Equal("b", answer.Selection!.Scene!.Id);
        Assert.Empty(fixture.Picker.Asked);
    }

    [Fact]
    public async Task GroupBoundToMultipleScenesAsksThePicker()
    {
        var fixture = new Fixture(new SceneSettings
        {
            Scenes = [Scene("first", "一"), Scene("second", "二")],
        });
        fixture.Memories.Save(new Dictionary<string, GroupMemory>
        {
            [GroupName.Normalize("群")] = new GroupMemory
            {
                DisplayName = "群",
                BoundSceneIDs = ["second", "first"],
            },
        });
        fixture.Picker.Answers.Enqueue(ScenePickerAnswer.Picked(
            fixture.Scenes.Load().Scene("second")!));

        var answer = await fixture.Coordinator().PrepareAsync(
            enabled: true, groupName: "群", captureTitle: false, allowDefault: false);

        Assert.Equal(SceneCoordinator.AnswerKind.Ready, answer.Kind);
        Assert.Single(fixture.Picker.Asked);
        // Candidates arrive in library order, not binding order.
        Assert.Equal(["first", "second"], fixture.Picker.Asked[0].Select(s => s.Id).ToArray());
        Assert.Equal("second", answer.Selection!.Scene!.Id);
    }

    [Fact]
    public async Task SingleBoundSceneGoesStraightThrough()
    {
        var fixture = new Fixture(new SceneSettings { Scenes = [Scene("a")] });
        fixture.Memories.Save(new Dictionary<string, GroupMemory>
        {
            [GroupName.Normalize("群")] = new GroupMemory
            {
                DisplayName = "群",
                BoundSceneIDs = ["a"],
            },
        });

        var answer = await fixture.Coordinator().PrepareAsync(
            enabled: true, groupName: "群", captureTitle: false, allowDefault: false);

        Assert.Equal("a", answer.Selection!.Scene!.Id);
        Assert.Empty(fixture.Picker.Asked);
    }

    [Fact]
    public async Task UnboundGroupAsksFromEnabledScenesAndRemembersTheBinding()
    {
        var fixture = new Fixture(new SceneSettings
        {
            Scenes = [Scene("a", "甲"), Scene("b", "乙"), Scene("off", "停用", enabled: false)],
        });
        var picked = fixture.Scenes.Load().Scene("b")!;
        fixture.Picker.Answers.Enqueue(ScenePickerAnswer.Picked(picked));

        var answer = await fixture.Coordinator().PrepareAsync(
            enabled: true, groupName: "新群", captureTitle: false, allowDefault: false);

        Assert.Equal(SceneCoordinator.AnswerKind.Ready, answer.Kind);
        Assert.Equal("b", answer.Selection!.Scene!.Id);
        Assert.Single(fixture.Picker.Asked);
        Assert.Equal(["a", "b"], fixture.Picker.Asked[0].Select(s => s.Id).ToArray());

        var memory = fixture.Memories.Load()[GroupName.Normalize("新群")];
        Assert.Equal(["b"], memory.BoundSceneIDs);
    }

    [Fact]
    public async Task UnboundGroupCancelLeavesScenesEmpty()
    {
        var fixture = new Fixture(new SceneSettings { Scenes = [Scene("a")] });
        fixture.Picker.Answers.Enqueue(ScenePickerAnswer.Cancelled);

        var answer = await fixture.Coordinator().PrepareAsync(
            enabled: true, groupName: "新群", captureTitle: false, allowDefault: false);

        Assert.Empty(answer.Selection!.Scenes);
        Assert.Empty(fixture.Memories.Load());
    }

    [Fact]
    public async Task PickerExpiryPropagatesAsExpired()
    {
        var fixture = new Fixture(new SceneSettings
        {
            Scenes = [Scene("a"), Scene("b")],
        });
        fixture.Memories.Save(new Dictionary<string, GroupMemory>
        {
            [GroupName.Normalize("群")] = new GroupMemory
            {
                DisplayName = "群",
                BoundSceneIDs = ["a", "b"],
            },
        });
        fixture.Picker.Answers.Enqueue(ScenePickerAnswer.Expired);

        var answer = await fixture.Coordinator().PrepareAsync(
            enabled: true, groupName: "群", captureTitle: false);

        Assert.Equal(SceneCoordinator.AnswerKind.Expired, answer.Kind);
    }

    /// <summary>No group name means nothing to bind to — no picker, no scenes.</summary>
    [Fact]
    public async Task NoGroupNameNeverAsks()
    {
        var fixture = new Fixture(new SceneSettings
        {
            Scenes = [Scene("a"), Scene("b")],
        });
        var answer = await fixture.Coordinator().PrepareAsync(
            enabled: true, groupName: null, captureTitle: false, allowDefault: false);
        Assert.Empty(answer.Selection!.Scenes);
        Assert.Empty(fixture.Picker.Asked);
    }

    [Fact]
    public async Task MissingGroupNameCanFallBackToTheDefaultScene()
    {
        var scene = Scene("d");
        var fixture = new Fixture(new SceneSettings
        {
            Scenes = [scene],
            DefaultSceneID = "d",
        });
        var answer = await fixture.Coordinator().PrepareAsync(
            enabled: true, groupName: null, captureTitle: false, allowDefault: true);
        Assert.Equal("d", answer.Selection!.Scene!.Id);
    }

    [Fact]
    public async Task WindowTitleSuppliesTheGroupName()
    {
        var fixture = new Fixture(new SceneSettings { Scenes = [Scene("a")] });
        var coordinator = fixture.Coordinator(
            titleReader: _ => Task.FromResult<GroupTitleParser.Title?>(
                new GroupTitleParser.Title("华东客户群", 12)));

        var answer = await coordinator.PrepareAsync(
            enabled: true, groupName: null, captureTitle: true, allowDefault: false);

        Assert.Equal("华东客户群", answer.Selection!.GroupName);
        // The named unbound group was asked; 直接转发 leaves scenes empty.
        Assert.Single(fixture.Picker.Asked);
        Assert.Empty(answer.Selection.Scenes);
    }

    [Fact]
    public async Task DisabledShortcutReturnsEmptyWithoutReadingAnything()
    {
        var fixture = new Fixture(new SceneSettings { Scenes = [Scene("a")] });
        var titleCalls = 0;
        var coordinator = fixture.Coordinator(
            titleReader: _ => { titleCalls++; return Task.FromResult<GroupTitleParser.Title?>(null); });

        var answer = await coordinator.PrepareAsync(
            enabled: false, groupName: null, captureTitle: false);

        Assert.Equal(SceneCoordinator.AnswerKind.Ready, answer.Kind);
        Assert.Empty(answer.Selection!.Scenes);
        Assert.Equal(0, titleCalls);
        Assert.Empty(fixture.Picker.Asked);
    }

    [Fact]
    public async Task CaptureTitleOnlyStillReadsTheWindow()
    {
        var fixture = new Fixture(new SceneSettings { Scenes = [Scene("a")] });
        var coordinator = fixture.Coordinator(
            titleReader: _ => Task.FromResult<GroupTitleParser.Title?>(
                new GroupTitleParser.Title("群名", null)),
            insightsReader: _ => throw new InvalidOperationException("must not run"));

        var answer = await coordinator.PrepareAsync(
            enabled: false, groupName: null, captureTitle: true);

        Assert.Equal("群名", answer.Selection!.GroupName);
        Assert.Empty(answer.Selection.Scenes);
    }

    [Fact]
    public async Task AdvanceWritesSceneWatermarkAndSenders()
    {
        var fixture = new Fixture(new SceneSettings { Scenes = [Scene("a")] });
        var end = new DateTimeOffset(2026, 9, 25, 13, 0, 0, TimeSpan.Zero);
        var selection = new SceneCoordinator.Selection
        {
            Scenes = [Scene("a")],
            GroupName = "项目群",
            Insights = new WeChatBatchInsights
            {
                Senders = new HashSet<string>(StringComparer.Ordinal) { "张三", "李四" },
                End = end,
            },
        };

        fixture.Coordinator().Advance(selection);

        var memory = fixture.Memories.Load()[GroupName.Normalize("项目群")];
        Assert.Equal("a", memory.LastSceneID);
        Assert.Equal(end, memory.LastSummaryAt);
        Assert.True(memory.Senders.SetEquals(new[] { "张三", "李四" }));
    }

    [Fact]
    public async Task AdvanceWithoutEndOnlyMergesSenders()
    {
        var fixture = new Fixture(new SceneSettings { Scenes = [Scene("a")] });
        var watermark = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
        fixture.Memories.Save(new Dictionary<string, GroupMemory>
        {
            [GroupName.Normalize("群")] = new GroupMemory
            {
                DisplayName = "群",
                LastSummaryAt = watermark,
                Senders = new HashSet<string>(StringComparer.Ordinal) { "张三" },
            },
        });
        var selection = new SceneCoordinator.Selection
        {
            Scenes = [],
            GroupName = "群",
            Insights = new WeChatBatchInsights
            {
                Senders = new HashSet<string>(StringComparer.Ordinal) { "李四" },
            },
        };

        fixture.Coordinator().Advance(selection);

        var memory = fixture.Memories.Load()[GroupName.Normalize("群")];
        Assert.Equal(watermark, memory.LastSummaryAt);
        Assert.True(memory.Senders.SetEquals(new[] { "张三", "李四" }));
    }

    [Fact]
    public async Task ShortcutControllerSelectsDigitAndNotifies()
    {
        var fixture = new Fixture(new SceneSettings
        {
            Scenes = [Scene("a", "甲"), Scene("b", "乙")],
        });
        var settings = fixture.Scenes.Load();
        var toasts = new List<string>();
        var controller = new SceneShortcutController(
            () => settings.EnabledScenes, fixture.Pending, notify: toasts.Add);

        controller.HandleSceneDigit(2);
        Assert.Single(toasts);
        Assert.Contains("乙", toasts[0]);
        Assert.Equal("b", fixture.Pending.ConsumePendingSceneId());

        // A digit past the end of the list and out-of-range digits do nothing.
        controller.HandleSceneDigit(9);
        controller.HandleSceneDigit(0);
        Assert.Null(fixture.Pending.ConsumePendingSceneId());
        Assert.Single(toasts);
    }

    // --- Ported platform-independent cases from SceneTests.swift --------------

    [Fact]
    public void ScenePackageRoundTripsWithoutLocalState()
    {
        var scene = new WeChatScene
        {
            Id = "com.example.customer-review",
            Name = "客户复盘",
            Summary = "summary",
            Instruction = "整理客户消息",
            OutputSpec = "要点、结论、待办",
            Keywords = ["客户", "甲方"],
            Enabled = true,
            PackageVersion = "1.2.3",
            Author = "作者",
            Applicability = "客户群",
        };
        var json = JsonSerializer.Serialize(ScenePackage.FromScene(scene), BatchManifest.JsonOptions);
        var decoded = JsonSerializer.Deserialize<ScenePackage>(json, BatchManifest.JsonOptions)!;
        Assert.Equal(scene.Id, decoded.Id);
        Assert.Equal("1.2.3", decoded.Version);
        Assert.Equal(["客户", "甲方"], decoded.Keywords);
        Assert.False(decoded.ToScene().Enabled);
    }

    [Fact]
    public void VersionsCompareByNumericComponents()
    {
        Assert.Equal(0, SceneVersion.Parse("1.2")!.Value.CompareTo(SceneVersion.Parse("1.2.0")!.Value));
        Assert.True(SceneVersion.Parse("1.10.0") > SceneVersion.Parse("1.2.9"));
        Assert.Null(SceneVersion.Parse("1.beta"));
        Assert.Null(SceneVersion.Parse(""));
    }

    [Fact]
    public void V1AndV2PackagesBecomeTasksWithDefaults()
    {
        const string v1 = """
            {
              "schemaVersion": 1,
              "id": "example.v1",
              "name": "旧任务",
              "version": "1.0.0",
              "instruction": "整理",
              "outputSpec": "输出"
            }
            """;
        var decodedV1 = JsonSerializer.Deserialize<ScenePackage>(v1, BatchManifest.JsonOptions)!;
        Assert.Equal([], decodedV1.RequiredSkillIDs);
        Assert.Equal(AgentIds.All, decodedV1.CompatibleAgents);
        Assert.False(decodedV1.IsOfficial);

        const string v2 = """
            {
              "schemaVersion": 2,
              "id": "example.v2",
              "name": "新任务",
              "version": "1.0.0",
              "instruction": "整理",
              "outputSpec": "输出",
              "requiredSkillIDs": ["wechatbridge.wechat-article-extract"],
              "compatibleAgents": ["chatGPTCodex"],
              "isOfficial": true
            }
            """;
        var decodedV2 = JsonSerializer.Deserialize<ScenePackage>(v2, BatchManifest.JsonOptions)!;
        Assert.Equal(["wechatbridge.wechat-article-extract"], decodedV2.RequiredSkillIDs);
        Assert.Equal([AgentId.ChatGptCodex], decodedV2.CompatibleAgents);
        Assert.True(decodedV2.IsOfficial);
        Assert.Equal(AgentId.WeSight, AgentIds.Matching(ShareAction.WeSight));
    }

    [Fact]
    public void OfficialTaskCopiesToAnEditableUserTask()
    {
        var official = new WeChatScene
        {
            Id = "official",
            Name = "官方任务",
            Instruction = "整理",
            OutputSpec = "输出",
            RequiredSkillIDs = ["skill"],
            CompatibleAgents = [AgentId.Doubao],
            IsOfficial = true,
        };
        var copied = new SceneSettings { Scenes = [official] }.CopiedAsUserTask(official);
        Assert.NotEqual(official.Id, copied.Id);
        Assert.False(copied.IsOfficial);
        Assert.True(copied.Enabled);
        Assert.Equal(official.RequiredSkillIDs, copied.RequiredSkillIDs);
        Assert.Equal(official.CompatibleAgents, copied.CompatibleAgents);
    }

    [Fact]
    public void PromptListsDeclaredSkillsUnderTheRequirementHeader()
    {
        var scene = new WeChatScene
        {
            Name = "提取文章",
            Instruction = "整理",
            OutputSpec = "输出",
            RequiredSkillIDs = ["skill-id"],
            IsOfficial = true,
        };
        var prompt = ScenePrompt.Render(scene, null,
            skills: new SkillRenderContext(AgentId.Claude,
                id => new SkillResolution(id, "文章提取", SkillRenderMode.Missing)));
        Assert.Contains("技能要求：\n- 使用「文章提取」技能", prompt);
    }

    [Fact]
    public void MatchingPrefersBindingThenLongestKeyword()
    {
        var first = Scene("customer", "客户复盘", keywords: ["客户"]);
        var second = Scene("large-customer", "大客户复盘", keywords: ["大客户"]);
        var settings = new SceneSettings
        {
            Scenes = [first, second],
            DefaultSceneID = first.Id,
        };
        var keyword = SceneResolver.Resolve(
            "华东大客户群", settings, new Dictionary<string, GroupMemory>());
        Assert.Equal(second.Id, keyword.Scene!.Id);
        Assert.Equal(SceneMatchSource.Keyword, keyword.Source);

        var memory = new GroupMemory
        {
            DisplayName = "华东大客户群",
            BoundSceneIDs = [first.Id],
        };
        var binding = SceneResolver.Resolve(
            "华东大客户群", settings,
            new Dictionary<string, GroupMemory> { [GroupName.Normalize("华东大客户群")] = memory });
        Assert.Equal(first.Id, binding.Scene!.Id);
        Assert.Equal(SceneMatchSource.Binding, binding.Source);
    }

    [Fact]
    public void GroupBindingKeepsMultipleCandidateScenesInLibraryOrder()
    {
        var first = Scene("first", "一");
        var second = Scene("second", "二");
        var settings = new SceneSettings { Scenes = [first, second] };
        var memory = new GroupMemory
        {
            DisplayName = "群",
            BoundSceneIDs = [second.Id, first.Id],
        };
        var resolved = SceneResolver.Resolve(
            "群", settings,
            new Dictionary<string, GroupMemory> { [GroupName.Normalize("群")] = memory });
        Assert.Equal([first.Id, second.Id], resolved.Scenes.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void FingerprintRequiresUniqueThresholdMatch()
    {
        var knownA = new GroupMemory
        {
            DisplayName = "客户群 A",
            Senders = new HashSet<string>(StringComparer.Ordinal) { "张三", "李四", "王五" },
        };
        var knownB = new GroupMemory
        {
            DisplayName = "客户群 B",
            Senders = new HashSet<string>(StringComparer.Ordinal) { "赵六", "钱七", "孙八" },
        };
        var memories = new Dictionary<string, GroupMemory>
        {
            [GroupName.Normalize("客户群 A")] = knownA,
            [GroupName.Normalize("客户群 B")] = knownB,
        };
        Assert.Equal(
            GroupName.Normalize("客户群 A"),
            GroupFingerprint.Match(
                new HashSet<string> { "张三", "李四" }, memories));
        Assert.Null(GroupFingerprint.Match(
            new HashSet<string> { "张三" }, memories));
        Assert.Null(GroupFingerprint.Match(
            new HashSet<string> { "张三", "赵六" },
            new Dictionary<string, GroupMemory>
            {
                ["a"] = new() { DisplayName = "A", Senders = new(StringComparer.Ordinal) { "张三", "甲" } },
                ["b"] = new() { DisplayName = "B", Senders = new(StringComparer.Ordinal) { "赵六", "乙" } },
            }));
    }

    [Fact]
    public void SenderFingerprintNormalizesAndMatches()
    {
        var fingerprint = SenderFingerprint.FromRawNames(["张三", "李四", " "]);
        Assert.Equal(2, fingerprint.Senders.Count);
        var memories = new Dictionary<string, GroupMemory>
        {
            ["key"] = new()
            {
                DisplayName = "客户群 A",
                Senders = new HashSet<string>(StringComparer.Ordinal) { "张三", "李四", "王五" },
            },
        };
        Assert.Equal("key", fingerprint.MatchMemoryKey(memories));
        Assert.Equal("客户群 A", fingerprint.MatchMemory(memories)!.DisplayName);
    }

    [Fact]
    public void ContinuationPromptIncludesOnlyNewerBoundary()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        var date = new DateTimeOffset(2026, 9, 17, 8, 30, 0, tz.GetUtcOffset(new DateTime(2026, 9, 17, 8, 30, 0)));
        var scene = new WeChatScene
        {
            Name = "日常",
            Instruction = "总结附件",
            OutputSpec = "输出要点",
            Enabled = true,
        };
        var culture = CultureInfo.InvariantCulture;
        var prompt = ScenePrompt.Render(scene, date, date.AddMinutes(60), culture: culture);
        Assert.Contains("2026-09-17 08:30", prompt);
        Assert.Contains("不要重复", prompt);

        Assert.DoesNotContain("不要重复",
            ScenePrompt.Render(scene, date, null, culture: culture));
        Assert.DoesNotContain("不要重复",
            ScenePrompt.Render(scene, date, date, culture: culture));
    }

    [Fact]
    public void GroupTitleParserHandlesCountsAndPlainNames()
    {
        Assert.Equal("华东客户群",
            GroupTitleParser.Parse(["华东客户群 (128)"])!.Value.Name);
        Assert.Equal(12,
            GroupTitleParser.Parse(["本周项目会（12）"])!.Value.MemberCount);
        Assert.Equal("没有人数",
            GroupTitleParser.Parse(["没有人数"])!.Value.Name);
        Assert.Null(GroupTitleParser.Parse(["   "]));
        Assert.Equal("华东客户群",
            GroupTitleParser.Parse(["微信", "华东客户群 (128)", "搜索"])!.Value.Name);
    }

    [Fact]
    public void WindowTitleReaderParsesInjectedTitles()
    {
        var reader = new WeChatWindowTitleReader(() => ["华东客户群 (128)"]);
        var title = reader.Read();
        Assert.Equal("华东客户群", title.Name);
        Assert.Equal(128, title.MemberCount);

        var empty = new WeChatWindowTitleReader(() => []);
        var error = Assert.Throws<WeChatTitleReaderException>(() => empty.Read());
        Assert.Equal(WeChatTitleReaderErrorKind.NoWindow, error.Kind);
        Assert.Null(empty.TryRead());
    }

    [Fact]
    public void DefaultSceneRequiresExplicitSelection()
    {
        var scene = new WeChatScene
        {
            Name = "客户复盘",
            Instruction = "总结",
            OutputSpec = "要点",
            Enabled = true,
        };
        var settings = new SceneSettings { Scenes = [scene] };
        Assert.Null(settings.DefaultSceneID);
        Assert.Null(settings.DefaultScene());
        Assert.Null(SceneResolver.Resolve(
            "未匹配群", settings, new Dictionary<string, GroupMemory>()).Scene);
    }

    [Fact]
    public void ExactGroupCanFallBackToItsLastScene()
    {
        var last = new WeChatScene
        {
            Id = "last",
            Name = "上次场景",
            Instruction = "总结",
            OutputSpec = "要点",
            Enabled = true,
        };
        var settings = new SceneSettings { Scenes = [last] };
        var memory = new GroupMemory { DisplayName = "项目群", LastSceneID = last.Id };
        var resolved = SceneResolver.Resolve(
            "项目群", settings,
            new Dictionary<string, GroupMemory> { [GroupName.Normalize("项目群")] = memory });
        Assert.Equal(last.Id, resolved.Scene!.Id);
        Assert.Equal(SceneMatchSource.LastUsed, resolved.Source);
    }

    [Fact]
    public void GroupMemoryAdvancesOnlyForward()
    {
        var old = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero) + TimeSpan.FromSeconds(100);
        var newer = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero) + TimeSpan.FromSeconds(200);
        var evenNewer = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero) + TimeSpan.FromSeconds(300);
        var first = GroupMemory.Advancing(
            null, "群", "a", new HashSet<string> { "张三" }, newer, at: old);
        var second = GroupMemory.Advancing(
            first, "群", "b", new HashSet<string> { "李四" }, old, at: newer);
        Assert.Equal(newer, second.LastSummaryAt);
        Assert.Equal("b", second.LastSceneID);
        Assert.Equal(new HashSet<string> { "张三", "李四" }, second.Senders);

        var third = GroupMemory.Advancing(
            second, "群", "c", new HashSet<string>(), evenNewer);
        Assert.Equal(evenNewer, third.LastSummaryAt);
    }

    [Fact]
    public void BatchStateContextIsBackwardCompatible()
    {
        const string oldJson = """{"schemaVersion":1,"shelved":[],"outcome":null}""";
        var decoded = JsonSerializer.Deserialize<BatchState>(oldJson, BatchManifest.JsonOptions)!;
        Assert.Null(decoded.ChatName);
        var updated = decoded.WithContext(chatName: "群", sceneId: "s", sceneName: "场景");
        Assert.Equal("群", updated.ChatName);
        Assert.Equal("场景", updated.SceneName);
    }
}
