using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Licensing;

namespace WeChatBridge.Windows.Services;

/// <summary>
/// The one place the licence layer is wired to the running application.
/// <para>
/// Everything the stack needs is decided here rather than in each caller, because
/// every one of these choices is a product decision and a caller that picks its own
/// would pick a different one: the config directory comes from
/// <see cref="AppLayout"/> so a portable build keeps its state on the stick; the
/// protector is DPAPI because that is the only secret storage this machine has; the
/// machine facts come from the registry through
/// <see cref="RegistryMachineFacts"/>; and portable mode is translated into
/// <see cref="LicenseRuntimeOptions.Portable"/> before the resolver ever runs.
/// </para>
/// <para>
/// Construction is cheap and does no I/O. The first read of
/// <see cref="Resolution"/> performs the resolve — which may mint an install identity
/// on an installed build — and every later read is the cached answer, so a UI binding
/// can poll it without re-entering the state machine.
/// </para>
/// </summary>
internal sealed class LicensingBootstrap
{
    private readonly LicenseResolver _resolver;
    private readonly LicenseRuntimeOptions _options;
    private readonly object _gate = new();
    private LicenseResolution? _resolution;

    private LicensingBootstrap(
        AppLayout layout,
        LicenseStore licenses,
        LicenseResolver resolver,
        LicenseRuntimeOptions options)
    {
        Layout = layout;
        Store = licenses;
        _resolver = resolver;
        _options = options;
    }

    /// <summary>
    /// The instance the application uses. Built over <see cref="AppLayout.Current"/>,
    /// so it follows whichever mode the layout resolved to — installed or portable.
    /// </summary>
    public static LicensingBootstrap Current { get; } = Create(AppLayout.Current);

    public static LicensingBootstrap Create(AppLayout layout, Action<string>? log = null)
    {
        // Every path below is derived from the layout. Naming %LOCALAPPDATA% here
        // would put a portable build's identity on the machine it happens to be
        // plugged into, which is the split AppLayout exists to prevent.
        var configDirectory = layout.Config;
        var licenses = new LicenseStore(configDirectory);
        var options = layout.IsPortable ? LicenseRuntimeOptions.Portable : LicenseRuntimeOptions.Installed;

        // Portable is the free tier of the buy-out product, and it has no login path
        // and is issued no licences — so it must not leave a trace either. Passing
        // LicenseRuntimeOptions.Portable makes the resolver return before it reads the
        // licence, mints an identity, records an anchor or touches the registry, and
        // the protector and machine reader below are simply never reached. They are
        // still constructed: building them allocates nothing and opens no file, and
        // keeping the construction identical to the installed case means portable mode
        // is one option rather than a second wiring that can drift from the first.
        var identity = new InstallIdentityStore(configDirectory, new DpapiSecretProtector());
        var verifier = new LicenseVerifier();
        var machineFacts = RegistryMachineFacts.CreateDefault();
        var resolver = new LicenseResolver(licenses, identity, verifier, machineFacts, options, log: log);

        return new LicensingBootstrap(layout, licenses, resolver, options);
    }

    public AppLayout Layout { get; }

    /// <summary>
    /// The signed licence file's home, so a redeem page can write an envelope and then
    /// call <see cref="Refresh"/>. Exposed rather than a save method because the
    /// Redeem layer owns the whole acceptance flow and duplicating half of it here
    /// would give two places to change.
    /// </summary>
    public LicenseStore Store { get; }

    /// <summary>
    /// What this directory is entitled to. Resolved on first read and cached; the
    /// answer cannot change while the app runs except through <see cref="Refresh"/>.
    /// </summary>
    public LicenseResolution Resolution
    {
        get
        {
            lock (_gate)
                return _resolution ??= _resolver.Resolve();
        }
    }

    /// <summary>
    /// The only value the UI is allowed to gate a Pro feature on.
    /// <see cref="LicenseResolution"/> carries the branch that produced it, the
    /// install id and the evidence — all of which belong in the diagnostics screen and
    /// in the log, none of which belong in an <c>if</c>.
    /// </summary>
    public bool IsPro => Resolution.IsPro;

    /// <summary>
    /// The licence belongs to another machine and support has to clear it.
    /// <see cref="LicenseResolution.NeedsManualUnbind"/>.
    /// </summary>
    public bool NeedsManualUnbind => Resolution.NeedsManualUnbind;

    /// <summary>
    /// Whether the decision was made without hardware evidence — the one path where a
    /// copied directory and a reinstalled machine look the same. Worth surfacing in
    /// support, never worth refusing on.
    /// </summary>
    public bool IsDegraded => Resolution.Degraded;

    /// <summary>
    /// Sharing from WeChat must keep working without a licence.
    /// <para>
    /// This is a product rule, not an observation: a licence gates the paid
    /// <em>extras</em> and nothing else. The entry picker, the forward, the write to
    /// the destination and the history all run exactly as they do on a free tier,
    /// because the product's promise is that forwarding works, and a customer whose
    /// Pro lapsed and who discovers that their WeChat messages stopped arriving has
    /// been sold a downgrade they never bought.
    /// </para>
    /// <para>
    /// It is stated here, in one place that is always true, so that a future caller
    /// gating delivery on <see cref="IsPro"/> is contradicting a property rather than
    /// quietly breaking a rule that existed only in someone's memory. Unlicensed,
    /// portable and rejected all return <c>true</c>; a licence failure is never a
    /// reason to stop forwarding.
    /// </para>
    /// </summary>
    public bool IsShareDeliveryAvailable => true;

    /// <summary>
    /// Re-reads the licence after the user has accepted one. Cheap, and the only way
    /// the answer changes while the app is running.
    /// </summary>
    public LicenseResolution Refresh()
    {
        lock (_gate)
            return _resolution = _resolver.Resolve();
    }

    /// <summary>
    /// What this build is. Constants rather than a value read back from disk, so the
    /// answer cannot be edited by anyone holding the file — see <see cref="Edition"/>.
    /// </summary>
    public string EditionName => Edition.Name;

    /// <summary>Whether this build has an account surface at all.</summary>
    public bool HasAccount => Edition.HasAccount;

    /// <summary>
    /// The one place the portable/installed decision is expressed to the licence
    /// layer. Portable is decided by the layout, once, and handed over as policy —
    /// <c>LicenseResolver</c> never inspects the layout itself.
    /// </summary>
    public bool IsPortable => !_options.CanBindToDevice;
}
