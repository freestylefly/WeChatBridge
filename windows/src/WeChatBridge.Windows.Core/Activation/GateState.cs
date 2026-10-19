namespace WeChatBridge.Windows.Core.Activation;

/// <summary>
/// Where this installation stands in the buy-out flow. The client state machine in
/// full, as one value, so a caller cannot read half of it.
/// <para>
/// The order matters: these are the states in the sequence a customer walks through,
/// and the gate decides what to show from the value alone.
/// </para>
/// </summary>
public enum GateState
{
    /// <summary>Not resolved yet. The gate must not guess; ask <see cref="GateSnapshot"/>.</summary>
    Unknown = 0,

    /// <summary>
    /// Portable mode. The free tier: no licence is issued to a stick, none is asked
    /// for, and none is refused. This is not the same as unlicensed, and treating it
    /// as such would nag every portable user to buy something they cannot use.
    /// </summary>
    Portable = 1,

    /// <summary>
    /// A verified buy-out licence on disk. The endpoint of the flow — and the reason
    /// every later state exists.
    /// </summary>
    Licensed = 2,

    /// <summary>Installed, no licence, not signed in. Show the login page.</summary>
    Anonymous = 3,

    /// <summary>
    /// Installed, signed in, no licence. The customer can buy; nothing is locked
    /// behind a licence they have not paid for.
    /// </summary>
    SignedIn = 4,

    /// <summary>
    /// A licence is on disk but belongs to another machine. Support has to clear it.
    /// Distinct from <see cref="Anonymous"/> because the fix is a re-sign, not a login.
    /// </summary>
    Rejected = 5,

    /// <summary>
    /// Installed, not signed in, and a licence file exists that did not verify.
    /// Telling this apart from <see cref="Anonymous"/> is the difference between
    /// "scan a code" and "contact support" in the user's first ten seconds.
    /// </summary>
    LicenseUnverifiable = 6,
}

/// <summary>
/// Everything the gate needs to choose what to show, in one value.
/// <para>
/// A record rather than three properties on purpose. The states are not independent:
/// signed in, has a licence and is portable cannot be mixed and matched, and a
/// caller reading them one at a time is one refactor away from showing a login page
/// to a customer who is already licensed.
/// </para>
/// </summary>
/// <param name="State">The single value every branch should switch on.</param>
/// <param name="IsPro">The licensing layer's verdict. Already-licensed customers never see a gate.</param>
/// <param name="IsSignedIn">Whether a usable bearer token is held.</param>
/// <param name="InstallId">This machine's id — the machine code the seller reads.</param>
/// <param name="AnchorDigest">The hardware anchor's digest — evidence for the seller, never the key.</param>
/// <param name="Nickname">The signed-in account's display name, or null.</param>
public sealed record GateSnapshot(
    GateState State,
    bool IsPro,
    bool IsSignedIn,
    string? InstallId,
    string AnchorDigest,
    string? Nickname)
{
    /// <summary>Whether the purchase page can be opened at all.</summary>
    public bool CanPurchase => (State is GateState.SignedIn or GateState.Anonymous) && !IsPro;

    /// <summary>Whether a login page is worth showing.</summary>
    public bool NeedsLogin => State == GateState.Anonymous;
}
