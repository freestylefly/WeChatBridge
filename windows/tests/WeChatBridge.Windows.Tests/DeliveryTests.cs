using System.Drawing;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Delivery;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// A fake OS for the delivery engine: records every touch in order and never
/// activates a real window or owns the real clipboard. The engine's contract —
/// clipboard-first fallback, foreground wait, paste order — is asserted purely
/// against the recorded op sequence.
/// </summary>
internal sealed class FakeOs
{
    public static readonly DateTimeOffset Stamp = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The target's own window/pid pair the fake reports as foreground.</summary>
    public const int TargetPid = 4242;
    public const int TargetHwnd = 7777;
    public const uint OwnPid = 999;

    public ResolvedTarget? Resolved { get; set; }
    public nint Foreground { get; set; } = TargetHwnd;
    public List<string> Ops { get; } = [];
    public List<TimeSpan> Delays { get; } = [];

    /// <summary>Per-call clipboard outcomes; a depleted queue falls back to the defaults.</summary>
    public Queue<bool> FileDropResults { get; } = new();
    public Queue<bool> TextResults { get; } = new();
    public bool KeystrokeOk { get; set; } = true;

    public DeliveryEnvironment Env { get; }

    public FakeOs()
    {
        Resolved = new ResolvedTarget(
            WindowsForwardTargets.For(ShareAction.Codex)!,
            TargetPid,
            TargetHwnd,
            @"C:\Apps\Codex\Codex.exe");
        Env = new DeliveryEnvironment
        {
            ResolveTarget = spec => { Ops.Add($"resolve:{spec.DisplayName}"); return Resolved; },
            ActivateAsync = (target, _) => { Ops.Add("activate"); return Task.FromResult(target); },
            ForegroundWindow = () => Foreground,
            WindowProcessId = hwnd => hwnd == TargetHwnd ? (uint)TargetPid : 1111u,
            WriteFileDropList = paths =>
            {
                Ops.Add($"files:{paths.Count}");
                return FileDropResults.Count == 0 || FileDropResults.Dequeue();
            },
            WriteClipboardText = text =>
            {
                Ops.Add($"text:{text}");
                return TextResults.Count == 0 || TextResults.Dequeue();
            },
            SendCtrlV = () => { Ops.Add("ctrlv"); return KeystrokeOk; },
            SendCtrlKey = key => { Ops.Add($"ctrlkey:0x{key:X2}"); return KeystrokeOk; },
            DelayAsync = (span, _) => { Delays.Add(span); return Task.CompletedTask; },
            ThisProcessId = () => OwnPid,
            Now = () => Stamp,
        };
    }
}

internal static class DeliveryFixtures
{
    public const string ArchivePath = @"D:\inbox\Ready\x\files\聊天记录 a.zip";

    public static ReadyBatch Batch(ShareAction action, params string[] paths)
    {
        var batchId = Guid.NewGuid();
        var created = new DateTimeOffset(2026, 9, 25, 11, 0, 0, TimeSpan.Zero);
        var items = paths
            .Select(p => new ReadyItem(
                Guid.NewGuid(), batchId, Path.GetFileName(p), p,
                100, "application/zip", created, action))
            .ToList();
        return new ReadyBatch(batchId, @"D:\inbox\Ready\x", created, action, items,
            Outcome: null, TargetName: null, ChatName: null, SceneID: null, SceneName: null,
            IsFirstSeen: true);
    }
}

public sealed class DeliveryEngineTests
{
    private const string A = DeliveryFixtures.ArchivePath;

    [Fact]
    public async Task ClipboardIsWrittenBeforeActivationSoEveryFailureKeepsTheFallback()
    {
        var os = new FakeOs();
        var engine = new DeliveryEngine(os.Env);
        var result = await engine.DeliverAsync(DeliveryFixtures.Batch(ShareAction.Codex, A));

        Assert.True(result.Delivered);
        Assert.Equal(BatchOutcomeKind.Delivered, result.Outcome.Kind);
        Assert.Equal(FakeOs.Stamp, result.Outcome.At);
        // The manual fallback is written before the target is even resolved;
        // the files are pasted by the same write that a manual Ctrl+V would get.
        Assert.Equal(["files:1", "resolve:Codex", "activate", "files:1", "ctrlv"], os.Ops);
        Assert.Contains(DeliveryEngine.ForegroundSettle, os.Delays);
    }

