using System.IO;
using WeChatBridge.Windows.Activation;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Activation;
using WeChatBridge.Windows.Core.Licensing;
using WeChatBridge.Windows.Core.Redeem;

namespace WeChatBridge.Windows.Services;

/// <summary>
/// Wires the buy-out purchase flow to the running application: where the licence
/// service is, the account session, the activation calls, and the single value the
/// startup gate branches on.
/// <para>
/// Everything is built lazily. A customer who is already licensed — which is most
/// runs of this app, after the first day — must not so much as construct an
/// <c>HttpClient</c>, because "0 network requests per launch" is a promise the
/// privacy wording makes and an allocation is not a request but a reader of this code
/// will have to check.
/// </para>
/// <para>
/// Nothing here decides what a licence entitles. That stays
/// <see cref="LicensingBootstrap.IsPro"/>, which is the one value the UI is allowed
/// to gate on; this class only decides which page to open.
/// </para>
/// </summary>
internal sealed class ActivationGateway : IDisposable
{
    private readonly LicensingBootstrap _licensing;
    private readonly Func<string?>? _baseAddressOverride;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private IActivationTransport? _transport;
    private AuthTokenStore? _tokenStore;
    private AccountService? _account;
    private ActivationService? _activation;
    private bool _disposed;

    private ActivationGateway(
        LicensingBootstrap licensing,
        Func<string?>? baseAddressOverride,
        Action<string>? log)
    {
        _licensing = licensing;
        _baseAddressOverride = baseAddressOverride;
        _log = log;
    }

    public static ActivationGateway Current { get; } = new(LicensingBootstrap.Current, null, null);

    /// <summary>Builds a gateway over a specific licensing stack, for tests.</summary>
    internal static ActivationGateway Create(
        LicensingBootstrap licensing,
        Func<string?>? baseAddressOverride = null,
        Action<string>? log = null) =>
        new(licensing, baseAddressOverride, log);

    /// <summary>This machine's id, or null before the resolver has minted one.</summary>
    public string? InstallId => _licensing.Resolution.InstallId;

    /// <summary>
    /// The hardware anchor's digest.
    /// <para>
    /// Shown next to the install id so a seller can tell a re-install from a
    /// different machine, and sent alongside it for the same reason. It is evidence,
    /// not identity: MAC addresses change with the network card, so anything bound to
    /// this would stop working for a customer who changed a part.
    /// </para>
    /// </summary>
    public string AnchorDigest
    {
        get
        {
            try
            {
                return RedeemMachineIdentity.DigestOf(RegistryMachineFacts.CreateDefault().Read());
            }
            catch (IOException)
            {
                // The registry read can fail on a locked-down machine. An empty digest
                // is honest: the seller sees that there is no corroborating evidence,
                // which is better than a fabricated one.
                return string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>Whether this build can be licensed at all.</summary>
    public bool IsPortable => _licensing.IsPortable;

    /// <summary>
    /// The session this machine already holds.
    /// <para>
    /// Held separately from <see cref="Account"/> on purpose: reading it must not
    /// build the transport. <see cref="Snapshot"/> is called from the gate on every
    /// state change, and the licensed customer it short-circuits for is the common
    /// case — so the cheap path has to genuinely be cheap. It re-reads rather than
    /// caching, because <see cref="AccountService.SignIn"/> writes the store from the
    /// other direction and a second cache would be a staleness bug waiting for the
    /// first sign-out.
    /// </para>
    /// </summary>
    public AuthTokens? StoredTokens
    {
        get
        {
            lock (_gate)
                return TokenStore.Load();
        }
    }

    /// <summary>Whether a bearer token is held and still usable.</summary>
    public bool IsSignedIn => StoredTokens is { } tokens && tokens.IsUsableAt(DateTimeOffset.UtcNow);

    private AuthTokenStore TokenStore
    {
        get
        {
            lock (_gate)
            {
                return _tokenStore ??= new AuthTokenStore(_licensing.Layout.Config, new DpapiSecretProtector());
            }
        }
    }

    /// <summary>The account session. Built on first use.</summary>
    public AccountService Account
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _account ??= new AccountService(
                    Transport(),
                    TokenStore,
                    PollSchedule.Login,
                    log: _log);
            }
        }
    }

