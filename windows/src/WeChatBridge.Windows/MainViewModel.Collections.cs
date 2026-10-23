using System.IO;
using System.Collections.ObjectModel;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Services;

namespace WeChatBridge.Windows;

public sealed partial class MainViewModel
{
    public sealed record CollectionHistoryRow(Guid Id, string Title, string Detail, IReadOnlyList<BatchRow> Batches)
    {
        public BatchRow? Latest => Batches.LastOrDefault();
    }
    public ObservableCollection<CollectionHistoryRow> CollectionRows { get; } = [];
    private void RefreshCollectionRows()
    {
        CollectionRows.Clear();
        foreach (var c in Collections.Ledger.Collections.Where(c => c.BatchIDs.Count > 0)
                     .OrderByDescending(c => c.Status == CollectionStatus.Collecting).ThenByDescending(c => c.CreatedAt))
        {
            var title = c.Name.Length > 0 ? c.Name : L10n.Format($"收集 · {c.CreatedAt.LocalDateTime:MM-dd HH:mm}");
            var status = L10n.Text(c.Status switch { CollectionStatus.Collecting => "收集中", CollectionStatus.Draft => "待发送", CollectionStatus.Delivering => "正在交付", CollectionStatus.Delivered => "已交付", _ => "可重试" });
            var members = _batches.Where(b => c.BatchIDs.Contains(b.Id)).ToList();
            var detail = L10n.Format($"{c.BatchIDs.Count} 批 · {ByteText.Format(members.Sum(b => b.ByteCount))} · {status} · {c.TargetName} {c.SceneName} {c.Detail}");
            if (_query.Length == 0 || (title + detail).Contains(_query, StringComparison.OrdinalIgnoreCase)
                || _batches.Where(b => c.BatchIDs.Contains(b.Id)).Any(b => HistoryLabels.Matches(b, _query)))
                CollectionRows.Add(new(c.Id, title, detail, c.BatchIDs
                    .Select(id => members.FirstOrDefault(b => b.Id == id)).OfType<ReadyBatch>()
                    .Select(b => new BatchRow { Batch = b }).ToList()));
        }
        OnPropertyChanged(nameof(CollectionRows));
    }
    private CollectionService? _collections;
    internal CollectionService Collections => _collections ??= new CollectionService(_paths, _reader);
    private CollectionWindow? _collectionWindow;
    public event Action? CollectionsChanged;
    internal CollectionImportProgress? ImportProgress { get; private set; }
    internal void RefreshImportProgress()
    {
        var state = CollectionImportProgress.Read(_paths);
        if (state is not null && !state.IsFresh(DateTimeOffset.UtcNow)) state = null;
        if (state == ImportProgress) return;
        ImportProgress = state;
        CollectionsChanged?.Invoke();
        if (state?.Phase is "saving" or "failed")
            ShowCollection(Collections.Ledger.Current?.Id ?? Guid.Empty, activate: false);
    }

    public event Action<string, string, Action>? DeliveryNotificationRequested;

    public string? DeliveryFolderPath
    {
        get => _settings.DeliveryFolderPath;
        set { _settings.DeliveryFolderPath = value; SaveSettings(); OnPropertyChanged(); RebuildEntries(); }
    }
    public string DeliverySubfolder
    {
        get => _settings.DeliverySubfolder;
        set { _settings.DeliverySubfolder = value; SaveSettings(); OnPropertyChanged(); }
    }
    public int LanguageIndex
    {
        get => _settings.Language switch { "zh-Hans" => 1, "en" => 2, _ => 0 };
        set
        {
            _settings.Language = value switch { 1 => "zh-Hans", 2 => "en", _ => "system" };
            SaveSettings(); OnPropertyChanged();
            ShowToast(L10n.Text("语言切换将在重新启动后生效。"));
        }
    }
    public bool OpenObsidianAfterDelivery
    {
        get => _settings.OpenObsidianAfterDelivery;
        set { _settings.OpenObsidianAfterDelivery = value; SaveSettings(); OnPropertyChanged(); }
    }