    [Fact]
    public async Task APromptIsPastedBeforeTheFilesAsASecondCtrlV()
    {
        var os = new FakeOs();
        var engine = new DeliveryEngine(os.Env);
        var result = await engine.DeliverAsync(
            DeliveryFixtures.Batch(ShareAction.Codex, A), prompt: "总结一下");

        Assert.True(result.Delivered);
        Assert.Equal(
            ["files:1", "resolve:Codex", "activate", "text:总结一下", "ctrlv", "files:1", "ctrlv"],
            os.Ops);
        Assert.Contains(DeliveryEngine.BetweenPastes, os.Delays);
        Assert.Equal(2, result.Plan.Count);
    }

    [Fact]
    public async Task ATargetThatNeverComesForegroundFailsWithTheFilesStillPasteable()
    {
        var os = new FakeOs { Foreground = 5555 }; // some other app's window
        var engine = new DeliveryEngine(
            os.Env with { ForegroundTimeout = TimeSpan.FromMilliseconds(100) });
        var result = await engine.DeliverAsync(DeliveryFixtures.Batch(ShareAction.Codex, A));

        Assert.False(result.Delivered);
        Assert.Equal(BatchOutcomeKind.Failed, result.Outcome.Kind);
        Assert.Equal(DeliveryFailureKind.DidNotBecomeActive, result.Failure);
        Assert.Equal("Codex", result.TargetName);
        // Fallback written at the start and rewritten on failure — no paste attempted.
        Assert.Equal(["files:1", "resolve:Codex", "activate", "files:1"], os.Ops);
    }

    [Fact]
    public async Task AnUninstalledTargetFailsBeforeAnyActivation()
    {
        var os = new FakeOs { Resolved = null };
        var engine = new DeliveryEngine(os.Env);
        var result = await engine.DeliverAsync(DeliveryFixtures.Batch(ShareAction.Claude, A));

        Assert.Equal(DeliveryFailureKind.NotInstalled, result.Failure);
        Assert.Equal("Claude", result.TargetName);
        // Fallback written up front and re-asserted by the failure path.
        Assert.Equal(["files:1", "resolve:Claude", "files:1"], os.Ops);
    }

    [Fact]
    public async Task ARefusedClipboardWriteMidPlanRestoresTheManualPayload()
    {
        var os = new FakeOs();
        // prompt text ok → file drop refused → fallback must rewrite the files.
        os.TextResults.Enqueue(true);
        os.FileDropResults.Enqueue(true);   // initial manual fallback write
        os.FileDropResults.Enqueue(false);  // the paste's own file write
        var engine = new DeliveryEngine(os.Env);
        var result = await engine.DeliverAsync(
            DeliveryFixtures.Batch(ShareAction.Codex, A), prompt: "总结一下");

        Assert.Equal(DeliveryFailureKind.ClipboardWriteFailed, result.Failure);
        // Only the prompt's Ctrl+V went out; the final op is the fallback
        // restoring the files after the refused file-drop write.
        Assert.Single(os.Ops, op => op == "ctrlv");
        Assert.Equal("files:1", os.Ops[^1]);
    }

    [Fact]
    public async Task ARejectedKeystrokeFailsAndLeavesTheFilesOnTheClipboard()
    {
        var os = new FakeOs { KeystrokeOk = false };
        var engine = new DeliveryEngine(os.Env);
        var result = await engine.DeliverAsync(DeliveryFixtures.Batch(ShareAction.Codex, A));

        Assert.Equal(DeliveryFailureKind.KeystrokeRejected, result.Failure);
        Assert.Equal("files:1", os.Ops[^1]);
    }

    [Fact]
    public async Task CustomActionNeedsTheIntentTargetAndNamesItInFailures()
    {
        var os = new FakeOs { Resolved = null };
        var engine = new DeliveryEngine(os.Env);
        var target = new ForwardTarget(
            @"C:\Tools\MyTerm.exe", "MyTerm", DateTimeOffset.UtcNow);

        var result = await engine.DeliverAsync(DeliveryFixtures.Batch(ShareAction.Custom, A), target);

        Assert.Equal(DeliveryFailureKind.NotInstalled, result.Failure);
        Assert.Equal("MyTerm", result.TargetName);
        Assert.Equal(["files:1", "resolve:MyTerm", "files:1"], os.Ops);
    }

