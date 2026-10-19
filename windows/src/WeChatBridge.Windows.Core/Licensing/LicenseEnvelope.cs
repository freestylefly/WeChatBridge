using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace WeChatBridge.Windows.Core.Licensing;

/// <summary>
/// The signed licence as it sits on disk. Every field name is pinned with
/// <see cref="JsonPropertyNameAttribute"/> rather than a naming policy: the wire format
/// is a contract with the server that issues these, and a policy change on either
/// side would silently break every licence already in the field.
/// </summary>
public sealed record LicenseEnvelope
{
    /// <summary>Format version. Selects the verification key — see <see cref="LicensePublicKeys"/>.</summary>
    [JsonPropertyName("v")]
    public int SchemaVersion { get; init; } = LicenseToken.CurrentSchemaVersion;

    /// <summary>The install id this licence is bound to. Never stored in the clear.</summary>
    [JsonPropertyName("sub")]
    public string Subject { get; init; } = string.Empty;

    /// <summary>Product plan, e.g. <c>buyout</c>.</summary>
    [JsonPropertyName("plan")]
    public string Plan { get; init; } = string.Empty;

    [JsonPropertyName("iat")]
    public DateTimeOffset IssuedAt { get; init; }

    /// <summary>Earliest valid instant, or <c>null</c> for a perpetual buyout licence.</summary>
    [JsonPropertyName("nbf")]
    public DateTimeOffset? NotBefore { get; init; }

    /// <summary>Expiry, or <c>null</c>. A buyout licence has none — see §10.10.</summary>
    [JsonPropertyName("exp")]
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Entitlement bits. Order carries no meaning; the payload sorts them.</summary>
    [JsonPropertyName("ent")]
    public IReadOnlyList<string> Entitlements { get; init; } = [];

    /// <summary>base64url ECDSA P-256 / SHA-256 signature over <see cref="LicenseCanonicalPayload"/>, IeeeP1363 fixed field.</summary>
    [JsonPropertyName("sig")]
    public string Signature { get; init; } = string.Empty;
}

/// <summary>
/// Formatting constants for the licence format. Shared by the canonical payload and
/// the verifier so the two cannot drift.
/// </summary>
public static class LicenseToken
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Field count of a canonical payload, including the domain separator line.</summary>
    public const int CanonicalLineCount = 8;

    /// <summary>Signature length: two 32-byte integers, fixed field concatenation.</summary>
    public const int SignatureLength = 64;
}

/// <summary>
/// The exact bytes the signature covers.
/// <para>
/// The signature is never taken over the JSON text. Field order, casing and
/// whitespace all change when the server's serialiser changes, and every licence in
/// the field would fail at once — with a signature that is perfectly valid, which is
/// close to undiagnosable in the field. This record fixes an order, one field per
/// line, writes a sentinel for every absent value so the line count never moves, and
/// sorts the entitlement bits before joining them.
/// </para>
/// <para>
/// The server must produce byte-identical output from the same licence. That is why
/// the rules are this small and this explicit, and why the test suite pins a shared
/// vector: a mismatch between the two sides shows up as "every customer's Pro
/// expired at once" and nothing else.
/// </para>
/// </summary>
public sealed record LicenseCanonicalPayload(
    int SchemaVersion,
    string Subject,
    string Plan,
    DateTimeOffset IssuedAt,
    DateTimeOffset? NotBefore,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> Entitlements)
{
    /// <summary>Domain separation, and a tripwire for a payload built by the wrong format.</summary>
    public const string DomainSeparator = "wechatbridge.license.v1";

    /// <summary>The value written for an absent field, so the line count is constant.</summary>
    public const string Absent = "-";

    public static LicenseCanonicalPayload From(LicenseEnvelope envelope) =>
        new(
            envelope.SchemaVersion,
            envelope.Subject,
            envelope.Plan,
            envelope.IssuedAt,
            envelope.NotBefore,
            envelope.ExpiresAt,
            envelope.Entitlements);

    /// <summary>The canonical text, without a trailing newline.</summary>
    public string Canonical =>
        string.Join('\n',
            DomainSeparator,
            $"v={SchemaVersion.ToString(CultureInfo.InvariantCulture)}",
            $"sub={Subject}",
            $"plan={Plan}",
            $"iat={Unix(IssuedAt)}",
            $"nbf={Optional(NotBefore)}",
            $"exp={Optional(ExpiresAt)}",
            $"ent={NormalizedEntitlements}");

    public byte[] Bytes => Encoding.UTF8.GetBytes(Canonical);

    public override string ToString() => Canonical;

    /// <summary>
    /// Sorted, upper-cased and de-duplicated, comma joined. Two servers that build the
    /// entitlement list in different orders — which is what any set iteration will do —
    /// still produce the same signed bytes.
    /// </summary>
    public string NormalizedEntitlements
    {
        get
        {
            var normalized = Entitlements
                .Where(bit => !string.IsNullOrWhiteSpace(bit))
                .Select(bit => bit.Trim().ToUpperInvariant())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            return normalized.Count == 0 ? Absent : string.Join(",", normalized);
        }
    }

    /// <summary>
    /// Unix seconds, never a formatted timestamp. Anything culture- or zone-dependent
    /// is a licence that verifies on the machine that issued it and nowhere else.
    /// </summary>
    private static string Unix(DateTimeOffset value) =>
        value.ToUniversalTime().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private static string Optional(DateTimeOffset? value) =>
        value is null ? Absent : Unix(value.Value);
}
