using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using WeChatBridge.Windows.Core.Licensing;

namespace WeChatBridge.Windows.Core.Redeem;

/// <summary>
/// One redemption code, normalised, with the digest the client stores instead of
/// the code itself.
/// <para>
/// <b>This normalisation table is part of the contract, not an implementation
/// detail.</b> The server normalises the same way before it burns the code and before
/// it looks the code up, and the two must agree character for character: the digest in
/// <c>redeem-pending.json</c> is taken over the canonical form, and the burned row is
/// keyed by the canonical form. If the two tables drift, the symptom is "every code is
/// invalid" with no signature error anywhere to point at — the request succeeds, the
/// server just cannot find the code it was sent. That is why the rules are these few
/// lines of hand-written <c>switch</c> rather than a dependency.
/// </para>
/// <para>
/// The shape is <c>WCB-XXXXX-XXXXX-XXXXX-XXXXX</c>: a fixed human-readable prefix and
/// twenty Crockford Base32 characters in four groups. Crockford's alphabet drops
/// <c>I</c>, <c>L</c>, <c>O</c> and <c>U</c>, which is what makes a code read aloud
/// over support chat survive the trip. There is no check digit on purpose: over HTTPS
/// a check digit only catches typing errors, which the paste button in the UI catches
/// better, and it would add a second place for the server to have a bug.
/// </para>
/// </summary>
public sealed record RedeemCode
{
    /// <summary>The human-readable source marker at the head of every code.</summary>
    public const string Prefix = "WCB";

    /// <summary>Characters per group, after the prefix.</summary>
    public const int GroupLength = 5;

    /// <summary>Number of groups after the prefix.</summary>
    public const int GroupCount = 4;

    /// <summary>Random characters in a code, excluding the prefix.</summary>
    public const int BodyLength = GroupLength * GroupCount;

    /// <summary>
    /// Length of <see cref="Prefix"/>. Spelled out because <c>Prefix.Length</c> is not a
    /// constant expression; a test pins the two together so they cannot drift.
    /// </summary>
    public const int PrefixLength = 3;

    /// <summary>Canonical total length, prefix included and separators excluded.</summary>
    public const int NormalizedLength = PrefixLength + BodyLength;

    /// <summary>The group separator, and the only character the canonical form contains.</summary>
    public const string Separator = "-";

    /// <summary>
    /// Crockford Base32: the ten digits and twenty-two letters, with <c>I L O U</c>
    /// removed. Deliberately hand-written — the whole alphabet is these thirty-two
    /// characters and the map is five lines below.
    /// </summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private RedeemCode(string canonical, string digest) =>
        (Canonical, Digest) = (canonical, digest);

    /// <summary>The one spelling of this code that is ever hashed or sent: <c>WCB-XXXXX-XXXXX-XXXXX-XXXXX</c>.</summary>
    public string Canonical { get; }

    /// <summary>
    /// base64url over SHA-256 of <see cref="Canonical"/>. This is what
    /// <c>redeem-pending.json</c> stores, because that file sits in clear text on a
    /// removable drive and a redemption code in the clear would be a spent-but-still-
    /// valuable credential sitting next to the licence it buys.
    /// </summary>
    public string Digest { get; }

    public override string ToString() => Canonical;

