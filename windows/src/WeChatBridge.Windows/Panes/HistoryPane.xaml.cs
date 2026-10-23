using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Panes;

/// <summary>
/// Every batch still on disk, newest first — the port of macOS HistoryPane.
/// A batch stays here after it has been forwarded, because "did that actually
/// arrive?" is the question the record answers.
/// </summary>
public partial class HistoryPane : UserControl
{
    public HistoryPane()
    {
        InitializeComponent();
        Loaded += (_, _) => HookModel();
        DataContextChanged += (_, _) => HookModel();
    }

    private MainViewModel? Model => DataContext as MainViewModel;
    private MainViewModel? _hooked;

    private void HookModel()
    {
        if (ReferenceEquals(_hooked, Model))
        {
            UpdateEmptyState();
            return;
        }
        if (_hooked is not null)
        {
            _hooked.Groups.CollectionChanged -= OnGroupsChanged;
            _hooked.PropertyChanged -= OnModelChanged;
        }
        _hooked = Model;
        if (_hooked is not null)
        {
            _hooked.Groups.CollectionChanged += OnGroupsChanged;
            _hooked.PropertyChanged += OnModelChanged;
        }
        UpdateEmptyState();
    }

    private void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.HasHistory) or nameof(MainViewModel.SummaryText) or nameof(MainViewModel.CollectionRows))
            UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        if (!IsLoaded || Model is null)
            return;
        var searching = Model.Query.Trim().Length > 0;
        var any = Model.Groups.Count > 0 || Model.CollectionRows.Count > 0;
        EmptyAll.Visibility = !any && !searching ? Visibility.Visible : Visibility.Collapsed;
        EmptyFilter.Visibility = !any && searching ? Visibility.Visible : Visibility.Collapsed;
        RecordList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = Model.HasHistory || Model.Collections.CanUndo;
    }

    private void OpenCollection_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MainViewModel.CollectionHistoryRow row) Model?.ShowCollection(row.Id);
    }

    private void CollectionDeliver_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MainViewModel.CollectionHistoryRow row) Model?.ShowCollectionDelivery(row.Id);
    }

    private void CollectionMore_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || (sender as FrameworkElement)?.DataContext is not MainViewModel.CollectionHistoryRow row) return;
        var menu = new ContextMenu();
        void Add(string title, Action action)
        {
            var item = new MenuItem { Header = L10n.Text(title) };
            item.Click += (_, _) =>
            {
                try { action(); model.Reload(); }
                catch (Exception error) { model.ShowToast(error.Message, warning: true); }
            };
            menu.Items.Add(item);
        }
        Add("保存为待发送", () => { model.Collections.Ledger.Editable(row.Id).Status = CollectionStatus.Draft; model.Collections.Save(); });
        Add("开始新的收集", () => { model.Collections.Ledger.ParkCurrent(); model.Collections.Save(); model.ShowToast(L10n.Text("旧收集已保存为待发送，下次分批分享将建立新收集。")); });
        Add("删除整组收集", () =>
        {
            var group = model.Collections.Ledger.Editable(row.Id);
            if (MessageBox.Show(Window.GetWindow(this), L10n.Format($"将本组 {group.BatchIDs.Count} 批原始文件移到系统回收站，可撤销。"),
                L10n.Text("删除整组收集"), MessageBoxButton.OKCancel) == MessageBoxResult.OK) model.Collections.DeleteCollection(row.Id);
        });
        Add("撤销", () => model.Collections.Undo());
        OpenMenu(sender, menu);
    }

    private static StackPanel? CollectionRenamePanel(object sender)
    {
        for (var element = sender as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is Border { Child: StackPanel card })
                foreach (var panel in card.Children.OfType<StackPanel>())
                    if (panel.Name == "CollectionRenamePanel") return panel;
        return null;
    }

    private void CollectionRename_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || (sender as FrameworkElement)?.DataContext is not MainViewModel.CollectionHistoryRow row
            || CollectionRenamePanel(sender) is not { } panel) return;
        var collection = model.Collections.Ledger.Collections.FirstOrDefault(c => c.Id == row.Id);
        if (collection is null || collection.Status == CollectionStatus.Delivering) return;
        var editor = panel.Children.OfType<TextBox>().Single();
        editor.Text = collection.Name;
        panel.Visibility = Visibility.Visible;
        editor.Focus();
        editor.SelectAll();
    }

    private void CollectionRenameSave_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || (sender as FrameworkElement)?.DataContext is not MainViewModel.CollectionHistoryRow row
            || CollectionRenamePanel(sender) is not { } panel) return;
        if (model.RenameCollection(row.Id, panel.Children.OfType<TextBox>().Single().Text))
            panel.Visibility = Visibility.Collapsed;
    }

    private void CollectionRenameCancel_Click(object sender, RoutedEventArgs e)
    {
        if (CollectionRenamePanel(sender) is { } panel) panel.Visibility = Visibility.Collapsed;
    }

    private static BatchRow? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as BatchRow;

    private void ToggleDetail_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            row.IsExpanded = !row.IsExpanded;
    }

    /// <summary>
    /// The whole card is the disclosure target — the chevron is just the
    /// affordance. Clicks that land inside the expanded file list (or on a
    /// button, which swallows them first) are theirs, not the toggle's.
    /// </summary>
    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border card || RowOf(sender) is not { } row)
            return;
        for (var el = e.OriginalSource as DependencyObject; el is not null && el != card;
             el = VisualTreeHelper.GetParent(el))
            if (el is FrameworkElement fe && fe.Name == "DetailPanel")
                return;
        row.IsExpanded = !row.IsExpanded;
    }

    /// <summary>
    /// 发给… — one row per destination, flattened because a WPF menu inside a
    /// list row has no business nesting a submenu either.
    /// </summary>
    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || RowOf(sender) is not { } row)
            return;
        var menu = new ContextMenu();
        foreach (var destination in model.Destinations())
        {
            var item = new MenuItem { Header = L10n.Format($"发给 {destination.Title}") };
            var picked = destination;
            item.Click += (_, _) =>
            {
                // Delivery drives its own recording and reloads when it ends.
                _ = model.PerformForward(row.Batch, picked.Action, picked.Target);
            };
            menu.Items.Add(item);
        }
        if (model.TargetRows.Count == 0)
        {
            menu.Items.Add(new Separator());
            var add = new MenuItem { Header = L10n.Text("添加应用…") };
            add.Click += (_, _) => model.Navigate(AppTab.Entries);
            menu.Items.Add(add);
        }
        OpenMenu(sender, menu);
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || RowOf(sender) is not { } row)
            return;
        var menu = new ContextMenu();

        var copy = new MenuItem { Header = L10n.Text("复制到剪贴板") };
        copy.Click += (_, _) => model.CopyBatchToClipboard(row.Batch);
        menu.Items.Add(copy);

        // macOS exposes resend only through the original share's own intent;
        // the row menu is where Windows keeps the same gesture reachable.
        var resend = new MenuItem { Header = L10n.Text("重新发送") };
        resend.Click += (_, _) => _ = model.Resend(row.Batch);
        menu.Items.Add(resend);

        menu.Items.Add(new Separator());

        var reveal = new MenuItem { Header = L10n.Text("在文件夹中显示") };
        reveal.Click += (_, _) => model.Reveal(row.Batch);
        menu.Items.Add(reveal);

        menu.Items.Add(new Separator());

        var discard = new MenuItem { Header = L10n.Text("移到回收站") };
        discard.Click += (_, _) => model.Discard(row.Batch);
        menu.Items.Add(discard);

        OpenMenu(sender, menu);
    }

    /// <summary>
    /// The header 「···」— macOS SettingsMoreActionsButton — holds what was a
    /// standalone button: 清空记录…, plus the field's own affordance.
    /// </summary>
    private void HeaderMore_Click(object sender, RoutedEventArgs e)
    {
        if (Model is null)
            return;
        var menu = new ContextMenu();
        var clear = new MenuItem { Header = L10n.Text("清空记录…"), IsEnabled = Model.HasHistory };
        clear.Click += Clear_Click;
        menu.Items.Add(clear);
        if (Model.Collections.CanUndo)
        {
            var undo = new MenuItem { Header = L10n.Text("撤销收集删除或移出") };
            undo.Click += (_, _) =>
            {
                try { Model.Collections.Undo(); Model.Reload(); }
                catch (Exception error) { Model.ShowToast(error.Message, warning: true); }
            };
            menu.Items.Add(undo);
        }
        OpenMenu(sender, menu);
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private static void OpenMenu(object sender, ContextMenu menu)
    {
        if (sender is not FrameworkElement anchor)
            return;
        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void RevealItem_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { } model && (sender as FrameworkElement)?.DataContext is ReadyItem item)
            model.RevealItem(item);
    }

    private void RevealInbox_Click(object sender, RoutedEventArgs e) => Model?.RevealInbox();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || !model.HasHistory)
            return;
        var message =
            L10n.Format($"将 {model.Batches.Count} 条记录（{ByteText.Format(model.Batches.Sum(b => b.ByteCount))}）移到回收站，可从回收站恢复。");
        var answer = MessageBox.Show(
            Window.GetWindow(this),
            message,
            L10n.Text("清空全部记录？"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (answer == MessageBoxResult.OK)
            model.DiscardAll();
    }
}
