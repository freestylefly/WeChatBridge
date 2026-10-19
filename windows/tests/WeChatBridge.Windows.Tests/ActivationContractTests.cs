using System.Globalization;
using System.Text;
using System.Text.Json;
using WeChatBridge.Windows.Core.Activation;
using WeChatBridge.Windows.Core.Licensing;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// The wire contract's serialisation rules, pinned: the timestamp converter in both
/// directions, the shared options object every service serialises through, the error
/// envelope reader, and the record semantics the UI branches on.
/// <para>
/// The converter is the one that earns this file. It reads unix seconds because the
/// Go issuer writes them, and RFC 3339 because the account views are strings — one
/// converter for both, so no caller has to know which field is which. The rejection
/// cases matter as much as the acceptance ones: a converter that silently returned
/// <c>default</c> for a bool would turn a broken server into a licence dated to year
/// one, and the first anybody noticed would be a signature check nobody could
/// explain.
/// </para>
/// </summary>
public sealed class ActivationContractTests
{
    private const string InstallId = "install-id-for-this-machine";

    private static JsonSerializerOptions ConverterOnly()
    {
        // The same encoder the shared options use, so the assertions below are about
        // the timestamp format and not about how a plus sign happens to be escaped.
        var options = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new TimestampConverter());
        return options;
    }

    private static DateTimeOffset ReadTimestamp(string json)
    {
        var carrier = JsonSerializer.Deserialize<TimestampCarrier>(json, ConverterOnly());
        if (carrier is null)
            throw new JsonException("the carrier did not deserialise");
        return carrier.At;
    }

    private sealed class TimestampCarrier
    {
        public DateTimeOffset At { get; set; }
    }

    // -----------------------------------------------------------------------
    // TimestampConverter — the two dialects it must read
    // -----------------------------------------------------------------------

    /// <summary>
    /// The envelope's dialect: unix seconds as a JSON number, because the canonical
    /// payload both sides sign is built from unix seconds and the wire form has to
    /// round-trip those bytes.
    /// </summary>
    [Fact]
    public void UnixSecondsAreReadAsAnInstant()
    {
        var seconds = TestTime.IssuedAt.ToUnixTimeSeconds();

        Assert.Equal(TestTime.IssuedAt, ReadTimestamp($"{{\"At\":{seconds}}}"));
    }

    /// <summary>A quoted unix second is still the envelope's dialect, not an error.</summary>
    [Fact]
    public void QuotedUnixSecondsAreReadAsAnInstant()
    {
        var seconds = TestTime.IssuedAt.ToUnixTimeSeconds();

        Assert.Equal(TestTime.IssuedAt, ReadTimestamp($"{{\"At\":\"{seconds}\"}}"));
    }

    /// <summary>The account views' dialect: RFC 3339, including a non-UTC offset.</summary>
    [Fact]
    public void Rfc3339IsReadAsTheSameInstantWhateverTheOffset()
    {
        Assert.Equal(TestTime.IssuedAt, ReadTimestamp("{\"At\":\"2026-03-01T00:00:00.0000000Z\"}"));
        Assert.Equal(TestTime.IssuedAt, ReadTimestamp("{\"At\":\"2026-03-01T08:00:00.0000000+08:00\"}"));
    }

    /// <summary>
    /// A fractional unix second truncates toward the second rather than throwing:
    /// the signer never emitted one, but a server that grows millisecond precision
    /// must not take a customer's licence down with it.
    /// </summary>
    [Fact]
    public void FractionalSecondsTruncateToTheSecond()
    {
        var seconds = TestTime.IssuedAt.ToUnixTimeSeconds();

        Assert.Equal(TestTime.IssuedAt, ReadTimestamp($"{{\"At\":{seconds + 0.75}}}"));
    }

    /// <summary>
    /// Everything else is a refusal, loudly. A <c>null</c> timestamp read as
    /// <c>default</c> is a licence dated to year one; a <c>true</c> read as an
    /// instant is a claim nobody made. Both must be exceptions, not values.
    /// </summary>
    [Theory]
    [InlineData("{\"At\":true}")]
    [InlineData("{\"At\":false}")]
    [InlineData("{\"At\":null}")]
    [InlineData("{\"At\":{}}")]
    [InlineData("{\"At\":[]}")]
    [InlineData("{\"At\":\"not a timestamp\"}")]
    public void AnythingThatIsNotAnInstantIsAJsonException(string json)
    {
        Assert.Throws<JsonException>(() => ReadTimestamp(json));
    }

    /// <summary>
    /// The write path normalises to UTC and emits the round-trip format —
    /// <c>DateTimeOffset</c>'s "O" always spells the offset numerically, so a value
    /// that arrived as +08:00 goes out as +00:00 and every reader sees the same
    /// instant.
    /// </summary>
    [Fact]
    public void WritesAreRfc3339InUtc()
    {
        var shanghai = new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.FromHours(8));

        var json = JsonSerializer.Serialize(new TimestampCarrier { At = shanghai }, ConverterOnly());

        Assert.Equal("{\"At\":\"2026-03-01T00:00:00.0000000+00:00\"}", json);
        Assert.Equal(TestTime.IssuedAt, ReadTimestamp(json));
    }

    // -----------------------------------------------------------------------
    // ActivationJson — the options every service serialises through
    // -----------------------------------------------------------------------

    /// <summary>
    /// A full session survives a round trip through the shared options: nested
    /// account, Chinese nickname, RFC 3339 expiry. This is the object
    /// <c>AuthTokenStore</c> writes and <c>AccountService</c> reads back.
    /// </summary>
    [Fact]
    public void ASessionSurvivesTheSharedOptions()
    {
        var tokens = new AuthTokens(
            "mock_at_abc",
            "mock_rt_def",
            TestTime.Now.AddHours(1),
            new AccountView("acc_1", "演示买家", "", "buyout", TestTime.IssuedAt, TestTime.IssuedAt, true));

        var json = JsonSerializer.Serialize(tokens, ActivationJson.Options);
        var back = JsonSerializer.Deserialize<AuthTokens>(json, ActivationJson.Options);

        Assert.NotNull(back);
        Assert.Equal(tokens, back);
        // Relaxed escaping: a Chinese nickname is not escape soup on the wire.
        Assert.Contains("演示买家", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The envelope the activation endpoint returns — short pinned field names,
    /// unix-second <c>iat</c>, null <c>nbf</c>/<c>exp</c> — deserialises through the
    /// shared options. This is the exact path <c>ActivationService.CollectAsync</c>
    /// takes, and it is the new-chain twin of the C-1 regression.
    /// </summary>
    [Fact]
    public void TheLicenceEndpointEnvelopeDeserialisesThroughTheSharedOptions()
    {
        var seconds = TestTime.IssuedAt.ToUnixTimeSeconds();
        var json = $"{{\"v\":1,\"sub\":\"{InstallId}\",\"plan\":\"buyout\",\"iat\":{seconds}," +
            "\"nbf\":null,\"exp\":null,\"ent\":[\"pro\"],\"sig\":\"c2ln\"}";

        var envelope = JsonSerializer.Deserialize<LicenseEnvelope>(json, ActivationJson.Options);

        Assert.NotNull(envelope);
        Assert.Equal(InstallId, envelope.Subject);
        Assert.Equal(TestTime.IssuedAt, envelope.IssuedAt);
        Assert.Null(envelope.NotBefore);
        Assert.Null(envelope.ExpiresAt);
        Assert.Equal(["pro"], envelope.Entitlements);
    }

    /// <summary>
    /// The envelope's field names are matched exactly — they are pinned with
    /// attributes because they are a contract with Go, and a renamed field must
    /// fail to bind rather than silently produce a default subject.
    /// </summary>
    [Fact]
    public void EnvelopeFieldNamesAreMatchedExactlyNotCaseInsensitively()
    {
        var seconds = TestTime.IssuedAt.ToUnixTimeSeconds();

        var exact = JsonSerializer.Deserialize<LicenseEnvelope>(
            $"{{\"v\":1,\"sub\":\"{InstallId}\",\"plan\":\"buyout\",\"iat\":{seconds},\"ent\":[],\"sig\":\"\"}}",
            ActivationJson.Options);
        var renamed = JsonSerializer.Deserialize<LicenseEnvelope>(
            $"{{\"v\":1,\"SUB\":\"{InstallId}\",\"plan\":\"buyout\",\"iat\":{seconds},\"ent\":[],\"sig\":\"\"}}",
            ActivationJson.Options);

        Assert.NotNull(exact);
        Assert.Equal(InstallId, exact.Subject);
        Assert.NotNull(renamed);
        Assert.Equal(string.Empty, renamed.Subject);
    }

    // -----------------------------------------------------------------------
    // ApiFailure — the error envelope
    // -----------------------------------------------------------------------

    [Fact]
    public void AWellFormedErrorEnvelopeIsRead()
    {
        var failure = ApiFailure.TryParse(
            "{\"error\":{\"code\":\"unauthorized\",\"message\":\"缺少访问令牌。\",\"request_id\":\"req-42\"}}");

        Assert.NotNull(failure);
        Assert.Equal("unauthorized", failure.Code);
        Assert.Equal("缺少访问令牌。", failure.Message);
        Assert.Equal("req-42", failure.RequestId);
    }

    /// <summary>
    /// Optional members missing is a normal envelope, not a broken one — the UI
    /// still gets a code to branch on.
    /// </summary>
    [Fact]
    public void AnErrorEnvelopeWithoutOptionalMembersStillReads()
    {
        var failure = ApiFailure.TryParse("{\"error\":{\"code\":\"conflict\"}}");

        Assert.NotNull(failure);
        Assert.Equal("conflict", failure.Code);
        Assert.Equal(string.Empty, failure.Message);
        Assert.Equal(string.Empty, failure.RequestId);
    }

    /// <summary>
    /// A proxy's HTML error page is the common non-envelope body. It reads as
    /// "nothing to report", which the callers already treat as a generic failure —
    /// not as a crash.
    /// </summary>
    [Theory]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    [InlineData("{\"data\":{}}")]
    [InlineData("{\"error\":\"a string, not an object\"}")]
    [InlineData("{\"error\":null}")]
    [InlineData("")]
    [InlineData("   ")]
    public void AnythingThatIsNotAnErrorEnvelopeReadsAsNothing(string body)
    {
        Assert.Null(ApiFailure.TryParse(body));
    }

    // -----------------------------------------------------------------------
    // Endpoints and records
    // -----------------------------------------------------------------------

    /// <summary>
    /// The ticket travels in a path, so it is escaped: a ticket that ever contained
    /// a slash or a space must not be able to address a different route.
    /// </summary>
    [Fact]
    public void TicketsAreEscapedIntoPaths()
    {
        Assert.Equal("v1/auth/session/abc123", ActivationEndpoint.SessionTicketPath("abc123"));
        Assert.Equal("v1/auth/session/a%2Fb%20c", ActivationEndpoint.SessionTicketPath("a/b c"));
        Assert.Equal("v1/auth/session/abc123/qr", ActivationEndpoint.QrImagePath("abc123"));
        Assert.Equal("v1/auth/session/a%2Fb%20c/qr", ActivationEndpoint.QrImagePath("a/b c"));
    }

    /// <summary>
    /// The default address is the reserved <c>.invalid</c> placeholder: a build that
    /// ships it fails at the first call instead of reaching a stranger's host, and
    /// the environment variable is the only supported override.
    /// </summary>
    [Fact]
    public void TheDefaultAddressIsTheReservedPlaceholder()
    {
        Assert.EndsWith(".invalid/", ActivationEndpoint.DefaultBaseAddress, StringComparison.Ordinal);
        Assert.Equal("WECHATBRIDGE_LICENSE_SERVER", ActivationEndpoint.BaseAddressEnvironmentVariable);
        Assert.Equal(TimeSpan.FromSeconds(30), ActivationEndpoint.DefaultTimeout);
    }

    /// <summary>
    /// A minute of slack, so a token that expires in flight produces a refresh
    /// rather than a 401 the user cannot explain. The boundary itself is exclusive:
    /// at exactly the slack edge the token is already due for a refresh.
    /// </summary>
    [Fact]
    public void ATokenIsUsableUntilAMinuteBeforeItExpires()
    {
        var tokens = new AuthTokens("at", "rt", TestTime.Now.AddMinutes(5), NewAccount());

        Assert.True(tokens.IsUsableAt(TestTime.Now));
        Assert.True(tokens.IsUsableAt(TestTime.Now.AddMinutes(3)));
        Assert.True(tokens.IsUsableAt(TestTime.Now.AddMinutes(4).AddSeconds(-1)));
        Assert.False(tokens.IsUsableAt(TestTime.Now.AddMinutes(4)));
        Assert.False(tokens.IsUsableAt(TestTime.Now.AddMinutes(6)));
    }

    /// <summary>
    /// "authorized" without tokens is not authorized: the poll that carries the
    /// session is the only one that ends a login, and a status word alone must not
    /// be mistaken for it.
    /// </summary>
    [Fact]
    public void AuthorizedMeansAuthorizedWithTokens()
    {
        var bare = new SessionPoll(AuthStatus.Authorized, null, null);
        var full = new SessionPoll(AuthStatus.Authorized, null, new AuthTokens("at", "rt", TestTime.Now, NewAccount()));

        Assert.False(bare.IsAuthorized);
        Assert.True(full.IsAuthorized);
        Assert.True(new SessionPoll(AuthStatus.Pending, null, null).IsPending);
        Assert.True(new SessionPoll(AuthStatus.Expired, null, null).IsExpired);
    }

    [Fact]
    public void ActivationStatusesAreNamedComparisons()
    {
        Assert.True(new ActivationStatusView(ActivationState.Confirmed, "lic-1").IsConfirmed);
        Assert.True(new ActivationStatusView(ActivationState.Rejected, null).IsRejected);
        Assert.True(new ActivationStatusView(ActivationState.Pending, null).IsPending);
        Assert.False(new ActivationStatusView("issued", null).IsConfirmed);
    }

    [Fact]
    public void SuccessIsTheTwoHundreds()
    {
        Assert.True(new ApiReply(200, "{}").IsSuccess);
        Assert.True(new ApiReply(299, "{}").IsSuccess);
        Assert.False(new ApiReply(199, "{}").IsSuccess);
        Assert.False(new ApiReply(300, "{}").IsSuccess);
        Assert.False(new ApiReply(500, "{}").IsSuccess);
    }

    /// <summary>What to call the user when only one word fits.</summary>
    [Fact]
    public void TheDisplayNameFallsBackToTheId()
    {
        Assert.Equal("演示买家", NewAccount().DisplayName);

        var blank = NewAccount() with { Nickname = "  " };
        Assert.Equal("acc_1", blank.DisplayName);
    }

    private static AccountView NewAccount() => new(
        "acc_1", "演示买家", "", "buyout", TestTime.IssuedAt, null, true);
}
