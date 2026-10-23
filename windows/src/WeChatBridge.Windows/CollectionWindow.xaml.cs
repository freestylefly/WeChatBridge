using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WeChatBridge.Windows.Components;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows;

/// <summary>The passive collector stays compact; inspection and delivery have independent windows.</summary>
public partial class CollectionWindow : Window
{
    private readonly MainViewModel _model;
    private Guid _id;
    private int _revision;
    private bool _positioned;
    internal CollectionDetailsWindow? DetailsWindow { get; private set; }
    internal CollectionDeliveryWindow? DeliveryWindow { get; private set; }

    public CollectionWindow(MainViewModel model, Guid id)
    {
        _model = model; _id = id;
        InitializeComponent();
        model.CollectionsChanged += Refresh;
        Loaded += (_, _) => { Refresh(); Dispatcher.BeginInvoke(PositionAtCorner, DispatcherPriority.Loaded); };
        SizeChanged += (_, _) => { if (_positioned && IsVisible) KeepOnScreen(); };
        Closed += (_, _) => { model.CollectionsChanged -= Refresh; DetailsWindow?.Close(); DeliveryWindow?.Close(); };
    }
    public void SelectCollection(Guid id)
    {
        if (_id != id) { _id = id; DetailsWindow?.Close(); }
        Refresh();
    }
    private async void Refresh()
    {
        if (!Dispatcher.CheckAccess()) { if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.BeginInvoke(Refresh); return; }
        var revision = ++_revision;
        var group = _model.Collections.Ledger.Collections.FirstOrDefault(g => g.Id == _id);
        var members = group?.BatchIDs ?? [];
        var batches = members.Select(id => _model.Batches.FirstOrDefault(b => b.Id == id)).OfType<ReadyBatch>().ToList();
        var busy = group?.Status == CollectionStatus.Delivering;
        DeliverButton.IsEnabled = batches.Count > 0 && !busy && DeliveryWindow is null;
        MoreButton.IsEnabled = !busy && group is not null;
        ManageButton.IsEnabled = batches.Count > 0;
        ManageButton.Content = L10n.Format($"查看 {batches.Count} 批");
        ResumeButton.Visibility = group?.Status is CollectionStatus.Draft or CollectionStatus.Retry ? Visibility.Visible : Visibility.Collapsed;
        ResumeButton.IsEnabled = batches.Count > 0 && !busy;
        Status.Text = L10n.Text(group?.Status switch
        {
            CollectionStatus.Collecting => "收集中", CollectionStatus.Draft => "待发送", CollectionStatus.Delivering => "正在交付",
            CollectionStatus.Delivered => "已交付", CollectionStatus.Retry => "待重试", _ => "收集不存在。",
        });
        SummaryText.Text = L10n.Format($"{batches.Count} 批 · {ByteText.Format(batches.Sum(b => b.ByteCount))}");
        RecentPanel.Visibility = batches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentChat.Text = batches.LastOrDefault()?.ChatName is { Length: > 0 } name ? name : L10n.Text("未添加会话备注");
        RecentBatch.Text = L10n.Format($"第 {batches.Count} 批");
        Hint.Text = group?.Detail is { Length: > 0 } detail ? detail : L10n.Text("继续从微信分享，下一批会追加到这里。");
        if (_model.ImportProgress?.Phase == "saving")
            Hint.Text = L10n.Format($"正在保存第 {batches.Count + 1} 批…");
        else if (_model.ImportProgress?.Phase == "failed")
            Hint.Text = L10n.Text("本批保存失败，之前的批次已保留。请从微信重新分享这一批。");
        if (group is null && _model.ImportProgress?.Phase is "saving" or "failed")
            Status.Text = L10n.Text(_model.ImportProgress.Phase == "saving" ? "正在保存" : "保存失败");
        try
        {
            var metadata = await Task.Run(() => batches.Select(b => CollectionBatchMetadata.Read(b.Items.Select(i => i.FullPath))).ToList());
            if (Dispatcher.HasShutdownStarted) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (revision != _revision || !IsLoaded) return;
                SummaryText.Text = CollectionSummary.Format(metadata, ByteText.Format(batches.Sum(b => b.ByteCount)));
                LastRange.Text = metadata.LastOrDefault() is { First: { } first, Last: { } last }
                    ? $"{first.Date.LocalDateTime:MM-dd HH:mm} — {last.Date.LocalDateTime:MM-dd HH:mm}" : L10n.Text("原始文件已保存");
            });
        }
        catch (Exception error)
        {
            if (!Dispatcher.HasShutdownStarted)
                _ = Dispatcher.BeginInvoke(() => { if (revision == _revision && IsLoaded) Message.Text = error.Message; });
        }
    }

    private void PositionAtCorner()
    {
        if (_positioned) return;
        _positioned = true;
        var area = CollectionPlacement.WorkArea(this, atCursor: true);
        Width = Math.Min(400, Math.Max(320, area.Width - 24)); MaxHeight = area.Height - 24;
        Left = CollectionPlacement.CursorOnRight(this, area) ? area.Left + 12 : area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 24; KeepOnScreen();
    }
    private void KeepOnScreen()
    {
        var area = CollectionPlacement.WorkArea(this);
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - ActualWidth));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
    }
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is Button) return;
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void Close_Click(object sender, RoutedEventArgs e) { DetailsWindow?.Close(); DeliveryWindow?.Close(); Hide(); }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Escape) { Close_Click(sender, e); e.Handled = true; } }
    private void Manage_Click(object sender, RoutedEventArgs e)
    {
        if (DetailsWindow is { } existing) { existing.Activate(); return; }
        var details = new CollectionDetailsWindow(_model, _id) { Owner = this };
        DetailsWindow = details;
        details.Closed += (_, _) => { if (DetailsWindow == details) DetailsWindow = null; };
        details.Show(); details.Activate();
    }
    private void Try(Action action)
    {
        try { action(); Message.Text = ""; _model.Reload(); }
        catch (Exception error) { Message.Text = error is System.IO.IOException or UnauthorizedAccessException
            ? L10n.Text("暂时无法访问收集文件，请检查文件权限或占用后重试。原始文件已保留。") : error.Message; }
    }
    private void Resume_Click(object sender, RoutedEventArgs e) => Try(() => { _model.Collections.Ledger.Resume(_id); _model.Collections.Save(); });
    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new CollectionMenu { PlacementTarget = MoreButton };
        menu.Resources.MergedDictionaries.Add(Resources);
        void Add(string title, Action action, bool enabled = true)
        {
            var item = new CollectionMenuItem { Header = L10n.Text(title), IsEnabled = enabled };
            item.Click += (_, _) => Try(action); menu.Items.Add(item);
        }
        Add("保存为待发送", () => { _model.Collections.Ledger.Editable(_id).Status = CollectionStatus.Draft; _model.Collections.Save(); });
        Add("撤销", _model.Collections.Undo, _model.Collections.CanUndo);
        Add("开始新的收集", () => { _model.Collections.Ledger.ParkCurrent(); _model.Collections.Save(); });
        Add("删除整组收集", () =>
        {
            var group = _model.Collections.Ledger.Editable(_id);
            if (CollectionConfirmationWindow.Confirm(this, L10n.Text("删除整组收集"), L10n.Format($"将本组 {group.BatchIDs.Count} 批原始文件移到系统回收站，可撤销。")))
                _model.Collections.DeleteCollection(_id);
        });
        menu.IsOpen = true;
    }
    public void BeginDelivery() => Deliver_Click(this, new RoutedEventArgs());
    private async void Deliver_Click(object sender, RoutedEventArgs e)
    {
        if (DeliveryWindow is { } existing) { existing.Activate(); return; }
        var group = _model.Collections.Ledger.Collections.FirstOrDefault(g => g.Id == _id);
        if (group is null || group.BatchIDs.Count == 0 || group.Status == CollectionStatus.Delivering) return;
        var id = _id;
        try
        {
            var delivery = new CollectionDeliveryWindow(_model, id);
            DeliveryWindow = delivery; DeliverButton.IsEnabled = false;
            var selection = await delivery.ChooseAsync(this);
            DeliveryWindow = null;
            if (selection is null) { Refresh(); return; }
            if (_id == id) { DetailsWindow?.Close(); Hide(); }
            await _model.DeliverCollection(id, selection.Action, selection.Target, selection.Scene, selection.Folder);
            if (_id == id) { Refresh(); if (_model.Collections.Ledger.Editable(id).Status == CollectionStatus.Retry) Show(); }
        }
        catch (Exception error) { DeliveryWindow = null; Show(); Refresh(); Message.Text = error.Message; }
    }
}
