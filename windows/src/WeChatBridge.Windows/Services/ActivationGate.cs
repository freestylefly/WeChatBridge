using System;
using System.IO;
using System.Threading;
using System.Windows;
using WeChatBridge.Windows.Activation;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Activation;

namespace WeChatBridge.Windows.Services;

/// <summary>Which page the gate decided to open.</summary>
internal enum GatePage
{
    /// <summary>Nothing. Licensed, portable, or already prompted.</summary>
    None,

    /// <summary>The WeChat sign-in window.</summary>
    Login,

    /// <summary>The buy-out purchase window.</summary>
    Purchase,

    /// <summary>Nothing opens a window; the user has to contact the seller.</summary>
    ContactSeller,
}

/// <summary>
/// The one place that turns a <see cref="GateSnapshot"/> into a page, and the only
/// place that opens the login and purchase windows.
/// <para>
/// Two things this deliberately does not do.
/// </para>
/// <para>
/// It does not lock anything. <see cref="GateState.SignedIn"/> is documented as
/// "nothing is locked behind a licence they have not paid for", and
/// <c>LicensingBootstrap.IsShareDeliveryAvailable</c> is constant true — forwarding
/// works for everyone, licensed or not. The gate is a path to buy, not a paywall.
/// </para>
/// <para>
/// It does not nag. A customer who has seen the page once and chosen not to buy is
/// told once more through the 通用 page, where a button can reopen it, and not on
/// every launch. An upsell that reappears at every start stops being read at the
/// second one.
/// </para>
/// </summary>
internal sealed class ActivationGate : IDisposable
{
    private readonly ActivationGateway _gateway;
    private readonly AppSettingsStore _settings;
    private readonly Action<string>? _log;
    private bool _disposed;

    private ActivationGate(ActivationGateway gateway, AppSettingsStore settings, Action<string>? log)
    {
        _gateway = gateway;
        _settings = settings;
        _log = log;
    }

    public static ActivationGate Current { get; } = new(ActivationGateway.Current, new AppSettingsStore(), null);

    /// <summary>Everything the gate knows, as one value.</summary>
    public GateSnapshot Snapshot => _gateway.Snapshot();

    /// <summary>
    /// The page this snapshot calls for, with no I/O and no side effects — so the
    /// decision can be tested and reasoned about separately from showing a window.
    /// </summary>
    internal static GatePage Decide(GateSnapshot snapshot) => snapshot.State switch
    {
        // The two states that need no page. Portable is the buy-out product's free
        // tier and is issued no licences, so treating it as unlicensed would nag
        // every portable user to buy something they cannot use.
        GateState.Licensed or GateState.Portable => GatePage.None,

        // No account yet: the purchase page has nothing to attach the request to.
        GateState.Anonymous => GatePage.Login,

        GateState.SignedIn => GatePage.Purchase,

        // Both of these are wrong on this machine and no login will fix them: a
        // licence bound elsewhere needs a re-sign, and a licence that does not
        // verify needs support. Telling the user to "scan a code" in either case
        // wastes the first ten seconds of their attempt to fix it.
        GateState.Rejected or GateState.LicenseUnverifiable => GatePage.ContactSeller,

        _ => GatePage.None,
    };

    /// <summary>One line about where this install stands, for the 通用 page.</summary>
    public string StatusText => Snapshot.State switch
    {
        GateState.Licensed => L10n.Text("已激活：本机已是买断版 Pro，许可证离线生效。"),
        GateState.Portable => L10n.Text("便携版是买断版的免费档，不签发许可证，也不需要激活。"),
        GateState.SignedIn => L10n.Format($"已登录为 {Snapshot.Nickname}，尚未购买许可证。"),
        GateState.Rejected => L10n.Text("这台机器上的许可证属于另一台机器，需要卖家重新签发。请联系开发者。"),
        GateState.LicenseUnverifiable => L10n.Text("本机有一份无法验证的许可证。请联系开发者处理。"),
        _ => L10n.Text("尚未登录。登录后才能购买许可证。"),
    };

