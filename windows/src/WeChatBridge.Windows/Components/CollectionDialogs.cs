using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Components;

public class CollectionDialogWindow : Window
{
    protected CollectionDialogWindow(string title)
    {
        Title = title; Width = 460; MinWidth = 320; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/WeChatBridge.Windows;component/Themes/AppTheme.xaml") });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/WeChatBridge.Windows;component/Components/CollectionComponents.xaml") });
        SetResourceReference(ForegroundProperty, "InkColor");
        Loaded += (_, _) => { var area = CollectionPlacement.WorkArea(this); MaxHeight = Math.Max(200, area.Height - 30); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }
    protected void SetBody(UIElement body)
    {
        var card = new CollectionCard { Content = body, Margin = new Thickness(14), Padding = new Thickness(22), CornerRadius = new CornerRadius(22) };
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 26, ShadowDepth = 4, Opacity = .14 };
        Content = card;
    }
    protected Grid Header(string text)
    {
        var header = new Grid { Margin = new Thickness(0, 0, 0, 16), Background = Brushes.Transparent };
        header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = text, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,30,0) });
        var close = new CollectionButton { Kind = CollectionButtonKind.Icon, Content = new System.Windows.Shapes.Path
            { Data = Geometry.Parse("M2,2 L12,12 M12,2 L2,12"), StrokeThickness = 1.4, Width = 14, Height = 14 } };
        ((System.Windows.Shapes.Path)close.Content).SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "InkSecondaryColor");
        System.Windows.Automation.AutomationProperties.SetName(close, L10n.Text("关闭"));
        close.Click += (_, _) => Close(); Grid.SetColumn(close, 1); header.Children.Add(close);
        header.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is TextBlock && e.LeftButton == MouseButtonState.Pressed) DragMove(); };
        return header;
    }
    protected static TextBlock Caption(string text) => new() { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) };
}

public sealed class CollectionConfirmationWindow : CollectionDialogWindow
{
    public CollectionConfirmationWindow(string title, string message) : base(title)
    {
        var body = new StackPanel(); body.Children.Add(Header(title));
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 21, Margin = new Thickness(0,0,0,20) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new CollectionButton { Content = L10n.Text("取消"), Kind = CollectionButtonKind.Ghost, Margin = new Thickness(0,0,8,0) };
        var confirm = new CollectionButton { Content = L10n.Text("删除至回收站"), Kind = CollectionButtonKind.Danger };
        cancel.Click += (_, _) => { DialogResult = false; }; confirm.Click += (_, _) => { DialogResult = true; };
        actions.Children.Add(cancel); actions.Children.Add(confirm); body.Children.Add(actions); SetBody(body);
        Loaded += (_, _) => cancel.Focus();
    }
    public static bool Confirm(Window owner, string title, string message)
        => new CollectionConfirmationWindow(title, message) { Owner = owner }.ShowDialog() == true;
}

