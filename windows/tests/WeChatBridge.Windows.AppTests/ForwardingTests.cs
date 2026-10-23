using System.IO;
using System.IO.Compression;
using System.Text;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Delivery;
using WeChatBridge.Windows.Services;
using WeChatBridge.Windows.Tests;

namespace WeChatBridge.Windows.AppTests;

public sealed class ForwardingTests
{
    [Fact]
    public async Task OriginalFilesCanBeDeliveredWithoutConversationNotes()
    {
        using var fixture = new TempInbox();
        var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
        var batch = await Add(fixture, reader, "unnamed.zip", "");
        var pasted = new List<PastePayload>();
        var model = new MainViewModel(fixture.Paths, Path.Combine(fixture.Root, "config"), false, copyPayload: pasted.Add);
        model.Reload();
        var id = model.Collections.Append(model.Batches.Single(), null);
        model.Reload();
        await model.DeliverCollection(id, ShareAction.Clipboard, null);
        Assert.Equal(CollectionStatus.Delivered, model.Collections.Ledger.Editable(id).Status);
        Assert.True(string.IsNullOrWhiteSpace(reader.StateFor(batch.Id)?.ChatName));
        Assert.Equal(BatchOutcomeKind.Copied, reader.StateFor(batch.Id)?.Outcome?.Kind);
        Assert.Single(pasted);
        Assert.True(File.Exists(batch.Items[0].FullPath));
    }

    [Fact]
    public void MissingAppEnableIsRejectedAndPersistedOff()
    {
        using var fixture = new TempInbox();
        var config = Path.Combine(fixture.Root, "config");
        var installed = false;
        var model = new MainViewModel(fixture.Paths, config, false, isAppInstalled: _ => installed);
        var warnings = new List<string>();
        model.ToastRequested += (text, _, _, _) => warnings.Add(text);
        model.SetEntryEnabled(ShareAction.Codex, true);
        Assert.False(model.IsEntryEnabled(ShareAction.Codex));
        Assert.False(model.IsDestinationInstalled(ShareAction.Codex, null));
        Assert.False(new AppSettingsStore(config).Load().IsEntryEnabled(ShareAction.Codex));
        Assert.Single(warnings);
        Assert.Contains("Codex", warnings[0]);
        Assert.False(model.Entries.Single(e => e.Action == ShareAction.Codex).IsEnabled);
        installed = true;
        Assert.True(model.IsDestinationInstalled(ShareAction.Codex, null));
        model.SetEntryEnabled(ShareAction.Codex, true);
        Assert.True(model.IsEntryEnabled(ShareAction.Codex));
        installed = false;
        model.SetEntryEnabled(ShareAction.Codex, false);
        Assert.False(model.IsEntryEnabled(ShareAction.Codex));
        Assert.Single(warnings);
    }