    [Fact]
    public async Task APathOnlyCustomTargetGetsOneTextLine()
    {
        var os = new FakeOs();
        var engine = new DeliveryEngine(os.Env);
        var target = new ForwardTarget(
            @"C:\Tools\MyTerm.exe", "MyTerm", DateTimeOffset.UtcNow, PastesPathOnly: true);

        var result = await engine.DeliverAsync(
            DeliveryFixtures.Batch(ShareAction.Custom, A), target, "总结一下");

        Assert.True(result.Delivered);
        // A terminal gets one line: prompt folded into the quoted paths. Both
        // the fallback write and the paste use the same text payload.
        var line = $"总结一下 \"{A}\" ";
        Assert.Equal([$"text:{line}", "resolve:MyTerm", "activate", $"text:{line}", "ctrlv"], os.Ops);
    }

    [Theory]
    [InlineData(ShareAction.Doubao)]
    [InlineData(ShareAction.Qwen)]
    public async Task AgentModeTargetsReadTheArchivePathRatherThanReceivingAFileDrop(ShareAction action)
    {
        // Ports the macOS agent-mode shortcut: a ZIP goes as a readable local
        // path, not a file the client must upload — Windows 千问 cannot take a
        // pasted archive either, so it shares Doubao's channel.
        var os = new FakeOs
        {
            Resolved = new ResolvedTarget(
                WindowsForwardTargets.For(action)!, FakeOs.TargetPid, FakeOs.TargetHwnd, null),
        };
        var engine = new DeliveryEngine(os.Env);
        var result = await engine.DeliverAsync(DeliveryFixtures.Batch(action, A));

        Assert.True(result.Delivered);
        var text = Assert.IsType<PastePayload.Text>(Assert.Single(result.Plan));
        Assert.Equal($"\"{A}\" ", text.Value);
    }

    [Fact]
    public async Task DoubaoOpensANewWorkTaskBeforeThePaste()
    {
        // The 对话 composer cannot read a local file; Ctrl+J lands a fresh
        // 工作任务 whose composer can. The hotkey goes out after the window is
        // foreground and before the text paste.
        var os = new FakeOs
        {
            Resolved = new ResolvedTarget(
                WindowsForwardTargets.For(ShareAction.Doubao)!,
                FakeOs.TargetPid, FakeOs.TargetHwnd, null),
        };
        var engine = new DeliveryEngine(os.Env);
        var result = await engine.DeliverAsync(DeliveryFixtures.Batch(ShareAction.Doubao, A), prompt: "看一下");

        Assert.True(result.Delivered);
        var hotkey = os.Ops.IndexOf("ctrlkey:0x4A");
        Assert.True(hotkey > os.Ops.IndexOf("activate") && hotkey < os.Ops.IndexOf("ctrlv"));
        Assert.Contains(DeliveryEngine.PrePasteHotkeySettle, os.Delays);
    }

    [Fact]
    public async Task ATargetWithoutAHotkeyPastesStraightAway()
    {
        var os = new FakeOs();
        var engine = new DeliveryEngine(os.Env);
        await engine.DeliverAsync(DeliveryFixtures.Batch(ShareAction.Codex, A));

        Assert.DoesNotContain(os.Ops, op => op.StartsWith("ctrlkey", StringComparison.Ordinal));
        Assert.DoesNotContain(DeliveryEngine.PrePasteHotkeySettle, os.Delays);
    }

    [Fact]
    public async Task NoTargetMeansNoPasteButTheFilesAreOnTheClipboard()
    {
        var os = new FakeOs();
        var engine = new DeliveryEngine(os.Env);
        // Clipboard action has no paste destination; the share helper already
        // did the copy. If the engine is driven there anyway it must fail
        // cleanly rather than pretend an app is missing.
        var result = await engine.DeliverAsync(DeliveryFixtures.Batch(ShareAction.Custom, A));

        Assert.Equal(DeliveryFailureKind.NoTarget, result.Failure);
        Assert.Equal(["files:1"], os.Ops);
    }

