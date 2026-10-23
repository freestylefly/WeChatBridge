using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Components;

namespace WeChatBridge.Windows;

public partial class CollectionDetailsWindow : Window
{
    private readonly MainViewModel _model;
    private Guid _id;
    private bool _refreshing;
    private int _revision;
    private bool _positioned;
    private bool _hasLoadedMetadata;
    private bool _followingLatest = true;
    private Guid? _referenceId;
    private List<CollectionBatchRow> _rows = [];
    public sealed class CollectionBatchRow : System.ComponentModel.INotifyPropertyChanged
    {
        public required ReadyBatch Batch { get; init; }
        public string ChatName { get; set; } = "";
        public bool IsDefault { get; set; }
        public string Title { get; init; } = "";
        public string ReferenceTitle { get; init; } = "";
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
        }
        private string _boundaries = L10n.Text("正在读取…");
        private CollectionBatchMetadata? _metadata;
        public CollectionBatchMetadata? Metadata => _metadata;
        public string Range => _metadata?.First is { } first && _metadata.Last is { } last
            ? $"{first.Date.LocalDateTime:yyyy-MM-dd HH:mm} — {last.Date.LocalDateTime:yyyy-MM-dd HH:mm}" : L10n.Text("条数暂不可用");
        public IReadOnlyList<ReferenceRow> References
        {
            get
            {
                var first = _metadata?.FirstRecords ?? [];
                return first.Select(r => new ReferenceRow(r, L10n.Text("较早")))
                    .Concat((_metadata?.LastRecords ?? []).Where(r => !first.Contains(r))
                        .Select(r => new ReferenceRow(r, L10n.Text("较晚")))).ToList();
            }
        }
        public string ReferenceNotice => _metadata is null ? L10n.Text("正在读取…")
            : References.Count == 0 ? L10n.Text("无法读取边界，原始 ZIP 已保留。") : "";
        public IReadOnlyList<ReferenceRow> BoundaryReferences => _metadata?.First is { } first
            ? first == _metadata.Last ? [new(first, L10n.Text("首条"))]
                : [new(first, L10n.Text("首条")), new(_metadata.Last!, L10n.Text("末条"))]
            : [];
        public IReadOnlyList<ReferenceRow> AdditionalReferences => References
            .Where(r => r.Record != _metadata?.First && r.Record != _metadata?.Last).ToList();
        public bool HasAdditionalReferences => AdditionalReferences.Count > 0;
        public string FirstPreview => Preview(_metadata?.First);
        public string LastPreview => Preview(_metadata?.Last);
        private string Preview(WeChatTranscriptRecord? record) => _metadata is null ? L10n.Text("正在读取…")
            : record is null ? L10n.Text("无法读取边界，原始 ZIP 已保留。")
            : $"{record.Date.LocalDateTime:MM-dd HH:mm} · {record.Sender} · {string.Join(" ", record.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))}";
        public void SetMetadata(CollectionBatchMetadata metadata)
        {
            _metadata = metadata;
            PropertyChanged?.Invoke(this, new(nameof(FirstPreview)));
            PropertyChanged?.Invoke(this, new(nameof(LastPreview)));
            PropertyChanged?.Invoke(this, new(nameof(Range)));
            PropertyChanged?.Invoke(this, new(nameof(References)));
            PropertyChanged?.Invoke(this, new(nameof(ReferenceNotice)));
            PropertyChanged?.Invoke(this, new(nameof(BoundaryReferences)));
            PropertyChanged?.Invoke(this, new(nameof(AdditionalReferences)));
            PropertyChanged?.Invoke(this, new(nameof(HasAdditionalReferences)));
        }
        public string Boundaries
        {
            get => _boundaries;
            set { _boundaries = value; PropertyChanged?.Invoke(this, new(nameof(Boundaries))); }
        }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
    public sealed record ReferenceRow(WeChatTranscriptRecord Record, string Label)
    {
        public string Text => $"{Label} · {Record.Date.LocalDateTime:yyyy-MM-dd HH:mm} · {Record.Sender}\n{Record.Text}";
        public string SenderTime => $"{Record.Sender} · {Record.Date.LocalDateTime:MM-dd HH:mm}";
        public string Content => Record.Text;
    }