    /// <summary>The activation calls. Built on first use.</summary>
    public ActivationService Activation
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _activation ??= new ActivationService(
                    _licensing.Store,
                    new LicenseVerifier(),
                    Transport(),
                    Account,
                    log: _log);
            }
        }
    }

    /// <summary>Everything the gate needs to choose a page, as one value.</summary>
    public GateSnapshot Snapshot()
    {
        var state = _licensing.Resolution;

        // The licence comes first, and it comes from disk. A customer who bought a
        // licence is entitled whatever their account session says, including when the
        // service is down and the refresh token has aged out — the whole point of
        // verifying offline is that the answer survives the network not being there.
        if (state.IsPro)
            return new GateSnapshot(GateState.Licensed, true, IsSignedIn, state.InstallId, string.Empty, StoredTokens?.Account.DisplayName);

        if (_licensing.IsPortable)
            return new GateSnapshot(GateState.Portable, false, false, null, string.Empty, null);

        // Only now is the hardware anchor worth reading: it names a machine for the
        // seller, and a machine that is not buying anything does not need one.
        var snapshot = new GateSnapshot(
            state.Activation == LicenseActivation.Unverifiable ? GateState.LicenseUnverifiable : GateState.Anonymous,
            false,
            IsSignedIn,
            state.InstallId,
            AnchorDigest,
            StoredTokens?.Account.DisplayName);

        if (_licensing.NeedsManualUnbind)
            return snapshot with { State = GateState.Rejected };
        return IsSignedIn ? snapshot with { State = GateState.SignedIn } : snapshot;
    }

    /// <summary>
    /// Makes sure there is a usable bearer token, refreshing the stored session if
    /// the access token has aged out. Returns the outcome either way; the caller
    /// decides what a signed-out user should see.
    /// <para>
    /// This is the one place in the app that reaches for the network without the user
    /// asking for something on screen, and it is deliberate and narrow: an expired
    /// token with a valid refresh token is the normal state of a customer who logged
    /// in once. It is bounded by the refresh deadline and it never loops.
    /// </para>
    /// </summary>
    public async Task<LoginOutcome> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        var account = Account;
        if (account.IsSignedIn)
            return LoginOutcome.Success(account.Current!);
        if (account.Current is null)
            return LoginOutcome.Failed(LoginFailure.SessionExpired, "尚未登录。");
        return await account.RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-reads the licence file after one was written, so the gate sees the change
    /// without a restart.
    /// </summary>
    public void RefreshLicence() => _licensing.Refresh();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _transport?.Dispose();
    }

    private IActivationTransport Transport()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _transport ??= new HttpActivationTransport(ResolveBaseAddress(), null);
        }
    }

    /// <summary>
    /// Where the licence service lives.
    /// <para>
    /// A reserved <c>.invalid</c> placeholder by default, which cannot resolve — so a
    /// build that shipped this would fail loudly rather than talk to a stranger. An
    /// override, whether from a test or an environment variable, is the only way to
    /// point it elsewhere; there is no settings-file entry, because a base address a
    /// customer can edit is a base address that can be edited to somewhere else, and
    /// the bearer token goes with it.
    /// </para>
    /// </summary>
    private Uri ResolveBaseAddress()
    {
        var configured = _baseAddressOverride?.Invoke()
            ?? Environment.GetEnvironmentVariable(ActivationEndpoint.BaseAddressEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(configured) && Uri.TryCreate(configured, UriKind.Absolute, out var uri))
        {
            _log?.Invoke($"activation: licence service at {uri}");
            return uri;
        }

        if (!string.IsNullOrWhiteSpace(configured))
            _log?.Invoke($"activation: '{configured}' is not a usable address; falling back to the placeholder.");

        return new Uri(ActivationEndpoint.DefaultBaseAddress);
    }
}
