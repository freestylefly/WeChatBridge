using System.Windows;
using System.Windows.Input;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Activation;

/// <summary>
/// The buy-out purchase window: a shell around <see cref="ActivationController"/>.
/// <para>
/// Same shape as <see cref="LoginWindow"/> — no logic beyond rendering, one
/// <c>Render()</c> driven by the controller's <c>Changed</c> event, so there is never a
/// second button handler quietly enabling something.
/// </para>
/// <para>
/// The one piece of state this window owns is <see cref="_confirmed"/>. The payment
/// code is a static image the seller pasted into the build; putting it on screen is the
/// moment the user starts owing someone money, so it waits until they have seen the
/// amount and the machine code they are binding.
/// </para>
/// </summary>
public partial class PurchaseWindow : Window
{
    /// <summary>
    /// PLACEHOLDER — the seller's WeChat id, shown for after-sales.
    /// <para>
    /// One constant rather than a value repeated across the XAML: it is a string the
    /// product owner fills in at release, and it appears both as an after-sales
    /// contact and in the purchase terms. Replace this and nothing else.
    /// </para>
    /// </summary>
    private const string SellerWeChat = "（待填写）";

    private readonly ActivationController _controller;

    /// <summary>The user has read the terms and asked to see the payment code.</summary>
    private bool _confirmed;

    public PurchaseWindow(ActivationController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        InitializeComponent();

        // Clamped the way every dialog here is: a fixed-size window on a small display
        // otherwise hangs off the bottom of the work area, with the button that
        // dismisses it off-screen.
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 32);

        _controller.Changed += Render;
        Closed += OnClosed;
    }

    /// <summary>True when the licence is on disk, so the gate can be re-read.</summary>
    public bool IsLicensed => _controller.IsLicensed;

    private void OnConfirmClicked(object sender, RoutedEventArgs e)
    {
        // Showing the payment code is not the same as registering. Nothing goes to the
        // service until the user says they have paid, so a window opened and abandoned
        // leaves no trace on the seller's ledger.
        _confirmed = true;
        Render();
    }

    private void OnPaidClicked(object sender, RoutedEventArgs e) => _ = RegisterAsync();

    private void OnRetryClicked(object sender, RoutedEventArgs e) => _ = RegisterAsync();

    private void OnStopClicked(object sender, RoutedEventArgs e)
    {
        _controller.Cancel();
        Render();
    }

    private void OnFinishClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCopyInstallId(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_controller.InstallId);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Someone else owns the clipboard. The code is on screen and selectable;
            // failing to copy is not worth an error box over.
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
        }
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private async Task RegisterAsync()
    {
        await _controller.RegisterAsync();
        Render();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _controller.Changed -= Render;

        // Cancel, not Dispose: the controller may still be mid-await on a poll, and
        // disposing a CancellationTokenSource out from under it is a race. It is
        // disposed by whoever created it.
        _controller.Cancel();
    }

    /// <summary>The only place this window decides what it looks like.</summary>
    private void Render()
    {
        // The controller raises Changed from its poll loop, which runs on the captured
        // context but is not guaranteed to be the dispatcher once a retry has bounced
        // through an await. Touching named elements from anything but the UI thread
        // throws, so every write goes through here.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Render);
            return;
        }

        InstallIdBox.Text = _controller.InstallId;
        AnchorDigestText.Text = string.IsNullOrWhiteSpace(_controller.AnchorDigest)
            ? L10n.Text("（无法读取，仅以机器码为准）")
            : _controller.AnchorDigest;
        SellerWeChatText.Text = SellerWeChat;

        // The transfer note is the machine code: it is what the seller matches a manual
        // WeChat transfer against, and the only link between a payment and a licence.
        PaymentNoteText.Text = _controller.InstallId;

        StatusText.Text = _controller.StatusMessage;

        // The payment code appears only after the terms have been read, and stays put
        // afterwards — a user who is mid-poll still needs to see what they paid for.
        PaymentCard.Visibility = _confirmed ? Visibility.Visible : Visibility.Collapsed;

        var stage = _controller.Stage;

        DetailText.Visibility = _controller.CanRetry ? Visibility.Visible : Visibility.Collapsed;
        DetailText.Text = _controller.CanRetry
            ? L10n.Text("登记没有丢失，可以直接重试。")
            : string.Empty;

        FinishButton.Visibility = stage == PurchaseStage.Done ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Visibility = _controller.IsPolling ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = _controller.CanRetry ? Visibility.Visible : Visibility.Collapsed;

        PaidButton.Visibility =
            _confirmed && stage == PurchaseStage.Idle ? Visibility.Visible : Visibility.Collapsed;

        ConfirmButton.Visibility =
            !_confirmed && stage == PurchaseStage.Idle ? Visibility.Visible : Visibility.Collapsed;

        // Once the money is moving there is nothing to confirm or claim; the only
        // honest button is the one that stops the wait.
        FooterNote.Visibility = _controller.IsPolling ? Visibility.Collapsed : Visibility.Visible;
    }
}