    [Fact]
    public async Task MixedCollectionUsesOnlyExplicitSceneAndRecordsItForEveryMember()
    {
        using var fixture = new TempInbox();
        var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
        var first = await Add(fixture, reader, "one.zip", "甲群");
        var second = await Add(fixture, reader, "two.zip", "乙群");
        var config = Path.Combine(fixture.Root, "config");
        var pickerCalls = 0;
        using var scenes = new SceneService(config,
            picker: (_, _) => { pickerCalls++; return Task.FromResult(ScenePickerAnswer.Cancelled); },
            titleReader: _ => Task.FromResult<GroupTitleParser.Title?>(new("无关的当前群", null)));
        var scene = scenes.AddScene();
        scene.Instruction = "本次明确选择的提示词";
        scene.CompatibleAgents = [AgentId.ChatGptCodex];
        scenes.UpdateScene(scene);
        var payloads = new List<PastePayload>();
        var model = new MainViewModel(fixture.Paths, config, false, Engine(reader, payloads.Add), scenes, payloads.Add);
        var group = model.Collections.Append(first, null);
        model.Collections.Append(second, null);
        await model.DeliverCollection(group, ShareAction.Codex, null, scene);
        Assert.Equal(0, pickerCalls);
        Assert.Contains(payloads.OfType<PastePayload.Text>(), p => p.Value.Contains("本次明确选择的提示词"));
        Assert.All(new[] { first, second }, b => Assert.Equal(scene.Id, reader.StateFor(b.Id)?.SceneID));
        Assert.Equal("甲群", reader.StateFor(first.Id)?.ChatName);
        Assert.Equal("乙群", reader.StateFor(second.Id)?.ChatName);
        model.Query = scene.Name;
        Assert.Single(model.CollectionRows);
        payloads.Clear();
        await model.DeliverCollection(group, ShareAction.Codex, null);
        Assert.Empty(payloads.OfType<PastePayload.Text>());
        Assert.All(new[] { first, second }, b => Assert.Null(reader.StateFor(b.Id)?.SceneID));
        Assert.Equal(0, pickerCalls);
    }
    private static async Task<ReadyBatch> Add(TempInbox fixture, InboxReader reader, string name, string chatName)
    {
        var source = fixture.WriteSource(name, "");
        using (var stream = File.Open(source, FileMode.Create))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("聊天记录.txt", CompressionLevel.NoCompression).Open(), new UTF8Encoding(false)))
            writer.Write("·甲\n2026年9月20日 09:10\n第一条\n");
        var committed = await InboxWriter.CommitAsync(fixture.Paths,
            [new InboxSourceFile(source, name, "application/zip", 0, 0)]);
        reader.RecordContext(committed.BatchId, chatName);
        return reader.LoadBatches().Single(b => b.Id == committed.BatchId);
    }

    private static SceneService Scenes(string directory)
    {
        var service = new SceneService(directory, titleReader: _ => Task.FromResult<GroupTitleParser.Title?>(null));
        var settings = service.LoadSettings();
        foreach (var scene in settings.Scenes) scene.Enabled = false;
        service.Scenes.Save(settings);
        return service;
    }

    private static DeliveryEngine Engine(InboxReader reader, Action<PastePayload> copied) => new(new DeliveryEnvironment
    {
        ResolveTarget = spec => new ResolvedTarget(spec, 123, 456, null),
        ActivateAsync = (target, _) => Task.FromResult(target),
        ForegroundWindow = () => 456,
        WindowProcessId = _ => 123,
        ThisProcessId = () => 999,
        WriteFileDropList = paths => { copied(new PastePayload.Files(paths)); return true; },
        WriteClipboardText = text => { copied(new PastePayload.Text(text)); return true; },
        SendCtrlV = () => true,
        DelayAsync = (_, _) => Task.CompletedTask,
    }, reader);

    [Fact]
    public async Task ChangingHubDestinationResolvesSelectedAppAndRecordsSelectedAction()
    {
        using var fixture = new TempInbox();
        var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
        var batch = await Add(fixture, reader, "one.zip", "项目群");
        var pasted = new List<PastePayload>();
        using var scenes = Scenes(Path.Combine(fixture.Root, "config"));
        var model = new MainViewModel(fixture.Paths, Path.Combine(fixture.Root, "config"), false,
            Engine(reader, pasted.Add), scenes, pasted.Add);
        await model.PerformForward(batch with { Action = ShareAction.Hub }, ShareAction.DeepSeekHarness, null);
        Assert.Equal(ShareAction.DeepSeekHarness, reader.StateFor(batch.Id)?.Action);
        Assert.Equal(BatchOutcomeKind.Delivered, reader.StateFor(batch.Id)?.Outcome?.Kind);
        Assert.NotEmpty(pasted);
        Assert.Contains(pasted, payload => payload is PastePayload.Files);
    }

    [Fact]
    public async Task ObsidianDeliveryIsQuietAndClearsOldSceneContext()
    {
        using var fixture = new TempInbox();
        var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
        var batch = await Add(fixture, reader, "one.zip", "项目群");
        reader.RecordContext(batch.Id, sceneId: "old-scene", sceneName: "旧场景");
        var vault = Path.Combine(fixture.Root, "vault");
        Directory.CreateDirectory(vault);
        var config = Path.Combine(fixture.Root, "config");
        new AppSettingsStore(config).Save(new AppSettings { ObsidianVaultPath = vault });
        using var scenes = Scenes(config);
        var model = new MainViewModel(fixture.Paths, config, false, scenes: scenes, copyPayload: _ => { });
        var notifications = new List<string>();
        model.DeliveryNotificationRequested += (_, button, _) => notifications.Add(button);
        Assert.False(model.OpenObsidianAfterDelivery);
        await model.PerformForward(batch, ShareAction.Obsidian, null);
        Assert.Equal(["打开笔记"], notifications);
        Assert.Null(reader.StateFor(batch.Id)?.SceneID);
        Assert.Equal("项目群", reader.StateFor(batch.Id)?.ChatName);
        Assert.Single(Directory.GetFiles(vault, "*.md", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CollectionDeliversAllOriginalsInOnePlanAndRecordsEveryMember()
    {
        using var fixture = new TempInbox();
        var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
        var first = await Add(fixture, reader, "one.zip", "项目群");
        var second = await Add(fixture, reader, "two.zip", "项目群");
        var config = Path.Combine(fixture.Root, "config");
        using var scenes = Scenes(config);
        var payloads = new List<PastePayload>();
        var model = new MainViewModel(fixture.Paths, config, false, Engine(reader, payloads.Add), scenes, payloads.Add);
        var group = model.Collections.Append(first, null);
        model.Collections.Append(second, null);
        await model.DeliverCollection(group, ShareAction.Codex, null);
        Assert.Equal(CollectionStatus.Delivered, model.Collections.Ledger.Editable(group).Status);
        Assert.All(payloads.OfType<PastePayload.Files>(), files => Assert.Equal([first.Items[0].FullPath, second.Items[0].FullPath], files.Paths));
        Assert.Equal(BatchOutcomeKind.Delivered, reader.StateFor(first.Id)?.Outcome?.Kind);
        Assert.Equal(BatchOutcomeKind.Delivered, reader.StateFor(second.Id)?.Outcome?.Kind);
    }

    [Fact]
    public async Task CollectionFolderDeliverySavesOnlyOriginalsAndMissingFolderIsRetryable()
    {
        using var fixture = new TempInbox();
        var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
        var first = await Add(fixture, reader, "one.zip", "甲群");
        var second = await Add(fixture, reader, "two.zip", "乙群");
        var config = Path.Combine(fixture.Root, "config");
        using var scenes = Scenes(config);
        var model = new MainViewModel(fixture.Paths, config, false, scenes: scenes, copyPayload: _ => { });
        var group = model.Collections.Append(first, null);
        model.Collections.Append(second, null);
        await model.DeliverCollection(group, ShareAction.Folder, null);
        Assert.Equal(CollectionStatus.Retry, model.Collections.Ledger.Editable(group).Status);
        Assert.All(new[] { first, second }, batch => Assert.True(File.Exists(batch.Items[0].FullPath)));
        var notes = Path.Combine(fixture.Root, "notes");
        Directory.CreateDirectory(notes);
        model.DeliveryFolderPath = notes;
        model.DeliverySubfolder = "项目/周报";
        await model.DeliverCollection(group, ShareAction.Folder, null);
        Assert.Empty(Directory.GetFiles(notes, "*.md", SearchOption.AllDirectories));
        var saved = Directory.GetFiles(notes, "*.zip");
        Assert.Equal(2, saved.Length);
        Assert.Equal(File.ReadAllBytes(first.Items[0].FullPath), File.ReadAllBytes(Path.Combine(notes, "one.zip")));
        Assert.Equal(File.ReadAllBytes(second.Items[0].FullPath), File.ReadAllBytes(Path.Combine(notes, "two.zip")));
        Assert.Equal(CollectionStatus.Delivered, model.Collections.Ledger.Editable(group).Status);
    }
}
