namespace WeChatBridge.Windows.Core.Licensing;

/// <summary>How the current directory ended up entitled, or why it did not.</summary>
public enum LicenseActivation
{
    /// <summary>No licence file. A fresh install, or the portable free tier.</summary>
    None,

    /// <summary>A licence file exists but did not verify. Never Pro, and never repaired.</summary>
    Unverifiable,

    /// <summary>The local identity opened and matches the licence subject. Nothing written.</summary>
    Matched,

    /// <summary>A different identity was present and the anchor did not contradict it; the licence subject wins.</summary>
    Adopted,

    /// <summary>No identity could be opened; the licence subject was restored.</summary>
    Recovered,

    /// <summary>The anchor contradicts this machine. Refused, and the local identity is left untouched.</summary>
    Rejected,
}

/// <summary>
/// What the resolver decided.
/// </summary>
/// <param name="IsPro">The only value the UI gates Pro features on.</param>
/// <param name="Activation">Which branch of the state machine produced this.</param>
/// <param name="Evidence">What the hardware anchor said. Never consulted when the identity blob opened.</param>
/// <param name="InstallId">The local install id after the call, or <c>null</c> if none could be established.</param>
/// <param name="Degraded">
/// Set when the decision was made without hardware evidence. Worth surfacing: it is
/// the one path where a genuinely foreign directory and a genuinely reinstalled one
/// are indistinguishable.
/// </param>
/// <param name="Detail">Machine-readable reason for logs and the diagnostics screen.</param>
public sealed record LicenseResolution(
    bool IsPro,
    LicenseActivation Activation,
    AnchorEvidence Evidence,
    string? InstallId,
    bool Degraded,
    string? Detail)
{
    /// <summary>The UI's manual-unbind path: the licence belongs to another machine.</summary>
    public bool NeedsManualUnbind => Activation == LicenseActivation.Rejected;
}

/// <summary>
/// Build-shaped policy the resolver must not guess at.
/// </summary>
/// <param name="CanBindToDevice">
/// False for the portable build. Portable is the free tier of the buyout product: it
/// has no login path and is issued no licences, so it must not mint an install
/// identity either. A stick that travels between machines would otherwise collect an
/// identity from each of them.
/// </param>
/// <param name="ClockSkewTolerance">How wrong the clock may be before it counts against the licence.</param>
public sealed record LicenseRuntimeOptions(bool CanBindToDevice, TimeSpan ClockSkewTolerance)
{
    /// <summary>An installed build, which binds to its machine.</summary>
    public static LicenseRuntimeOptions Installed { get; } = new(true, TimeSpan.FromDays(30));

    /// <summary>The portable build: free tier, no identity, no anchor.</summary>
    public static LicenseRuntimeOptions Portable { get; } = new(false, TimeSpan.FromDays(30));
}
