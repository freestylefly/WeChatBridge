using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Activation;

namespace WeChatBridge.Windows.Activation;

/// <summary>What the purchase window is currently showing.</summary>
public enum PurchaseStage
{
    /// <summary>Nothing registered yet.</summary>
    Idle,

    /// <summary>Sending the registration.</summary>
    Registering,

    /// <summary>Registered; waiting for the seller to confirm a payment.</summary>
    WaitingSeller,

    /// <summary>Confirmed; fetching the licence.</summary>
    Collecting,

    /// <summary>Licence written and verified.</summary>
    Done,

    /// <summary>The seller declined. The registration stays on their ledger.</summary>
    Rejected,

    /// <summary>Something went wrong and the user has a button to press.</summary>
    Failed,
}

/// <summary>
/// The buy-out purchase as a state machine, with no WPF type in sight.
/// <para>
/// The long part of this flow is a person waiting for another person. The seller has
/// to check their own 微信零账 and click 确认, which takes minutes or hours. So the
/// polling here is not an implementation detail — it is the feature, and its shape
/// (bounded, backing off, stoppable) is what keeps a customer from watching their
/// laptop ping all afternoon.
/// </para>
/// <para>
/// No money moves through this class. The user sees the seller's payment code and
/// scans it in WeChat; there is no payment API to call and no order to lose.
/// </para>
/// </summary>
public sealed class ActivationController : IDisposable
{
    private readonly ActivationService _activation;
    private readonly PollSchedule _schedule;
    private readonly int? _amountFen;
    private readonly Action<string>? _log;
    private readonly Action _onLicensed;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    private Task? _polling;

    public ActivationController(
        ActivationService activation,
        string? installId,
        string anchorDigest,
        int? amountFen = null,
        Action? onLicensed = null,
        Action<string>? log = null)
    {
        _activation = activation ?? throw new ArgumentNullException(nameof(activation));
        _schedule = PollSchedule.Activation;
        _amountFen = amountFen;
        _log = log;
        _onLicensed = onLicensed ?? (() => { });

        // The machine code is fixed for the life of the window: it is this machine's
        // id, and a purchase registered against one machine must never be silently
        // retried against another.
        InstallId = installId ?? string.Empty;
        AnchorDigest = anchorDigest;
    }

    public event Action? Changed;

    public PurchaseStage Stage { get; private set; } = PurchaseStage.Idle;

    /// <summary>One line, in the user's language, saying where the purchase stands.</summary>
    public string StatusMessage { get; private set; } = L10n.Text("扫码付款后点击「我已付款」，登记等待卖家确认。");

    /// <summary>The machine code the seller reads. The install id is the key.</summary>
    public string InstallId { get; }

    /// <summary>The hardware anchor's digest: evidence for the seller, never the key.</summary>
    public string AnchorDigest { get; }

    /// <summary>Whether there is a machine code to show at all.</summary>
    public bool CanRegister => !string.IsNullOrWhiteSpace(InstallId) && !IsBusy;

    public bool IsBusy => Stage is PurchaseStage.Registering or PurchaseStage.Collecting;

    /// <summary>The window is polling for the seller. The only useful button is 停止.</summary>
    public bool IsPolling => Stage == PurchaseStage.WaitingSeller;

    public bool CanRetry => Stage is PurchaseStage.Failed or PurchaseStage.Rejected;

    /// <summary>Whether the licence is on disk and the gate has been told.</summary>
    public bool IsLicensed => Stage == PurchaseStage.Done;

    /// <summary>
    /// Registers the machine, then polls until the seller confirms and the licence
    /// arrives.
    /// <para>
    /// Safe to call twice: registration is idempotent server-side per request, and a
    /// second press after a failure is the honest response to "I don't know if that
    /// went through".
    /// </para>
    /// </summary>
    public async Task RegisterAsync()
    {
        if (_disposed || string.IsNullOrWhiteSpace(InstallId))
        {
            StatusMessage = L10n.Text("本机尚未生成机器码，请重新启动软件后再试。");
            Stage = PurchaseStage.Failed;
            Raise();
            return;
        }

        Stage = PurchaseStage.Registering;
        StatusMessage = L10n.Text("正在登记…");
        Raise();

        var outcome = await _activation.RegisterAsync(InstallId, AnchorDigest, _amountFen, _lifetime.Token)
            .ConfigureAwait(true);

        // Only a registration the server actually recorded is worth polling for.
        // "No error" is not the same thing: an unreachable service also reports no
        // error field, and polling for an order that was never created would leave the
        // user watching a spinner for an hour for nothing.
        if (!outcome.IsRegistered)
        {
            Apply(outcome);
            return;
        }

        StatusMessage = outcome.Detail;
        Stage = PurchaseStage.WaitingSeller;
        Raise();

        _polling = PollUntilIssuedAsync();
        await _polling.ConfigureAwait(true);
    }

    /// <summary>Stops polling. The registration stays on the server's ledger either way.</summary>
    public void Cancel()
    {
        if (!_lifetime.IsCancellationRequested)
            _lifetime.Cancel();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Cancel();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The wait itself. Every iteration goes out on its own cadence and comes back
    /// before the next one is due, so 停止 takes effect within one delay rather than
    /// after a pending HTTP call times out.
    /// </summary>
    private async Task PollUntilIssuedAsync()
    {
        var attempt = 0;
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_schedule.DelayBefore(attempt++), _lifetime.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var status = await _activation.CheckStatusAsync(_lifetime.Token).ConfigureAwait(true);
                if (!status.Confirmed)
                {
                    StatusMessage = status.Detail;
                    Raise();
                    if (status.Failure == ActivationFailure.Rejected)
                    {
                        Stage = PurchaseStage.Rejected;
                        Raise();
                        return;
                    }
                    continue;
                }

                // Confirmed. The read can lose a race with the signing, which the
                // service reports as 409 — so a not-yet-readable licence is a pause,
                // not a failure.
                Stage = PurchaseStage.Collecting;
                StatusMessage = L10n.Text("卖家已确认，正在取许可证…");
                Raise();

                var collected = await _activation.CollectAsync(InstallId, _lifetime.Token).ConfigureAwait(true);
                Apply(collected);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ApiTimeoutException error)
            {
                // A single slow poll is not a failed purchase. Say so and carry on.
                _log?.Invoke($"activation: poll timed out ({error.Message})");
                StatusMessage = L10n.Text("服务响应较慢，正在重试…");
                Raise();
            }
            catch (ApiTransportException error)
            {
                _log?.Invoke($"activation: poll did not reach the service ({error.Message})");
                StatusMessage = L10n.Text("网络不稳定，正在重试…");
                Raise();
            }
        }
    }

    private void Apply(ActivationOutcome outcome)
    {
        StatusMessage = outcome.Detail;
        Stage = outcome.Succeeded
            ? PurchaseStage.Done
            : outcome.Failure == ActivationFailure.Rejected
                ? PurchaseStage.Rejected
                : PurchaseStage.Failed;
        Raise();

        if (!outcome.Succeeded)
            return;

        // The gate caches its answer, so it has to be told. Getting this wrong is not
        // a cosmetic bug: the app would keep telling a paying customer they are not
        // licensed until they restarted it.
        _onLicensed();
    }

    private void Raise() => Changed?.Invoke();
}
