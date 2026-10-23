using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Onboarding;

public partial class OnboardingWindow : Window
{
    private readonly MainViewModel _model;
    private readonly OnboardingStateStore _store;
    private readonly Func<Task<bool>> _registrationProbe;
    private readonly List<WizardEntry> _entries;
    private int _step;
    private bool _registered;
    private bool _checking;
    public int CurrentStep => _step;
    public OnboardingWindow(MainViewModel model, OnboardingStateStore? store = null, Func<Task<bool>>? registrationProbe = null)
    {
        _model = model;
        _store = store ?? new OnboardingStateStore();
        _registrationProbe = registrationProbe ?? ShareRegistrationProbe.CheckAsync;
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 32);
        _model.RebuildEntries();
        _entries = _model.Entries.Where(row => row.Action != ShareAction.Custom)
            .Select(row => new WizardEntry(row, _model, RefreshSummary)).ToList();
        EntriesList.ItemsSource = _entries;
        StartupToggle.IsChecked = _model.AutoStartEnabled;
        ShowStep(Math.Clamp(_store.Load().Step, 0, 3));
        Loaded += async (_, _) => await CheckRegistrationAsync();
    }
    private void ShowStep(int step)
    {
        _step = Math.Clamp(step, 0, 3);
        var pages = new UIElement[] { StepWelcome, StepEntries, StepResidency, StepDone };
        for (var i = 0; i < pages.Length; i++) pages[i].Visibility = i == _step ? Visibility.Visible : Visibility.Collapsed;
        StepList.SelectedIndex = _step;
        StepCount.Text = L10n.Format($"第 {_step + 1} 步，共 4 步");
        BackButton.Visibility = _step > 0 ? Visibility.Visible : Visibility.Hidden;
        NextButton.Content = L10n.Text(_step switch { 0 => "开始设置", 3 => "完成设置", _ => "下一步" });
        PageScroll.ScrollToTop();
        RefreshSummary();
        _store.SaveStep(_step);
    }
    private async Task CheckRegistrationAsync()
    {
        if (_checking) return;
        _checking = true;
        RefreshButton.IsEnabled = false;
        RegistrationTitle.Text = L10n.Text("正在检查分享入口…");
        try { _registered = await _registrationProbe(); } catch { _registered = false; }
        finally { _checking = false; RefreshButton.IsEnabled = true; }
        RegistrationTitle.Text = L10n.Text(_registered ? "Windows 分享入口已注册" : "未检测到 Windows 分享入口");
        RegistrationDetail.Text = L10n.Text(_registered ? "请重启微信，在「转发到其他应用」里确认「微信流」。"
            : "请使用完整安装包重新安装并允许管理员授权，也可以先完成目标配置。");
        RegistrationMark.Text = _registered ? "✓" : "!";
        RegistrationMark.Foreground = (System.Windows.Media.Brush)FindResource(_registered ? "BrandColor" : "WarnInkColor");
        RefreshSummary();
    }
    private void RefreshSummary()
    {
        if (SelectedSummary is null) return;
        var enabled = _entries.Where(row => row.Enabled).Select(row => row.Title).ToArray();
        SelectedSummary.Text = enabled.Length > 0 ? string.Join("、", enabled) : L10n.Text("尚未开启目标，可稍后在「入口」页设置。");
        SummaryStartup.Text = L10n.Text(_model.AutoStartEnabled ? "登录后在后台运行" : "需要时再启动");
        SummaryRegistration.Text = L10n.Text(_registered ? "已注册到 Windows，下一步请在微信中确认。" : "分享入口待检查；目标配置仍会保存。");
        SelectionCount.Text = L10n.Format($"已开启 {_entries.Count(row => row.Enabled)} 个目标");
    }
    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await CheckRegistrationAsync();
    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
    private void OnBackClicked(object sender, RoutedEventArgs e) => ShowStep(_step - 1);
    private void OnDragMove(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void OnNextClicked(object sender, RoutedEventArgs e)
    {
        if (_step < 3) { ShowStep(_step + 1); return; }
        foreach (var entry in _entries.Where(entry => !entry.Available)) _model.SetEntryEnabled(entry.Action, false);
        _store.MarkCompleted();
        Close();
    }
    private void OnStartupClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var requested = StartupToggle.IsChecked == true;
            _model.AutoStartEnabled = requested;
            StartupToggle.IsChecked = _model.AutoStartEnabled;
            StartupFeedback.Text = L10n.Text(requested != _model.AutoStartEnabled
                ? "系统未允许修改启动设置，请稍后在「通用」页重试。"
                : _model.AutoStartEnabled ? "已开启，登录 Windows 后自动在后台运行。" : "已关闭，需要时再启动微信流。");
        }
        catch
        {
            StartupToggle.IsChecked = _model.AutoStartEnabled;
            StartupFeedback.Text = L10n.Text("系统未允许修改启动设置，请稍后在「通用」页重试。");
        }
        RefreshSummary();
    }
    public sealed class WizardEntry : INotifyPropertyChanged
    {
        private readonly MainViewModel _model;
        private readonly Action _changed;
        private bool _enabled;
        public ShareAction Action { get; }
        public string Title { get; }
        public string Detail { get; }
        public bool Available { get; }
        public event PropertyChangedEventHandler? PropertyChanged;
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value || !Available) return;
                _model.SetEntryEnabled(Action, value);
                _enabled = _model.IsEntryEnabled(Action);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
                _changed();
            }
        }
        public WizardEntry(EntryRow row, MainViewModel model, Action changed)
        {
            _model = model; _changed = changed; Action = row.Action; Title = row.Title;
            // Installed-app gating arrives with the destination-aware entry catalog;
            // until then every built-in entry is offerable, matching the Entries pane.
            Available = true;
            _enabled = Available && row.IsEnabled;
            Detail = row.Detail;
        }
    }
}