    public CollectionDetailsWindow(MainViewModel model, Guid id)
    {
        _model = model;
        _id = id;
        InitializeComponent();
        _model.CollectionsChanged += Refresh;
        Loaded += (_, _) =>
        {
            SetManagement(true);
            Refresh();
            if (IsVisible) Dispatcher.BeginInvoke(PositionAtCorner, DispatcherPriority.Loaded);
        };
        SizeChanged += (_, _) => { if (IsVisible && _positioned) KeepOnScreen(); };
        Closed += (_, _) => _model.CollectionsChanged -= Refresh;
    }

    public void SelectCollection(Guid id)
    {
        if (_id != id) { _id = id; _followingLatest = true; _referenceId = null; }
        Refresh();
    }

    private async void Refresh()
    {
        if (!Dispatcher.CheckAccess()) { if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.BeginInvoke(Refresh); return; }
        if (!IsInitialized || _refreshing) return;
        _refreshing = true;
        var revision = ++_revision;
        var group = _model.Collections.Ledger.Collections.FirstOrDefault(c => c.Id == _id);
        var busy = group?.Status == CollectionStatus.Delivering;
        ManagementPanel.IsEnabled = !busy;
        DeliverButton.IsEnabled = group?.BatchIDs.Count > 0 && !busy;
        MoreButton.IsEnabled = !busy;
        ResumeButton.IsEnabled = group?.BatchIDs.Count > 0 && !busy && group?.Status != CollectionStatus.Collecting;
        ResumeButton.Visibility = group?.Status is CollectionStatus.Draft or CollectionStatus.Retry ? Visibility.Visible : Visibility.Collapsed;
        UndoButton.IsEnabled = !busy && _model.Collections.CanUndo;
        UndoButton.Visibility = _model.Collections.CanUndo ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.IsEnabled = !busy;
        var batches = _model.Batches.ToDictionary(b => b.Id);
        var focused = NameEditor.Visibility == Visibility.Visible ? NameEditor.DataContext as CollectionBatchRow : null;
        _rows = (group?.BatchIDs ?? []).Where(batches.ContainsKey).Select((id, index) => new CollectionBatchRow
        {
            Batch = batches[id], ChatName = focused?.Batch.Id == id ? focused.ChatName : batches[id].ChatName ?? "",
            IsDefault = focused?.Batch.Id == id ? focused.IsDefault : group?.DefaultChatName is { Length: > 0 } defaultName && batches[id].ChatName == defaultName,
            Title = L10n.Format($"第 {index + 1} 批 · {batches[id].Items.Count} 个原始文件 · {ByteText.Format(batches[id].ByteCount)}"),
            ReferenceTitle = L10n.Format($"第 {index + 1} 批"),
        }).ToList();
        ReferenceSelector.ItemsSource = _rows;
        ReferenceSelector.Visibility = _rows.Count > 5 ? Visibility.Visible : Visibility.Collapsed;
        ReferenceTabs.ItemsSource = _rows;
        ReferenceTabs.Visibility = _rows.Count <= 5 ? Visibility.Visible : Visibility.Collapsed;
        var last = (_followingLatest ? null : _rows.FirstOrDefault(r => r.Batch.Id == _referenceId)) ?? _rows.LastOrDefault();
        ReferenceSelector.SelectedItem = last;
        SelectReference(last);
        var recent = _rows.LastOrDefault();
        RecentChat.Text = recent?.ChatName is { Length: > 0 } recentName ? recentName : L10n.Text("未命名聊天");
        RecentBatch.Text = recent?.ReferenceTitle ?? "";
        RecentPanel.Visibility = ManagementPanel.Visibility == Visibility.Visible || recent is null ? Visibility.Collapsed : Visibility.Visible;
        Status.Text = group is null ? L10n.Text("收集不存在。")
            : StatusName(group.Status);
        SummaryText.Text = L10n.Format($"{_rows.Count} 批 · {ByteText.Format(_rows.Sum(r => r.Batch.ByteCount))}");
        var unnamed = _rows.Count(r => string.IsNullOrWhiteSpace(r.ChatName));
        DeliverButton.Content = L10n.Text("选择去向…");
        Hint.Text = !string.IsNullOrWhiteSpace(group?.Detail) ? group.Detail
            : unnamed > 0 ? L10n.Text("文件已保存，可直接交付；会话备注可稍后补充。")
            : group?.Status == CollectionStatus.Draft ? L10n.Text("文件已保存，随时可选择去向。")
            : L10n.Text("继续从微信分享，下一批会追加到这里。");
        _refreshing = false;
        try
        {
            var rows = _rows;
            var metadata = await Task.Run(() => rows.Select(r => CollectionBatchMetadata.Read(r.Batch.Items.Select(i => i.FullPath))).ToList());
            if (Dispatcher.HasShutdownStarted) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (revision != _revision || !IsLoaded) return;
                for (var i = 0; i < rows.Count; i++)
                {
                    var info = metadata[i];
                    static string Describe(WeChatTranscriptRecord? record) => record is null ? L10n.Text("无法读取边界，原始 ZIP 已保留。")
                        : $"{record.Date.LocalDateTime:yyyy-MM-dd HH:mm} · {record.Sender}\n{record.Text}";
                    rows[i].Boundaries = L10n.Format($"消息数：{info.Count?.ToString() ?? L10n.Text("未知")}\n\n首条：{Describe(info.First)}\n\n末条：{Describe(info.Last)}");
                    rows[i].SetMetadata(info);
                }
                if (!_hasLoadedMetadata) { _hasLoadedMetadata = true; BatchScroll.ScrollToTop(); }
                if (group is not null) SummaryText.Text = CollectionSummary.Format(metadata, ByteText.Format(rows.Sum(r => r.Batch.ByteCount)));
                LastRange.Text = rows.LastOrDefault()?.Metadata?.First is not null
                    ? rows[^1].Range : L10n.Text("原始文件已保存");
            });
        }
        catch (Exception error)
        {
            if (!Dispatcher.HasShutdownStarted)
                _ = Dispatcher.BeginInvoke(() => { if (IsLoaded) Message.Text = error.Message; });
        }
    }

    private void PositionAtCorner()
    {
        if (_positioned) return;
        _positioned = true;
        var area = CollectionPlacement.WorkArea(this, atCursor: true);
        Width = Math.Min(490, Math.Max(320, area.Width - 24));
        MaxHeight = area.Height - 24;
        Left = CollectionPlacement.CursorOnRight(this, area) ? area.Left + 12 : area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 24;
        KeepOnScreen();
    }
    private void KeepOnScreen()
    {
        var area = CollectionPlacement.WorkArea(this);
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - ActualWidth));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
    }
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // The close button shares the drag strip but must retain its normal click.
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is Button) return;
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void Close_Click(object sender, RoutedEventArgs e) { CancelName_Click(sender, e); Keyboard.ClearFocus(); Close(); }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && NameEditor.Visibility == Visibility.Visible)
        { CancelName_Click(sender, e); Message.Text = ""; e.Handled = true; }
        else if (e.Key == Key.Enter && NameEditor.Visibility == Visibility.Visible)
        { SaveName_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape && CloseButton.IsEnabled) { Close_Click(sender, e); e.Handled = true; }
    }
    private void Manage_Click(object sender, RoutedEventArgs e) => SetManagement(ManagementPanel.Visibility != Visibility.Visible);
    private void SetManagement(bool expanded)
    {
        ManagementPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        RecentPanel.Visibility = expanded || _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ManageButton.Content = L10n.Text(expanded ? "收起详情" : "批次详情");
        var area = CollectionPlacement.WorkArea(this);
        BatchScroll.MaxHeight = Math.Min(360, Math.Max(80, area.Height - 340));
        if (!expanded) CancelName_Click(this, new RoutedEventArgs());
        if (IsVisible) Dispatcher.BeginInvoke(KeepOnScreen, DispatcherPriority.Loaded);
    }

    private static string StatusName(CollectionStatus status) => status switch
    {
        CollectionStatus.Collecting => L10n.Text("收集中"), CollectionStatus.Draft => L10n.Text("待发送"),
        CollectionStatus.Delivering => L10n.Text("正在交付"), CollectionStatus.Delivered => L10n.Text("已交付"),
        _ => L10n.Text("可重试"),
    };
    private void Try(Action action)
    {
        try { action(); Message.Text = ""; _model.Reload(); }
        catch (Exception error)
        {
            System.Diagnostics.Trace.WriteLine(error);
            Message.Text = error is UnauthorizedAccessException or IOException
                ? L10n.Text("暂时无法访问收集文件，请检查文件权限或占用后重试。原始文件已保留。") : error.Message;
        }
    }
    private void Resume_Click(object sender, RoutedEventArgs e) => Try(() =>
    {
        _model.Collections.Ledger.Resume(_id); _model.Collections.Save();
        _model.ShowToast(L10n.Text("后续分批分享将追加到这组收集；其他当前收集已保存为待发送。"));
    });
    private void Undo_Click(object sender, RoutedEventArgs e) => Try(() => _model.Collections.Undo());
    private async void Recognize_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CollectionBatchRow row) return;
        var id = _id;
        Message.Text = L10n.Text("正在识别当前微信会话…");
        var title = await Services.WeChatUiTitleReader.ReadWithFallbackAsync();
        if (NameEditor.Visibility != Visibility.Visible) return;
        if (title is null) { Message.Text = L10n.Text("未能识别，请手动填写会话名称。"); return; }
        if (_id != id || NameEditor.DataContext is not CollectionBatchRow current || current.Batch.Id != row.Batch.Id) return;
        current.ChatName = title.Value.Name;
        ChatNameInput.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        Message.Text = L10n.Text("已识别，请确认名称后保存。");
    }
    private void Reference_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || ReferenceSelector.SelectedItem is not CollectionBatchRow row) return;
        _followingLatest = row == _rows.LastOrDefault();
        _referenceId = row.Batch.Id;
        CancelName_Click(this, new RoutedEventArgs());
        SelectReference(row);
    }
    private void SelectReference(CollectionBatchRow? row)
    {
        ReferenceMore.IsExpanded = false;
        foreach (var batch in _rows) batch.IsSelected = ReferenceEquals(batch, row);
        LatestReference.DataContext = NameEditor.DataContext = row;
        LatestChat.Text = row?.ChatName is { Length: > 0 } name ? name : L10n.Text("未命名聊天");
        LatestBatch.Text = row?.Title ?? "";
        RenameButton.IsEnabled = row is not null;
    }
    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (LatestReference.DataContext is not CollectionBatchRow row) return;
        _followingLatest = false;
        _referenceId = row.Batch.Id;
        NameEditor.Visibility = Visibility.Visible;
        ChatNameInput.Focus();
        ChatNameInput.SelectAll();
    }
    private void CancelName_Click(object sender, RoutedEventArgs e)
    {
        if (NameEditor.DataContext is CollectionBatchRow row)
        {
            row.ChatName = _model.Batches.FirstOrDefault(b => b.Id == row.Batch.Id)?.ChatName ?? "";
            row.IsDefault = _model.Collections.Ledger.Collections.FirstOrDefault(c => c.Id == _id)?.DefaultChatName is { Length: > 0 } defaultName
                && row.ChatName == defaultName;
            ChatNameInput.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            DefaultNameCheck.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateTarget();
        }
        NameEditor.Visibility = Visibility.Collapsed;
        var group = _model.Collections.Ledger.Collections.FirstOrDefault(c => c.Id == _id);
        DeliverButton.IsEnabled = _rows.Count > 0 && group?.Status != CollectionStatus.Delivering;
    }
    private void SaveName_Click(object sender, RoutedEventArgs e)
    {
        if (NameEditor.DataContext is not CollectionBatchRow row) return;
        if (string.IsNullOrWhiteSpace(row.ChatName))
        {
            Message.Text = L10n.Text("请填写群或联系人名称。");
            ChatNameInput.Focus();
            return;
        }
        NameEditor.Visibility = Visibility.Collapsed;
        Try(() => _model.Collections.SetChatName(_id, row.Batch.Id, row.ChatName.Trim(), DefaultNameCheck.IsChecked == true));
    }
    private void BatchMore_Click(object sender, RoutedEventArgs e)
    {
        if (LatestReference.DataContext is not CollectionBatchRow row || sender is not Button button) return;
        var menu = new CollectionMenu { PlacementTarget = button };
        menu.Resources.MergedDictionaries.Add(Resources);
        var detach = new CollectionMenuItem { Header = L10n.Text("移出本次收集") };
        detach.Click += (_, _) => Try(() => _model.Collections.Detach(_id, row.Batch.Id));
        menu.Items.Add(detach);
        var remove = new CollectionMenuItem { Header = L10n.Text("删除（可撤销）"), Foreground = (Brush)FindResource("DangerColor") };
        remove.Click += (_, _) => Try(() =>
        {
            if (CollectionConfirmationWindow.Confirm(this, L10n.Text("删除至回收站"), L10n.Text("这一批原始文件将移到回收站，可撤销。")))
                _model.Collections.Remove(_id, row.Batch.Id);
        });
        menu.Items.Add(remove);
        menu.IsOpen = true;
    }
    private void ReferenceTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CollectionBatchRow row) ReferenceSelector.SelectedItem = row;
    }
    private async void CopyKeyword_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ReferenceRow row } button) return;
        try
        {
            Clipboard.SetText(string.Join(" ", row.Record.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
            button.Content = L10n.Text("已复制");
            await Task.Delay(1200);
            button.Content = L10n.Text("复制这条文字");
        }
        catch (Exception error) { Message.Text = error.Message; }
    }
    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new CollectionMenu();
        menu.Resources.MergedDictionaries.Add(Resources);
        void Add(string title, Action action)
        {
            var item = new CollectionMenuItem { Header = L10n.Text(title) };
            item.Click += (_, _) => Try(action);
            menu.Items.Add(item);
        }
        Add("保存为待发送", () =>
        {
            _model.Collections.Ledger.Editable(_id).Status = CollectionStatus.Draft;
            _model.Collections.Save();
        });
        Add("撤销", () => _model.Collections.Undo());
        Add("开始新的收集", () =>
        {
            _model.Collections.Ledger.ParkCurrent(); _model.Collections.Save();
            _model.ShowToast(L10n.Text("旧收集已保存为待发送，下次分批分享将建立新收集。"));
        });
        Add("删除整组收集", () =>
        {
            var group = _model.Collections.Ledger.Editable(_id);
            if (CollectionConfirmationWindow.Confirm(this, L10n.Text("删除整组收集"),
                L10n.Format($"将本组 {group.BatchIDs.Count} 批原始文件移到系统回收站，可撤销。")))
                _model.Collections.DeleteCollection(_id);
        });
        menu.PlacementTarget = MoreButton;
        menu.IsOpen = true;
    }
    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (LatestReference.DataContext is CollectionBatchRow row) _model.Reveal(row.Batch);
    }
    private void Deliver_Click(object sender, RoutedEventArgs e)
    {
        if (NameEditor.Visibility == Visibility.Visible && NameEditor.DataContext is CollectionBatchRow edited && !string.IsNullOrWhiteSpace(edited.ChatName))
        {
            SaveName_Click(sender, e);
            if (!string.IsNullOrEmpty(Message.Text)) return;
        }
        else CancelName_Click(sender, e);
        _model.ShowCollectionDelivery(_id);
    }
}
