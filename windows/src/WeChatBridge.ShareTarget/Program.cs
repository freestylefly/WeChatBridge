using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WeChatBridge.Windows;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Services;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.Storage;
using WpfClipboard = System.Windows.Clipboard;

namespace WeChatBridge.ShareTarget;

internal static class Program
{
    private const string ChangeEventName = "Local\\WeChatBridge.Windows.InboxChanged";

    /// <summary>The main app's single-instance mutex — probing it tells us whether a launch is needed at all.</summary>
    private const string MainMutexName = "Local\\WeChatBridge.Windows.Main";

    /// <summary>The named pipe a stub uses to hand a share to the resident keeper.</summary>
    internal const string PipeName = "WeChatBridge.ShareTarget.Pipe";

    /// <summary>One keeper per session — doubles as the residency election when two cold starts race.</summary>
    private const string KeeperMutexName = "Local\\WeChatBridge.ShareTarget.Keeper";

    /// <summary>Serialises share handling in the keeper: two shares in quick succession must not open two pickers or race the clipboard.</summary>
    private static readonly SemaphoreSlim ActivationGate = new(1, 1);

    /// <summary>Held for the process lifetime — releasing it would let a second cold start become a competing keeper.</summary>
    private static Mutex? _keeperMutex;

    /// <summary>Set by <see cref="KeeperMainAsync"/> when this process wins the keeper election.</summary>
    private static volatile bool _resident;

    /// <summary>
    /// Process roles. Only the first helper pays the cold start: it becomes
    /// the keeper and stays resident, WPF already warm. Every later share
    /// activation launches a stub that forwards the file list over
    /// <see cref="PipeName"/>, brokers the ShareOperation reports, and exits —
    /// the picker then comes from a process that is already running.
    /// (The inbox <c>AppInstance.RedirectActivationTo</c> has no desktop-side
    /// delivery mechanism — that redirect event exists only in WindowsAppSDK's
    /// AppLifecycle, which the sparse package cannot load — so the keeper is
    /// reached over a named pipe instead.)
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        // Installer probe must not create an inbox, launch the keeper or open a picker.
        if (args.Contains("--registration-check", StringComparer.Ordinal))
        {
            Environment.Exit(ShareIdentityCheck.Run());
            return;
        }
        var paths = new InboxPaths();

        // Dev/self-test hook: `WeChatBridge.ShareTarget.exe --picker-preview`
        // opens the entry panel with sample rows so the design can be
        // exercised without a real WeChat share. Checked first so a preview
        // never forwards to a resident keeper.
        if (args.Contains("--picker-preview", StringComparer.Ordinal))
        {
            RunMessageLoop(() => PreviewEntryPicker());
            return;
        }

        if (StubForwarder.TryForward(paths))
            return;