    /// <summary>Whether the 通用 page's button should be offered.</summary>
    public bool CanOpenFromSettings => Decide(Snapshot) is GatePage.Login or GatePage.Purchase or GatePage.ContactSeller;

    /// <summary>
    /// Opens the page this install calls for, if it has not already been shown.
    /// <para>
    /// Only ever called on an interactive launch. A <c>--background</c> instance is
    /// the share helper's — it must not put a window on the user's screen in the
    /// middle of a delivery.
    /// </para>
    /// </summary>
    public GatePage ShowAtStartup(Window owner, Action<string>? log = null)
    {
        var page = Decide(Snapshot);
        if (page == GatePage.None)
            return page;

        if (_settings.Load().ActivationGateShown)
            return GatePage.None;

        MarkShown();
        return Open(owner, page, log ?? _log);
    }

    /// <summary>Opens a page on request, from the 通用 page. Ignores the shown-once flag.</summary>
    public GatePage OpenManually(Window owner, Action<string>? log = null) =>
        Open(owner, Decide(Snapshot), log ?? _log);

    private GatePage Open(Window owner, GatePage page, Action<string>? log)
    {
        switch (page)
        {
            case GatePage.Login:
                return ShowLogin(owner, log);

            case GatePage.Purchase:
                return ShowPurchase(owner, log);

            case GatePage.ContactSeller:
                // A window here would offer nothing to click. Say it once, plainly.
                MessageBox.Show(
                    owner,
                    StatusText,
                    L10n.Text("微信流"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return page;

            default:
                return GatePage.None;
        }
    }

    private GatePage ShowLogin(Window owner, Action<string>? log)
    {
        using var controller = new AccountController(_gateway.Account, _gateway.InstallId, log);
        new LoginWindow(controller) { Owner = owner }.ShowDialog();
        return controller.IsSignedIn ? ContinueAfterLogin(owner, log) : GatePage.Login;
    }

    /// <summary>
    /// A successful sign-in leads straight to the purchase page.
    /// <para>
    /// Merged on purpose: the two pages answer one question ("how do I get this") and
    /// a customer who has just proved they have WeChat open should not have to find
    /// the next step themselves.
    /// </para>
    /// </summary>
    private GatePage ContinueAfterLogin(Window owner, Action<string>? log)
    {
        var session = _gateway.EnsureSessionAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (!session.Succeeded)
        {
            log?.Invoke($"activation: signed in, but the session is not usable ({session.Failure})");
            return GatePage.Login;
        }

        return ShowPurchase(owner, log);
    }

    private GatePage ShowPurchase(Window owner, Action<string>? log)
    {
        using var controller = new ActivationController(
            _gateway.Activation,
            _gateway.InstallId,
            _gateway.AnchorDigest,
            onLicensed: _gateway.RefreshLicence,
            log: log);

        var window = new PurchaseWindow(controller) { Owner = owner };
        window.ShowDialog();

        // The gate caches its answer, so a licence written while this window was open
        // has to be told. Without this the app keeps telling a customer who just paid
        // that they are not licensed until they restart.
        if (controller.IsLicensed)
        {
            _gateway.RefreshLicence();
            log?.Invoke("activation: licence written and re-read");
        }

        return controller.IsLicensed ? GatePage.None : GatePage.Purchase;
    }

    /// <summary>
    /// Remembers that the page has been offered. Written even when the user bought
    /// nothing: the flag is "has been offered", not "has bought", and re-prompting a
    /// customer who already declined is the behaviour this exists to stop.
    /// </summary>
    private void MarkShown()
    {
        try
        {
            var settings = _settings.Load();
            if (settings.ActivationGateShown)
                return;
            settings.ActivationGateShown = true;
            _settings.Save(settings);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A gate flag we could not write means the page may be offered twice.
            // That is a worse outcome than writing it, but not a failure worth
            // interrupting startup for.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
    }
}