    /// <summary>
    /// Parses any spelling a human might produce — pasted, typed, dictated over
    /// support, lower case, spaces instead of dashes — into the canonical one.
    /// </summary>
    public static bool TryParse(string? input, [NotNullWhen(true)] out RedeemCode? code)
    {
        code = null;
        var body = NormalizeBody(input);
        if (body is null || body.Length != NormalizedLength)
            return false;
        if (!body.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        var canonical = new StringBuilder(Prefix.Length + BodyLength + GroupCount);
        canonical.Append(Prefix).Append(Separator);
        for (var index = 0; index < BodyLength; index++)
        {
            if (index > 0 && index % GroupLength == 0)
                canonical.Append(Separator);
            canonical.Append(body[Prefix.Length + index]);
        }

        code = new RedeemCode(canonical.ToString(), DigestOf(canonical.ToString()));
        return true;
    }

    /// <summary>
    /// Upper case, separators removed, Crockford's anti-confusion mapping applied.
    /// <c>null</c> when the input carries a character no code can contain.
    /// </summary>
    /// <remarks>
    /// Crockford's decode map folds <c>I</c> and <c>L</c> to <c>1</c> and <c>O</c> to
    /// <c>0</c>, because those are the three substitutions people actually make when
    /// they copy a code by hand. <c>U</c> has no mapping: it is not in the alphabet at
    /// all, and decoding it to <c>V</c> would turn a typo into a different valid code
    /// rather than into a rejection. A code with a <c>U</c> in it is not a code.
    /// </remarks>
    public static string? NormalizeBody(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var builder = new StringBuilder(input.Length);
        foreach (var raw in input.Trim().ToUpperInvariant())
        {
            // Hyphens are the canonical separator and whitespace is what a code turns
            // into when it has been through an email client. Underscore is not part of
            // the format; it is accepted because it is what a PDF text layer and a
            // spreadsheet cell tend to produce, and rejecting it would be pedantry at
            // the customer's expense.
            if (char.IsWhiteSpace(raw) || raw == Separator[0] || raw == '_')
                continue;

            var character = raw switch
            {
                'I' or 'L' => '1',
                'O' => '0',
                _ => raw,
            };

            if (Alphabet.IndexOf(character) < 0)
                return null;

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// What the input box shows while the user types: upper case, dashes in the
    /// canonical places, everything that cannot appear in a code removed.
    /// </summary>
    /// <remarks>
    /// Never throws and never returns null, because it runs on every keystroke and a
    /// malformed intermediate value is the normal state of a half-typed code. Dropping
    /// the invalid characters rather than rejecting them is a product choice: the field
    /// is for pasting, and a user typing <c>O</c> where a zero belongs should watch it
    /// become <c>0</c> rather than hit an error they cannot act on.
    /// </remarks>
    public static string FormatForDisplay(string? input)
    {
        var normalized = NormalizeBody(input);
        if (string.IsNullOrEmpty(normalized))
            return string.Empty;

        // A prefix typed in lower case arrives already upper-cased above, and anything
        // before it is gone. Truncating rather than wrapping is what makes the dash
        // positions stay right: 24 characters is the longest canonical code, and the
        // 23rd body character is the last one that has a place.
        if (normalized.Length > NormalizedLength)
            normalized = normalized[..NormalizedLength];

        if (normalized.Length <= Prefix.Length)
            return normalized;

        var builder = new StringBuilder(normalized.Length + GroupCount + 1);
        builder.Append(normalized, 0, Prefix.Length).Append(Separator);
        for (var index = Prefix.Length; index < normalized.Length; index++)
        {
            var offset = index - Prefix.Length;
            if (offset > 0 && offset % GroupLength == 0)
                builder.Append(Separator);
            builder.Append(normalized[index]);
        }

        return builder.ToString();
    }

    /// <summary>The digest of a canonical code. Exposed so a test can pin the shared vector.</summary>
    public static string DigestOf(string canonical) =>
        Base64Url.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    /// <summary>
    /// A fresh idempotency key: 16 CSPRNG bytes, base64url.
    /// </summary>
    /// <remarks>
    /// This value is the entire difference between a retry that recovers and a retry
    /// that produces "this code was already used on another device". It is written to
    /// disk <em>before</em> the first request and reused by every later attempt, which
    /// turns the second attempt from "redeem again" into "tell me what you decided
    /// last time". See <see cref="PendingRedemption"/>.
    /// </remarks>
    public static string NewRequestId()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.Encode(bytes);
    }

    /// <summary>Whether a string parses. A convenience over <see cref="TryParse"/> for guards.</summary>
    public static bool IsValid(string? input) => TryParse(input, out _);
}