    [Fact]
    public async Task IntentDrivenCustomForwardRebuildsTheTarget()
    {
        var os = new FakeOs { Resolved = null };
        var engine = new DeliveryEngine(os.Env);
        var intent = new BatchIntent
        {
            Action = ShareAction.Custom,
            RequestedAt = DateTimeOffset.UtcNow,
            TargetBundleIdentifier = @"C:\Tools\MyTerm.exe",
            TargetDisplayName = "MyTerm",
        };

        var result = await engine.DeliverAsync(DeliveryFixtures.Batch(ShareAction.Custom, A), intent);

        Assert.Equal("MyTerm", result.TargetName);
        Assert.Contains("resolve:MyTerm", os.Ops);
    }

    [Fact]
    public async Task DeliveredOutcomeIsRecordedIntoStateJson()
    {
        using var fixture = new TempInbox();
        var source = fixture.WriteSource("聊天记录.zip", "zip-bytes");
        var commit = await InboxWriter.CommitAsync(
            fixture.Paths, [new InboxSourceFile(source, "聊天记录.zip", "application/zip", 0, 0)]);
        var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
        var committed = Path.Combine(commit.BatchDirectory, "files", "聊天记录.zip");
        var batch = DeliveryFixtures.Batch(ShareAction.Codex, committed) with { Id = commit.BatchId };

        var os = new FakeOs();
        var engine = new DeliveryEngine(os.Env, inbox: reader);
        var result = await engine.DeliverAsync(batch);

        Assert.True(result.Delivered);
        var state = reader.StateFor(commit.BatchId);
        Assert.NotNull(state?.Outcome);
        Assert.Equal(BatchOutcomeKind.Delivered, state!.Outcome!.Kind);
        Assert.Equal("Codex", state.TargetName);
    }

    [Fact]
    public async Task FailedOutcomeIsRecordedWithTheReasonAndTarget()
    {
        using var fixture = new TempInbox();
        var source = fixture.WriteSource("聊天记录.zip", "zip-bytes");
        var commit = await InboxWriter.CommitAsync(
            fixture.Paths, [new InboxSourceFile(source, "聊天记录.zip", "application/zip", 0, 0)]);
        var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
        var batch = DeliveryFixtures.Batch(ShareAction.Codex) with { Id = commit.BatchId };

        var os = new FakeOs { Resolved = null };
        var engine = new DeliveryEngine(os.Env, inbox: reader);
        var result = await engine.DeliverAsync(batch);

        Assert.False(result.Delivered);
        var state = reader.StateFor(commit.BatchId);
        Assert.Equal(BatchOutcomeKind.Failed, state!.Outcome!.Kind);
        Assert.Contains("Codex", state.Outcome!.Detail);
        Assert.Equal("Codex", state.TargetName);
    }
}

public sealed class DeliveryTargetTableTests
{
    [Fact]
    public void EveryPasteEntryResolvesToASpecAndNonPasteEntriesDoNot()
    {
        foreach (var action in new[]
                 {
                     ShareAction.Codex, ShareAction.Claude, ShareAction.Doubao,
                     ShareAction.Qwen, ShareAction.WorkBuddy, ShareAction.WeSight,
                 })
        {
            var spec = WindowsForwardTargets.For(action);
            Assert.NotNull(spec);
            Assert.NotEmpty(spec!.ProcessNames);
            Assert.NotEmpty(spec.ExeCandidates);
            Assert.Equal(action.TargetDisplayName(), spec.DisplayName);
        }
        // Obsidian is a vault write, Clipboard is finished by the helper,
        // Custom names no app of its own.
        Assert.Null(WindowsForwardTargets.For(ShareAction.Obsidian));
        Assert.Null(WindowsForwardTargets.For(ShareAction.Clipboard));
        Assert.Null(WindowsForwardTargets.For(ShareAction.Custom));
    }

    [Fact]
    public void DoubaoAndQwenReadLocalArchives()
    {
        // Both Windows clients can read a local ZIP path but cannot take a
        // pasted archive; the rest receive a real file drop.
        Assert.True(WindowsForwardTargets.For(ShareAction.Doubao)!.ReadsLocalArchives);
        Assert.True(WindowsForwardTargets.For(ShareAction.Qwen)!.ReadsLocalArchives);
        Assert.False(WindowsForwardTargets.For(ShareAction.Codex)!.ReadsLocalArchives);
        Assert.False(WindowsForwardTargets.For(ShareAction.Claude)!.ReadsLocalArchives);
        Assert.False(WindowsForwardTargets.For(ShareAction.WorkBuddy)!.ReadsLocalArchives);
        Assert.False(WindowsForwardTargets.For(ShareAction.WeSight)!.ReadsLocalArchives);
    }

