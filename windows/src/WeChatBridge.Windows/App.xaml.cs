using System.Threading;
using System.Windows;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Onboarding;

namespace WeChatBridge.Windows;

public partial class App : Application
{
    public App() => L10n.Language = new AppSettingsStore().Load().Language;
    private const string MutexName = "Local\\WeChatBridge.Windows.Main";
    private const string ChangeEventName = "Local\\WeChatBridge.Windows.InboxChanged";
    /// <summary>
    /// A second *interactive* launch asks for the window. Kept separate from
    /// <see cref="ChangeEventName"/>: a share must never pull our window to the
    /// front — the delivery engine is busy foregrounding the target app, and a
    /// window that grabs focus mid-paste steals the Ctrl+V.
    /// </summary>
    private const string ForegroundEventName = "Local\\WeChatBridge.Windows.Foreground";
    /// <summary>
    /// The helper's "a batch is staged, the pick is still open" signal — the
    /// cue to start the scene reads inside the user's decision time. Distinct
    /// from <see cref="ChangeEventName"/>: that one means "committed, reload".
    /// </summary>
    private const string PrefetchEventName = PrefetchHint.EventName;

    private Mutex? _mutex;
    private EventWaitHandle? _changeEvent;
    private EventWaitHandle? _foregroundEvent;
    private EventWaitHandle? _prefetchEvent;
    private EventWaitHandle? _collectionImportEvent;
    private CancellationTokenSource? _shutdown;
    private InboxPaths? _paths;
    private MainViewModel? _model;
    private TrayIconService? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _paths = new InboxPaths();
        _paths.EnsureCreated();

        var background = e.Args.Any(a => string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase));
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
            if (new OnboardingStateStore().NeedsOnboarding())
                new OnboardingWindow(model) { Owner = window }.Show();
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
                return;
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
