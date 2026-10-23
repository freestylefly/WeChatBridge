using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Components;

namespace WeChatBridge.Windows;

public partial class CollectionDeliveryWindow : Window
{
    internal sealed record Selection(ShareAction Action, ForwardTarget? Target, WeChatScene? Scene, string? Folder);
    private sealed record TargetOption(ShareAction Action, ForwardTarget? Target)
    {
        public string Title => Action switch
        {
            ShareAction.Clipboard => L10n.Text("复制文件"),
            ShareAction.Folder => L10n.Text("保存到文件夹"),
            _ => Target?.DisplayName ?? Action.TargetDisplayName(),
        };
        public string Key => Target?.BundleIdentifier ?? Action.RawValue();
        public object? Icon => Target is { } target ? AppLogos.IconFor(target) : AppLogos.PathFor(Action);
        public string Badge => Action switch
        {
            ShareAction.Clipboard => "ZIP",
            ShareAction.Folder => "↓",
            _ => Title.Length == 0 ? "A" : Title[..1].ToUpperInvariant(),
        };
        public string Detail => L10n.Text(Action switch
        {
            ShareAction.Clipboard => "复制原始文件，稍后手动粘贴",
            ShareAction.Folder => "保留原始 ZIP，保存到你选择的文件夹",
            ShareAction.Obsidian => "整理为笔记，写入已配置的知识库",
            _ => "打开应用并附加原始文件",
        });
    }
    private sealed record SceneOption(WeChatScene? Scene)
    {
        public string Title => Scene?.Name ?? L10n.Text("直接交付，不附加场景");
    }
    private readonly MainViewModel _model;
    private IReadOnlyList<ReadyBatch> _batches = [];
    private readonly Guid _id;
    private int _revision;
    private bool _busy;
    internal CollectionFolderWindow? FolderWindow { get; private set; }
    private readonly TaskCompletionSource<Selection?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal CollectionDeliveryWindow(MainViewModel model, Guid id)
    {
        _model = model;
        _id = id;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/WeChatBridge.Windows;component/Themes/AppTheme.xaml") });
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height - 30;
        RefreshSummary();
        var targets = model.Destinations().Append(new ForwardDestination { Action = ShareAction.Clipboard })
            .Where(d => model.IsEntryEnabled(d.Action) && model.IsDestinationInstalled(d.Action, d.Target))
            .Select(d => new TargetOption(d.Action, d.Target)).ToList();
        Targets.ItemsSource = targets;
        Targets.SelectedItem = targets.FirstOrDefault(t => t.Key == model.LastCollectionTarget);
        Target_Changed(this, new SelectionChangedEventArgs(SelectorEvent(), Array.Empty<object>(), Array.Empty<object>()));
        model.CollectionsChanged += RefreshSummary;
        Closed += (_, _) => { model.CollectionsChanged -= RefreshSummary; FolderWindow?.Close(); _completion.TrySetResult(null); };
        Loaded += (_, _) =>
        {
            var area = CollectionPlacement.WorkArea(this);
            MaxHeight = area.Height - 24;
            DeliveryScroll.MaxHeight = Math.Max(120, Math.Min(470, area.Height - 250));
            RefreshSummary();
        };
    }

