using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Panes;

/// <summary>
/// What the Share menu offers, and the switches that decide which of it shows —
/// the port of macOS EntriesPane + the ForwardTargetList editor and the Obsidian
/// settings the macOS version hides behind sheets.
/// </summary>
public partial class EntriesPane : UserControl
{
    public EntriesPane()
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
            UpdateTargetsEmpty();
            return;
        }
        if (_hooked is not null)
            _hooked.TargetRows.CollectionChanged -= OnTargetsChanged;
        _hooked = Model;
        if (_hooked is not null)
            _hooked.TargetRows.CollectionChanged += OnTargetsChanged;
        UpdateTargetsEmpty();
    }

    private void OnTargetsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateTargetsEmpty();

    private void UpdateTargetsEmpty()
    {
        if (!IsLoaded || Model is null)
            return;
        TargetsEmpty.Visibility = Model.TargetRows.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void EntryCheck_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { } model
            && (sender as FrameworkElement)?.DataContext is EntryRow row
            && sender is CheckBox box)
        {
            model.SetEntryEnabled(row.Action, box.IsChecked == true);
            box.SetCurrentValue(CheckBox.IsCheckedProperty, model.IsEntryEnabled(row.Action));
        }
    }

    /// <summary>设置… / 管理… scroll the matching card into view.</summary>
    private void Configure_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EntryRow row)
            return;
        (row.Action == ShareAction.Obsidian ? ObsidianCard : row.Action == ShareAction.Folder ? FolderCard : CustomCard).BringIntoView();
    }

    private void ChooseDeliveryFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = L10n.Text("选择沉淀文件夹"), Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true && Model is { } model)
            model.DeliveryFolderPath = dialog.FolderName;
    }

    private void ChooseVault_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;
        var dialog = new OpenFolderDialog
        {
            Title = L10n.Text("选择知识库"),
            Multiselect = false,
        };
        if (model.ObsidianVaultPath is { Length: > 0 } current && Directory.Exists(current))
            dialog.FolderName = current;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            model.ObsidianVaultPath = dialog.FolderName;
    }

    private void RevealVault_Click(object sender, RoutedEventArgs e)
    {
        if (Model?.ObsidianVaultPath is not { Length: > 0 } path || !Directory.Exists(path))
            return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            // Explorer is always there; a refused launch is not worth a dialog.
        }
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L10n.Text("选择应用"),
            Filter = L10n.Text("可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*"),
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        NewTargetPath.Text = dialog.FileName;
        if (NewTargetName.Text.Trim().Length == 0)
            NewTargetName.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
    }

    private void AddTarget_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;
        var identifier = NewTargetPath.Text.Trim();
        if (identifier.Length == 0)
        {
            model.ShowToast(L10n.Text("请填写 .exe 路径或应用 AUMID"), warning: true);
            return;
        }
        model.AddForwardTarget(identifier, NewTargetName.Text);
        NewTargetName.Text = string.Empty;
        NewTargetPath.Text = string.Empty;
    }

    private void RemoveTarget_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { } model && (sender as FrameworkElement)?.DataContext is ForwardTargetRow row)
            model.RemoveForwardTarget(row.Target);
    }

    private void PastesPathOnly_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { } model
            && (sender as FrameworkElement)?.DataContext is ForwardTargetRow row
            && sender is CheckBox box)
        {
            model.SetPastesPathOnly(row.Target, box.IsChecked == true);
        }
    }
}

/// <summary>
/// Logo bindings for the 入口 rows: <see cref="AppLogos"/> resolves the PNG;
/// the converters below only adapt the two row types so a DataTemplate can
/// bind them, mirroring ShareEntryList's bundled-logo lookup.
/// </summary>
public sealed class RowLogoPathConverter : IValueConverter
{
    public static string? PathFor(object? row) => row switch
    {
        EntryRow entry => AppLogos.PathFor(entry.Action),
        ForwardTargetRow target => AppLogos.PathFor(target.DisplayName),
        _ => null,
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        PathFor(value);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Segoe Fluent/MDL2 codepoint for rows that are not real apps — the
/// clipboard, the 发送到自定义 summary row, and the hub entry itself — so the
/// picker and this pane draw the same three-layer mark: PNG → glyph → letter.
/// </summary>
public sealed class RowGlyphConverter : IValueConverter
{
    public static string? GlyphFor(object? row) => row switch
    {
        EntryRow { Action: ShareAction.Clipboard } => "",
        EntryRow { Action: ShareAction.Custom } => "",
        EntryRow { Action: ShareAction.Hub } => "",
        ForwardTargetRow { Target: { } target }
            when AppLogos.PathFor(target.DisplayName) is null => "",
        _ => null,
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        GlyphFor(value) ?? string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Three-way visibility: parameter "logo" shows the PNG, "glyph" the Segoe
/// mark, anything else the letter badge. Each row draws exactly one.
/// </summary>
public sealed class RowLogoVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasLogo = RowLogoPathConverter.PathFor(value) is not null;
        var hasGlyph = RowGlyphConverter.GlyphFor(value) is not null;
        var visible = Equals(parameter, "glyph") ? hasGlyph
            : Equals(parameter, "logo") ? hasLogo
            : !hasLogo && !hasGlyph;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>The badge letter: the destination's own name, not the entry verb — 发给 Codex → C.</summary>
public sealed class RowLogoInitialConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value switch
        {
            EntryRow entry => entry.Action.TargetDisplayName(),
            ForwardTargetRow target => target.DisplayName,
            _ => null,
        };
        return name is { Length: > 0 } ? name[..1].ToUpperInvariant() : "?";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