    // Both manual retries and incoming shares must share the same clipboard queue.
    public Task PerformForward(ReadyBatch batch, ShareAction action, ForwardTarget? target,
        bool freshShare = false, SceneService.ShareContextPrefetch? prefetch = null)
    {
        var predecessor = _pendingForward;
        return _pendingForward = Run();
        async Task Run()
        {
            try { await predecessor; } catch { }
            try { await PerformForwardCore(batch, action, target, freshShare, prefetch); }
            catch (Exception error) { Record(BatchOutcomeKind.Failed, batch.Id, error.Message); ShowToast(error.Message, warning: true); Reload(); }
        }
    }

    public void ShowCollection(Guid? id = null, bool activate = true)
    {
        try
        {
            id ??= Collections.Ledger.Current?.Id ?? Collections.Ledger.Collections.LastOrDefault()?.Id;
            if (id is null) { ShowToast(L10n.Text("从分享入口选择「分批收集到微信流」开始收集。")); return; }
            if (_collectionWindow is { } existing)
            {
                existing.SelectCollection(id.Value);
                existing.Show();
                if (activate) existing.Activate();
                return;
            }
            _collectionWindow = new CollectionWindow(this, id.Value);
            _collectionWindow.Closed += (_, _) => _collectionWindow = null;
            _collectionWindow.Show();
            if (activate) _collectionWindow.Activate();
        }
        catch (Exception error) { ShowToast(error.Message, warning: true); }
    }

    public bool RenameCollection(Guid id, string name)
    {
        try
        {
            Collections.Ledger.Rename(id, name);
            Collections.Save();
            Reload();
            return true;
        }
        catch (Exception error) { ShowToast(error.Message, warning: true); return false; }
    }

    public void ShowCollectionDelivery(Guid id)
    {
        ShowCollection(id);
        _collectionWindow?.BeginDelivery();
    }

