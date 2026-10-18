namespace WeChatBridge.Windows.Core.Licensing;

/// <summary>
/// Decides whether this directory is entitled to Pro, and leaves the machine in a
/// consistent state afterwards.
/// <para>
/// The state machine has three invariants, and each exists because of a specific way
/// the obvious version breaks a paying customer:
/// </para>
/// <list type="number">
/// <item>
/// <b>When the identity blob opens and matches, the hardware anchor never vetoes.</b>
/// Opening it means DPAPI released the secret for this machine and this Windows user,
/// which is itself a machine-binding proof. Requiring the anchor on top of that only
/// evicts users who changed motherboard, updated their BIOS, or whose hardware query
/// failed — while adding nothing, because the attacker cannot open the blob either.
/// </item>
/// <item>
/// <b>The anchor is only a recovery criterion, used when the blob cannot be opened.</b>
/// That is precisely the shape of "the whole Config directory was copied somewhere
/// else", and it is the entire protection this layer offers against a moved licence.
/// </item>
/// <item>
/// <b>Recovery reads only <c>license.json</c> and the anchor computed now.</b> It never
/// consults anything the blob held, so deleting the blob is always safe and an
/// interrupted recovery can always simply be run again. No transaction log, no
/// half-state to reconcile on next launch.
/// </item>
/// </list>
/// <para>
/// What this does not defend against is a full image clone that copies the registry
/// and the user profile together: that keeps the SID, so DPAPI opens. Closing it would
/// mean checking the anchor even on the matching-blob path, at the cost of a motherboard
/// swap requiring a support call. That trade is not worth making, and the licensing
/// text says so out loud rather than implying a protection that is not there.
/// </para>
/// </summary>
public sealed class LicenseResolver
{
    private readonly LicenseStore _licenses;
    private readonly InstallIdentityStore _identity;
    private readonly LicenseVerifier _verifier;
    private readonly IMachineFactsReader _machineFacts;
    private readonly LicenseRuntimeOptions _options;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string>? _log;

    public LicenseResolver(
        LicenseStore licenses,
        InstallIdentityStore identity,
        LicenseVerifier verifier,
        IMachineFactsReader machineFacts,
        LicenseRuntimeOptions? options = null,
        Func<DateTimeOffset>? clock = null,
        Action<string>? log = null)
    {
        _licenses = licenses;
        _identity = identity;
        _verifier = verifier;
        _machineFacts = machineFacts;
        _options = options ?? LicenseRuntimeOptions.Installed;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = log;
    }

    public LicenseResolution Resolve()
    {
        var now = _clock();

        // Portable is the free tier of the same product: fully usable, Pro gated off,
        // and it never writes an identity. A stick that mints one on every machine it
        // visits is a stick that eventually hands a real licence to the wrong one.
        if (!_options.CanBindToDevice)
            return new LicenseResolution(false, LicenseActivation.None, AnchorEvidence.NoEvidence, null, false, "portable");

        var envelope = _licenses.Load();
        if (envelope is null)
        {
            var existing = _identity.Load();
            if (existing is null)
                existing = EnsureIdentity(now);
            else
                _identity.SaveAnchor(_machineFacts.Read());
            return new LicenseResolution(false, LicenseActivation.None, AnchorEvidence.NoEvidence, existing.InstallId, false, "no license");
        }

        var verification = _verifier.Verify(envelope, now, _options.ClockSkewTolerance);
        if (!verification.IsValid)
        {
            // A licence that does not verify is not repaired, rewritten or retried. The
            // local identity is left exactly as it was.
            var keep = _identity.Load();
            _log?.Invoke($"license rejected: {verification.Failure}");
            return new LicenseResolution(false, LicenseActivation.Unverifiable, AnchorEvidence.NoEvidence, keep?.InstallId, false, verification.Failure);
        }

        var subject = verification.License!.Subject;
        var local = _identity.Load();

        if (local is not null && string.Equals(local.InstallId, subject, StringComparison.Ordinal))
        {
            // Invariant 1. The blob opened here, so this is the machine; the anchor is
            // not read and nothing is written.
            return new LicenseResolution(true, LicenseActivation.Matched, AnchorEvidence.SameMachine, subject, false, "identity blob opened on this machine");
        }

        var observed = _machineFacts.Read();
        var evidence = DeviceAnchor.Compare(_identity.LoadAnchor(), observed);

        if (evidence == AnchorEvidence.DifferentMachine)
        {
            // The one refusal in the layer, and it never touches the local identity:
            // a foreign directory must not be able to relabel this machine, or a
            // refused copy would quietly become a valid install on the next launch.
            _log?.Invoke("license refused: hardware anchor says another machine");
            // Minting an identity here is not overwriting one: there is none to
            // preserve, and the machine still needs one to buy against later. The
            // anchor, though, is exactly what must NOT be refreshed: writing this
            // machine's own facts over the foreign ones would make the *next* launch
            // find two matching anchors, read SameMachine, and take the Adopted path
            // — turning a refusal that lasts one launch into no refusal at all.
            // Support clears the licence (see LicenseStore.Clear), after which the
            // no-licence branch below reclaims the anchor for this machine.
            var keep = local ?? EnsureIdentity(now, recordAnchor: false);
            return new LicenseResolution(
                false,
                LicenseActivation.Rejected,
                evidence,
                keep.InstallId,
                false,
                "anchor belongs to another machine");
        }

        if (evidence == AnchorEvidence.NoEvidence)
        {
            // Invariant 3, in its most important form: no evidence is not guilt. The
            // realistic case is a paid customer who reinstalled Windows on a machine
            // whose registry the app cannot read. Denying them costs the purchase;
            // the opposite mistake costs one entry against the server's device cap.
            _log?.Invoke("license admitted without hardware evidence; recovery path taken");
        }

        var activation = local is null ? LicenseActivation.Recovered : LicenseActivation.Adopted;
        var firstSeen = local?.FirstSeenAt ?? now;
        _identity.Save(new InstallIdentity(subject, firstSeen));
        _identity.SaveAnchor(observed);
        _log?.Invoke($"license {activation.ToString().ToLowerInvariant()} from anchor evidence {evidence}");
        return new LicenseResolution(true, activation, evidence, subject, evidence == AnchorEvidence.NoEvidence, activation.ToString().ToLowerInvariant());
    }

    /// <summary>
    /// Mints the identity an install has from its first launch, before anyone has
    /// bought anything. The user never sees it; it exists so that when they do buy,
    /// there is something to bind a licence to.
    /// </summary>
    /// <param name="recordAnchor">
    /// <c>false</c> on the refusal path, where the stored anchor is the only remaining
    /// record that this licence was not bought here and must survive untouched.
    /// </param>
    private InstallIdentity EnsureIdentity(DateTimeOffset now, bool recordAnchor = true)
    {
        var identity = new InstallIdentity(InstallIdentityStore.NewInstallId(), now);
        _identity.Save(identity);
        if (recordAnchor)
            _identity.SaveAnchor(_machineFacts.Read());
        _log?.Invoke("install identity created");
        return identity;
    }
}