    [Fact]
    public void ACustomTargetCarryingAnExePathResolvesByPathAndProcessName()
    {
        var spec = WindowsForwardTargets.ForCustom(
            new ForwardTarget(@"C:\Tools\MyTerm.exe", "MyTerm", DateTimeOffset.UtcNow));
        Assert.Equal(["MyTerm"], spec.ProcessNames);
        Assert.Equal([@"C:\Tools\MyTerm.exe"], spec.ExeCandidates);
        Assert.Null(spec.Aumid);
    }

    [Fact]
    public void ACustomTargetCarryingAnAumidLaunchesThroughAppsFolder()
    {
        var spec = WindowsForwardTargets.ForCustom(
            new ForwardTarget("SomeApp_abc123!App", "SomeApp", DateTimeOffset.UtcNow));
        Assert.Equal("SomeApp_abc123!App", spec.Aumid);
        Assert.Empty(spec.ProcessNames);
    }

    [Fact]
    public void ACustomTargetCarryingABareNameTriesProcessAndPath()
    {
        var spec = WindowsForwardTargets.ForCustom(
            new ForwardTarget("FooTerm", "Foo", DateTimeOffset.UtcNow));
        Assert.Equal(["FooTerm"], spec.ProcessNames);
        Assert.Equal(["FooTerm.exe"], spec.ExeCandidates);
    }

    [Fact]
    public void IntentTargetIsRebuiltFromTheTwoFlatFields()
    {
        var intent = new BatchIntent
        {
            Action = ShareAction.Custom,
            RequestedAt = DateTimeOffset.UtcNow,
            TargetBundleIdentifier = @"C:\Tools\MyTerm.exe",
            TargetDisplayName = "MyTerm",
        };
        var target = WindowsForwardTargets.IntentTarget(intent);
        Assert.Equal(@"C:\Tools\MyTerm.exe", target!.BundleIdentifier);
        Assert.Equal("MyTerm", target.DisplayName);
        Assert.Null(WindowsForwardTargets.IntentTarget(null));
        Assert.Null(WindowsForwardTargets.IntentTarget(new BatchIntent
        {
            Action = ShareAction.Codex,
            RequestedAt = DateTimeOffset.UtcNow,
        }));
    }

    [Fact]
    public async Task TheTableIsInjectableSoAnExeNameFixNeverTouchesTheEngine()
    {
        var os = new FakeOs();
        var engine = new DeliveryEngine(
            os.Env,
            targets: new Dictionary<ShareAction, WindowsForwardTarget>
            {
                [ShareAction.Codex] = new WindowsForwardTarget("Codex Dev", ["CodexDev"], ["CodexDev.exe"]),
            });
        var result = await engine.DeliverAsync(
            DeliveryFixtures.Batch(ShareAction.Codex, DeliveryFixtures.ArchivePath));
        // The spec that reached the resolver came from the override table.
        Assert.True(result.Delivered);
        Assert.Equal("Codex Dev", result.TargetName);
        Assert.Contains("resolve:Codex Dev", os.Ops);
    }
}

/// <summary>「发送到自定义」 without a panel — ported from CustomForwardDecisionTests.</summary>
public sealed class DeliveryDecisionTests
{
    private static ForwardTarget Target(string id) =>
        new(id, id, new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void AnEmptyListAsksNothingAndSendsNothing() =>
        Assert.IsType<CustomForwardDecision.None>(CustomForwardDecision.Decide([]));

    [Fact]
    public void ASingleAppIsForwardedWithoutAsking()
    {
        var only = Target("C:\\Tools\\MyTerm.exe");
        var decision = Assert.IsType<CustomForwardDecision.Single>(CustomForwardDecision.Decide([only]));
        Assert.Equal(only, decision.Target);
    }

    [Fact]
    public void TwoOrMoreAppsAreOfferedInTheOrderGiven()
    {
        var list = new List<ForwardTarget> { Target("a"), Target("b"), Target("c") };
        // Order is preserved because the caller has already put the last-used
        // app first, and the panel's first row is the one Return picks.
        var decision = Assert.IsType<CustomForwardDecision.Choose>(CustomForwardDecision.Decide(list));
        Assert.Equal(list, decision.Targets);
    }
}

/// <summary>Panel placement ported from PointerPlacementTests.swift, y-axis flipped for Windows.</summary>
public sealed class DeliveryPointerPlacementTests
{
    /// <summary>A 1440×900 screen with the taskbar taken off the bottom.</summary>
    private static readonly RectangleF Screen = new(0, 0, 1440, 875);
    private static readonly SizeF Panel = new(260, 140);

