using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeChatBridge.Windows.Core.Activation;

/// <summary>
/// The buy-out purchase flow as it travels over the wire: the frozen auth
/// endpoints plus the three activation endpoints this branch adds.
/// <para>
/// Every field name is pinned with <see cref="JsonPropertyNameAttribute"/> rather
/// than a naming policy. This is a contract with <c>server/</c>, whose handlers are
/// written in Go, and a policy change on either side would silently break every
/// purchase in flight. The shapes below were read off
/// <c>server/mock/server.js</c>, which mirrors the real handlers byte for byte.
/// </para>
/// <para>
/// This file names no networking type, on purpose. Core is the assembly
/// <c>EditionIsolationTests</c> certifies as naming none at all; the verbs live in
/// <c>WeChatBridge.Windows/Activation/HttpActivationTransport.cs</c>.
/// </para>
/// </summary>
public static class ActivationEndpoint
{
    /// <summary>
    /// Where the licence service is expected to live.
    /// <para>
    /// A reserved placeholder, deliberately and loudly: <c>.invalid</c> is reserved
    /// by RFC 2606 and can never resolve, so a build that ships this default cannot
    /// accidentally reach a stranger's server. It is a compile-time constant for the
    /// same reason <c>RedeemEndpoint.BaseAddress</c> is — the shipped default is not
    /// something a settings file should be able to change silently.
    /// </para>
    /// </summary>
    public const string DefaultBaseAddress = "https://api.wechatbridge.invalid/";

    /// <summary>Environment variable that points a dev build at a local server.</summary>
    public const string BaseAddressEnvironmentVariable = "WECHATBRIDGE_LICENSE_SERVER";

    public const string SessionPath = "v1/auth/session";
    public const string RefreshPath = "v1/auth/refresh";
    public const string ActivationRequestPath = "v1/activation/request";
    public const string ActivationStatusPath = "v1/activation/status";
    public const string ActivationLicencePath = "v1/activation/licence";

    /// <summary>Session poll path for a ticket; the ticket is 32 hex characters.</summary>
    public static string SessionTicketPath(string ticket) =>
        $"{SessionPath}/{Uri.EscapeDataString(ticket)}";

    /// <summary>The QR image the user scans, served by the same host as the session.</summary>
    public static string QrImagePath(string ticket) =>
        $"{SessionPath}/{Uri.EscapeDataString(ticket)}/qr";

    /// <summary>
    /// One call's deadline. Long enough for a phone scan and short enough that a
    /// black-holed connection cannot leave a window spinning forever.
    /// </summary>
    public static TimeSpan DefaultTimeout => TimeSpan.FromSeconds(30);
}

/// <summary>Statuses a login ticket can report while the user is deciding.</summary>
public static class AuthStatus
{
    public const string Pending = "pending";
    public const string Expired = "expired";
    public const string Authorized = "authorized";
}

/// <summary>Statuses an activation request moves through.</summary>
public static class ActivationState
{
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";
    public const string Rejected = "rejected";
}

/// <summary>Error codes the licence service returns, from <c>httpx.Fail</c>.</summary>
public static class ApiErrorCodes
{
    public const string Unauthorized = "unauthorized";
    public const string BadRequest = "bad_request";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string Internal = "internal";
    public const string RateLimited = "rate_limited";
}

/// <summary>The QR handshake ticket returned by <c>POST /v1/auth/session</c>.</summary>
public sealed record SessionTicket(string Ticket, string AuthorizeUrl, string QrImagePath, DateTimeOffset ExpiresAt);

/// <summary>The WeChat account behind a session. Display only; it grants nothing by itself.</summary>
public sealed record AccountView(
    string Id,
    string Nickname,
    string AvatarUrl,
    string Plan,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    bool WechatBound)
{
    /// <summary>What to call the user when only one word fits.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Nickname) ? Id : Nickname;
}

/// <summary>A signed-in session: the bearer token, the token that renews it, and who they are.</summary>
public sealed record AuthTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, AccountView Account)
{
    /// <summary>
    /// Whether the token is still usable. A minute of slack, because a token that
    /// expires in flight produces a 401 the user cannot explain, and the refresh
    /// round trip is cheap.
    /// </summary>
    public bool IsUsableAt(DateTimeOffset now) => now < ExpiresAt.AddMinutes(-1);
}