        // No keeper answered: become it — handle this activation, then stay
        // resident serving the stubs that follow.
        RunMessageLoop(() => KeeperMainAsync(paths));
    }

    /// <summary>
    /// The WPF dispatcher as a main loop: <paramref name="run"/> executes on
    /// the STA thread and its continuations land back here. A process that won
    /// the keeper election never shuts the loop down — it keeps pumping so
    /// piped shares can surface the picker without another cold start.
    /// </summary>
    private static void RunMessageLoop(Func<Task> run)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = run();
        task.ContinueWith(t =>
        {
            _ = t.Exception; // observed; every branch already logs its own failure
            if (!_resident)
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        });
        Dispatcher.Run();
    }

    /// <summary>
    /// The keeper role: win the election, open the stub pipe, then handle the
    /// activation this process was itself launched for. A process that lost a
    /// double cold-start race still handles its own activation — it just does
    /// not become resident.
    /// </summary>
    private static async Task KeeperMainAsync(InboxPaths paths)
    {
        _keeperMutex = new Mutex(true, KeeperMutexName, out var isKeeper);
        if (isKeeper)
        {
            _resident = true;
            _ = ServeStubsAsync(paths);
        }
        else
        {
            _keeperMutex.Dispose();
            _keeperMutex = null;
        }

        // Unpackaged/debug runs have no activation payload — a throw here must
        // not fault the keeper task before the dispatcher even settles.
        IActivatedEventArgs? activated = null;
        try
        {
            activated = AppInstance.GetActivatedEventArgs();
        }
        catch (Exception error)
        {
            InboxLogger.Write(paths, "读取激活参数失败。", error);
        }
        await HandleActivationAsync(paths, activated);

        // Warm XAML/JSON/JIT after the activation settles, so the first piped
        // share runs on hot paths — residency is worthless if the warm-up
        // still lands inside someone's share.
        if (_resident)
            _ = WarmKeeperAsync(paths);
    }

    /// <summary>
    /// The accept loop: each connection is one share from a stub. Handling is
    /// marshalled onto the STA dispatcher — the gate inside serialises it
    /// against any share already in flight.
    /// </summary>
    private static async Task ServeStubsAsync(InboxPaths paths)
    {
        var ui = Dispatcher.CurrentDispatcher;
        while (true)
        {
            try
            {
                var pipe = new NamedPipeServerStream(
                    PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync();
                _ = ui.InvokeAsync(async () =>
                {
                    using (pipe)
                        await HandleStubShareAsync(paths, pipe);
                });
            }
            catch (Exception error)
            {
                InboxLogger.Write(paths, "stub 通道异常", error);
                await Task.Delay(250); // don't hot-loop on a broken pipe
            }
        }
    }

    /// <summary>
    /// One piped share, handled inside the keeper. A connection means a share
    /// is in flight, so the panel goes up in its receiving state before the
    /// file list even arrives; the stub's single payload line then carries
    /// either the files or an abort reason.
    /// </summary>
    private static async Task HandleStubShareAsync(InboxPaths paths, Stream pipe)
    {
        Trace(paths, "keeper.pipe-accepted");
        await ActivationGate.WaitAsync();
        var writer = new StreamWriter(pipe) { AutoFlush = true };
        try
        {
            var action = ResolveEntryAction(paths);
            var (entryPick, picker) = action == ShareAction.Hub
                ? AskShareEntry(paths)
                : (Task.FromResult<EntryPickerAnswer?>(null), null);
            if (picker is not null)
                Trace(paths, "share.picker-up");

            var line = await new StreamReader(pipe).ReadLineAsync();
            var payload = line is null ? null
                : JsonSerializer.Deserialize<StubSharePayload>(line, StubForwarder.JsonOptions);
            if (payload is null || payload.Abort is not null || payload.Items is not { Count: > 0 })
            {
                picker?.Close();
                await writer.WriteLineAsync(JsonSerializer.Serialize(
                    new StubShareReply(false, payload?.Abort ?? "空分享负载。"),
                    StubForwarder.JsonOptions));
                return;
            }

            var sources = payload.Items.Select((item, index) =>
                new InboxSourceFile(item.Path, item.Name, item.ContentType, index, 0)).ToList();

            await ProcessShareAsync(paths, sources, action, entryPick, picker);
            await writer.WriteLineAsync(JsonSerializer.Serialize(
                new StubShareReply(true, null), StubForwarder.JsonOptions));
        }
        catch (Exception error)
        {
            InboxLogger.Write(paths, "stub 分享处理失败", error);
            try
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(
                    new StubShareReply(false, error.Message), StubForwarder.JsonOptions));
            }
            catch { }
        }
        finally
        {
            ActivationGate.Release();
        }
    }

    /// <summary>
    /// One share activation — the keeper's own, launched by Windows directly.
    /// Runs entirely on the dispatcher thread, gated so a piped share cannot
    /// interleave with a picker that is already open.
    /// </summary>
    private static async Task HandleActivationAsync(InboxPaths paths, IActivatedEventArgs? activated)
    {
        await ActivationGate.WaitAsync();
        try
        {
            if (activated is null)
            {
                InboxLogger.Write(paths, "未取得 Share Target 激活参数。");
                return;
            }

            if (activated.Kind != ActivationKind.ShareTarget)
            {
                InboxLogger.Write(paths, $"收到非分享激活：{activated.Kind}");
                return;
            }

            // For share activation the args object itself implements
            // IShareTargetActivatedEventArgs.
            if (activated is not IShareTargetActivatedEventArgs shareArgs)
            {
                InboxLogger.Write(paths, $"Share Target 激活参数类型不匹配：{activated.GetType().FullName}");
                return;
            }

            var operation = shareArgs.ShareOperation;
            // Reports tolerate duplicates: a stub that fell back to local
            // handling already issued Started/DataRetrieved on this operation.
            StubForwarder.ShareReports.Started(operation);
            var action = ResolveEntryAction(paths);
            var (entryPick, picker) = action == ShareAction.Hub
                ? AskShareEntry(paths)
                : (Task.FromResult<EntryPickerAnswer?>(null), null);
            if (picker is not null)
                Trace(paths, "share.picker-up");

            try
            {
                if (!operation.Data.Contains(StandardDataFormats.StorageItems))
                    throw new InboxValidationException("PoC 只接收文件型分享，当前分享不包含 StorageItems。");
                var sources = await SourcesAsync(operation);
                StubForwarder.ShareReports.DataRetrieved(operation);
                await ProcessShareAsync(paths, sources, action, entryPick, picker);
                StubForwarder.ShareReports.Completed(operation);
            }
            catch (Exception error)
            {
                // A panel raised before the failure must not linger showing
                // its receiving strip until the deadline.
                picker?.Close();
                InboxLogger.Write(paths, "Share Target 处理失败", error);
                StubForwarder.ShareReports.Failed(operation, error.Message);
            }
        }
        catch (Exception error)
        {
            InboxLogger.Write(paths, "Share Target 激活初始化失败", error);
        }
        finally
        {
            ActivationGate.Release();
        }
    }

    /// <summary>The share's file list, validated and flattened — the keeper-local counterpart of what a stub pipes over.</summary>
    private static async Task<List<InboxSourceFile>> SourcesAsync(ShareOperation operation)
    {
        var storageItems = await operation.Data.GetStorageItemsAsync().AsTask();
        if (storageItems.Count == 0)
            throw new InboxValidationException("分享中没有文件。");

        var sources = new List<InboxSourceFile>(storageItems.Count);
        for (var itemIndex = 0; itemIndex < storageItems.Count; itemIndex++)
        {
            if (storageItems[itemIndex] is not StorageFile file)
                throw new InboxValidationException("分享项不是可读取的文件。");
            if (string.IsNullOrWhiteSpace(file.Path))
                throw new InboxValidationException($"无法读取分享文件：{file.Name}");

            sources.Add(new InboxSourceFile(
                file.Path,
                file.Name,
                file.ContentType,
                itemIndex,
                0));
        }
        return sources;
    }

    /// <summary>
    /// The share's tail end, identical for both entry paths: context line,
    /// staging overlapped with the pick, atomic commit, clipboard, and the
    /// main process. ShareOperation reporting is the caller's — a stub does
    /// it for piped shares, this process for its own activation.
    /// </summary>
    private static async Task ProcessShareAsync(
        InboxPaths paths,
        IReadOnlyList<InboxSourceFile> sources,
        ShareAction action,
        Task<EntryPickerAnswer?>? entryPick,
        EntryPickerWindow? picker)
    {
        // What arrived, under the picker's title — known the moment the
        // file names are, while the user is still reading the rows.
        picker?.SetContext(ContextLine(sources));
        var titleSnapshot = WeChatBridge.Windows.Services.WeChatUiTitleReader.ReadWithFallbackAsync();
        Trace(paths, $"share.sources count={sources.Count}");

        // Staging overlaps the pick: whatever the user answers, the batch
        // commits — only the intent/outcome written on top of it differs.
        var stageTask = InboxWriter.StageAsync(
            paths, sources, cancellationToken: default, limits: null, action: action);
        CollectionImportSession? receipt = null;
        using var stopReceipt = new CancellationTokenSource();
        var receiptChoice = StartReceiptAsync();
        async Task StartReceiptAsync()
        {
            try
            {
                var choice = entryPick is null ? null : await entryPick.WaitAsync(stopReceipt.Token);
                var (resolved, _) = ResolveForward(action, choice);
                if (resolved?.Action != ShareAction.Collect) return;
                receipt = new CollectionImportSession(paths);
                try { StartMainProcess(paths, Guid.Empty); }
                catch (Exception error) { InboxLogger.Write(paths, "保存进度唤醒主程序失败", error); }
            }
            catch (OperationCanceledException) { }
        }
        try
        {
            var staged = await stageTask;
            // Tell the resident app which batch is coming while the user is still
            // weighing the rows — its title read and ZIP parse then finish inside
            // the decision time instead of after the click.
            PrefetchHint.Publish(paths, staged);
            Trace(paths, $"share.staged batch={staged.BatchId:N}");
            var answer = entryPick is null ? null : await entryPick;
            Trace(paths, $"share.answer kind={answer?.Kind.ToString() ?? "none"}");

            await receiptChoice;
            var (intent, initialOutcome) = ResolveForward(action, answer);
            string? chatName = null;
            try { chatName = (await titleSnapshot.WaitAsync(TimeSpan.FromSeconds(3)))?.Name; } catch { }
            var committed = await InboxWriter.CommitAsync(
                paths, staged, intent, initialOutcome, chatName: chatName);
            Trace(paths, $"share.committed batch={committed.BatchId:N}");
            // The batch is durable at this point, so everything below is an
            // optimisation that is allowed to fail. Sweeping here is what keeps
            // debris from a share that was killed mid-copy from accumulating —
            // off the dispatcher so it cannot stall the next pick.
            _ = Task.Run(() => paths.PruneStaging());
            // The final picker choice owns the action; Hub may have selected Collect.
            var clipboardWritten = ShareActions.WritesClipboardOnReceipt(action, intent)
                && TryWriteClipboard(committed.Manifest.Items, committed.BatchDirectory, paths);
            Trace(paths, $"share.clipboard ok={clipboardWritten}");
            receipt?.Finish(true);
            SignalMainProcess();
            StartMainProcess(paths, committed.BatchId);
            InboxLogger.Write(paths, $"分享批次已提交：{committed.BatchId}; action={action.RawValue()}; intent={intent?.Action.RawValue() ?? "none"}; clipboard={clipboardWritten}");
        }
        finally
        {
            stopReceipt.Cancel();
            await receiptChoice;
            receipt?.Dispose();
        }
    }

    /// <summary>
    /// The header's payload line: what WeChat handed over, in one glance.
    /// </summary>
    private static string ContextLine(IReadOnlyList<InboxSourceFile> sources)
    {
        var bytes = sources.Sum(s =>
        {
            try { return new FileInfo(s.SourcePath).Length; }
            catch { return 0L; }
        });
        var size = ByteText.Format(bytes);
        return sources.Count == 1
            ? $"{sources[0].DisplayName} · {size}"
            : $"{sources[0].DisplayName} 等 {sources.Count} 个 · {size}";
    }

    /// <summary>
    /// The Hub question asked in this process. The window comes back alongside
    /// its answer task so the caller can still fill the payload line after the
    /// share's files are known. A picker that cannot be built falls back to
    /// "picked Hub": the intent then still says hub and the app asks its own
    /// picker, so a helper-side failure never loses the share's question.
    /// </summary>
    private static (Task<EntryPickerAnswer?> Answer, EntryPickerWindow? Window) AskShareEntry(InboxPaths paths)
    {
        try
        {
            var (settings, targets) = EntryConfig();
            var options = ShareEntryCatalog.BuildOptions(settings, targets);
            switch (options.Count)
            {
                // Nothing enabled: leave the hub intent; the app surfaces its
                // 还没有开启任何入口 toast rather than answering silently here.
                case 0:
                    return (Task.FromResult<EntryPickerAnswer?>(
                        EntryPickerAnswer.Picked(ShareAction.Hub, null)), null);
                case 1:
                    return (Task.FromResult<EntryPickerAnswer?>(
                        EntryPickerAnswer.Picked(options[0].Action, options[0].Target)), null);
                default:
                    var window = new EntryPickerWindow(options)
                    {
                        ContextLine = "正在接收文件…",
                        IsReceiving = true,
                    };
                    return (Await(window), window);

                    static async Task<EntryPickerAnswer?> Await(EntryPickerWindow picker) =>
                        await picker.ShowAndAwait();
            }
        }
        catch (Exception error)
        {
            InboxLogger.Write(paths, "入口选择面板初始化失败，交给主程序询问。", error);
            return (Task.FromResult<EntryPickerAnswer?>(
                EntryPickerAnswer.Picked(ShareAction.Hub, null)), null);
        }
    }

    /// <summary>
    /// Settings and custom targets, cached by file write time: the keeper asks
    /// on every share, but the files only change when the user edits the 入口
    /// pane — a stat per file is the whole cost of staying correct.
    /// </summary>
    private static (AppSettings Settings, List<ForwardTarget> Targets) EntryConfig()
    {
        var settingsPath = Path.Combine(ConfigStore.DefaultDirectory, AppSettings.FileName);
        var targetsPath = Path.Combine(ConfigStore.DefaultDirectory, ForwardTargetStore.FileName);
        var writes = (WriteTime(settingsPath), WriteTime(targetsPath));
        if (_entryCache is { } cached && cached.Writes == writes)
            return (cached.Settings, cached.Targets);
        var settings = new AppSettingsStore().Load();
        var targets = new ForwardTargetStore().Load();
        _entryCache = (writes, settings, targets);
        return (settings, targets);

        static DateTime WriteTime(string path) =>
            File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
    }

    private static ((DateTime Settings, DateTime Targets) Writes, AppSettings Settings, List<ForwardTarget> Targets)? _entryCache;

    /// <summary>Stage-boundary marks in windows.log — the only way to see where a share actually spent its time.</summary>
    private static void Trace(InboxPaths paths, string stage) =>
        InboxLogger.Write(paths, $"[trace] {stage}");

    /// <summary>
    /// Maps the entry pick — or the non-Hub share-menu entry — onto the
    /// intent/outcome pair committed with the batch. A picked entry writes its
    /// resolved intent so the app forwards without asking again; a cancelled
    /// or expired pick settles the batch right here, since the question was
    /// already answered with "nothing".
    /// </summary>
    private static (BatchIntent? Intent, BatchOutcome? InitialOutcome) ResolveForward(
        ShareAction action, EntryPickerAnswer? answer)
    {
        switch (answer?.Kind)
        {
            case EntryPickerAnswerKind.Picked:
                var picked = answer.Action ?? ShareAction.Hub;
                return (picked.NeedsIntent()
                    ? new BatchIntent
                    {
                        Action = picked,
                        RequestedAt = DateTimeOffset.UtcNow,
                        TargetBundleIdentifier = answer.Target?.BundleIdentifier,
                        TargetDisplayName = answer.Target?.DisplayName,
                    }
                    : null, null);
            case EntryPickerAnswerKind.Cancelled:
                return (null, new BatchOutcome(
                    BatchOutcomeKind.Failed, "已在转发面板取消", DateTimeOffset.UtcNow));
            case EntryPickerAnswerKind.Expired:
                return (null, new BatchOutcome(
                    BatchOutcomeKind.Expired, "转发面板超时未选择", DateTimeOffset.UtcNow));
            default:
                // Not the hub: the share-menu entry itself is the answer.
                return (action.NeedsIntent()
                    ? new BatchIntent { Action = action, RequestedAt = DateTimeOffset.UtcNow }
                    : null, null);
        }
    }

    /// <summary>
    /// Self-test: the entry panel over demo rows. Lets the redesign be previewed
    /// (`--picker-preview`) without standing up a real WeChat share.
    /// </summary>
    private static async Task PreviewEntryPicker()
    {
        var demo = new List<EntryPickerOption>
        {
            new(ShareAction.Codex, null, "激活 ChatGPT 并直接粘贴到输入框。"),
            new(ShareAction.Claude, null, "激活 Claude 并直接粘贴到输入框。"),
            new(ShareAction.Doubao, null, "激活豆包，把压缩包路径粘贴到输入框。"),
            new(ShareAction.Obsidian, null, "知识库：Notes"),
            new(ShareAction.Clipboard, null, "只复制，不自动粘贴"),
            new(ShareAction.Custom, new ForwardTarget("com.example.work", "工作台", DateTimeOffset.UtcNow), "com.example.work"),
        };
        var window = new EntryPickerWindow(demo)
        {
            ContextLine = "聊天记录_产品讨论组.zip · 203 KB",
        };
        var answer = await window.ShowAndAwait();
        InboxLogger.Write(new InboxPaths(),
            $"picker-preview: kind={answer.Kind} action={answer.Action?.RawValue() ?? "null"} target={answer.Target?.DisplayName ?? "null"}");
    }

    /// <summary>
    /// Which share-menu entry invoked us. The sparse package now declares a
    /// single <c>&lt;Application&gt;</c> — Share.Hub, shown to WeChat as 「微信流」 —
    /// whose <c>{PackageFamilyName}!Share.Hub</c> AUMID is what
    /// <c>AppInfo.Current</c> reports here. The legacy per-entry ids are still
    /// recognised so a batch written by the old nine-application package keeps
    /// its intended destination.
    /// An unresolved AUMID — an unpackaged debug run, or a manifest older than
    /// either scheme — degrades to 复制到剪贴板, the entry whose work the
    /// helper finishes itself.
    /// </summary>
    private static ShareAction ResolveEntryAction(InboxPaths paths)
    {
        try
        {
            var aumid = AppInfo.Current.AppUserModelId;
            InboxLogger.Write(paths, $"当前进程 AUMID：{aumid ?? "(null)"}");
            var appId = aumid?.Split('!').LastOrDefault();
            foreach (var action in ShareActions.All.Append(ShareAction.Hub))
            {
                if (string.Equals(appId, action.ShareEntryId(), StringComparison.Ordinal))
                    return action;
            }
            InboxLogger.Write(paths, $"未识别的分享入口 AUMID：{aumid}，按复制到剪贴板处理。");
        }
        catch (Exception error)
        {
            // No package identity at all (plain exe run) — clipboard is correct.
            InboxLogger.Write(paths, "读取应用身份失败，按复制到剪贴板处理。", error);
        }
        return ShareAction.Clipboard;
    }

    private static bool TryWriteClipboard(
        IReadOnlyList<ManifestItem> items,
        string batchDirectory,
        InboxPaths paths)
    {
        try
        {
            var files = new StringCollection();
            foreach (var item in items)
            {
                var path = Path.GetFullPath(Path.Combine(batchDirectory, item.RelativePath));
                if (!File.Exists(path))
                    throw new FileNotFoundException("批次文件不存在", path);
                files.Add(path);
            }

            Exception? clipboardError = null;
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                // The keeper's dispatcher thread is already STA — write in
                // place instead of paying a thread spawn per share.
                try
                {
                    WpfClipboard.SetFileDropList(files);
                }
                catch (Exception error)
                {
                    clipboardError = error;
                }
            }
            else
            {
                var clipboardThread = new Thread(() =>
                {
                    try
                    {
                        WpfClipboard.SetFileDropList(files);
                    }
                    catch (Exception error)
                    {
                        clipboardError = error;
                    }
                });
                clipboardThread.SetApartmentState(ApartmentState.STA);
                clipboardThread.Start();
                clipboardThread.Join();
            }

            if (clipboardError is not null)
                ExceptionDispatchInfo.Capture(clipboardError).Throw();

            return true;
        }
        catch (Exception error)
        {
            InboxLogger.Write(paths, "写入 FileDropList 失败；批次已保留", error);
            return false;
        }
    }

    private static void SignalMainProcess()
    {
        try
        {
            using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ChangeEventName);
            signal.Set();
        }
        catch
        {
            // The main process will discover Ready batches during its next scan.
        }
    }

    private static void StartMainProcess(InboxPaths paths, Guid batchId)
    {
        var root = Directory.GetParent(AppContext.BaseDirectory)?.FullName;
        if (root is null)
            return;

        var mainPath = Path.Combine(root, "WeChatBridge.Windows.exe");
        if (!File.Exists(mainPath))
            return;

        // A resident instance only needs the InboxChanged signal, which
        // SignalMainProcess already sent — spawning a process that loses the
        // mutex and exits just burns ~100 ms of process creation per share.
        try
        {
            using var _ = Mutex.OpenExisting(MainMutexName);
            Trace(paths, "share.main-resident skip-launch");
            return;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
        catch
        {
            // Probe inconclusive — launching anyway is the harmless side.
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = mainPath,
            Arguments = $"--background --batch-id {batchId:D}",
            WorkingDirectory = root,
            UseShellExecute = true
        });
    }

    /// <summary>
    /// Post-activation warm-up for the keeper role: building one picker off
    /// camera pays the XAML parse and assembly loads, and the JSON runs prime
    /// the serializers — so the first piped share rides warm code paths.
    /// </summary>
    private static async Task WarmKeeperAsync(InboxPaths paths)
    {
        try
        {
            await Task.Delay(50); // let real share work keep dispatcher priority
            var probe = new List<EntryPickerOption>
            {
                new(ShareAction.Clipboard, null, "warm-up")
            };
            var window = new EntryPickerWindow(probe);
            window.Close();
            _ = JsonSerializer.Serialize(new StubShareReply(true, null), StubForwarder.JsonOptions);
            _ = JsonSerializer.Serialize(
                new BatchManifest(BatchManifest.CurrentSchemaVersion,
                    Guid.NewGuid(), DateTimeOffset.UtcNow, "warmup", []),
                BatchManifest.JsonOptions);
            Trace(paths, "keeper.warm done");
        }
        catch (Exception error)
        {
            InboxLogger.Write(paths, "keeper 预热失败（不影响分享）", error);
        }
    }
}