public sealed class CollectionFolderWindow : CollectionDialogWindow
{
    public sealed record FolderRow(string Title, string Path);
    private readonly CollectionTextBox _path = new() { Name = "FolderPath", Placeholder = L10n.Text("输入文件夹完整路径") };
    private readonly CollectionTargetList _folders = new() { Name = "FolderList", DisplayMemberPath = nameof(FolderRow.Title), MaxHeight = 230 };
    private readonly TextBlock _error = Caption("");
    private readonly CollectionButton _confirm = new() { Content = L10n.Text("保存到这里"), Kind = CollectionButtonKind.Primary, IsEnabled = false, Name = "FolderConfirm" };
    private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _directory;
    private int _revision;
    public CollectionFolderWindow(string? initialPath, IReadOnlyList<string> recent) : base(L10n.Text("保存原始文件"))
    {
        var body = new StackPanel(); body.Children.Add(Header(Title));
        body.Children.Add(Caption(L10n.Text("选择文件夹，保留原始 ZIP 和媒体内容。")));
        if (recent.Count > 0)
        {
            var choices = new CollectionComboBox { ItemsSource = recent, Margin = new Thickness(0,0,0,12) };
            System.Windows.Automation.AutomationProperties.SetName(choices, L10n.Text("最近使用的文件夹"));
            choices.SelectionChanged += (_, _) => { if (choices.SelectedItem is string path) Navigate(path); };
            body.Children.Add(Caption(L10n.Text("最近使用的文件夹"))); body.Children.Add(choices);
        }
        body.Children.Add(_path);
        System.Windows.Automation.AutomationProperties.SetName(_path, L10n.Text("文件夹路径"));
        var navigation = new WrapPanel { Margin = new Thickness(0,5,0,8) };
        var go = new CollectionButton { Content = L10n.Text("打开路径"), Kind = CollectionButtonKind.Ghost };
        go.Click += (_, _) => Navigate(_path.Text);
        var up = new CollectionButton { Content = L10n.Text("上一级"), Kind = CollectionButtonKind.Ghost };
        up.Click += (_, _) => { if (_directory is { } path && Directory.GetParent(path) is { } parent) Navigate(parent.FullName); else ShowDrives(); };
        navigation.Children.Add(up); navigation.Children.Add(go); body.Children.Add(navigation);
        _path.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { Navigate(_path.Text); e.Handled = true; } };
        _path.TextChanged += (_, _) => _confirm.IsEnabled = _directory is not null && string.Equals(_path.Text, _directory, StringComparison.OrdinalIgnoreCase);
        _folders.MouseDoubleClick += (_, _) => OpenSelected();
        _folders.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; } };
        body.Children.Add(_folders);
        var open = new CollectionButton { Content = L10n.Text("进入选中文件夹"), Kind = CollectionButtonKind.Ghost, HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += (_, _) => OpenSelected(); body.Children.Add(open);
        _error.SetResourceReference(TextBlock.ForegroundProperty, "DangerColor"); body.Children.Add(_error);
        var actions = new Grid { Margin = new Thickness(0,14,0,0) }; actions.ColumnDefinitions.Add(new()); actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var cancel = new CollectionButton { Content = L10n.Text("取消"), Kind = CollectionButtonKind.Ghost, HorizontalAlignment = HorizontalAlignment.Left };
        cancel.Click += (_, _) => Close(); actions.Children.Add(cancel);
        _confirm.Click += (_, _) => { if (_directory is not null && _confirm.IsEnabled) { _result.TrySetResult(_directory); Close(); } };
        Grid.SetColumn(_confirm,1); actions.Children.Add(_confirm); body.Children.Add(actions); SetBody(body);
        Closed += (_, _) => { ++_revision; _result.TrySetResult(null); };
        var start = new[] { initialPath }.Concat(recent).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
            ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Loaded += (_, _) => Navigate(start);
    }
    private void OpenSelected() { if (_folders.SelectedItem is FolderRow row) Navigate(row.Path); }
    private void ShowDrives()
    {
        ++_revision; _directory = null; _confirm.IsEnabled = false;
        _folders.ItemsSource = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => new FolderRow(d.Name, d.RootDirectory.FullName)).ToList();
    }
    private async void Navigate(string requested)
    {
        var revision = ++_revision; _confirm.IsEnabled = false; _error.Text = L10n.Text("正在读取…");
        try
        {
            var path = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(requested.Trim()));
            var children = await Task.Run(() => Directory.EnumerateDirectories(path).Select(p => new FolderRow(System.IO.Path.GetFileName(p),p)).OrderBy(r => r.Title,StringComparer.CurrentCultureIgnoreCase).ToList());
            if (revision != _revision) return;
            _directory = path; _path.Text = path; _folders.ItemsSource = children; _confirm.IsEnabled = true; _error.Text = "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (revision != _revision) return;
            _directory = null; _error.Text = L10n.Text("无法打开这个文件夹，请检查路径和访问权限。");
        }
    }
    public Task<string?> ChooseAsync(Window owner) { Owner = owner; Show(); Activate(); return _result.Task; }
}