/// <summary>
/// One poll of a login ticket. <see cref="Tokens"/> is populated only once the
/// server says <c>authorized</c>, which is the only outcome that ends the login.
/// </summary>
public sealed record SessionPoll(string Status, DateTimeOffset? ExpiresAt, AuthTokens? Tokens)
{
    public bool IsPending => string.Equals(Status, AuthStatus.Pending, StringComparison.Ordinal);
    public bool IsExpired => string.Equals(Status, AuthStatus.Expired, StringComparison.Ordinal);
    public bool IsAuthorized => string.Equals(Status, AuthStatus.Authorized, StringComparison.Ordinal) && Tokens is not null;
}

/// <summary>The server's answer to a registration: an id to poll against.</summary>
public sealed record ActivationAck(string RequestId, string Status);

/// <summary>The server's answer to a status poll.</summary>
public sealed record ActivationStatusView(string Status, string? LicenseId)
{
    public bool IsPending => string.Equals(Status, ActivationState.Pending, StringComparison.Ordinal);
    public bool IsConfirmed => string.Equals(Status, ActivationState.Confirmed, StringComparison.Ordinal);
    public bool IsRejected => string.Equals(Status, ActivationState.Rejected, StringComparison.Ordinal);
}

/// <summary>
/// A licence on its way to the client. <see cref="Token"/> is the base64url form
/// the server ships; <see cref="Envelope"/> is the same bytes already decoded, so a
/// client never has to re-derive it and can compare the two.
/// </summary>
public sealed record LicencePayload(string Token, Licensing.LicenseEnvelope Envelope);

/// <summary>
/// The error envelope, <c>{ error: { code, message, request_id } }</c>.
/// <para>
/// The server's <c>message</c> is already user-facing Chinese, written for a support
/// conversation. The UI prefers it over anything invented here, because the two
/// drift otherwise and the user is the one who can see the drift.
/// </para>
/// </summary>
public sealed record ApiFailure(string Code, string Message, string RequestId)
{
    /// <summary>Reads an error envelope, or null when the body is not one.</summary>
    public static ApiFailure? TryParse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.Object)
                return null;

            var code = error.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
            var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var requestId = error.TryGetProperty("request_id", out var r) ? r.GetString() ?? "" : "";
            return new ApiFailure(code, message, requestId);
        }
        catch (JsonException)
        {
            // A proxy's HTML error page is the common case here, not a malformed
            // envelope. Nothing to parse means nothing to report.
            return null;
        }
    }
}

/// <summary>
/// Reads timestamps in every shape this contract uses, and nothing else.
/// <para>
/// The licence envelope pins <c>iat</c>/<c>nbf</c>/<c>exp</c> as unix seconds — the
/// Go issuer writes <c>int64</c>, and the canonical payload both sides sign is built
/// from unix seconds, so the wire form has to be too or the signature would be taken
/// over bytes nobody round-trips. The account views, by contrast, are RFC 3339
/// strings, because they are read by humans and never signed.
/// </para>
/// <para>
/// One converter for both, rather than two options objects, because a caller that
/// has to know which field is which will eventually guess wrong and fail on a
/// customer's login rather than in a test.
/// </para>
/// </summary>
public sealed class TimestampConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var seconds))
                    return DateTimeOffset.FromUnixTimeSeconds(seconds);
                if (reader.TryGetDouble(out var fractional))
                    return DateTimeOffset.FromUnixTimeSeconds((long)fractional);
                throw new JsonException("A timestamp number is not a representable instant.");

            case JsonTokenType.String:
                var text = reader.GetString();
                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                    return parsed;
                // A server that quotes its unix seconds is still speaking the
                // envelope's dialect, and refusing it would fail a purchase for a
                // formatting detail nobody would notice.
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var quoted))
                    return DateTimeOffset.FromUnixTimeSeconds(quoted);
                throw new JsonException($"Not a timestamp: '{text}'.");

            default:
                throw new JsonException($"A timestamp cannot be {reader.TokenType}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
}

/// <summary>
/// Serialisation settings for everything on this wire.
/// <para>
/// <c>UnsafeRelaxedJsonEscaping</c> matches the rest of the product: a nickname is
/// Chinese, and a buy-out customer seeing <c>\uXXXX</c> in the UI is a bug report.
/// </para>
/// </summary>
public static class ActivationJson
{
    /// <summary>
    /// Case-sensitive and un-relaxed about property names, on the same reasoning as
    /// the field pinning: a contract with another language is not improved by
    /// guessing what it meant.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            PropertyNameCaseInsensitive = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new TimestampConverter());
        return options;
    }
}
