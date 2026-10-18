using System.IO;
using System.Threading;
using System.Windows;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Redeem;
using WeChatBridge.Windows.Onboarding;
using WeChatBridge.Windows.Redeem;
using WeChatBridge.Windows.Services;

namespace WeChatBridge.Windows;

public partial class App : Application
{
    public App() => L10n.Language = new AppSettingsStore().Load().Language;

    // Every machine-wide name gets the layout suffix. A portable copy and an
    // installed copy on the same machine would otherwise silently evict each
    // other: the second launch creates its Inbox, loses the single-instance
    // mutex, and exits with no window and no message. Installed builds keep an
    // empty suffix, so their names are unchanged.
    private static readonly string Suffix = AppLayout.Current.CoordinationSuffix;
    private static string MutexName => "Local\\WeChatBridge.Windows.Main" + Suffix;
    private static string ChangeEventName => "Local\\WeChatBridge.Windows.InboxChanged" + Suffix;
    /// <summary>
    /// A second *interactive* launch asks for the window. Kept separate from
    /// <see cref="ChangeEventName"/>: a share must never pull our window to the
    /// front — the delivery engine is busy foregrounding the target app, and a
    /// window that grabs focus mid-paste steals the Ctrl+V.
    /// </summary>
    private static string ForegroundEventName => "Local\\WeChatBridge.Windows.Foreground" + Suffix;
    /// <summary>
    /// The helper's "a batch is staged, the pick is still open" signal — the
    /// cue to start the scene reads inside the user's decision time. Distinct
    /// from <see cref="ChangeEventName"/>: that one means "committed, reload".
    /// </summary>
    private static string PrefetchEventName => PrefetchHint.EventName + Suffix;

    /// <summary>How long the inbox relay waits after an I/O failure before retrying.</summary>
    private static readonly TimeSpan RelayRetryDelay = TimeSpan.FromSeconds(2);

