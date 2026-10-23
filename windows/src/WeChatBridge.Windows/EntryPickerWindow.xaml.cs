using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Delivery;

namespace WeChatBridge.Windows;

/// <summary>What the share-entry picker answered.</summary>
public enum EntryPickerAnswerKind
{
    Picked,
    Cancelled,
    Expired,
}

/// <summary>
/// One row of the 「微信流」 picker: a built-in destination (action, no target)
/// or one of the user's own apps (action = Custom, target set).
/// </summary>
public sealed record EntryPickerOption(ShareAction Action, ForwardTarget? Target, string? Detail);

public sealed record EntryPickerAnswer(EntryPickerAnswerKind Kind, ShareAction? Action, ForwardTarget? Target)
{
    public static EntryPickerAnswer Picked(ShareAction action, ForwardTarget? target) =>
        new(EntryPickerAnswerKind.Picked, action, target);

    public static EntryPickerAnswer Cancelled { get; } = new(EntryPickerAnswerKind.Cancelled, null, null);

    public static EntryPickerAnswer Expired { get; } = new(EntryPickerAnswerKind.Expired, null, null);
}

/// <summary>
/// The panel 「微信流」 opens: the single share-sheet entry the sparse package
/// registers forwards here, and this is where the 入口 pane's switches become
/// visible in the share flow — only enabled rows are listed.
///
/// Same interaction grammar as <see cref="TargetPickerWindow"/>: near-cursor
/// placement, digit shortcuts, Esc cancels, and an expiration tied to
/// <see cref="BatchIntent.FreshnessWindow"/> so an unanswered share cannot
/// paste into whatever the user has opened since.
/// </summary>
public partial class EntryPickerWindow : Window
{
    /// <summary>
    /// The theme dictionary every host merges: the main app already holds it at
    /// Application level, while the share-target helper — a separate exe that
    /// links these same sources — has no App.xaml at all. Merging into the
    /// window's own resources makes the panel render identically in both.
    /// </summary>
    internal static readonly Uri ThemeSource =
        new("pack://application:,,,/Themes/AppTheme.xaml", UriKind.Absolute);

    private readonly TaskCompletionSource<EntryPickerAnswer> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _deadline;
    private readonly IReadOnlyList<EntryPickerOption> _options;
    private readonly ScaleTransform _entranceScale = new(0.96, 0.96);

    /// <summary>The share's payload as one line under the title — file name and size.</summary>
    public static readonly DependencyProperty ContextLineProperty =
        DependencyProperty.Register(
            nameof(ContextLine), typeof(string), typeof(EntryPickerWindow),
            new PropertyMetadata(null));

    public string? ContextLine
    {
        get => (string?)GetValue(ContextLineProperty);
        set => SetValue(ContextLineProperty, value);
    }

    /// <summary>Whether the share's files are still arriving — drives the marquee strip under the title.</summary>
    public static readonly DependencyProperty IsReceivingProperty =
        DependencyProperty.Register(
            nameof(IsReceiving), typeof(bool), typeof(EntryPickerWindow),
            new PropertyMetadata(false));

    public bool IsReceiving
    {
        get => (bool)GetValue(IsReceivingProperty);
        set => SetValue(IsReceivingProperty, value);
    }

    /// <summary>Fills the header's payload line later — the helper learns the file name only once WeChat hands the share over. Landing the real line also clears the receiving strip.</summary>
    public void SetContext(string? text) =>
        Dispatcher.Invoke(() =>
        {
            ContextLine = string.IsNullOrWhiteSpace(text) ? null : text;
            IsReceiving = false;
        });