    internal string? LastCollectionTarget => _settings.LastCollectionTarget;
    internal string? LastCollectionScene => _settings.LastCollectionScene;
    internal IReadOnlyList<string> RecentCollectionFolders => _settings.RecentCollectionFolders;
    internal void RememberCollectionFolder(string folder)
    {
        _settings.RecentCollectionFolders.RemoveAll(p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase));
        _settings.RecentCollectionFolders.Insert(0, folder);
        _settings.RecentCollectionFolders = _settings.RecentCollectionFolders.Take(8).ToList();
        SaveSettings();
    }
    internal Task<WeChatBridge.Windows.Core.Delivery.ResolvedTarget?>? ResolveCollectionTarget(ReadyBatch batch, ShareAction action, ForwardTarget? target)
        => Delivery.BeginResolve(batch with { Action = action }, target);

    internal void RememberCollectionChoice(ShareAction action, ForwardTarget? target, WeChatScene? scene)
    {
        _settings.LastCollectionTarget = target?.BundleIdentifier ?? action.RawValue();
        _settings.LastCollectionScene = scene?.Id;
        SaveSettings();
    }

    public Task DeliverCollection(Guid id, ShareAction action, ForwardTarget? target,
        WeChatScene? selectedScene = null, string? folderPath = null)
    {
        if (action is ShareAction.Collect or ShareAction.Hub) throw new InvalidOperationException(L10n.Text("请选择交付目标。"));
        if (!_settings.IsEntryEnabled(action)) throw new InvalidOperationException(L10n.Text("该交付入口已停用。"));
        if (action is ShareAction.Clipboard or ShareAction.Folder or ShareAction.Obsidian) selectedScene = null;
        if (selectedScene is { } selected && (!selected.Enabled ||
            (AgentIds.Matching(action) ?? AgentIds.MatchingBundleId(target?.BundleIdentifier)) is { } validationAgent && !selected.CompatibleAgents.Contains(validationAgent)))
            throw new InvalidOperationException(L10n.Text("该场景不适用于所选目标。"));
        // Conversation names are optional labels; original files can be delivered intact without them.
        var batches = Collections.Freeze(id, requireChatNames: false);
        var requestedAt = DateTimeOffset.UtcNow;
        CollectionsChanged?.Invoke();
        var previous = _pendingForward;
        return _pendingForward = Run();
        async Task Run()
        {
            var succeeded = false;
            var detail = L10n.Text("交付未完成，原始文件已保留。");
            try
            {
                try { await previous; } catch { }
                if (action is not (ShareAction.Clipboard or ShareAction.Folder or ShareAction.Obsidian)
                    && DateTimeOffset.UtcNow - requestedAt > BatchIntent.FreshnessWindow)
                    throw new InvalidOperationException(L10n.Text("交付等待过久，请重新确认目标后重试。"));
                // Confirmation already chose the scene. Never invoke the single-share picker here.
                var aggregate = batches[0] with
                {
                    Action = action,
                    Items = batches.SelectMany(b => b.Items).ToList(),
                    ChatName = batches.Select(b => b.ChatName).Distinct().Count() == 1 ? batches[0].ChatName : null,
                };
                if (action == ShareAction.Folder)
                {
                    var root = folderPath ?? _settings.DeliveryFolderPath;
                    if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException(L10n.Text("请选择保存原始文件的文件夹。"));
                    var saved = await Task.Run(() => FolderDelivery.Save(aggregate.Items.Select(i => i.FullPath).ToList(), root));
                    succeeded = true;
                    detail = L10n.Format($"已保存 {saved.Count} 个原始文件到文件夹。");
                    DeliveryNotificationRequested?.Invoke(detail, L10n.Text("查看文件"), () => RevealFile(saved[0]));
                }
                else if (action == ShareAction.Obsidian)
                {
                    var root = _settings.ObsidianVaultPath;
                    if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException(L10n.Text("请先在入口页设置归档文件夹。"));
                    var notes = new List<string>();
                    foreach (var batch in batches)
                        notes.AddRange(await Task.Run(() => KnowledgeDelivery.Deliver(batch.Items.Select(i => i.FullPath).ToList(), root,
                            _settings.ObsidianSubfolder, batch.ChatName, null)));
                    succeeded = true;
                    detail = L10n.Format($"已保存 {notes.Distinct().Count()} 篇笔记，原始 ZIP 已保留。");
                    if (action == ShareAction.Obsidian && _settings.OpenObsidianAfterDelivery) OpenInObsidian(root, notes);
                    DeliveryNotificationRequested?.Invoke(detail, action == ShareAction.Obsidian ? L10n.Text("打开笔记") : L10n.Text("查看文件"),
                        () => { if (action == ShareAction.Obsidian) OpenInObsidian(root, notes); else RevealFile(notes[0]); });
                }
                else if (action == ShareAction.Clipboard)
                {
                    succeeded = CopyItems(aggregate.Items, false);
                    detail = succeeded ? L10n.Format($"已复制 {aggregate.Items.Count} 个原始文件。") : L10n.Text("复制失败，原始文件已保留。");
                }
                else
                {
                    var choices = await Task.WhenAll(batches.Select(batch => Scenes.ExplicitCollectionChoiceAsync(batch, selectedScene)));
                    var choice = await Scenes.ExplicitCollectionChoiceAsync(aggregate, selectedScene);
                    var agent = AgentIds.Matching(action) ?? AgentIds.MatchingBundleId(target?.BundleIdentifier);
                    var prompt = Scenes.RenderPrompt(choice, agent,
                        selectedScene?.EffectiveSkillIDs().Count > 0 ? Skills.PromptContext(agent) : null);
                    var result = await Delivery.DeliverAsync(aggregate, target, prompt);
                    succeeded = result.Delivered;
                    detail = succeeded ? L10n.Text("已执行粘贴，请在目标确认附件。") : result.Outcome.Detail ?? detail;
                    if (succeeded) foreach (var memberChoice in choices) Scenes.CompleteForward(memberChoice);
                }
            }
            catch (Exception error)
            {
                detail = error.Message;
                ShowToast(L10n.Format($"{detail}；原始文件已保留，可重试。"), warning: true);
            }
            finally
            {
                foreach (var batch in batches)
                {
                    _reader.RecordAction(batch.Id, action);
                    _reader.ClearSceneContext(batch.Id);
                    if (selectedScene is not null) _reader.RecordContext(batch.Id, sceneId: selectedScene.Id, sceneName: selectedScene.Name);
                    Record(succeeded ? (action == ShareAction.Clipboard ? BatchOutcomeKind.Copied : BatchOutcomeKind.Delivered)
                        : BatchOutcomeKind.Failed, batch.Id, detail, target?.DisplayName ?? action.TargetDisplayName());
                }
                Collections.Finish(id, succeeded, target?.DisplayName ?? action.TargetDisplayName(), detail, selectedScene?.Name);
                Reload();
            }
        }
    }
}