    private Mutex? _mutex;
    private EventWaitHandle? _changeEvent;
    private EventWaitHandle? _foregroundEvent;
    private EventWaitHandle? _prefetchEvent;
    private EventWaitHandle? _collectionImportEvent;
    private CancellationTokenSource? _shutdown;
    private InboxPaths? _paths;
    private MainViewModel? _model;
    private TrayIconService? _tray;
    private WorkBuddyArchiveWatcher? _workBuddyWatcher;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var background = e.Args.Any(a => string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase));
        _paths = new InboxPaths();
        // A portable build's Inbox lives on a removable drive, which can be
        // read-only, full, or mid-rename. Directory.CreateDirectory throws for
        // all three, and letting it escape OnStartup means no window and no
        // explanation — the same "nothing happened" the mutex path below used
        // to produce. Say what is wrong instead.
        try
        {
            _paths.EnsureCreated();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    L10n.Text("无法在 {0} 创建数据目录。请检查磁盘是否已插入、是否可写，或换一个目录再试。"),
                    _paths.Root) + "\n\n" + error.Message,
                L10n.Text("微信流"), MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // A share that arrives while we are running spawns a second
            // --background instance (the helper always launches us). It must
            // exit quietly — foregrounding belongs to interactive launches
            // only, otherwise every share would steal focus mid-delivery.
            if (!background)
                SignalExistingInstance();
            Shutdown();
            return;
        }

        // Debris from a helper that was killed mid-copy. Swept by the owning instance
        // only, and it runs long after any such copy would have died. Matches the macOS
        // app, which prunes on start for the same reason.
        _paths.PruneStaging();

        _changeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ChangeEventName);
        _foregroundEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ForegroundEventName);
        _prefetchEvent = new EventWaitHandle(false, EventResetMode.AutoReset, PrefetchEventName);
        _collectionImportEvent = new EventWaitHandle(false, EventResetMode.AutoReset, CollectionImportProgress.EventName);
        _shutdown = new CancellationTokenSource();
        var requestedBatch = ReadArgument(e.Args, "--batch-id");
        var model = new MainViewModel(_paths);
        _model = model;
        model.WorkBuddyAutoImportChanged += ConfigureWorkBuddyWatcher;
        ConfigureWorkBuddyWatcher(model.WorkBuddyAutoImport);
        var window = new MainWindow(model, requestedBatch);
        MainWindow = window;
        // The tray exists in every instance — it is the resident process's
        // only chrome when the window is hidden.
        _tray = new TrayIconService(model, () => window, Shutdown);
        // Closing the window hides it to the tray; the share relay must stay
        // resident, so only the tray's 退出 really ends the process.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // The share helper launches us with --background purely so the fresh
        // intent is consumed and the forward runs. Showing the window here
        // would fight the target app for the foreground — and the guide must
        // never pop mid-share either.
        if (!background)
        {
            window.Show();
            // The guide is written for an installed build: it tells an unregistered
            // user to reinstall the full package, offers a sign-in-startup toggle
            // that a removable drive cannot honour, and lists twelve entries that
            // mean nothing before the share menu is registered. Portable mode skips
            // it — the Entries page carries the share-registration action instead.
            if (!AppLayout.Current.IsPortable && new OnboardingStateStore().NeedsOnboarding())
                new OnboardingWindow(model) { Owner = window }.Show();

            // The licence layer is resolved once, here, rather than lazily by whichever
            // pane happens to ask first: a purchased licence should already be in force
            // before anything reads it, and a resolution that fails belongs in the log
            // next to startup rather than surfacing later as a pane that quietly shows
            // the wrong thing.
            var licensing = LicensingBootstrap.Current;
            LogStartup($"licensing: edition={licensing.EditionName} pro={licensing.IsPro} " +
                $"activation={licensing.Resolution.Activation} detail={licensing.Resolution.Detail}");
            // An unresolved redemption is reported and left alone. Nothing retries it:
            // a request the user never asked for is the one thing the buy-out edition's
            // promise rules out, so the pending record waits for them to open the window
            // and press the button (design doc 3.2).
            if (new PendingRedemptionStore(AppLayout.Current.Config).Load() is { } pending)
                LogStartup($"redeem: an earlier attempt (request {pending.RequestId}) has no answer; open the redemption window to continue it");

            if (e.Args.Any(a => string.Equals(a, "--redeem", StringComparison.OrdinalIgnoreCase)))
                ShowRedeemWindow(window);

            // The buy-out gate. Reached only from an interactive launch — the
            // --background instance above returns long before this, because putting a
            // window up would steal the foreground from the target app mid-delivery.
            //
            // It locks nothing: forwarding is available to everyone, and this only
            // offers a path to buying. Offered once per install; the 通用 page keeps
            // the button for anyone who wants it later.
            var gate = ActivationGate.Current;
            var page = gate.ShowAtStartup(window, LogStartup);
            LogStartup($"activation: gate offered {page}, state={gate.Snapshot.State}");
        }
        // MainWindow's inbox load rides the Loaded event — a hidden window
        // never raises it, so a background launch must load explicitly or it
        // would idle forever with intents unconsumed (seen live 2026-09-25:
        // helper relaunched us hidden and two shares were never delivered).
        Dispatcher.BeginInvoke(() =>
        {
            // A hint can predate this process — the share that launched us
            // staged while we were still starting. Draining it here starts
            // the scene reads before Reload announces the batch.
            model.RefreshImportProgress();
            model.ConsumePrefetchHint();
            model.RebuildEntries();
            window.RefreshBatches();
        });
        _ = WaitForChangesAsync(_shutdown.Token);
    }

    /// <summary>
    /// Writes a startup or redemption line to the Inbox log, and nowhere else.
    /// <para>
    /// The guard is not defensive noise: this method is handed to
    /// <see cref="RedeemController.Create"/> as a delegate, and a delegate that throws
    /// because a nullable field happens to be null would turn "could not write the log"
    /// into "the redemption window broke".
    /// </para>
    /// </summary>
    private void LogStartup(string message)
    {
        if (_paths is not null)
            InboxLogger.Write(_paths, message);
    }

    /// <summary>
    /// Opens the buy-out edition's redemption dialog, modally.
    /// <para>
    /// This is the only way into the network in this product, and it is deliberately
    /// a command the user types rather than something the app volunteers: the
    /// buy-out edition's published promise is that it does not go online on its own,
    /// and a network request nobody asked for is the promise broken whether or not the
    /// bytes were harmless.
    /// </para>
    /// </summary>
    private void ShowRedeemWindow(Window owner)
    {
        var licensing = LicensingBootstrap.Current;
        var controller = RedeemController.Create(licensing, LogStartup);
        try
        {
            // Two builds that cannot honour a redemption, and both say so in the window
            // rather than failing at the button. A portable stick is the buy-out
            // product's free tier and is issued no licences; a machine that is already
            // Pro has nothing left to buy.
            if (licensing.IsPortable)
                controller.MarkUnavailable("便携版是买断版的免费档，不签发许可证。请在安装版上兑换。");
            else if (licensing.IsPro)
                controller.MarkUnavailable("这台机器已经是买断版 Pro，不需要再兑换。");
            else if (controller.HasUnresolvedPending)
                controller.SetStatus(L10n.Text("上一次兑换没有得到结果。重新粘贴同一个兑换码再点「兑换」，系统会用同一个请求编号问一次服务端，不会重复发证。"));

            new RedeemWindow(controller) { Owner = owner }.ShowDialog();
        }
        finally
        {
            // Drops the connection pool. It does not seal the channel: closing the
            // window is not the same as having redeemed anything.
            controller.Dispose();
        }
    }

    /// <summary>
    /// 通用页's 重新运行设置向导 — the macOS SettingsActions.restartOnboarding
    /// port. A finished guide restarts at the top; the completion flag stays
    /// so the next launch does not think the app was never introduced.
    /// </summary>
    public void RestartOnboarding()
    {
        var store = new OnboardingStateStore();
        var state = store.Load();
        if (state.Step != 0)
        {
            state.Step = 0;
            store.Save(state);
        }
        if (_model is not null)
            new OnboardingWindow(_model, store) { Owner = MainWindow }.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _workBuddyWatcher?.Dispose();
        _model?.DisposeServices();
        _shutdown?.Cancel();
        _changeEvent?.Dispose();
        _foregroundEvent?.Dispose();
        _prefetchEvent?.Dispose();
        _collectionImportEvent?.Dispose();
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _mutex.Dispose();
        }
        base.OnExit(e);
    }

    private void ConfigureWorkBuddyWatcher(bool enabled)
    {
        _workBuddyWatcher?.Dispose();
        _workBuddyWatcher = null;
        if (!enabled || _model is null || _paths is null) return;
        try
        {
            _workBuddyWatcher = new WorkBuddyArchiveWatcher(
                path => Dispatcher.BeginInvoke(new Action(() => _ = ImportWorkBuddyArchiveAsync(path))),
                error => InboxLogger.Write(_paths, L10n.Text("WorkBuddy 自动导入监听失败"), error));
            InboxLogger.Write(_paths, $"WorkBuddy 自动导入已启用：{WorkBuddyArchiveWatcher.DefaultDirectory}");
        }
        catch (Exception error)
        {
            InboxLogger.Write(_paths, L10n.Text("WorkBuddy 自动导入监听失败"), error);
            _model.ShowToast(L10n.Text("无法监视 WorkBuddy 聊天记录目录。"), warning: true);
        }
    }

    private async Task ImportWorkBuddyArchiveAsync(string path)
    {
        if (_model is null || MainWindow is not MainWindow window) return;
        await _model.ImportWorkBuddyArchiveAsync(path);
        window.RefreshBatches();
    }

    private async Task WaitForChangesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var signalled = await Task.Run(
                    () => WaitHandle.WaitAny(new WaitHandle?[] { _changeEvent, _foregroundEvent, _prefetchEvent, _collectionImportEvent }
                        .OfType<WaitHandle>().ToArray(), 500),
                    cancellationToken);
                if (cancellationToken.IsCancellationRequested) return;
                if (signalled == WaitHandle.WaitTimeout)
                {
                    if (_model?.ImportProgress is not null) Dispatcher.Invoke(() => _model.RefreshImportProgress());
                    continue;
                }

                Dispatcher.Invoke(() =>
                {
                    // A prefetch hint touches no window — the resident process
                    // is expected to be hidden while it runs the reads.
                    if (signalled == 3)
                    {
                        _model?.RefreshImportProgress();
                        return;
                    }
                    if (signalled == 2)
                    {
                        _model?.ConsumePrefetchHint();
                        return;
                    }
                    if (MainWindow is not MainWindow window)
                        return;
                    window.RefreshBatches();
                    // Only an explicit second launch pulls the window forward —
                    // the share helper's inbox signal just refreshes the list.
                    if (signalled == 1)
                        window.BringToFront();
                });
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error)
            {
                if (_paths is not null)
                    InboxLogger.Write(_paths, L10n.Text("监听 Inbox 失败"), error);
                // Keep relaying. A removable drive makes transient errors routine
                // — a slow read, an indexer holding a handle, the volume being
                // re-enumerated — and one of them used to end the share relay for
                // the life of the process. Back off so a permanently broken root
                // does not spin, and so a drive that comes back recovers on its own.
                try { await Task.Delay(RelayRetryDelay, cancellationToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ForegroundEventName);
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { }
    }

    private static string? ReadArgument(string[] args, string name)
    {
        for (var index = 0; index + 1 < args.Length; index++)
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        return null;
    }
}