    private async void RefreshSummary()
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(RefreshSummary); return; }
        var group = _model.Collections.Ledger.Collections.FirstOrDefault(c => c.Id == _id);
        var members = group?.BatchIDs.ToHashSet() ?? [];
        _batches = _model.Batches.Where(b => members.Contains(b.Id))
            .OrderBy(b => group!.BatchIDs.IndexOf(b.Id)).ToList();
        var revision = ++_revision;
        var batches = _batches;
        Summary.Text = L10n.Format($"{batches.Count} 批 · {ByteText.Format(batches.Sum(b => b.ByteCount))}");
        UpdateConfirm();
        try
        {
            var metadata = await Task.Run(() => batches.Select(b => CollectionBatchMetadata.Read(b.Items.Select(i => i.FullPath))).ToList());
            if (!Dispatcher.HasShutdownStarted) await Dispatcher.InvokeAsync(() =>
            {
                if (revision == _revision && IsVisible)
                    Summary.Text = CollectionSummary.Format(metadata, ByteText.Format(batches.Sum(b => b.ByteCount)));
            });
        }
        catch (Exception error) { if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.BeginInvoke(() => { if (IsVisible) Error.Text = error.Message; }); }
    }

    internal Task<Selection?> ChooseAsync(Window owner)
    {
        Owner = owner;
        Show();
        Activate();
        return _completion.Task;
    }

    private void Target_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Scenes is null || Confirm is null) return;
        Error.Text = "";
        var target = Targets.SelectedItem as TargetOption;
        var agent = target is null ? null : AgentIds.Matching(target.Action) ?? AgentIds.MatchingBundleId(target.Target?.BundleIdentifier);
        var enabled = target is not null && target.Action is not (ShareAction.Clipboard or ShareAction.Folder or ShareAction.Obsidian);
        var scenes = new List<SceneOption> { new(null) };
        if (enabled) scenes.AddRange(_model.Scenes.LoadSettings().EnabledScenes
            .Where(s => agent is null || s.CompatibleAgents.Contains(agent.Value)).Select(s => new SceneOption(s)));
        Scenes.ItemsSource = scenes;
        Scenes.IsEnabled = enabled;
        ScenePanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        DestinationHint.Text = L10n.Text(target?.Action switch
        {
            ShareAction.Clipboard => "复制后，前往目标应用手动粘贴文件。",
            ShareAction.Folder => "下一步选择文件夹，原始 ZIP 将完整保存。",
            ShareAction.Obsidian => "聊天记录将整理成笔记，原始 ZIP 仍会保留。",
            _ => "附加到应用后，请在目标应用确认附件。",
        });
        Scenes.SelectedItem = scenes.FirstOrDefault(s => s.Scene?.Id is { } id && id == _model.LastCollectionScene) ?? scenes[0];
        UpdateConfirm();
        if (target is not null) Targets.ScrollIntoView(target);
    }
    private void UpdateConfirm()
    {
        if (Confirm is null) return;
        var target = Targets.SelectedItem as TargetOption;
        Confirm.IsEnabled = target is not null && !_busy && _batches.Count > 0;
        Confirm.Content = target?.Action switch
        {
            ShareAction.Clipboard => L10n.Format($"复制 {_batches.Sum(b => b.Items.Count)} 个文件"),
            ShareAction.Folder => L10n.Format($"保存 {_batches.Sum(b => b.Items.Count)} 个文件"),
            null => L10n.Text("请选择交付目标"),
            _ => L10n.Format($"附加到 {target.Title}"),
        };
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = System.Windows.Media.VisualTreeHelper.GetParent(element))
            if (element is Button) return;
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || FolderWindow is not null || _batches.Count == 0 || Targets.SelectedItem is not TargetOption target) return;
        string? folder = null;
        if (target.Action == ShareAction.Folder)
        {
            var picker = new CollectionFolderWindow(_model.DeliveryFolderPath, _model.RecentCollectionFolders);
            FolderWindow = picker; Confirm.IsEnabled = Targets.IsEnabled = false;
            folder = await picker.ChooseAsync(this);
            FolderWindow = null;
            if (_completion.Task.IsCompleted) return;
            Targets.IsEnabled = true;
            if (folder is null) { UpdateConfirm(); return; }
            _model.RememberCollectionFolder(folder);
        }
        _busy = true;
        Confirm.IsEnabled = Targets.IsEnabled = Scenes.IsEnabled = false;
        try
        {
            if (target.Action is not (ShareAction.Clipboard or ShareAction.Folder or ShareAction.Obsidian))
            {
                var pending = _model.ResolveCollectionTarget(_batches[0], target.Action, target.Target);
                if (pending is null || await pending is null)
                    throw new InvalidOperationException(L10n.Format($"这台电脑上没有找到 {target.Title}，请选择其他目标。"));
            }
            if (_completion.Task.IsCompleted) return;
            var scene = target.Action is ShareAction.Clipboard or ShareAction.Folder or ShareAction.Obsidian
                ? null : (Scenes.SelectedItem as SceneOption)?.Scene;
            _model.RememberCollectionChoice(target.Action, target.Target, scene);
            _completion.TrySetResult(new(target.Action, target.Target, scene, folder));
            _busy = false;
            Close();
        }
        catch (Exception error)
        {
            Error.Text = error.Message;
            _busy = false;
            Targets.IsEnabled = true;
            Target_Changed(sender, new SelectionChangedEventArgs(SelectorEvent(), Array.Empty<object>(), Array.Empty<object>()));
            Error.Text = error.Message;
        }
    }
    private static RoutedEvent SelectorEvent() => System.Windows.Controls.Primitives.Selector.SelectionChangedEvent;
}
