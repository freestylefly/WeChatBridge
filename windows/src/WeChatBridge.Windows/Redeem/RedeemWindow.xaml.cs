using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Redeem;

namespace WeChatBridge.Windows.Redeem;

/// <summary>
/// The buy-out edition's redemption dialog.
/// <para>
/// A thin shell around <see cref="RedeemController"/>: it renders the stage, forwards
 /// clicks, and normalises what is typed. No decision about what a failure means, or
 /// about which button goes with it, is made here — that is the controller's, and it
/// is the part worth testing without a window.
/// </para>
/// <para>
/// There is no QR code in this window. Plan B is a pasted code rather than a scanned
/// one (§3.3), so the field is the whole input story: it takes focus on open, accepts
/// a paste in any spelling, and re-renders what was pasted into the canonical one as
/// it arrives. Typing is not the intended path and the UI does not pretend otherwise.
/// </para>
/// </summary>
public partial class RedeemWindow : Window
{
    private readonly RedeemController _controller;
    private bool _normalising;

    public RedeemWindow(RedeemController controller)
    {
        _controller = controller;
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 32);
        _controller.Changed += Render;
        PendingCard.Visibility = _controller.HasUnresolvedPending ? Visibility.Visible : Visibility.Collapsed;
        Render();
        Loaded += OnLoaded;
        // Cancelling rather than disposing: the controller owns the channel, and a
        // closed dialog must not close a channel the user is still allowed to use in a
        // later session. Cancelling stops the in-flight task from updating a window
        // that is no longer there, which is the actual requirement — no dangling work.
        Closed += (_, _) =>
        {
            _controller.Changed -= Render;
            _controller.Cancel();
        };
    }

    /// <summary>The stage currently on screen. Exposed for the window's own tests.</summary>
    public RedeemStage Stage => _controller.Stage;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Focus before anything else can steal it, and select what is there so a paste
        // replaces a half-typed code rather than appending to it.
        CodeInput.Focus();
        CodeInput.SelectAll();
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    /// <summary>
    /// Normalises as the user types or pastes.
    /// <para>
    /// The caret goes to the end rather than being tracked character by character: the
    /// length can change under a keystroke when a character folds (<c>O</c> to
    /// <c>0</c>) or a separator is removed, and a caret that lands in the wrong place
    /// in a 23-character field is a worse bug than one that jumps to the end.
    /// </para>
    /// </summary>
    private void OnCodeChanged(object sender, TextChangedEventArgs e)
    {
        if (_normalising)
            return;
        var normalized = RedeemCode.FormatForDisplay(CodeInput.Text);
        if (!string.Equals(normalized, CodeInput.Text, StringComparison.Ordinal))
        {
            _normalising = true;
            CodeInput.Text = normalized;
            CodeInput.CaretIndex = CodeInput.Text.Length;
            _normalising = false;
        }

        _controller.Code = CodeInput.Text;
        Render();
    }

    private void OnPasteClicked(object sender, RoutedEventArgs e)
    {
        // A redemption code arrives by mail or off an order page, so pasting is the
        // path that will actually be used and it has to work. The clipboard can also be
        // held by another process at the moment we ask, which throws rather than
        // returning null, and that must not take the window down.
        string? text = null;
        try
        {
            if (Clipboard.ContainsText())
                text = Clipboard.GetText();
        }
        catch (ExternalException)
        {
            text = null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _controller.SetStatus(L10n.Text("剪贴板里没有文本。"));
            return;
        }

        CodeInput.Text = RedeemCode.FormatForDisplay(text);
        CodeInput.Focus();
        CodeInput.CaretIndex = CodeInput.Text.Length;
    }

    private void OnRedeemClicked(object sender, RoutedEventArgs e) => _ = SubmitAsync();

    private void OnRetryClicked(object sender, RoutedEventArgs e) => _ = SubmitAsync();

    private void OnDiscardClicked(object sender, RoutedEventArgs e)
    {
        _controller.DiscardPending();
        PendingCard.Visibility = _controller.HasUnresolvedPending ? Visibility.Visible : Visibility.Collapsed;
        Render();
    }

    private void OnFinishClicked(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// One attempt, awaited so the window is never torn down underneath it.
    /// <para>
    /// The task is not held: it is owned by the controller's lifetime token, and the
    /// <c>Closed</c> handler cancels that token. Nothing here can reach a disposed
    /// element because the controller stops raising changes once it has been
    /// cancelled, and this method's last act is a render that is a no-op by then.
    /// </para>
    /// </summary>
    private async Task SubmitAsync()
    {
        RedeemOutcome outcome;
        try
        {
            outcome = await _controller.SubmitAsync();
        }
        catch (RedeemChannelSealedException)
        {
            // Raised when the channel closed between the click and the call — a licence
            // from an earlier attempt in the same process.
            _controller.MarkUnavailable("兑换通道已经关闭：许可证已写入，本次不会再发起任何网络请求。");
            Render();
            return;
        }

        PendingCard.Visibility = _controller.HasUnresolvedPending ? Visibility.Visible : Visibility.Collapsed;
        if (!outcome.Succeeded && outcome.Failure == RedeemFailure.None)
            return; // cancelled; the window is on its way out
        Render();
    }

    private void Render()
    {
        var stage = _controller.Stage;
        var done = stage == RedeemStage.Done;
        var retry = _controller.CanRetry;

        SuccessPanel.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        EditorPanel.Visibility = done ? Visibility.Collapsed : Visibility.Visible;

        FinishButton.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        // One primary action at a time. A disabled 兑换 next to an enabled 重试 would
        // be two buttons where the user's next move has exactly one shape.
        RedeemButton.Visibility = done || retry ? Visibility.Collapsed : Visibility.Visible;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.IsEnabled = !_controller.IsBusy;
        DiscardButton.Visibility = _controller.CanDiscard && _controller.HasUnresolvedPending
            ? Visibility.Visible : Visibility.Collapsed;

        PasteButton.IsEnabled = _controller.CanEditCode;
        CodeInput.IsEnabled = _controller.CanEditCode;
        RedeemButton.IsEnabled = !_controller.IsBusy && _controller.CanSubmit;
        RedeemButton.Content = _controller.IsBusy ? L10n.Text("兑换中…") : L10n.Text("兑换");

        StatusText.Text = _controller.StatusMessage;
        if (done)
            IssuedText.Text = _controller.StatusMessage;
    }
}