    public EntryPickerWindow(IReadOnlyList<EntryPickerOption> options)
    {
        _options = options;
        // Before InitializeComponent: the XAML's StaticResource lookups resolve
        // during the load, so the merge must already be in place.
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = ThemeSource });
        L10n.Language = new AppSettingsStore().Load().Language;
        InitializeComponent();
        Root.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        Root.RenderTransform = _entranceScale;
        EntryList.ItemsSource = options
            .Select((option, index) => new Row(option, index))
            .ToList();
        EntryList.SelectedIndex = options.Count > 0 ? 0 : -1;
        _deadline = new DispatcherTimer { Interval = BatchIntent.FreshnessWindow };
        _deadline.Tick += (_, _) => Finish(EntryPickerAnswer.Expired);
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) => Finish(EntryPickerAnswer.Cancelled);
        Left = -10000;
        Top = -10000;
    }

    /// <summary>Shows the panel near the cursor and completes with the pick.</summary>
    public static Task<EntryPickerAnswer> ChooseAsync(
        IReadOnlyList<EntryPickerOption> options,
        CancellationToken cancellationToken = default,
        Window? owner = null,
        string? contextLine = null)
    {
        var window = new EntryPickerWindow(options);
        if (contextLine is not null)
            window.ContextLine = contextLine;
        return window.ShowAndAwait(cancellationToken, owner);
    }

    /// <summary>
    /// The instance half of <see cref="ChooseAsync"/>: the share-target helper
    /// keeps the window reference so it can update <see cref="ContextLine"/>
    /// while the user is still reading the panel.
    /// </summary>
    public Task<EntryPickerAnswer> ShowAndAwait(
        CancellationToken cancellationToken = default,
        Window? owner = null)
    {
        TryOwn(this, owner);
        cancellationToken.Register(
            () => Dispatcher.BeginInvoke(Close));
        _deadline.Start();
        Show();
        PositionWindowNearCursor(this);
        Activate();
        EntryList.Focus();
        return _completion.Task;
    }

    /// <summary>
    /// Assigns <paramref name="owner"/> only when it already has a handle. WPF
    /// throws when the owner was never shown — the tray-resident main window is
    /// exactly that until the user opens settings once.
    /// </summary>
    internal static void TryOwn(Window window, Window? owner)
    {
        if (owner is not null &&
            new WindowInteropHelper(owner).Handle != IntPtr.Zero)
            window.Owner = owner;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(170));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, duration) { EasingFunction = ease });
        _entranceScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, duration) { EasingFunction = ease });
        _entranceScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, duration) { EasingFunction = ease });
    }

    private static void PositionWindowNearCursor(Window window)
    {
        if (!NativeMethods.GetCursorPos(out var point))
            return;
        var source = PresentationSource.FromVisual(window);
        var toDip = source?.CompositionTarget?.TransformFromDevice
                    ?? Matrix.Identity;
        var dip = toDip.Transform(new System.Windows.Point(point.X, point.Y));
        var workArea = SystemParameters.WorkArea;
        var frame = PointerPlacement.Frame(
            new SizeF((float)window.ActualWidth, (float)window.ActualHeight),
            new PointF((float)dip.X, (float)dip.Y),
            new RectangleF(
                (float)workArea.Left, (float)workArea.Top,
                (float)workArea.Width, (float)workArea.Height));
        window.Left = frame.Left;
        window.Top = frame.Top;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Return:
                Confirm();
                e.Handled = true;
                break;
            case Key.Escape:
                Finish(EntryPickerAnswer.Cancelled);
                e.Handled = true;
                break;
            default:
                if (Keyboard.Modifiers != ModifierKeys.None)
                    return;
                var digit = e.Key switch
                {
                    >= Key.D1 and <= Key.D9 => (int)e.Key - (int)Key.D1 + 1,
                    >= Key.NumPad1 and <= Key.NumPad9 => (int)e.Key - (int)Key.NumPad1 + 1,
                    _ => 0,
                };
                if (digit > 0 && FinishAt(digit - 1))
                    e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (_options.Count == 0)
            return;
        var next = Math.Clamp(EntryList.SelectedIndex + delta, 0, _options.Count - 1);
        EntryList.SelectedIndex = next;
        EntryList.ScrollIntoView(EntryList.Items[next]);
    }

    private void OnRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { Content: Row row })
            Finish(EntryPickerAnswer.Picked(row.Option.Action, row.Option.Target));
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) =>
        Finish(EntryPickerAnswer.Cancelled);

    private void Confirm()
    {
        if (EntryList.SelectedItem is Row row)
        {
            Finish(EntryPickerAnswer.Picked(row.Option.Action, row.Option.Target));
            return;
        }
        Finish(EntryPickerAnswer.Cancelled);
    }

    private bool FinishAt(int index)
    {
        if (index < 0 || index >= _options.Count)
            return false;
        var option = _options[index];
        Finish(EntryPickerAnswer.Picked(option.Action, option.Target));
        return true;
    }

    private void Finish(EntryPickerAnswer answer)
    {
        if (_completion.TrySetResult(answer))
        {
            _deadline.Stop();
            Close();
        }
    }

    /// <summary>
    /// Row wrapper: the digit on the right edge is the shortcut. Logo lookup is
    /// layered — an installed app's official mark first, the Segoe glyph for
    /// entries that are not apps (clipboard, custom, the hub itself), and the
    /// letter badge only when nothing else applies.
    /// </summary>
    private sealed class Row(EntryPickerOption option, int index)
    {
        public EntryPickerOption Option { get; } = option;
        public ShareAction Action => Option.Action;
        public ForwardTarget? Target => Option.Target;

        /// <summary>The row's own words: built-ins keep their share-menu verb,
        /// custom targets name themselves.</summary>
        public string Title =>
            Target?.DisplayName ?? Action.EntryTitle();

        /// <summary>A one-line hint — built-ins say what they do, customs their id.</summary>
        public string? Detail =>
            Option.Detail ?? Target?.BundleIdentifier;

        /// <summary>Logo PNG path or extracted exe icon — built-in or custom.</summary>
        public object? Logo { get; } = option.Target is { } target
            ? AppLogos.IconFor(target)
            : AppLogos.PathFor(option.Action);

        /// <summary>
        /// Segoe Fluent/MDL2 codepoint for entries with no real icon to show:
        /// clipboard, custom-target rows without a known logo, and the hub
        /// row itself. Empty when <see cref="Logo"/> or the letter badge
        /// carries the row.
        /// </summary>
        public string Glyph { get; } = option switch
        {
            { Action: ShareAction.Clipboard } => "",     // Copy
            { Action: ShareAction.Hub } => "",           // Send
            { Action: ShareAction.Custom, Target: { } t } when
                AppLogos.IconFor(t) is null => "", // AppIconDefault
            { Action: ShareAction.Custom } => "",
            _ => string.Empty,
        };

        /// <summary>Letter-badge fallback only when neither a logo nor a glyph exists.</summary>
        public string Initial =>
            Title.Length > 0 ? Title[..1].ToUpperInvariant() : "?";

        public bool HasGlyph => Glyph.Length > 0;
        public bool HasLogo => Logo is not null;
        public bool HasBadge => !HasLogo && !HasGlyph;

        public string Shortcut =>
            index < 9 ? (index + 1).ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }
    }
}