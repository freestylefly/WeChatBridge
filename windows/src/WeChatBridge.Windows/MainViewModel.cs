using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Delivery;
using WeChatBridge.Windows.Services;
using WpfClipboard = System.Windows.Clipboard;

namespace WeChatBridge.Windows;

/// <summary>Which pane the window shows. A subset of the macOS SettingsTab for now.</summary>
public enum AppTab
{
    History,
    General,
    Entries,
    Scenes,
    Skills,
    About,
}

/// <summary>Status pill colour, mirroring StatusPill.Tone on macOS.</summary>
public enum StatusTone
{
    /// <summary>已送达 / 已复制.</summary>
    Live,
    /// <summary>未送达.</summary>
    Warn,
    /// <summary>未执行.</summary>
    Neutral,
}

/// <summary>One row of the 记录 list — a batch as it is drawn, not as it is stored.</summary>
public sealed class BatchRow : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _readingBoundaries;
    private CollectionBatchMetadata? _metadata;
    public string Range
    {
        get
        {
            if (_metadata is null && !_readingBoundaries) _ = ReadBoundaries();
            return _metadata?.First is { } first && _metadata.Last is { } last
                ? $"{first.Date.LocalDateTime:yyyy-MM-dd HH:mm} — {last.Date.LocalDateTime:yyyy-MM-dd HH:mm}"
                : L10n.Text(_readingBoundaries ? "正在读取…" : "未能读取位置参考，原始文件已保存");
        }
    }
    public string BoundaryText { get; private set; } = "";

    private async Task ReadBoundaries()
    {
        _readingBoundaries = true;
        try
        {
            _metadata = await Task.Run(() => CollectionBatchMetadata.Read(Batch.Items.Select(i => i.FullPath)));
            var references = _metadata.FirstRecords.Select(r => (Label: L10n.Text("较早"), Record: r))
                .Concat(_metadata.LastRecords.Select(r => (Label: L10n.Text("较晚"), Record: r)));
            BoundaryText = CollectionSummary.Format([_metadata], SizeText) + "\n"
                + string.Join("\n\n", references.Select(r => $"{r.Label} · {r.Record.Date.LocalDateTime:yyyy-MM-dd HH:mm} · {r.Record.Sender}\n{r.Record.Text}"));
        }
        catch { _metadata = new(null, null, null); BoundaryText = L10n.Text("未能读取位置参考，原始文件已保存"); }
        finally
        {
            _readingBoundaries = false;
            PropertyChanged?.Invoke(this, new(nameof(Range)));
            PropertyChanged?.Invoke(this, new(nameof(BoundaryText)));
        }
    }

    public required ReadyBatch Batch { get; init; }

    /// <summary>Whether the item detail under the row is open.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            if (value && _metadata is null && !_readingBoundaries) _ = ReadBoundaries();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public string Title => HistoryLabels.PaneTitle(Batch);
    public string Subtitle => HistoryLabels.PaneSubtitle(Batch);
    public string Clock => HistoryLabels.ClockTime(Batch.CreatedAt);
    public string Destination => HistoryLabels.Destination(Batch);
    public string StatusText => StatusTextFor(Batch);
    public StatusTone Tone => StatusToneFor(Batch);

    public IReadOnlyList<ReadyItem> Items => Batch.Items;
    public string SizeText => ByteText.Format(Batch.ByteCount);

    private static string StatusTextFor(ReadyBatch batch) => batch.Outcome?.Kind switch
    {
        BatchOutcomeKind.Delivered => L10n.Format($"{HistoryLabels.DestinationName(batch)} · 已送达"),
        BatchOutcomeKind.Copied => L10n.Text("已复制"),
        BatchOutcomeKind.Failed => batch.Outcome.Detail is { Length: > 0 } detail
            ? L10n.Format($"未送达 · {detail}")
            : L10n.Text("未送达"),
        _ => batch.Action == ShareAction.Collect ? L10n.Text("收集中") : L10n.Text("未执行"),
    };

    private static StatusTone StatusToneFor(ReadyBatch batch) => batch.Outcome?.Kind switch
    {
        BatchOutcomeKind.Delivered or BatchOutcomeKind.Copied => StatusTone.Live,
        BatchOutcomeKind.Failed => StatusTone.Warn,
        _ => StatusTone.Neutral,
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One day of records under a 今天/昨天/date heading.</summary>
public sealed class HistoryGroup
{
    public required string Title { get; init; }
    public required IReadOnlyList<BatchRow> Rows { get; init; }
}

/// <summary>One entry of a 发给… menu: a built-in entry or one of the user's own apps.</summary>
public sealed class ForwardDestination
{
    public required ShareAction Action { get; init; }
    /// <summary>Null for the built-ins, whose destination the action already names.</summary>
    public ForwardTarget? Target { get; init; }
    /// <summary>The app, not the entry: inside a menu already titled 发给, 「发给 Codex」 says it twice.</summary>
    public string Title => Target?.DisplayName ?? Action.TargetDisplayName();
}

/// <summary>One row of the 入口 pane — a share-menu entry and its switch.</summary>
public sealed class EntryRow
{
    public required ShareAction Action { get; init; }
    public required bool IsEnabled { get; set; }
    public required string Detail { get; init; }
    public required bool DetailIsWarning { get; init; }
    /// <summary>Obsidian 设置… and 发送到自定义 管理… are the only rows with one.</summary>
    public string? ConfigureTitle { get; init; }
    public string Title => Action.EntryTitle();
}

/// <summary>
/// One row of the 「发送到自定义」 editor. The 未安装 pill is said out loud rather
/// than left to fail at forward time — an exe may have been uninstalled since it
/// was added, and this row is the only place that can be noticed calmly.
/// </summary>
public sealed class ForwardTargetRow
{
    public required ForwardTarget Target { get; init; }

    public string DisplayName => Target.DisplayName;
    public string Identifier => Target.BundleIdentifier;
    public bool PastesPathOnly => Target.PastesPathOnly;

    /// <summary>
    /// Exe paths can be checked on disk; an AUMID cannot be resolved without the
    /// packaged-app APIs, so it is trusted rather than flagged.
    /// </summary>
    public bool IsMissing
    {
        get
        {
            var id = Identifier;
            var looksLikePath = Path.IsPathRooted(id)
                || id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            return looksLikePath && !File.Exists(id);
        }
    }
}

/// <summary>
/// Everything the window reads and asks for. Ported from AppModel.swift: the
/// model owns no copy of the truth — <see cref="Reload"/> re-reads Ready and
/// rebuilds both lists, so a lost notification, a crash mid-import or a user
/// deleting a batch in Explorer all recover to the same state.
/// </summary>
public sealed partial class MainViewModel : INotifyPropertyChanged
{
    private readonly InboxPaths _paths;
    private readonly InboxReader _reader;
    private readonly AppSettingsStore _settingsStore;
    private readonly ForwardTargetStore _targetStore;

    private AppSettings _settings;
    private List<ReadyBatch> _batches = [];
    private DeliveryEngine? _delivery;
    private readonly Action<PastePayload>? _copyPayload;
    private SceneService? _scenes;
    private CustomTargetService? _customTargets;
    private SkillService? _skills;
    /// <summary>
    /// macOS chains forwards through <c>ActionRunner.pending</c>: two
    /// unserialised forwards interleave — the second's clipboard write lands
    /// while the first still waits to paste, and the first delivers the
    /// second's files. One intent runs at a time.
    /// </summary>
    private Task _pendingForward = Task.CompletedTask;
    /// <summary>
    /// The reads a helper-published <see cref="PrefetchHint"/> already started —
    /// the scene work that used to begin only after the user's entry pick.
    /// Consumed once by the matching batch; a hint for a cancelled or different
    /// share is discarded rather than applied across batches.
    /// </summary>
    private SceneService.ShareContextPrefetch? _hintedPrefetch;
    private Guid? _hintedBatchId;
    private string _query = string.Empty;
    private string? _inboxFailure;

    public MainViewModel(InboxPaths paths, string? configDirectory = null, bool manageLogin = true,
        DeliveryEngine? delivery = null, SceneService? scenes = null, Action<PastePayload>? copyPayload = null,
        Func<WindowsForwardTarget, bool>? isAppInstalled = null)
    {
        _paths = paths;
        _reader = new InboxReader(paths);
        _settingsStore = new AppSettingsStore(configDirectory);
        _targetStore = new ForwardTargetStore(configDirectory);
        _delivery = delivery;
        _copyPayload = copyPayload;
        _scenes = scenes;
        _settings = _settingsStore.Load();
        _isAppInstalled = isAppInstalled;
        // Residency is the whole point of the background instance: reconcile
        // the stored preference with the Run key at every start so a fresh
        // install self-registers and a user deletion stays deleted.
        if (manageLogin && _settings.LaunchAtLogin != WeChatBridge.Windows.LaunchAtLogin.IsRegistered())
            WeChatBridge.Windows.LaunchAtLogin.Apply(_settings.LaunchAtLogin);
    }

    /// <summary>A status capsule inside the window: message, optional button, its action.</summary>
    public event Action<string, string?, Action?, bool>? ToastRequested;

    /// <summary>A pane asking the window to switch tabs (添加应用… → 入口).</summary>
    public event Action<AppTab>? NavigationRequested;

    public ObservableCollection<HistoryGroup> Groups { get; } = [];
    public ObservableCollection<ForwardTargetRow> TargetRows { get; } = [];
    public ObservableCollection<EntryRow> Entries { get; } = [];

    public IReadOnlyList<ReadyBatch> Batches => _batches;
    public string? InboxFailure { get => _inboxFailure; private set => Set(ref _inboxFailure, value); }
    public string InboxRoot => _paths.Root;

    /// <summary>Created lazily so constructing the view model never touches Win32.</summary>
    /// <summary>
    /// The scene pipeline, lazily built so nothing registers hotkeys or reads
    /// stores for a process that never forwards. The picker callback always
    /// lands on the UI dispatcher — ScenePickerWindow creates a Window.
    /// </summary>
    internal SceneService Scenes => _scenes ??= new SceneService(
        // UIA reads the live chat name out of mmui::MainWindow; the window
        // title only ever says 「微信」 and stays as the fallback.
        titleReader: _ => Services.WeChatUiTitleReader.ReadWithFallbackAsync(),
        picker: (scenes, ct) => System.Windows.Application.Current?.Dispatcher
                .InvokeAsync(() => ScenePickerWindow.ChooseAsync(
                    scenes, ct, System.Windows.Application.Current.MainWindow))
                .Task.Unwrap()
            ?? Task.FromResult(ScenePickerAnswer.Cancelled),
        hotkeys: new WindowsSceneHotkeySource(),
        notify: text => ToastRequested?.Invoke(text, null, null, false));

    internal CustomTargetService CustomTargets =>
        _customTargets ??= CustomTargetService.CreateWindowed(_targetStore);

    /// <summary>
    /// The shared skill catalog/status probe — the pane owns one too, but a
    /// second instance is just a read view, which is all the forward path
    /// needs for the missing-skills nudge.
    /// </summary>
    internal SkillService Skills => _skills ??= new SkillService(
        scenes: () => Scenes.LoadSettings().EnabledScenes);

    /// <summary>
    /// Releases whatever was lazily built — App.OnExit is the only caller.
    /// Touching the lazy accessors here must not create them.
    /// </summary>
    internal void DisposeServices() => _scenes?.Dispose();

    private DeliveryEngine Delivery => _delivery ??= new DeliveryEngine(
        DeliveryEnvironment.Create(message => InboxLogger.Write(_paths, message)),
        _reader);

    /// <summary>The retention choice indexes <see cref="AppSettings.RetentionChoices"/>.</summary>
    public int RetentionIndex
    {
        get
        {
            var index = RetentionIndexOf(_settings.HistoryRetentionDays);
            return index < 0 ? RetentionIndexOf(AppSettings.DefaultHistoryRetentionDays) : index;
        }
        set
        {
            if (value < 0 || value >= AppSettings.RetentionChoices.Count)
                return;
            var days = AppSettings.RetentionChoices[value];
            if (days == _settings.HistoryRetentionDays)
                return;
            _settings.HistoryRetentionDays = days;
            SaveSettings();
            OnPropertyChanged(nameof(RetentionIndex));
            Reload();
        }
    }

    /// <summary>开机自动启动 — the toggle reads the Run key itself so a registry edit elsewhere is shown, not overwritten.</summary>
    public bool AutoStartEnabled
    {
        get => WeChatBridge.Windows.LaunchAtLogin.IsRegistered();
        set
        {
            WeChatBridge.Windows.LaunchAtLogin.Apply(value);
            _settings.LaunchAtLogin = WeChatBridge.Windows.LaunchAtLogin.IsRegistered();
            SaveSettings();
            OnPropertyChanged(nameof(AutoStartEnabled));
        }
    }

    public string? ObsidianVaultPath
    {
        get => _settings.ObsidianVaultPath;
        set
        {
            if (value == _settings.ObsidianVaultPath)
                return;
            _settings.ObsidianVaultPath = value;
            SaveSettings();
            OnPropertyChanged(nameof(ObsidianVaultPath));
            RebuildEntries();
        }
    }

    public string ObsidianSubfolder
    {
        get => _settings.ObsidianSubfolder;
        set
        {
            if (value == _settings.ObsidianSubfolder)
                return;
            _settings.ObsidianSubfolder = value;
            SaveSettings();
            OnPropertyChanged(nameof(ObsidianSubfolder));
        }
    }

    public string Query
    {
        get => _query;
        set
        {
            if (Set(ref _query, value))
            { RebuildGroups(); RefreshCollectionRows(); }
        }
    }

    /// <summary>「共 4 条 · 占用 12 MB · 默认保留 7 天」.</summary>
    public string SummaryText =>
        L10n.Format($"共 {_batches.Count} 条 · 占用 {ByteText.Format(_batches.Sum(b => b.ByteCount))} · {RetentionText}");

    public bool HasHistory => _batches.Count > 0;

    private string RetentionText =>
        _settings.HistoryRetentionDays == 0 ? L10n.Text("永久保留") : L10n.Format($"默认保留 {_settings.HistoryRetentionDays} 天");

    private static int RetentionIndexOf(int days)
    {
        for (var i = 0; i < AppSettings.RetentionChoices.Count; i++)
            if (AppSettings.RetentionChoices[i] == days)
                return i;
        return -1;
    }

    // MARK: - Loading

    /// <summary>
    /// The helper's prefetch signal lands here: the batch is staged and the
    /// entry pick is still in front of the user, so starting the title read
    /// and transcript parse now hides them inside the decision time. Called
    /// on the dispatcher — it only kicks off tasks and stashes them for
    /// <see cref="EnqueueForward"/> to pick up.
    /// </summary>
    public void ConsumePrefetchHint()
    {
        var hint = PrefetchHint.Consume(_paths);
        if (hint is null)
            return;
        _hintedPrefetch = Scenes.PrefetchForShare(hint.ResolvePaths(_paths), captureTitle: true);
        _hintedBatchId = hint.BatchId;
        InboxLogger.Write(_paths, $"[trace] main.prefetch-hint batch={hint.BatchId:N}");
    }

    /// <summary>One-shot hand-off of the hinted prefetch to its batch — never across batches.</summary>
    private SceneService.ShareContextPrefetch? TakePrefetchHint(Guid batchId)
    {
        var hinted = _hintedBatchId == batchId ? _hintedPrefetch : null;
        _hintedPrefetch = null;
        _hintedBatchId = null;
        return hinted;
    }

    /// <summary>
    /// Re-reads Ready, carries out anything the app has never processed, then
    /// ages out what the retention window says is finished with.
    /// </summary>
    public void Reload()
    {
        try
        {
            _paths.EnsureCreated();
            InboxFailure = null;
            _ = Collections;
            Publish(_reader.LoadBatches());
            var uncollected = _batches.Where(b => b.Action == ShareAction.Collect
                         && !Collections.Ledger.SeenBatchIDs.Contains(b.Id)).OrderBy(b => b.CreatedAt).ToList();
            foreach (var collected in uncollected)
                Collections.Append(collected, null);
            if (uncollected.Count > 0) Publish(_batches.Select(b => b with
                { ChatName = _reader.StateFor(b.Id)?.ChatName ?? b.ChatName }).ToList());

            // IsFirstSeen comes from state.json having had to be written, so it
            // is true exactly once for a given batch — across relaunches, rescans
            // and second windows. That is what a forward needs: replaying one
            // hours later would paste into someone else's app.
            var recorded = false;
            foreach (var batch in _batches.Where(b => b.IsFirstSeen))
                recorded = Announce(batch) || recorded;
            if (recorded)
                Publish(_reader.LoadBatches());

            // Read and deleted in one go, so a message is said exactly once
            // however many times the inbox is rescanned.
            foreach (var failure in _reader.ConsumeFailures())
                ToastRequested?.Invoke(L10n.Format($"没能接住这次转发：{failure.Message}"), null, null, true);

            PruneHistory();
        }
        catch (Exception error)
        {
            InboxFailure = error.Message;
            InboxLogger.Write(_paths, L10n.Text("主程序读取 Inbox 失败"), error);
        }
    }

    /// <summary>Returns true when it wrote an outcome, so the caller reloads.</summary>
    private bool Announce(ReadyBatch batch)
    {
        // The share-target helper can settle a batch itself — a cancelled or
        // expired entry pick commits state.json alongside the files. An outcome
        // already on disk is the batch's answer; nothing here may overwrite it.
        if (batch.Outcome is not null)
            return false;
        if (batch.Action == ShareAction.Collect)
        {
            _reader.ConsumeIntent(batch.Id);
            // Collection receipt owns no clipboard work and must not wait behind a paste.
            // The helper committed the title snapshot together with these files.
            var id = Collections.Append(batch, null);
            ShowCollection(id, activate: false);
            return false;
        }
        switch (_reader.ConsumeIntent(batch.Id))
        {
            case { Kind: ConsumedIntentKind.Ready, Intent: { } intent }:
                // Queued, not parallel: delivery rewrites the clipboard, so two
                // in flight would paste each other's files. Nothing is recorded
                // here — the forward's own outcome lands at its end.
                EnqueueForward(batch, intent);
                return false;
            case { Kind: ConsumedIntentKind.Expired, Intent: { } intent }:
                // Only a paste can go stale. A copy was finished by the helper
                // itself, and BatchState.Initial already said 已复制.
                if (intent.Action == ShareAction.Clipboard)
                    return false;
                Record(BatchOutcomeKind.Expired, batch.Id);
                return true;
            default:
                if (batch.Action == ShareAction.Clipboard)
                    return false;
                // A forward whose one-shot request is already gone: consumed by
                // a run that then died, or never written. Nothing can be carried
                // out, and the history must not claim otherwise.
                Record(BatchOutcomeKind.Expired, batch.Id);
                return true;
        }
    }

    private ForwardTarget? ResolveTarget(BatchIntent intent)
    {
        if (intent.TargetBundleIdentifier is not { } id)
            return null;
        return _targetStore.Load().FirstOrDefault(t => t.BundleIdentifier == id)
            ?? new ForwardTarget(id, intent.TargetDisplayName ?? id, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Chains the forward behind whatever is still delivering — the port of
    /// <c>ActionRunner.pending</c>. Freshness is checked again here, not only
    /// where the intent came off disk: a forward can wait behind an unanswered
    /// picker or a target app that never comes forward, and a request the user
    /// has stopped thinking about must not paste into whatever they opened
    /// since. The files stay on the clipboard either way.
    /// </summary>
    private void EnqueueForward(ReadyBatch batch, BatchIntent intent)
    {
        var previous = _pendingForward;
        _pendingForward = RunAfter(previous);

        async Task RunAfter(Task predecessor)
        {
            // Title read + transcript parse run ahead of the scene decision —
            // ideally already started by the helper's prefetch hint while the
            // entry pick was still open; otherwise they start now, while a
            // previous forward may still be delivering.
            var prefetch = TakePrefetchHint(batch.Id);
            InboxLogger.Write(_paths,
                $"[trace] main.enqueue batch={batch.Id:N} prefetch={(prefetch is not null ? "hint" : "fresh")}");
            prefetch ??= Scenes.PrefetchForShare(
                batch.Items.Select(i => i.FullPath).ToList(), captureTitle: true);
            try
            {
                await predecessor;
            }
            catch
            {
                // A failed forward must not stall the queue behind it.
            }
            if (intent.Action != ShareAction.Collect && !intent.IsFresh())
            {
                Record(BatchOutcomeKind.Expired, batch.Id);
                Reload();
                return;
            }
            try
            {
                await PerformForwardCore(batch, intent.Action, ResolveTarget(intent),
                    freshShare: true, prefetch: prefetch);
            }
            catch (Exception error)
            {
                // A forward that faults must not vanish silently: the batch
                // needs a recorded outcome and the queue needs to move on.
                InboxLogger.Write(_paths, L10n.Text("转发执行异常"), error);
                Record(BatchOutcomeKind.Failed, batch.Id, error.Message);
                Reload();
            }
        }
    }

    /// <summary>Ages out finished history. A non-positive window keeps forever.</summary>
    private void PruneHistory()
    {
        if (_settings.HistoryRetentionDays <= 0)
            return;
        var window = TimeSpan.FromDays(_settings.HistoryRetentionDays);
        if (_reader.PruneHistory(window, protectedBatchIDs: Collections.Ledger.ProtectedBatchIDs) > 0)
            Publish(_reader.LoadBatches());
    }

    private void Publish(List<ReadyBatch> loaded)
    {
        _batches = loaded;
        RebuildGroups();
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HasHistory));
        RefreshCollectionRows();
        CollectionsChanged?.Invoke();
    }

    private void RebuildGroups()
    {
        var collected = Collections.Ledger.Collections.SelectMany(c => c.BatchIDs).ToHashSet();
        var filtered = _batches.Where(b => !collected.Contains(b.Id) && HistoryLabels.Matches(b, _query)).ToList();
        var groups = filtered
            .GroupBy(b => b.CreatedAt.LocalDateTime.Date)
            .OrderByDescending(g => g.Key)
            .Select(g => new HistoryGroup
            {
                Title = HistoryLabels.SectionTitle(g.Key),
                Rows = g.Select(b => new BatchRow { Batch = b }).ToList(),
            })
            .ToList();
        Groups.Clear();
        foreach (var group in groups)
            Groups.Add(group);
        OnPropertyChanged(nameof(Groups));
    }

    // MARK: - Actions on a batch

    /// <summary>
    /// 重新发送: what macOS routes through ActionRunner — the same forward the
    /// original share would have run, driven again through the delivery engine.
    /// </summary>
    public async Task Resend(ReadyBatch batch)
    {
        var target = batch.Action == ShareAction.Custom
            ? _targetStore.Load().FirstOrDefault(t => t.BundleIdentifier == batch.TargetName || t.DisplayName == batch.TargetName)
            : null;
        await PerformForward(batch, batch.Action, target);
    }

    /// <summary>复制到剪贴板 from the row menu — the same gesture the helper made.</summary>
    public void CopyBatchToClipboard(ReadyBatch batch)
    {
        if (!CopyItems(batch.Items, pathsOnly: false))
            return;
        Record(BatchOutcomeKind.Copied, batch.Id);
        ToastRequested?.Invoke(L10n.Text("已复制到剪贴板"), null, null, false);
        Reload();
    }

    /// <summary>
    /// One path for every destination, matching ActionRunner.forward's seam.
    /// Clipboard copies are still done here; real forwards go through
    /// <see cref="DeliveryEngine"/>, which writes the manual payload first so
    /// every failure leaves the user one Ctrl+V away, and records its own
    /// outcome into state.json. <paramref name="freshShare"/> is set only for
    /// the intent-driven path — the one moment reading the WeChat window title
    /// is honest — while resends and 「发给…」 reuse what the batch recorded.
    /// </summary>
    private async Task PerformForwardCore(ReadyBatch batch, ShareAction action, ForwardTarget? target,
        bool freshShare = false, SceneService.ShareContextPrefetch? prefetch = null)
    {
        // 「微信流」 intent: the share menu names no destination, so the app asks
        // once the batch is durable. The pick maps onto the same action/target
        // pair every other path already uses, which keeps outcome records and
        // the scene pipeline identical.
        if (action == ShareAction.Hub)
        {
            var pick = await PickShareEntry(batch);
            switch (pick.Kind)
            {
                case EntryPickerAnswerKind.Picked:
                    action = pick.Action!.Value;
                    target = pick.Target;
                    break;
                case EntryPickerAnswerKind.Expired:
                    Record(BatchOutcomeKind.Expired, batch.Id);
                    Reload();
                    return;
                default:
                    Record(BatchOutcomeKind.Failed, batch.Id, L10n.Text("已取消选择入口。"));
                    Reload();
                    return;
            }
        }

        // The selected destination must reach target resolution and state, not the old Hub action.
        batch = batch with { Action = action };
        _reader.RecordAction(batch.Id, action);
        if (action == ShareAction.Collect)
        {
            if (!_settings.IsEntryEnabled(action))
            {
                CopyItems(batch.Items, false);
                Record(BatchOutcomeKind.Failed, batch.Id, L10n.Text("入口已停用，文件已在剪贴板"));
                Reload();
                return;
            }
            var context = await Scenes.ResolveForShareAsync(batch.Items.Select(i => i.FullPath).ToList(),
                groupName: batch.ChatName, captureTitle: false, prefetch: prefetch, resolveScenes: false);
            var id = Collections.Append(batch, context?.GroupName);
            Reload();
            ShowCollection(id, activate: false);
            return;
        }

        var pathsOnly = action == ShareAction.Custom
            && target is not null
            && (target.PastesPathOnly
                || _targetStore.Load().FirstOrDefault(t => t.BundleIdentifier == target.BundleIdentifier)?.PastesPathOnly == true);

        if (!_settings.IsEntryEnabled(action))
        {
            CopyItems(batch.Items, pathsOnly);
            Record(BatchOutcomeKind.Failed, batch.Id, L10n.Text("入口已停用，文件已在剪贴板"), target?.DisplayName);
            ToastRequested?.Invoke(L10n.Format($"{action.EntryTitle()} 已在入口设置中停用；文件已复制到剪贴板"), null, null, true);
            Reload();
            return;
        }

        if (action == ShareAction.Clipboard)
        {
            if (CopyItems(batch.Items, pathsOnly: false))
                Record(BatchOutcomeKind.Copied, batch.Id);
            Reload();
            return;
        }

        // 「发送到自定义」 without a bundle id — the share helper cannot carry
        // one — resolves through the picker: zero targets prompts toward the
        // 入口 page, one forwards straight away, many pop the floating panel.
        if (target is null && action == ShareAction.Custom)
        {
            var resolution = await CustomTargets.ResolveAsync();
            switch (resolution.Kind)
            {
                case CustomTargetResolutionKind.Picked when resolution.Target is { } picked:
                    target = picked;
                    break;
                case CustomTargetResolutionKind.NoTargets:
                    Record(BatchOutcomeKind.Failed, batch.Id, L10n.Text("还没有添加自定义应用。"));
                    ToastRequested?.Invoke(L10n.Text("还没有添加自定义应用。"), L10n.Text("去入口页添加"), () => Navigate(AppTab.Entries), true);
                    Reload();
                    return;
                case CustomTargetResolutionKind.Expired:
                    Record(BatchOutcomeKind.Expired, batch.Id);
                    Reload();
                    return;
                default:
                    Record(BatchOutcomeKind.Failed, batch.Id, L10n.Text("已取消选择目标。"));
                    Reload();
                    return;
            }
        }

        // Target resolution — process scan + PATH probing — overlaps the scene
        // decision rather than starting after it. Null when the action has no
        // deliverable target; DeliverAsync then resolves (and fails) itself.
        var preresolved = Delivery.BeginResolve(batch, target);

        // Obsidian takes notes, not scenes — its decision is only the title
        // read that names the group, so the scene picker never pops for it.
        var resolveScenes = action is not (ShareAction.Obsidian or ShareAction.Folder);

        // The scene decision — pending shortcut → group binding → picker →
        // optional default. Null means the picker let the share expire; macOS
        // records that as expired rather than forwarding it bare.
        var choice = await Scenes.ResolveForShareAsync(
            batch.Items.Select(i => i.FullPath).ToList(),
            groupName: batch.ChatName,
            captureTitle: freshShare && batch.ChatName is null,
            prefetch: prefetch,
            resolveScenes: resolveScenes);
        if (choice is null)
        {
            Record(BatchOutcomeKind.Expired, batch.Id);
            Reload();
            return;
        }
        InboxLogger.Write(_paths, $"[trace] main.scene-resolved batch={batch.Id:N}");
        if (choice.GroupName is not null || choice.Scene is not null)
            _reader.RecordContext(batch.Id, choice.GroupName, choice.Scene?.Id, choice.Scene?.Name);

        // Obsidian writes the note with the group name from the title read —
        // the front matter gets no scene, since Obsidian never picks one.
        if (action is ShareAction.Obsidian or ShareAction.Folder)
        {
            var vault = action == ShareAction.Obsidian ? _settings.ObsidianVaultPath : _settings.DeliveryFolderPath;
            var destinationName = action.TargetDisplayName();
            _reader.ClearSceneContext(batch.Id);
            if (string.IsNullOrEmpty(vault))
            {
                Record(BatchOutcomeKind.Failed, batch.Id, L10n.Text("尚未选择归档文件夹"), destinationName);
                ToastRequested?.Invoke(L10n.Format($"还没有设置{destinationName}路径"), L10n.Text("去入口页设置"), () => Navigate(AppTab.Entries), true);
            }
            else
            {
                try
                {
                    var paths = batch.Items.Select(i => i.FullPath).ToList();
                    var written = KnowledgeDelivery.Deliver(
                        paths, vault, action == ShareAction.Obsidian ? _settings.ObsidianSubfolder : _settings.DeliverySubfolder,
                        choice.GroupName, null);
                    if (action == ShareAction.Obsidian && _settings.OpenObsidianAfterDelivery)
                        OpenInObsidian(vault, written);
                    // No scene or insights to advance; the call still stamps
                    // the group's memory entry (name + last-seen), the same
                    // bookkeeping every other destination performs.
                    Scenes.CompleteForward(choice);
                    Record(BatchOutcomeKind.Delivered, batch.Id, null, destinationName);
                    DeliveryNotificationRequested?.Invoke(L10n.Format($"已沉淀到{destinationName}（{written.Distinct().Count()} 篇笔记）"),
                        action == ShareAction.Obsidian ? L10n.Text("打开笔记") : L10n.Text("查看文件"),
                        () => { if (action == ShareAction.Obsidian) OpenInObsidian(vault, written); else RevealFile(written[0]); });
                }
                catch (KnowledgeDelivery.FailureException error)
                {
                    CopyItems(batch.Items, pathsOnly: false);
                    Record(BatchOutcomeKind.Failed, batch.Id, error.Message, destinationName);
                    ToastRequested?.Invoke(L10n.Format($"{error.Message} 文件已复制到剪贴板"), null, null, true);
                }
            }
            Reload();
            return;
        }

        var agent = AgentIds.Matching(action)
            ?? AgentIds.MatchingBundleId(target?.BundleIdentifier);
        var prompt = Scenes.RenderPrompt(choice, agent,
            choice.Scene?.EffectiveSkillIDs().Count > 0 ? Skills.PromptContext(agent) : null);

        var name = target?.DisplayName ?? action.TargetDisplayName();
        try
        {
            var result = await Delivery.DeliverAsync(batch, target, prompt, preresolved: preresolved);
            // advance only on a real landing — the scene watermark must not
            // swallow a batch that never reached its app.
            if (result.Delivered)
            {
                Scenes.CompleteForward(choice);
                // macOS removed the success capsule — the user is looking at
                // the target app with their files already in it. What it kept
                // is the missing-skills nudge: a delivered batch whose scene
                // names skills this agent does not have yet gets pointed at 技能.
                var missing = MissingSkills(choice.Scene, agent);
                if (missing.Count > 0)
                {
                    var names = string.Join("、", missing);
                    ToastRequested?.Invoke(
                        L10n.Format($"场景「{choice.Scene!.Name}」引用的技能不可用：{names}"),
                        L10n.Text("查看技能"), () => Navigate(AppTab.Skills), true);
                }
            }
            else
            {
                ToastRequested?.Invoke(
                    L10n.Format($"{result.Outcome.Detail ?? $"没能发给 {name}"}；文件已在剪贴板"),
                    null, null, true);
            }
        }
        catch (Exception error)
        {
            CopyItems(batch.Items, pathsOnly);
            Record(BatchOutcomeKind.Failed, batch.Id, error.Message, target?.DisplayName);
            ToastRequested?.Invoke(L10n.Format($"转发失败：{error.Message}；文件已在剪贴板"), null, null, true);
            InboxLogger.Write(_paths, L10n.Text("投递引擎异常"), error);
        }
        Reload();
    }

    /// <summary>
    /// Brings up each note just written — Obsidian registers the obsidian://
    /// handler, and a vault is named after its folder. Best-effort only: a
    /// machine without the handler (or a renamed vault folder) still keeps
    /// the delivered note, so every failure is swallowed.
    /// </summary>
    private static void OpenInObsidian(string vaultPath, IReadOnlyList<string> notes)
    {
        var vaultName = Path.GetFileName(
            vaultPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(vaultName))
            return;
        foreach (var note in notes)
        {
            try
            {
                var relative = Path.GetRelativePath(vaultPath, note).Replace('\\', '/');
                var file = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
                Process.Start(new ProcessStartInfo(
                    $"obsidian://open?vault={Uri.EscapeDataString(vaultName)}&file={file}")
                {
                    UseShellExecute = true,
                });
            }
            catch
            {
                // Opening is cosmetic — the note is already in the vault.
            }
        }
    }

    /// <summary>
    /// The macOS <c>missingSkills</c> port: display names of the skills a
    /// delivered scene references that this agent can use neither natively nor
    /// through the library's SKILL.md. Touches <see cref="Skills"/> lazily — a
    /// share with no scene never builds the catalog.
    /// </summary>
    private List<string> MissingSkills(WeChatScene? scene, AgentId? agent)
    {
        if (scene is null || agent is null || scene.EffectiveSkillIDs().Count == 0)
            return [];
        return scene.EffectiveSkillIDs()
            .Select(id => Skills.Resolve(id, agent))
            .Where(skill => skill.Mode == SkillRenderMode.Missing)
            .Select(skill => skill.DisplayName)
            .ToList();
    }

    /// <summary>
    /// The 「微信流」 answer: build the enabled-entry list and either auto-pick
    /// (zero or one option) or ask through <see cref="EntryPickerWindow"/>.
    /// The 0/1 fast paths mirror <c>CustomForwardDecision</c>: a list with
    /// one row is not a question.
    /// </summary>
    private async Task<EntryPickerAnswer> PickShareEntry(ReadyBatch? batch = null)
    {
        var options = ShareEntryCatalog.BuildOptions(_settings, _targetStore.Load());

        switch (options.Count)
        {
            case 0:
                ToastRequested?.Invoke(L10n.Text("还没有开启任何入口。"), L10n.Text("去入口页开启"), () => Navigate(AppTab.Entries), true);
                return EntryPickerAnswer.Cancelled;
            case 1:
            {
                var only = options[0];
                return EntryPickerAnswer.Picked(only.Action, only.Target);
            }
            default:
                return await EntryPickerWindow.ChooseAsync(
                    options, contextLine: batch is null ? null : BatchContextLine(batch));
        }
    }

    /// <summary>The picker's payload line: what arrived, in one glance.</summary>
    private static string BatchContextLine(ReadyBatch batch) => batch.Items.Count switch
    {
        1 => $"{batch.Items[0].DisplayName} · {ByteText.Format(batch.ByteCount)}",
        var count => L10n.Format($"{count} 个文件 · {ByteText.Format(batch.ByteCount)}"),
    };

    /// <summary>发给… menu contents: built-ins first, then the user's own order.</summary>
    public IReadOnlyList<ForwardDestination> Destinations()
    {
        var builtIns = new[]
        {
            ShareAction.Codex, ShareAction.Claude, ShareAction.Doubao, ShareAction.Qwen,
            ShareAction.WorkBuddy, ShareAction.WeSight, ShareAction.DeepSeekHarness, ShareAction.Obsidian, ShareAction.Folder,
        }.Select(a => new ForwardDestination { Action = a });
        return builtIns
            .Concat(TargetRows.Select(t => new ForwardDestination { Action = ShareAction.Custom, Target = t.Target }))
            .ToList();
    }

    /// <summary>The clipboard write both share and resend go through. UI thread is STA.</summary>
    private bool CopyItems(IReadOnlyList<ReadyItem> items, bool pathsOnly)
    {
        try
        {
            var existing = items.Where(i => File.Exists(i.FullPath)).ToList();
            if (existing.Count == 0)
            {
                ToastRequested?.Invoke(L10n.Text("批次文件已不在磁盘上"), null, null, true);
                return false;
            }
            if (_copyPayload is not null)
            {
                _copyPayload(pathsOnly ? new PastePayload.Text(WindowsClipboard.ShellLine(existing.Select(i => i.FullPath).ToList()))
                    : new PastePayload.Files(existing.Select(i => i.FullPath).ToList()));
                return true;
            }
            if (pathsOnly)
            {
                WpfClipboard.SetText(string.Join(' ', existing.Select(i => $"\"{i.FullPath}\"")));
            }
            else
            {
                var files = new StringCollection();
                foreach (var item in existing)
                    files.Add(item.FullPath);
                WpfClipboard.SetFileDropList(files);
            }
            return true;
        }
        catch (Exception error)
        {
            InboxLogger.Write(_paths, L10n.Text("写入剪贴板失败"), error);
            ToastRequested?.Invoke(L10n.Format($"写入剪贴板失败：{error.Message}"), null, null, true);
            return false;
        }
    }

    private void Record(BatchOutcomeKind kind, Guid batchId, string? detail = null, string? targetName = null)
    {
        try
        {
            _reader.RecordOutcome(
                new BatchOutcome(kind, detail, DateTimeOffset.UtcNow),
                batchId,
                targetName);
        }
        catch (Exception error)
        {
            InboxLogger.Write(_paths, L10n.Text("写入批次状态失败"), error);
        }
    }

    // MARK: - Explorer / removal

    /// <summary>在文件夹中显示 — selects the batch's first file in Explorer.</summary>
    public void Reveal(ReadyBatch batch)
    {
        var first = batch.Items.FirstOrDefault(i => File.Exists(i.FullPath));
        if (first is null)
        {
            ToastRequested?.Invoke(L10n.Text("批次文件已不在磁盘上"), null, null, true);
            return;
        }
        RevealFile(first.FullPath);
    }

    public void RevealItem(ReadyItem item) => RevealFile(item.FullPath);

    public void RevealInbox()
    {
        try
        {
            _paths.EnsureCreated();
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_paths.Ready}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception error)
        {
            ToastRequested?.Invoke(L10n.Format($"无法打开文件夹：{error.Message}"), null, null, true);
        }
    }

    private void RevealFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception error)
        {
            ToastRequested?.Invoke(L10n.Format($"无法打开文件夹：{error.Message}"), null, null, true);
        }
    }

    /// <summary>移到回收站 — the archive may be the only copy, so it stays recoverable.</summary>
    public void Discard(ReadyBatch batch)
    {
        try
        {
            if (Collections.Ledger.ProtectedBatchIDs.Contains(batch.Id))
                throw new InvalidOperationException(L10n.Text("这批文件仍在收集中，请从收集窗口移除（可撤销）。"));
            _reader.Discard(batch.Id);
        }
        catch (Exception error)
        {
            InboxFailure = error.Message;
        }
        Reload();
    }

    /// <summary>清空记录 — every batch to the Recycle Bin. The caller confirms first.</summary>
    public void DiscardAll()
    {
        foreach (var batch in _batches.Where(b => !Collections.Ledger.ProtectedBatchIDs.Contains(b.Id)))
        {
            try { _reader.Discard(batch.Id); }
            catch (Exception error) { InboxFailure = error.Message; }
        }
        Reload();
    }

    // MARK: - Settings

    public bool IsEntryEnabled(ShareAction action) => _settings.IsEntryEnabled(action);

    internal bool IsDestinationInstalled(ShareAction action, ForwardTarget? target)
        => action != ShareAction.Custom
            ? ShareEntryAvailability.CanEnable(action, [], _isAppInstalled)
            : target is not null && ShareEntryAvailability.CanEnable(action, [target], _isAppInstalled);

    private readonly Func<WindowsForwardTarget, bool>? _isAppInstalled;

    public void SetEntryEnabled(ShareAction action, bool enabled)
    {
        if (enabled && !ShareEntryAvailability.CanEnable(action, _targetStore.Load(), _isAppInstalled))
        {
            _settings.SetEntryEnabled(action, false);
            SaveSettings();
            RebuildEntries();
            var message = action == ShareAction.Custom
                ? L10n.Text("请先添加至少一个已安装的应用，再开启自定义入口。")
                : L10n.Format($"请先安装 {action.TargetDisplayName()}，再开启此入口。");
            ToastRequested?.Invoke(message, null, null, true);
            return;
        }
        _settings.SetEntryEnabled(action, enabled);
        SaveSettings();
        RebuildEntries();
    }

    public void RebuildEntries()
    {
        var targets = _targetStore.Load();
        Entries.Clear();
        foreach (var action in ShareActions.All)
        {
            Entries.Add(new EntryRow
            {
                Action = action,
                IsEnabled = _settings.IsEntryEnabled(action),
                Detail = EntryDetail(action, targets),
                DetailIsWarning = EntryDetailIsWarning(action, targets),
                ConfigureTitle = action switch
                {
                    ShareAction.Obsidian or ShareAction.Folder => L10n.Text("设置…"),
                    ShareAction.Custom => L10n.Text("管理…"),
                    _ => null,
                },
            });
        }
        ReloadTargets(targets);
    }

    private string EntryDetail(ShareAction action, IReadOnlyList<ForwardTarget> targets) =>
        ShareEntryCatalog.Detail(action, _settings, targets);

    private bool EntryDetailIsWarning(ShareAction action, IReadOnlyList<ForwardTarget> targets) => action switch
    {
        ShareAction.Obsidian => string.IsNullOrEmpty(_settings.ObsidianVaultPath),
        ShareAction.Folder => string.IsNullOrEmpty(_settings.DeliveryFolderPath),
        ShareAction.Custom => targets.Count == 0,
        _ => false,
    };

    private void ReloadTargets(IReadOnlyList<ForwardTarget>? targets = null)
    {
        TargetRows.Clear();
        foreach (var target in targets ?? _targetStore.Load())
            TargetRows.Add(new ForwardTargetRow { Target = target });
    }

    public void AddForwardTarget(string bundleIdentifier, string displayName)
    {
        bundleIdentifier = bundleIdentifier.Trim();
        displayName = displayName.Trim();
        if (bundleIdentifier.Length == 0)
            return;
        if (displayName.Length == 0)
            displayName = Path.GetFileNameWithoutExtension(bundleIdentifier);
        var targets = _targetStore.Load();
        if (targets.Any(t => t.BundleIdentifier == bundleIdentifier))
        {
            ToastRequested?.Invoke(L10n.Format($"「{displayName}」已在列表中"), null, null, false);
            return;
        }
        targets.Add(new ForwardTarget(
            bundleIdentifier,
            displayName,
            DateTimeOffset.UtcNow,
            PastesPathOnly: LooksLikeTerminal(bundleIdentifier)));
        _targetStore.Save(targets);
        ReloadTargets(targets);
        RebuildEntries();
    }

    public void RemoveForwardTarget(ForwardTarget target)
    {
        var targets = _targetStore.Load();
        targets.RemoveAll(t => t.BundleIdentifier == target.BundleIdentifier);
        _targetStore.Save(targets);
        ReloadTargets(targets);
        RebuildEntries();
    }

    public void SetPastesPathOnly(ForwardTarget target, bool pastesPathOnly)
    {
        var targets = _targetStore.Load();
        var index = targets.FindIndex(t => t.BundleIdentifier == target.BundleIdentifier);
        if (index < 0 || targets[index].PastesPathOnly == pastesPathOnly)
            return;
        var current = targets[index];
        targets[index] = current with { PastesPathOnly = pastesPathOnly };
        _targetStore.Save(targets);
        ReloadTargets(targets);
    }

    /// <summary>
    /// Terminals are added with 只粘贴文件路径 already on — the Windows mirror of
    /// the macOS terminal bundle-id list: a terminal can never take a pasted file.
    /// </summary>
    private static bool LooksLikeTerminal(string identifier)
    {
        var name = Path.GetFileNameWithoutExtension(identifier);
        return name is "cmd" or "powershell" or "pwsh" or "wt" or "WindowsTerminal"
            or "hyper" or "alacritty" or "wezterm" or "mintty" or "Tabby";
    }

    private void SaveSettings() => _settingsStore.Save(_settings);

    public void Navigate(AppTab tab) => NavigationRequested?.Invoke(tab);

    public void ShowToast(string message, string? actionTitle = null, Action? action = null, bool warning = false) =>
        ToastRequested?.Invoke(message, actionTitle, action, warning);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
