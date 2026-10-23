using WeChatBridge.Windows.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WeChatBridge.Windows.Panes;

namespace WeChatBridge.Windows;

/// <summary>
/// 微信流's only real window: a quiet column of navigation on the left, one
/// scrolling pane on the right — the port of the macOS settings window, with
/// 记录 as the landing pane because the window is the app's whole surface here.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>No action: long enough to read four words.</summary>
    private static readonly TimeSpan PlainToastDuration = TimeSpan.FromSeconds(1.8);
    /// <summary>Something to click has to outlive a glance.</summary>
    private static readonly TimeSpan ActionableToastDuration = TimeSpan.FromSeconds(8);
    /// <summary>Motion.toastIn — alpha plus a 6 px drop, ease-out.</summary>
    private static readonly TimeSpan ToastInDuration = TimeSpan.FromMilliseconds(160);
    /// <summary>Motion.toastOut — leaving is slower than arriving here only
    /// because the fade is the dismissal; nobody watches a window go.</summary>
    private static readonly TimeSpan ToastOutDuration = TimeSpan.FromMilliseconds(200);
    /// <summary>Metrics.toastDrop — the distance a fresh capsule falls.</summary>
    private const double ToastDrop = 6.0;

    private readonly MainViewModel _model;
    private readonly string? _requestedBatch;
    private readonly DispatcherTimer _toastTimer = new();
    private DateTimeOffset _toastDeadline;
    private TimeSpan _toastRemaining;
    private Action? _toastAction;
    private bool _toastHovering;
    private bool _batchSelected;
    /// <summary>Bumped on every show/hide so a replaced fade cannot retire a
    /// capsule that a newer toast is still using.</summary>
    private int _toastGeneration;

    private readonly Dictionary<AppTab, (string Title, string Caption, UIElement Pane)> _panes;

    public MainWindow(MainViewModel model, string? requestedBatch)
    {
        _model = model;
        _requestedBatch = requestedBatch;
        InitializeComponent();

        var scenesPane = new ScenesPane();
        var skillsPane = new SkillsPane();
        _panes = new Dictionary<AppTab, (string, string, UIElement)>
        {
            [AppTab.History] = (L10n.Text("记录"), L10n.Text("每一批转发的去向与结果"), new HistoryPane()),
            [AppTab.General] = (L10n.Text("通用"), L10n.Text("运行状态、启动项与保留策略"), new GeneralPane()),
            [AppTab.Entries] = (L10n.Text("入口"), L10n.Text("微信「转发到其他应用」里的可用操作"), new EntriesPane()),
            [AppTab.Scenes] = (L10n.Text("场景"), L10n.Text("按群聊绑定提示词与适用 Agent"), scenesPane),
            [AppTab.Skills] = (L10n.Text("技能中心"), L10n.Text("微信流统一管理的技能库"), skillsPane),
            [AppTab.About] = (L10n.Text("关于"), L10n.Text("版本信息与项目链接"), new AboutPane()),
        };
        scenesPane.Bind(model.Scenes, model.Skills);
        skillsPane.ScenesRequested += skillId =>
        {
            SelectTab(AppTab.Scenes);
            scenesPane.ShowScenesReferencing(skillId);
        };

        DataContext = _model;
        PaneHost.Content = _panes[AppTab.History].Pane;
        PaneCaption.Text = _panes[AppTab.History].Caption;
        NavList.SelectedItem = NavList.Items.OfType<ListBoxItem>()
            .FirstOrDefault(i => i.Tag as string == nameof(AppTab.History));

        _model.ToastRequested += ShowToast;
        _model.NavigationRequested += SelectTab;
        _toastTimer.Tick += (_, _) => { if (!_toastHovering) HideToast(); };

        Loaded += (_, _) =>
        {
            _model.RebuildEntries();
            RefreshBatches();
            SelectRequestedBatch();
        };

        // Close hides to the tray, mirroring the macOS menu-bar app — the
        // resident process must keep consuming shares. The tray's 退出 calls
        // Application.Shutdown, which proceeds despite this cancel.
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    public MainViewModel Model => _model;

    /// <summary>Called by App when the inbox changed or a second instance was launched.</summary>
    public void RefreshBatches() => _model.Reload();

    /// <summary>Second launch foregrounds the existing window.</summary>
    public void BringToFront()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        // Topmost flick: Activate alone cannot steal focus from another app.
        Topmost = true;
        Activate();
        Topmost = false;
        Focus();
    }

    private void SelectTab(AppTab tab)
    {
        if (!_panes.TryGetValue(tab, out var pane))
            return;
        PaneTitle.Text = pane.Title;
        PaneCaption.Text = pane.Caption;
        PaneHost.Content = pane.Pane;
        foreach (var item in NavList.Items.OfType<ListBoxItem>())
        {
            if (item.Tag is string tag && Enum.TryParse<AppTab>(tag, out var t) && t == tab)
            {
                NavList.SelectedItem = item;
                break;
            }
        }
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is ListBoxItem { Tag: string tag }
            && Enum.TryParse<AppTab>(tag, out var tab))
        {
            SelectTab(tab);
        }
    }

    /// <summary>The helper launches us with --batch-id: land the user on that row.</summary>
    private void SelectRequestedBatch()
    {
        if (_requestedBatch is null || _batchSelected)
            return;
        if (!Guid.TryParse(_requestedBatch, out var id))
            return;
        foreach (var row in _model.Groups.SelectMany(g => g.Rows))
        {
            if (row.Batch.Id == id)
            {
                row.IsExpanded = true;
                _batchSelected = true;
                return;
            }
        }
    }

    // MARK: - Toast

    /// <summary>
    /// The in-window port of Toast.swift: a capsule in the top-right corner.
    /// Nothing here reports success for forwards — but a desktop window the user
    /// may not be watching still needs somewhere to say a share never arrived.
    /// </summary>
    private void ShowToast(string message, string? actionTitle, Action? action, bool warning)
    {
        _toastTimer.Stop();
        // Retire any fade-out still in flight before this capsule re-enters.
        _toastGeneration++;
        ToastText.Text = message;
        ToastText.Foreground = warning
            ? (Brush)FindResource("WarningColor")
            : (Brush)FindResource("OnBrandColor");
        _toastAction = action;
        ToastAction.Content = actionTitle;
        ToastAction.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;

        // A replacement toast only regains opacity where it stands — replaying
        // the drop for every file in a burst would read as flicker. A capsule
        // that was away falls the last 6 px into place while it fades in.
        var wasVisible = Toast.Visibility == Visibility.Visible;
        Toast.Visibility = Visibility.Visible;
        var easeIn = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var fadeIn = new DoubleAnimation
        {
            To = 1.0,
            Duration = new Duration(ToastInDuration),
            EasingFunction = easeIn,
        };
        var drop = new DoubleAnimation
        {
            To = 0.0,
            Duration = new Duration(ToastInDuration),
            EasingFunction = easeIn,
        };
        if (!wasVisible)
            drop.From = -ToastDrop;
        Toast.BeginAnimation(OpacityProperty, fadeIn);
        ToastShift.BeginAnimation(TranslateTransform.YProperty, drop);

        _toastHovering = false;
        _toastRemaining = action is null ? PlainToastDuration : ActionableToastDuration;
        ResumeToastTimer();
    }

    private void ResumeToastTimer()
    {
        if (_toastHovering || _toastRemaining <= TimeSpan.Zero)
            return;
        _toastDeadline = DateTimeOffset.UtcNow + _toastRemaining;
        _toastTimer.Interval = _toastRemaining;
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        _toastAction = null;
        if (Toast.Visibility != Visibility.Visible)
            return;
        var generation = ++_toastGeneration;
        var easeOut = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var fadeOut = new DoubleAnimation
        {
            To = 0.0,
            Duration = new Duration(ToastOutDuration),
            EasingFunction = easeOut,
        };
        var lift = new DoubleAnimation
        {
            To = -ToastDrop,
            Duration = new Duration(ToastOutDuration),
            EasingFunction = easeOut,
        };
        fadeOut.Completed += (_, _) =>
        {
            // A toast raised during the fade has already turned the alpha back
            // up; hiding here would bury a message nobody has read.
            if (generation == _toastGeneration)
                Toast.Visibility = Visibility.Collapsed;
        };
        Toast.BeginAnimation(OpacityProperty, fadeOut);
        ToastShift.BeginAnimation(TranslateTransform.YProperty, lift);
    }

    /// <summary>The countdown stops while the pointer is on the toast.</summary>
    private void Toast_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _toastHovering = true;
        _toastTimer.Stop();
        _toastRemaining = _toastDeadline - DateTimeOffset.UtcNow;
    }

    private void Toast_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _toastHovering = false;
        ResumeToastTimer();
    }

    private void ToastAction_Click(object sender, RoutedEventArgs e)
    {
        var action = _toastAction;
        HideToast();
        action?.Invoke();
    }
}
