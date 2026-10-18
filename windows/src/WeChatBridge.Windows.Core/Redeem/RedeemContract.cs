using System.Text.Json.Serialization;

namespace WeChatBridge.Windows.Core.Redeem;

/// <summary>
/// Where the redemption endpoint is, and what the client sends it.
/// <para>
/// <b>Every constant in this class is a placeholder.</b> <c>.invalid</c> is a reserved
/// TLD that can never resolve (RFC 2606), so a build left as it is fails loudly at the
/// first redemption instead of quietly talking to somebody else's server. Replace the
/// three constants below with the real ones before the buy-out edition ships.
/// </para>
/// <para>
/// The endpoint is a <em>burn</em>, not a trade. It takes a one-time code and destroys
/// it. The earlier draft of this contract was <c>POST /v1/license/activate</c> taking a
/// reusable <c>licenseKey</c>, which is four different products' worth of trouble: a
/// leaked key becomes a permanent minting right; "used on another machine" and "used on
/// two machines" become indistinguishable; a lost response is unrecoverable because
/// there is no server state to consult; and nothing is ever burned, so there is no
/// "already used" for support to quote. One code, one <c>(code, installId)</c> pair,
/// one licence — that is the invariant, and it is what the idempotency key exists to
/// make survivable.
/// </para>
/// </summary>
public static class RedeemEndpoint
{
    /// <summary>PLACEHOLDER — replace with the production base address before shipping.</summary>
    public const string BaseAddress = "https://api.wechatbridge.invalid/";

    /// <summary>The one path the buy-out edition is ever allowed to reach.</summary>
    public const string Path = "v1/license/redeem";

    /// <summary>PLACEHOLDER base address and path, joined at compile time.</summary>
    public const string AbsoluteUrl = BaseAddress + Path;

    /// <summary>
    /// PLACEHOLDER — the product the code must be for. Sent as <c>productId</c> so a
    /// code minted for the subscription edition cannot be spent here, and so the
    /// server's ledger says which product the sale belongs to. §3.1's body does not
    /// list it; it is added here as the one field still to confirm with the server,
    /// because a code that redeems the wrong product is not recoverable after the burn.
    /// </summary>
    public const string ProductId = "wechatbridge-windows-buyout";

    /// <summary>
    /// How long one attempt may take before the client gives up and offers Retry.
    /// <para>
    /// Not an exponential backoff and not an automatic retry. This is a one-shot
    /// action the user took deliberately; retrying it behind their back would make
    /// "the app sent a request I never asked for" indistinguishable from silence, and
    /// that is the opposite of the promise the buy-out edition makes.
    /// </para>
    /// </summary>
    public static TimeSpan DefaultTimeout => TimeSpan.FromSeconds(30);
}

/// <summary>
/// The request body. Field names are pinned with attributes rather than a naming
/// policy, for the same reason <c>LicenseEnvelope</c> pins its own: this is a contract
/// with a server that has not been written yet, and the first thing that will change
/// it is a serialiser's naming policy on one side.
/// </summary>
public sealed record RedeemRequest(
    [property: JsonPropertyName("redeemCode")] string RedeemCode,
    [property: JsonPropertyName("installId")] string InstallId,
    [property: JsonPropertyName("deviceDigest")] string DeviceDigest,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("productId")] string ProductId);

/// <summary>Status values that mean the caller now holds a licence.</summary>
public static class RedeemStatus
{
    /// <summary>The code was burned on this request and a licence was issued.</summary>
    public const string Redeemed = "redeemed";

    /// <summary>This exact <c>requestId</c> already burned the code; the original envelope is being returned.</summary>
    public const string AlreadyRedeemed = "alreadyRedeemed";
}

/// <summary>
/// Rejection codes the server may put in <see cref="RedeemResponse.Reason"/> when the
/// status is not one of <see cref="RedeemStatus"/>.
/// <para>
/// None of these is retryable. Every one of them means the user's next action is to
/// enter a different code or to contact support, never to press the button again —
/// which is why the UI swaps its button for those cases rather than showing an error
/// with a Retry on it.
/// </para>
/// </summary>
public static class RedeemReason
{
    /// <summary>No such code, or not one this product's ledger holds.</summary>
    public const string InvalidCode = "invalidCode";

    /// <summary>Burned on a different <c>requestId</c>, which means a different redemption.</summary>
    public const string AlreadyRedeemedElsewhere = "alreadyRedeemedElsewhere";

    /// <summary>Past its 90-day window. Support can extend it; nobody can self-serve it.</summary>
    public const string Expired = "expired";

    /// <summary>Voided by support.</summary>
    public const string Revoked = "revoked";

    /// <summary>A real code, for the other product.</summary>
    public const string ProductMismatch = "productMismatch";

    /// <summary>Too many attempts against this code or this address.</summary>
    public const string RateLimited = "rateLimited";
}

/// <summary>The response body.</summary>
public sealed record RedeemResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("licenseToken")] string LicenseToken,
    [property: JsonPropertyName("licenseId")] string LicenseId,
    [property: JsonPropertyName("redeemedAt")] DateTimeOffset? RedeemedAt,
    [property: JsonPropertyName("reason")] string? Reason)
{
    /// <summary>
    /// Whether this status hands the caller a licence. Unknown statuses do not: a
    /// server that grows a new one must not have this client treat it as success and
    /// write <c>null</c> to <c>license.json</c>.
    /// </summary>
    public bool GrantsLicense =>
        string.Equals(Status, RedeemStatus.Redeemed, StringComparison.Ordinal)
        || string.Equals(Status, RedeemStatus.AlreadyRedeemed, StringComparison.Ordinal);
}
