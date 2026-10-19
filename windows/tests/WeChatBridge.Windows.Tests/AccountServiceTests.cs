using System.Text;
using System.Text.Json;
using WeChatBridge.Windows.Core.Activation;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// The WeChat login, driven through the transport seam: the QR ticket, the poll
/// that waits for a phone, the refresh that keeps a customer from scanning every
/// morning, and the sign-out that clears locally even when the revoke never lands.
/// <para>
/// Two properties earn most of these tests. First, <b>a failed poll is not a failed
/// login</b> — the ticket is untouched, so the loop keeps asking; only the ticket
/// itself dying ends the wait. Second, <b>the wait stops talking</b>: the injected
/// delay records the cadence instead of sleeping through it, so "hammers the service
/// for an hour" is an assertion rather than a soak test.
/// </para>
/// </summary>
public sealed class AccountServiceTests
{
    private const string InstallId = "Zx8Kq3nT7pQ1mR4vW9yB0";
    private const string Ticket = "0123456789abcdef0123456789abcdef";

    private static AccountView Buyer() => new(
        "acc_demo0001", "演示买家", "", "buyout", TestTime.IssuedAt, TestTime.IssuedAt, true);

    private static AuthTokens FreshTokens() => new(
        "mock_at_1", "mock_rt_1", TestTime.Now.AddHours(1), Buyer());

    /// <summary>The authorized poll body, in the mock's dialect: RFC 3339 strings.</summary>
    private static string Authorized(string accessToken = "mock_at_1", string refreshToken = "mock_rt_1") =>
        $"{{\"status\":\"{AuthStatus.Authorized}\",\"accessToken\":\"{accessToken}\",\"refreshToken\":\"{refreshToken}\"," +
        $"\"expiresAt\":\"{TestTime.Now.AddHours(1):O}\",\"account\":{AccountJson()}}}";

    private static string AccountJson() =>
        $"{{\"id\":\"acc_demo0001\",\"nickname\":\"演示买家\",\"avatarUrl\":\"\",\"plan\":\"buyout\"," +
        $"\"createdAt\":\"{TestTime.IssuedAt:O}\",\"lastLoginAt\":\"{TestTime.IssuedAt:O}\",\"wechatBound\":true}}";

    private static string Pending() =>
        $"{{\"status\":\"{AuthStatus.Pending}\",\"expiresAt\":\"{TestTime.Now.AddMinutes(5):O}\"}}";

    private static AccountService Service(
        FakeActivationTransport transport,
        TempLicenseConfig config,
        Action<string>? log = null,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? timeout = null) =>
        new(
            transport,
            new AuthTokenStore(config.Root, config.Protector),
            PollSchedule.Login,
            clock ?? (() => TestTime.Now),
            delay ?? ((span, ct) => Task.CompletedTask),
            timeout,
            log);

    // -----------------------------------------------------------------------
    // The QR ticket
    // -----------------------------------------------------------------------

    [Fact]
    public async Task BeginLoginSendsTheInstallIdAndReadsTheTicket()
    {
        using var config = new TempLicenseConfig();
        var transport = new FakeActivationTransport();
        transport.Reply(201, $"{{\"ticket\":\"{Ticket}\",\"authorizeUrl\":\"https://open.weixin.qq.com/connect/qrconnect?state={Ticket}\"," +
            $"\"qrImageUrl\":\"/v1/auth/session/{Ticket}/qr\",\"expiresAt\":\"{TestTime.Now.AddMinutes(5):O}\"}}");
        using var service = Service(transport, config);

        var ticket = await service.BeginLoginAsync(InstallId, "device-1", CancellationToken.None);

        var request = transport.Single();
        Assert.Equal("POST", request.Method);
        Assert.Equal(ActivationEndpoint.SessionPath, request.Path);
        Assert.Null(request.BearerToken);
        var body = FakeActivationTransport.BodyOf(request);
        Assert.Equal(InstallId, body.GetProperty("installId").GetString());
        Assert.Equal("device-1", body.GetProperty("deviceId").GetString());

        Assert.Equal(Ticket, ticket.Ticket);
        Assert.Equal($"/v1/auth/session/{Ticket}/qr", ticket.QrImagePath);
        Assert.Contains("qrconnect", ticket.AuthorizeUrl, StringComparison.Ordinal);
        Assert.Equal(TestTime.Now.AddMinutes(5), ticket.ExpiresAt);
    }

