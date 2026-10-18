using WeChatBridge.Windows.Core.Licensing;

namespace WeChatBridge.Windows.Core.Redeem;

/// <summary>Why a redemption did not produce a licence.</summary>
public enum RedeemFailure
{
    /// <summary>Nothing went wrong. Not used on a successful outcome.</summary>
    None,

    /// <summary>The text is not a code: wrong length, wrong prefix, or a character the alphabet does not have.</summary>
    MalformedCode,

    /// <summary>No such code. Retry is pointless; a different code is the fix.</summary>
    InvalidCode,

    /// <summary>
    /// Burned on a different <c>requestId</c>. This is the case that must never be
    /// reported to a user standing at the machine that did it — that would be an
    /// accusation, and a false one when the local record was lost.
    /// </summary>
    AlreadyRedeemedElsewhere,

    /// <summary>Past its 90-day window.</summary>
    Expired,

    /// <summary>Voided by support.</summary>
    Revoked,

    /// <summary>A real code, for the other product.</summary>
    ProductMismatch,

    /// <summary>Too many attempts. Retryable in the sense that time may help, never in the sense that pressing again immediately will.</summary>
    RateLimited,

    /// <summary>The server rejected it with something this build does not model.</summary>
    Rejected,

    /// <summary>No response: connection refused, reset, timed out, or the server answered 5xx.</summary>
    Unreachable,

    /// <summary>This directory has no install identity, so there is nothing to bind a licence to.</summary>
    NoInstallId,

    /// <summary>
    /// A previous redemption for a <em>different</em> code is still unresolved. Starting
    /// a second one would abandon a code the server may already have burned.
    /// </summary>
    PendingMismatch,

    /// <summary>The server returned something this build cannot read.</summary>
    MalformedResponse,

    /// <summary>The envelope came back but does not verify against the compiled-in key.</summary>
    LicenseUnverifiable,

    /// <summary>The envelope verifies but is bound to a different install.</summary>
    SubjectMismatch,
}

/// <summary>
/// What one redemption attempt produced.
/// <param name="Failure">The reason, or <see cref="RedeemFailure.None"/>.</param>
/// <param name="CanRetry">
/// Whether pressing the same button again can help. True only for
/// <see cref="RedeemFailure.Unreachable"/>, and the retry reuses the request id from
/// disk — so it asks the server what it already decided rather than redeeming again.
/// </param>
/// <param name="Detail">Machine-readable reason for the log. Never shown raw.</param>
/// <param name="License">The persisted envelope on success.</param>
/// <param name="RedeemedAt">When the server says it burned the code.</param>
/// </summary>
public sealed record RedeemOutcome(
    RedeemFailure Failure,
    bool CanRetry,
    string Detail,
    LicenseEnvelope? License = null,
    DateTimeOffset? RedeemedAt = null)
{
    public static RedeemOutcome Success(LicenseEnvelope license, DateTimeOffset? redeemedAt, string detail) =>
        new(RedeemFailure.None, false, detail, license, redeemedAt);

    public static RedeemOutcome Failed(RedeemFailure failure, bool canRetry, string detail) =>
        new(failure, canRetry, detail);

    public bool Succeeded => Failure == RedeemFailure.None;
}

/// <summary>
/// Turns an HTTP status and a server reason into a <see cref="RedeemFailure"/>.
/// <para>
/// The split is between <b>retryable</b> and <b>needs a different code</b>, and it is
/// the only classification this flow has. Anything that might have happened on the wire
/// — no connection, a timeout, a 5xx, a 429 — is retryable, because the server may
/// never have seen the request and the client cannot tell. Anything that came back with
/// an answer is not: the user needs a different code, or support, and a Retry button
/// that says "try that exact code again" is a lie in that situation.
/// </para>
/// <para>
/// <c>409</c> is the interesting one. It means the code is burned by a different
/// <c>requestId</c> — the local idempotency record was lost, or the code was genuinely
/// used elsewhere. The server checks its own table first, so reaching a 409 means this
/// really is not this machine's redemption, and the message says "used on another
/// device" rather than "invalid".
/// </para>
/// </summary>
public static class RedeemClassification
{
    /// <summary>
    /// Statuses that leave the outcome unknown. <c>408</c> and <c>429</c> are here
    /// rather than with the other 4xx because both are statements about timing, not
    /// about the code.
    /// </summary>
    public static bool IsRetryableStatus(int statusCode) =>
        statusCode is 408 or 425 or 429 || statusCode >= 500;

    public static bool IsSuccessStatus(int statusCode) =>
        statusCode is 200 or 201;

    /// <summary>Maps a rejection reason to a failure. Unknown reasons are not silently accepted.</summary>
    public static RedeemFailure FromReason(string? reason) => reason switch
    {
        RedeemReason.InvalidCode => RedeemFailure.InvalidCode,
        RedeemReason.AlreadyRedeemedElsewhere => RedeemFailure.AlreadyRedeemedElsewhere,
        RedeemReason.Expired => RedeemFailure.Expired,
        RedeemReason.Revoked => RedeemFailure.Revoked,
        RedeemReason.ProductMismatch => RedeemFailure.ProductMismatch,
        RedeemReason.RateLimited => RedeemFailure.RateLimited,
        _ => RedeemFailure.Rejected,
    };
}
