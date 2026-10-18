using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Activation;

namespace WeChatBridge.Windows.Activation;

/// <summary>
/// The QR login window: a shell around <see cref="AccountController"/>.
/// <para>
/// No logic lives here beyond turning bytes into an image. The state comes from the
/// controller's <c>Changed</c> event and is applied by the single <see cref="Render"/>
/// at the bottom, which is the same shape as <c>RedeemWindow</c> — one place where the
/// window's appearance is decided, so there is never a second button handler quietly
/// enabling something.
/// </para>
/// </summary>
public partial class LoginWindow : Window
{
    private readonly AccountController _controller;

    public LoginWindow(AccountController controller)
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

    /// <summary>True when the user closed the window without finishing, not 取消.</summary>
    public bool IsSignedIn => _controller.Stage == LoginStage.Done;

    private void OnStartClicked(object sender, RoutedEventArgs e) => _ = StartAsync();

    private void OnFinishClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private async Task StartAsync()
    {
        await _controller.StartAsync();
        Render();
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
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
        QrImage.Source = Decode(_controller.QrPng);
        var hasCode = QrImage.Source is not null;

        QrImage.Visibility = hasCode ? Visibility.Visible : Visibility.Collapsed;
        QrPlaceholder.Visibility = hasCode ? Visibility.Collapsed : Visibility.Visible;
        QrPlaceholder.Text = _controller.IsBusy
            ? L10n.Text("正在获取二维码…")
            : L10n.Text("点击下方按钮获取二维码。");

        StatusText.Text = _controller.StatusMessage;

        AuthorizeText.Text = _controller.AuthorizeUrl;
        AuthorizeText.Visibility = string.IsNullOrWhiteSpace(_controller.AuthorizeUrl)
            ? Visibility.Collapsed
            : Visibility.Visible;

        CountdownText.Text = _controller.SecondsRemaining > 0
            ? L10n.Format($"二维码剩余 {_controller.SecondsRemaining} 秒")
            : L10n.Text("二维码有效期 5 分钟");

        var done = _controller.Stage == LoginStage.Done;
        FinishButton.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        StartButton.IsEnabled = _controller.CanStart;

        // The button doubles as 刷新 while a code is on screen: a ticket that has been
        // open for five minutes and never scanned is expired, and the user's next move
        // is to get a new one.
        StartButton.Content = _controller.IsWaiting
            ? L10n.Text("换一个二维码")
            : _controller.CanRetry
                ? L10n.Text("重试")
                : L10n.Text("获取二维码");
    }

    /// <summary>
    /// Bytes to a frozen bitmap.
    /// <para>
    /// The freeze is not optional: the bytes arrive on a pool thread, and an
    /// unfrozen <c>BitmapImage</c> carries a thread affinity that would throw the
    /// moment the UI thread touched it.
    /// </para>
    /// <para>
    /// Unconditional, and safe to be unconditional. The frame is built from a
    /// <see cref="MemoryStream"/> this method disposes before returning, and
    /// <c>BitmapCacheOption.OnLoad</c> has already pulled the pixels in, so there is
    /// no shared state a freeze could damage — and <c>Freeze</c> is a no-op on an
    /// object that is frozen already.
    /// </para>
    /// </summary>
    private static ImageSource? Decode(byte[]? png)
    {
        if (png is null || png.Length == 0)
            return null;

        try
        {
            using var stream = new MemoryStream(png);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch (NotSupportedException)
        {
            // Not an image the WPF decoder recognises. The status line already says
            // something went wrong, so a silent null here is better than a crash.
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
