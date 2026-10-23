using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Services;

namespace WeChatBridge.Windows.Panes;

/// <summary>
/// The skills pane: one card per skill in the app-owned library — the port of
/// macOS SkillsPane with the agent-install machinery removed. WeChatBridge
/// manages skills itself; scenes reference them via <c>{{skill:id}}</c> and
/// the prompt hands the agent the library path. This class only routes
/// clicks, owns the search chrome, and shows the import/remove flows.
/// </summary>
public partial class SkillsPane : UserControl
{
    private readonly SkillService _service = new();
    private bool _viewReady;

    public SkillsPane()
    {
        InitializeComponent();
        Root.DataContext = _service;
        _service.Rows.CollectionChanged += OnRowsChanged;
        Loaded += (_, _) => RefreshView();
        Unloaded += (_, _) => _viewReady = false;
    }

    /// <summary>The service the pane binds to — exposed for the integrator.</summary>
    public SkillService Service => _service;

    /// <summary>用于 N 个场景 was clicked: the host shows the scenes referencing this skill id.</summary>
    public event Action<string>? ScenesRequested;

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshView();

    private void RefreshView()
    {
        if (!IsLoaded)
            return;
        _viewReady = true;
        ApplyFilter();
        if (_service.LibraryIssue is { } issue)
            ShowNotice(issue, warning: true);
    }

    private void ApplyFilter()
    {
        if (!_viewReady)
            return;
        var needle = SearchBox.Text.Trim();
        var visible = _service.Rows.Where(r => r.MatchesQuery(needle)).ToList();
        SkillList.ItemsSource = visible;
        SkillList.Visibility = visible.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    // MARK: - Row actions

    private static SkillRow? CardOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as SkillRow;

    private void SceneBadge_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { HasScenes: true } row)
            ScenesRequested?.Invoke(row.Skill.Id);
    }

    private void ToggleDetails_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } row)
            row.IsExpanded = !row.IsExpanded;
    }

    /// <summary>导入 ZIP… — the user picks one skill package archive.</summary>
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L10n.Text("导入技能包"),
            Filter = L10n.Text("ZIP 压缩包 (*.zip)|*.zip"),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        _ = ImportArchive(dialog.FileName);
    }

    private async Task ImportArchive(string path)
    {
        try
        {
            var info = await _service.ImportArchiveAsync(path);
            ShowNotice(
                L10n.Format($"已导入技能「{info.DisplayName}」，场景提示词里可用 {{{{skill:{info.Id}}}}} 引用。"),
                warning: false);
        }
        catch (Exception error) when (error is SkillException or IOException
            or UnauthorizedAccessException or InvalidDataException)
        {
            ShowNotice(error.Message, warning: true);
        }
        ApplyFilter();
    }

    /// <summary>从技能库移除 — imported skills only; the package moves to the backups.</summary>
    private void RemoveSkill_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } row)
            return;
        var scenes = row.SceneCount > 0
            ? L10n.Format($"\n仍有 {row.SceneCount} 个场景引用它，移除后这些场景的提示词将找不到技能文件。")
            : "";
        var answer = MessageBox.Show(
            Window.GetWindow(this),
            L10n.Format($"技能包会先移入备份目录，不会直接删除。{scenes}"),
            L10n.Format($"移除技能「{row.Skill.Name}」？"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (answer == MessageBoxResult.OK)
            _ = RemoveSkill(row);
    }

    private async Task RemoveSkill(SkillRow row)
    {
        try
        {
            await _service.RemoveSkillAsync(row);
            ShowNotice(L10n.Text("技能已从技能库移除，原包在备份目录中可恢复。"), warning: false);
        }
        catch (Exception error)
        {
            ShowNotice(error.Message, warning: true);
        }
        ApplyFilter();
    }

    private void ShowNotice(string message, bool warning)
    {
        NoticeText.Text = message;
        NoticeBar.SetResourceReference(
            BackgroundProperty, warning ? "WarnFillColor" : "LiveFillColor");
        NoticeText.SetResourceReference(
            TextBlock.ForegroundProperty, warning ? "WarnInkColor" : "LiveInkColor");
        NoticeBar.Visibility = Visibility.Visible;
    }
}
