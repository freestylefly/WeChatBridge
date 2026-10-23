using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Delivery;

namespace WeChatBridge.Windows;

/// <summary>
/// What the target picker answered. <see cref="TargetPickerAnswerKind.Expired"/>
/// is distinct from <see cref="TargetPickerAnswerKind.Cancelled"/> for the same
/// reason as on macOS: one is the user saying no, the other is nobody saying
/// anything — an expired share is a dead batch, a cancelled pick still leaves
/// the files on the clipboard as a considered choice.
/// </summary>
public enum TargetPickerAnswerKind
{
    Picked,
    Cancelled,
    Expired,
}

public sealed record TargetPickerAnswer(TargetPickerAnswerKind Kind, ForwardTarget? Target)
{
    public static TargetPickerAnswer Picked(ForwardTarget target) =>
        new(TargetPickerAnswerKind.Picked, target);

    public static TargetPickerAnswer Cancelled { get; } = new(TargetPickerAnswerKind.Cancelled, null);

    public static TargetPickerAnswer Expired { get; } = new(TargetPickerAnswerKind.Expired, null);
}

/// <summary>
/// The question 「发送到自定义」 asks once the share arrives in the app with no
/// destination — ported from <c>TargetPickerPanel.swift</c>. Shown only when
/// there is a genuine choice: the no-app and one-app answers are decided before
/// anything appears (<see cref="CustomForwardDecision"/>).
///
/// Two rules carried over from the macOS panel:
///
/// • The window is built and thrown away with the question — a panel that
///   outlives its request is a window nobody will ever close.
/// • The list arrives last-used first and the top row is pre-selected, so
///   Return takes the app the user chose the previous time.
///
/// Every exit — a click, Return, a digit, Esc, the deadline — ends in
/// <see cref="Finish"/>, which completes the answer exactly once.
/// </summary>
public partial class TargetPickerWindow : Window
{
    private readonly TaskCompletionSource<TargetPickerAnswer> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _deadline;
    private readonly IReadOnlyList<ForwardTarget> _targets;
    /// <summary>The 0.96 → 1 settle of Motion.panelIn, applied to the panel.</summary>
    private readonly ScaleTransform _entranceScale = new(0.96, 0.96);

    /// <summary>What was shared, printed under the title when the caller knows it.</summary>
    public static readonly DependencyProperty ContextLineProperty =
        DependencyProperty.Register(
            nameof(ContextLine), typeof(string), typeof(TargetPickerWindow),
            new PropertyMetadata(null));

    public string? ContextLine
    {
        get => (string?)GetValue(ContextLineProperty);
        set => SetValue(ContextLineProperty, value);
    }

    public TargetPickerWindow(IReadOnlyList<ForwardTarget> targets)
    {
        _targets = targets;
        Resources.MergedDictionaries.Add(
            new ResourceDictionary { Source = EntryPickerWindow.ThemeSource });
        InitializeComponent();
        Root.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        Root.RenderTransform = _entranceScale;
        TargetList.ItemsSource = targets
            .Select((target, index) => new Row(target, index))
            .ToList();
        // The first row is the last-used app — Return picks it, as on macOS.
        TargetList.SelectedIndex = targets.Count > 0 ? 0 : -1;
        _deadline = new DispatcherTimer { Interval = BatchIntent.FreshnessWindow };
        _deadline.Tick += (_, _) => Finish(TargetPickerAnswer.Expired);
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) => Finish(TargetPickerAnswer.Cancelled);
        // Manual placement starts off-screen; the real position is applied once
        // Show has given the window a size and a PresentationSource for DPI math.
        Left = -10000;
        Top = -10000;
    }

    /// <summary>
    /// The service-facing entry point: shows the panel near the cursor and
    /// completes with the answer — picked, cancelled, or expired.
    /// </summary>
    public static Task<TargetPickerAnswer> ChooseAsync(
        IReadOnlyList<ForwardTarget> targets,
        CancellationToken cancellationToken = default,
        Window? owner = null,
        string? contextLine = null)
    {
        var window = new TargetPickerWindow(targets);
        if (contextLine is not null)
            window.ContextLine = contextLine;
        EntryPickerWindow.TryOwn(window, owner);
        cancellationToken.Register(
            () => window.Dispatcher.BeginInvoke(window.Close));
        window._deadline.Start();
        window.Show();
        PositionWindowNearCursor(window);
        window.Activate();
        window.TargetList.Focus();
        return window._completion.Task;
    }

    /// <summary>
    /// Motion.panelIn on the way in: alpha 0 → 1 with the content settling
    /// 0.96 → 1, ease-out, in the 160–180 ms the macOS capsule takes.
    /// </summary>
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

    /// <summary>
    /// A question about a click belongs next to that click, the way a context
    /// menu does — <see cref="PointerPlacement.Frame"/> hangs the panel
    /// below-right of the pointer, flipping sides rather than covering it.
    /// </summary>
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
            case Key.Return: // the main and keypad Enter keys share this value in WPF
                Confirm();
                e.Handled = true;
                break;
            case Key.Escape:
                Finish(TargetPickerAnswer.Cancelled);
                e.Handled = true;
                break;
            default:
                // 1–9 pick by position, which is what every row prints on its
                // right edge. A modified digit is somebody else's shortcut, and
                // an out-of-range one is ignored rather than read as "cancel".
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

    /// <summary>
    /// Arrow keys move the highlight, stopping at both ends rather than
    /// wrapping: ↑ from the last-used row is a gesture towards the row the
    /// user already has, and jumping to the bottom would be a surprise.
    /// </summary>
    private void MoveSelection(int delta)
    {
        if (_targets.Count == 0)
            return;
        var next = Math.Clamp(TargetList.SelectedIndex + delta, 0, _targets.Count - 1);
        TargetList.SelectedIndex = next;
        TargetList.ScrollIntoView(TargetList.Items[next]);
    }

    /// <summary>A row click is the macOS row's Button: it answers immediately.</summary>
    private void OnRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { Content: Row row })
            Finish(TargetPickerAnswer.Picked(row.Target));
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) =>
        Finish(TargetPickerAnswer.Cancelled);

    private void Confirm()
    {
        if (TargetList.SelectedItem is Row row)
        {
            Finish(TargetPickerAnswer.Picked(row.Target));
            return;
        }
        Finish(TargetPickerAnswer.Cancelled);
    }

    /// <summary>
    /// An index that is not a row is not an answer at all — it is ignored,
    /// never read as a cancellation (the macOS 5-over-3-rows bug).
    /// </summary>
    private bool FinishAt(int index)
    {
        if (index < 0 || index >= _targets.Count)
            return false;
        Finish(TargetPickerAnswer.Picked(_targets[index]));
        return true;
    }

    private void Finish(TargetPickerAnswer answer)
    {
        if (_completion.TrySetResult(answer))
        {
            _deadline.Stop();
            Close();
        }
    }

    /// <summary>
    /// Row wrapper: the printed digit is the shortcut that picks it, and the
    /// logo is the app's own mark where <see cref="AppLogos"/> has one.
    /// </summary>
    private sealed class Row(ForwardTarget target, int index)
    {
        public ForwardTarget Target { get; } = target;
        public string DisplayName => Target.DisplayName;
        /// <summary>Bundled PNG path or the target exe's extracted icon.</summary>
        public object? Logo { get; } = AppLogos.IconFor(target);
        /// <summary>The letter-badge fallback when no logo file exists.</summary>
        public string Initial =>
            DisplayName.Length > 0 ? DisplayName[..1].ToUpperInvariant() : "?";
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