    /// <summary>
    /// The server's message is already user-facing Chinese written for a support
    /// conversation; the exception carries it rather than inventing a second one.
    /// </summary>
    [Fact]
    public async Task BeginLoginSurfacesTheServersOwnMessage()
    {
        using var config = new TempLicenseConfig();
        var transport = new FakeActivationTransport();
        transport.Reply(401, "{\"error\":{\"code\":\"unauthorized\",\"message\":\"缺少访问令牌。\",\"request_id\":\"req-7\"}}");
        using var service = Service(transport, config);

        var error = await Assert.ThrowsAsync<ApiException>(
            () => service.BeginLoginAsync(InstallId, null, CancellationToken.None));

        Assert.Equal(401, error.StatusCode);
        Assert.Equal(ApiErrorCodes.Unauthorized, error.Code);
        Assert.Equal("缺少访问令牌。", error.Message);
        Assert.Equal("req-7", error.RequestId);
    }

    [Fact]
    public async Task BeginLoginRefusesABodyItCannotRead()
    {
        using var config = new TempLicenseConfig();
        var transport = new FakeActivationTransport();
        transport.Reply(201, "{\"ticket\":null}");
        using var service = Service(transport, config);

        var error = await Assert.ThrowsAsync<ApiException>(
            () => service.BeginLoginAsync(InstallId, null, CancellationToken.None));

        Assert.Contains("无法识别", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BeginLoginRejectsANullInstallId()
    {
        using var config = new TempLicenseConfig();
        using var service = Service(new FakeActivationTransport(), config);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.BeginLoginAsync(null!, null, CancellationToken.None));
    }

    // -----------------------------------------------------------------------
    // The poll
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PollReportsPendingWithoutTokens()
    {
        using var config = new TempLicenseConfig();
        var transport = new FakeActivationTransport();
        transport.Reply(200, Pending());
        using var service = Service(transport, config);

        var poll = await service.PollAsync(Ticket, CancellationToken.None);

        Assert.True(poll.IsPending);
        Assert.False(poll.IsAuthorized);
        Assert.Null(poll.Tokens);
        var request = transport.Single();
        Assert.Equal("GET", request.Method);
        Assert.Equal($"v1/auth/session/{Ticket}", request.Path);
    }

    [Fact]
    public async Task PollReportsAuthorizedWithTheWholeSession()
    {
        using var config = new TempLicenseConfig();
        var transport = new FakeActivationTransport();
        transport.Reply(200, Authorized());
        using var service = Service(transport, config);

        var poll = await service.PollAsync(Ticket, CancellationToken.None);

        Assert.True(poll.IsAuthorized);
        Assert.NotNull(poll.Tokens);
        Assert.Equal("mock_at_1", poll.Tokens.AccessToken);
        Assert.Equal("mock_rt_1", poll.Tokens.RefreshToken);
        Assert.Equal("演示买家", poll.Tokens.Account.Nickname);
        Assert.True(poll.Tokens.Account.WechatBound);
        Assert.Equal("buyout", poll.Tokens.Account.Plan);
    }

    /// <summary>
    /// A ticket the server no longer knows is expired, whichever way it says so —
    /// 404 from a forgotten id, 410 from one that was retired on purpose.
    /// </summary>
    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    public async Task PollReadsAGoneTicketAsExpired(int statusCode)
    {
        using var config = new TempLicenseConfig();
        var transport = new FakeActivationTransport();
        transport.Reply(statusCode, "{\"error\":{\"code\":\"not_found\",\"message\":\"请求的内容不存在。\",\"request_id\":\"\"}}");
        using var service = Service(transport, config);

        var poll = await service.PollAsync(Ticket, CancellationToken.None);

        Assert.True(poll.IsExpired);
        Assert.Null(poll.Tokens);
    }

    [Fact]
    public async Task PollSurfacesAServerRefusal()
    {
        using var config = new TempLicenseConfig();
        var transport = new FakeActivationTransport();
        transport.Reply(500, "{\"error\":{\"code\":\"internal\",\"message\":\"服务器内部错误，请稍后重试。\",\"request_id\":\"req-9\"}}");
        using var service = Service(transport, config);

        var error = await Assert.ThrowsAsync<ApiException>(() => service.PollAsync(Ticket, CancellationToken.None));

        Assert.Equal(500, error.StatusCode);
    }

    // -----------------------------------------------------------------------
    // The wait
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WaitForScanSignsInTheMomentTheScanLands()
    {
        using var config = new TempLicenseConfig();
        var transport = new FakeActivationTransport();
        transport.Reply(200, Authorized());
        var delays = new List<TimeSpan>();
        using var service = Service(transport, config, delay: (span, ct) => { delays.Add(span); return Task.CompletedTask; });

        var outcome = await service.WaitForScanAsync(
            new SessionTicket(Ticket, "https://open.weixin.qq.com/", $"/v1/auth/session/{Ticket}/qr", TestTime.Now.AddMinutes(5)),
            TimeSpan.FromMinutes(5));

        Assert.True(outcome.Succeeded, outcome.Detail);
        Assert.Equal(LoginFailure.None, outcome.Failure);
        // The wait is over before it starts: no backoff is spent on a scan that
        // already happened.
        Assert.Empty(delays);
        Assert.True(service.IsSignedIn);
        Assert.Equal("mock_at_1", service.BearerToken);
        // Signed in means written through: the next launch does not scan again.
        Assert.NotNull(new AuthTokenStore(config.Root, config.Protector).Load());
    }

    [Fact]
    public async Task WaitForScanKeepsWaitingWithTheCadenceTheScheduleDictates()
    {
        using var config = new TempLicenseConfig();
        var transport = new FakeActivationTransport();
        transport.Reply(200, Pending());
        transport.Reply(200, Authorized("mock_at_2", "mock_rt_2"));
        var delays = new List<TimeSpan>();
        var polls = 0;
        using var service = Service(transport, config, delay: (span, ct) => { delays.Add(span); return Task.CompletedTask; });

        var outcome = await service.WaitForScanAsync(
            new SessionTicket(Ticket, "", "", TestTime.Now.AddMinutes(5)),
            TimeSpan.FromMinutes(5),
            _ => polls++);

        Assert.True(outcome.Succeeded, outcome.Detail);
        Assert.Equal(2, polls);
        // Exactly one backoff, and it is the schedule's first delay — 1.5s, not a
        // fixed one-second hammer.
        Assert.Equal([PollSchedule.Login.DelayBefore(0)], delays);
        Assert.Equal("mock_at_2", service.BearerToken);
    }

    [Fact]
    public async Task WaitForScanStopsWhenTheTicketExpires()
    {
        using var config = new TempLicenseConfig();
        var transport = new FakeActivationTransport();
        transport.Reply(200, $"{{\"status\":\"{AuthStatus.Expired}\",\"expiresAt\":null}}");
        using var service = Service(transport, config);

        var outcome = await service.WaitForScanAsync(
            new SessionTicket(Ticket, "", "", TestTime.Now),
            TimeSpan.FromMinutes(5));

        Assert.False(outcome.Succeeded);
        Assert.Equal(LoginFailure.TicketExpired, outcome.Failure);
    }

    /// <summary>
    /// A user who walks away leaves the app going quiet, not loud: the polls keep
    /// coming at the schedule's cadence while the budget lasts, and the failure says
    /// "refresh and scan again" rather than blaming the network.
    /// </summary>
    [Fact]
    public async Task WaitForScanGivesUpWhenTheBudgetRunsOut()
    {
        using var config = new TempLicenseConfig();
        var transport = new FakeActivationTransport();
        var now = TestTime.Now;
        // Every backoff jumps the fake clock by twice the wait, so the budget runs
        // out after a handful of polls instead of five minutes of them.
        using var service = Service(
            transport,
            config,
            clock: () => now,
            delay: (span, ct) => { now += span + span; return Task.CompletedTask; });
        for (var i = 0; i < 10; i++)
            transport.Reply(200, Pending());

        var outcome = await service.WaitForScanAsync(
            new SessionTicket(Ticket, "", "", now),
            TimeSpan.FromSeconds(10));

        Assert.False(outcome.Succeeded);
        Assert.Equal(LoginFailure.TimedOut, outcome.Failure);
        Assert.True(transport.SendCount >= 2);
    }

    /// <summary>
    /// The property the whole retry story rests on: a poll that never reached the
    /// service is not a failed login. The ticket is untouched, the next attempt is
    /// free, and the user still gets in.
    /// </summary>
    [Fact]
    public async Task WaitForScanSurvivesAPollThatNeverReachedTheService()
    {
        using var config = new TempLicenseConfig();
        var transport = new FakeActivationTransport();
        transport.Throw("the connection was reset");
        transport.Reply(200, Authorized());
        using var service = Service(transport, config);

        var outcome = await service.WaitForScanAsync(
            new SessionTicket(Ticket, "", "", TestTime.Now.AddMinutes(5)),
            TimeSpan.FromMinutes(5));

        Assert.True(outcome.Succeeded, outcome.Detail);
        Assert.Equal(2, transport.SendCount);
    }

    [Fact]
    public async Task WaitForScanReportsUnreachableWhenTheServiceStaysSilent()
    {
        using var config = new TempLicenseConfig();
        var transport = new FakeActivationTransport();
        var now = TestTime.Now;
        using var service = Service(
            transport,
            config,
            clock: () => now,
            delay: (span, ct) => { now += span + span; return Task.CompletedTask; });
        for (var i = 0; i < 10; i++)
            transport.Throw();

        var outcome = await service.WaitForScanAsync(
            new SessionTicket(Ticket, "", "", now),
            TimeSpan.FromSeconds(10));

        Assert.False(outcome.Succeeded);
        Assert.Equal(LoginFailure.Unreachable, outcome.Failure);
    }

    // -----------------------------------------------------------------------
    // The QR image
    // -----------------------------------------------------------------------

    /// <summary>
    /// The image goes through the transport, at the path the server named, with the
    /// leading slash stripped — so a relative path resolves against the base address
    /// instead of replacing its last segment.
    /// </summary>
    [Fact]
    public async Task FetchQrGoesThroughTheTransportWithTheServersOwnPath()
    {
        using var config = new TempLicenseConfig();
        var transport = new FakeActivationTransport();
        transport.ReplyBytes(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        using var service = Service(transport, config);

        var png = await service.FetchQrAsync(
            new SessionTicket(Ticket, "", $"/v1/auth/session/{Ticket}/qr", TestTime.Now),
            CancellationToken.None);

        Assert.Equal($"v1/auth/session/{Ticket}/qr", Assert.Single(transport.BytePaths));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png);
    }

    [Fact]
    public async Task FetchQrFallsBackToTheConventionalPathWhenTheServerOmittedOne()
    {
        using var config = new TempLicenseConfig();
        var transport = new FakeActivationTransport();
        transport.ReplyBytes(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        using var service = Service(transport, config);

        await service.FetchQrAsync(new SessionTicket(Ticket, "", "", TestTime.Now), CancellationToken.None);

        Assert.Equal(ActivationEndpoint.QrImagePath(Ticket), Assert.Single(transport.BytePaths));
    }

    // -----------------------------------------------------------------------
    // Refresh — how a customer goes months without scanning
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RefreshTradesARefreshTokenForANewSession()
    {
        using var config = new TempLicenseConfig();
        var store = new AuthTokenStore(config.Root, config.Protector);
        store.Save(FreshTokens());
        var transport = new FakeActivationTransport();
        transport.Reply(200, Authorized("mock_at_2", "mock_rt_2"));
        using var service = Service(transport, config);

        var outcome = await service.RefreshAsync(CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Detail);
        Assert.Equal("mock_at_2", service.BearerToken);
        var request = transport.Single();
        Assert.Equal("POST", request.Method);
        Assert.Equal(ActivationEndpoint.RefreshPath, request.Path);
        Assert.Equal("mock_rt_1", FakeActivationTransport.BodyOf(request).GetProperty("refreshToken").GetString());
        Assert.Equal("mock_at_2", store.Load()?.AccessToken);
    }

    [Fact]
    public async Task RefreshWithoutASessionIsNotAnErrorWorthRetrying()
    {
        using var config = new TempLicenseConfig();
        using var service = Service(new FakeActivationTransport(), config);

        var outcome = await service.RefreshAsync(CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(LoginFailure.SessionExpired, outcome.Failure);
    }

    /// <summary>
    /// When the server refuses the refresh token, the honest answer is to make the
    /// user scan again — carrying a token that will never work only teaches them to
    /// distrust the error messages.
    /// </summary>
    [Fact]
    public async Task RefreshMakesTheUserScanAgainWhenTheServerRefuses()
    {
        using var config = new TempLicenseConfig();
        var store = new AuthTokenStore(config.Root, config.Protector);
        store.Save(FreshTokens());
        var transport = new FakeActivationTransport();
        transport.Reply(401, "{\"error\":{\"code\":\"unauthorized\",\"message\":\"登录已过期。\",\"request_id\":\"\"}}");
        using var service = Service(transport, config);

        var outcome = await service.RefreshAsync(CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(LoginFailure.SessionExpired, outcome.Failure);
        Assert.Null(service.Current);
        Assert.Null(store.Load());
    }

    [Fact]
    public async Task RefreshSignsOutLocallyOnAnUnreadableAnswer()
    {
        using var config = new TempLicenseConfig();
        var store = new AuthTokenStore(config.Root, config.Protector);
        store.Save(FreshTokens());
        var transport = new FakeActivationTransport();
        transport.Reply(200, "not json at all");
        using var service = Service(transport, config);

        var outcome = await service.RefreshAsync(CancellationToken.None);

        Assert.Equal(LoginFailure.MalformedResponse, outcome.Failure);
        Assert.Null(service.Current);
    }

    // -----------------------------------------------------------------------
    // Sign-out
    // -----------------------------------------------------------------------

    /// <summary>
    /// A customer who clicked 退出 on a shared machine means it: the local session
    /// goes even if the revoke never reaches the service.
    /// </summary>
    [Fact]
    public async Task SignOutForgetsLocallyEvenWhenTheRevokeNeverLands()
    {
        using var config = new TempLicenseConfig();
        var store = new AuthTokenStore(config.Root, config.Protector);
        store.Save(FreshTokens());
        var transport = new FakeActivationTransport();
        transport.Throw("the connection was reset");
        using var service = Service(transport, config);

        await service.SignOutAsync(CancellationToken.None);

        Assert.Null(service.Current);
        Assert.Null(store.Load());
        var request = transport.Single();
        Assert.Equal("DELETE", request.Method);
        Assert.Equal(ActivationEndpoint.SessionPath, request.Path);
        Assert.Equal("mock_rt_1", FakeActivationTransport.BodyOf(request).GetProperty("refreshToken").GetString());
    }

    [Fact]
    public async Task SignOutWithoutASessionSendsNothing()
    {
        using var config = new TempLicenseConfig();
        var transport = new FakeActivationTransport();
        using var service = Service(transport, config);

        await service.SignOutAsync(CancellationToken.None);

        Assert.Empty(transport.Requests);
    }

    // -----------------------------------------------------------------------
    // The stored session
    // -----------------------------------------------------------------------

    [Fact]
    public void TheConstructorReadsTheStoredSessionOnce()
    {
        using var config = new TempLicenseConfig();
        new AuthTokenStore(config.Root, config.Protector).Save(FreshTokens());
        using var service = Service(new FakeActivationTransport(), config);

        Assert.True(service.IsSignedIn);
        Assert.Equal("mock_at_1", service.BearerToken);
        Assert.Equal("演示买家", service.Current!.Account.DisplayName);
    }

    /// <summary>
    /// A session file another user wrote fails closed: no readable session, which
    /// costs one QR scan — the alternative is a stranger's token buying on this
    /// machine's account.
    /// </summary>
    [Fact]
    public void ASessionFromAnotherMachineReadsAsSignedOut()
    {
        using var config = new TempLicenseConfig();
        new AuthTokenStore(config.Root, new FakeSecretProtector("somebody-else")).Save(FreshTokens());
        using var service = Service(new FakeActivationTransport(), config);

        Assert.False(service.IsSignedIn);
        Assert.Null(service.Current);
    }
}