    [Fact]
    public void HangsBelowRightOfThePointer()
    {
        var frame = PointerPlacement.Frame(Panel, new PointF(400, 600), Screen);
        Assert.Equal(410, frame.Left);
        Assert.Equal(610, frame.Top);
    }

    [Fact]
    public void FlipsLeftRatherThanRunningOffTheRightEdge()
    {
        var frame = PointerPlacement.Frame(Panel, new PointF(1400, 600), Screen);
        Assert.Equal(1390, frame.Right);
        Assert.True(frame.Right <= Screen.Right - 12);
    }

    [Fact]
    public void FlipsAboveRatherThanSittingUnderThePointer()
    {
        var frame = PointerPlacement.Frame(Panel, new PointF(400, 820), Screen);
        Assert.Equal(670, frame.Top);
    }

    /// <summary>
    /// A second display to the left has negative coordinates; the panel has to
    /// stay on it rather than being pulled back towards the origin.
    /// </summary>
    [Fact]
    public void StaysOnTheScreenThePointerIsOn()
    {
        var left = new RectangleF(-1920, -200, 1920, 1080);
        var frame = PointerPlacement.Frame(Panel, new PointF(-1000, 500), left);
        Assert.Equal(-990, frame.Left);
        Assert.True(left.Contains(frame));
    }

    /// <summary>Neither side fits: clamping has the last word, and the whole panel is still on screen.</summary>
    [Fact]
    public void ClampsWhenNeitherSideFits()
    {
        var narrow = new RectangleF(0, 0, 300, 200);
        var frame = PointerPlacement.Frame(Panel, new PointF(290, 10), narrow);
        Assert.True(RectangleF.Inflate(narrow, -12, -12).Contains(frame));
    }
}

/// <summary>Format choice and display names ported from RepresentationTests.swift.</summary>
public sealed class DeliveryRepresentationTests
{
    [Fact]
    public void PrefersStorageItemsOverTheLinkTheSameShareAlsoOffers()
    {
        var choice = Representation.Choose(["StorageItems", "Uri", "Text"]);
        Assert.Equal(new Representation("StorageItems", LoadStrategy.File), choice);
    }

    [Fact]
    public void AcceptsAShellFileDropAsRealFiles()
    {
        var choice = Representation.Choose(["FileDrop", "Text"]);
        Assert.Equal(new Representation("FileDrop", LoadStrategy.File), choice);
    }

    [Fact]
    public void NeverMistakesALinkForTheFileItPointsAt()
    {
        // Same rule as macOS refusing a file representation of public.url:
        // staging a Uri would produce a tiny link file, not the attachment.
        Assert.Null(Representation.Choose(["Uri", "Text"]));
        Assert.Null(Representation.Choose([]));
    }

    [Fact]
    public void DisplayNamePrefersTheProviderNameOverTheStagedFile()
    {
        var name = Representation.DisplayName("聊天记录 测试.zip", @"C:\x\Zip归档.zip");
        Assert.Equal("聊天记录 测试.zip", name);
    }

    [Fact]
    public void DisplayNameBorrowsTheStagedExtensionWhenTheNameHasNone()
    {
        var name = Representation.DisplayName("聊天记录", @"C:\x\Zip归档.zip");
        Assert.Equal("聊天记录.zip", name);
    }

    [Fact]
    public void DisplayNameFallsBackToTheStagedFileWhenTheProviderNamedNothing()
    {
        var name = Representation.DisplayName(null, @"C:\x\Zip归档.zip");
        Assert.Equal("Zip归档.zip", name);
    }
}
