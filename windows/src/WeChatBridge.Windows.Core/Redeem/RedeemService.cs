using System.Text.Json;
using WeChatBridge.Windows.Core.Activation;
using WeChatBridge.Windows.Core.Licensing;

namespace WeChatBridge.Windows.Core.Redeem;

/// <summary>
/// Burns one redemption code and keeps the licence it returns.
/// <para>
/// Everything the flow decides lives here: what a code normalises to, which attempt a
/// retry belongs to, which failures a user can do anything about, and the instant
/// after the licence is on disk at which this build stops being allowed to talk to the
/// network. The window layer owns none of it — it renders
/// <see cref="RedeemOutcome"/>s and offers a Retry button or a different button.
/// </para>
/// <para>
/// <b>The invariant this class exists to protect:</b> one code produces at most one
/// <c>(code, installId)</c> pair, and therefore at most one licence. Two mechanisms
/// carry it. The server burns conditionally inside one transaction, so concurrent
/// attempts cannot both win. Here, the client writes its idempotency record
/// <em>before</em> the first request and reuses that request id forever after, so the
/// one failure mode that would break the invariant — the same user retrying because a
/// response was lost — becomes a lookup instead of a second attempt.
/// </para>
/// <para>
/// No exponential backoff, and no automatic retry of any kind. A redemption is a
/// deliberate one-shot action; an automatic retry makes "the app sent a request while I
/// was doing nothing" indistinguishable from silence, and the user who is mid-button
/// press deserves to know exactly what their machine did.
/// </para>
/// </summary>
public sealed class RedeemService : IDisposable
{
    private readonly LicenseStore _licenses;
    private readonly LicenseVerifier _verifier;
    private readonly PendingRedemptionStore _pending;
    private readonly IRedeemTransport _transport;
    private readonly RedeemMachineIdentity _machine;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _clockSkewTolerance;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string>? _log;
    private readonly JsonSerializerOptions _json = CreateJson();

    /// <summary>
    /// C-1 (契约登记册 §11.7): both the response envelope and the licence token
    /// inside it can carry timestamps the Go side wrote as unix seconds
    /// (<c>license.go:91-93</c>), and the default reader refuses a JSON number for a
    /// <see cref="System.DateTimeOffset"/> — which surfaced as
    /// <see cref="RedeemFailure.MalformedResponse"/> on a perfectly good licence, or a
    /// throw out of <see cref="Accept"/>. The converter reads both dialects.
    /// </summary>
    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new TimestampConverter());
        return options;
    }

    public RedeemService(
        LicenseStore licenses,
        LicenseVerifier verifier,
        PendingRedemptionStore pending,
        IRedeemTransport transport,
        RedeemMachineIdentity machine,
        TimeSpan? timeout = null,
        TimeSpan? clockSkewTolerance = null,
        Func<DateTimeOffset>? clock = null,
        Action<string>? log = null)
    {
        _licenses = licenses;
        _verifier = verifier;
        _pending = pending;
        _transport = transport;
        _machine = machine;
        _timeout = timeout ?? RedeemEndpoint.DefaultTimeout;
        _clockSkewTolerance = clockSkewTolerance ?? LicenseRuntimeOptions.Installed.ClockSkewTolerance;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = log;
    }

    /// <summary>The redemption this directory has started and not yet seen answered.</summary>
    public PendingRedemption? Pending => _pending.Load();

    /// <summary>Whether the channel has been closed, i.e. a licence is already on disk.</summary>
    public bool IsSealed => _transport.IsSealed;

    /// <summary>
    /// Redeems <paramref name="code"/>. One attempt per call: the caller decides
    /// whether to try again, and a retry passes the same code and gets the same
    /// request id.
    /// </summary>
    /// <exception cref="RedeemChannelSealedException">
    /// A licence has already been persisted on this build. Raised before anything else,
    /// so a caller that loops cannot reach the transport.
    /// </exception>
    public async Task<RedeemOutcome> RedeemAsync(string code, CancellationToken cancellationToken)
    {
        if (_transport.IsSealed)
            throw new RedeemChannelSealedException();

        if (!RedeemCode.TryParse(code, out var parsed))
            return RedeemOutcome.Failed(RedeemFailure.MalformedCode, false, "code does not normalise");

        if (!_machine.CanBind)
            return RedeemOutcome.Failed(RedeemFailure.NoInstallId, false, "no install identity to bind the licence to");

        var requestId = ResolveRequestId(parsed, out var mismatch);
        if (mismatch is not null)
            return mismatch;

        cancellationToken.ThrowIfCancellationRequested();
        var reply = await SendAsync(parsed, requestId, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome is not null)
            return reply.Outcome;

        return Accept(reply.Response!, requestId);
    }

    /// <summary>
    /// Throws away an unresolved pending record so a different code can be redeemed.
    /// <para>
    /// Deliberately explicit and never automatic. If the previous attempt may have
    /// burned its code, starting a new one silently would leave the customer with a
    /// burnt code and no licence — so this is a button they press with the consequence
    /// in front of them.
    /// </para>
    /// </summary>
    public void DiscardPending() => _pending.Clear();

    public void Dispose() => _transport.Dispose();

    /// <summary>
    /// The request id this code must use, writing it down first if this is the first
    /// attempt. Returns the pending record's id unchanged when it matches, which is
    /// what makes a retry a question rather than another burn.
    /// </summary>
    private string ResolveRequestId(RedeemCode code, out RedeemOutcome? mismatch)
    {
        mismatch = null;
        var existing = _pending.Load();
        if (existing is null)
        {
            var fresh = PendingRedemption.Create(code.Digest, _clock());
            _pending.Save(fresh);
            return fresh.RequestId;
        }

        if (string.Equals(existing.CodeDigest, code.Digest, StringComparison.Ordinal))
            return existing.RequestId;

        mismatch = RedeemOutcome.Failed(
            RedeemFailure.PendingMismatch,
            false,
            $"pending {existing.RequestId} belongs to another code digest");
        return string.Empty;
    }

    /// <summary>
    /// One attempt, with the timeout owned here rather than by the HTTP client.
    /// <para>
    /// A <c>HttpClient</c> with its own timeout raises the same
    /// <c>TaskCanceledException</c> as a user pressing Cancel, and no <c>catch</c> can
    /// tell them apart after the fact. The linked token source below fires first for
    /// one and not the other, which is the only reason the two paths can be told apart
    /// at all — so <c>HttpClient.Timeout</c> is infinite everywhere in this layer.
    /// </para>
    /// </summary>
    private async Task<Attempt> SendAsync(RedeemCode code, string requestId, CancellationToken cancellationToken)
    {
        var request = new RedeemRequest(
            code.Canonical,
            _machine.InstallId ?? string.Empty,
            _machine.DeviceDigest,
            requestId,
            RedeemEndpoint.ProductId);

        using var deadline = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        RedeemHttpReply reply;
        try
        {
            reply = await _transport.PostAsync(JsonSerializer.Serialize(request, _json), linked.Token)
                .ConfigureAwait(false);
        }
        catch (RedeemTransportException error)
        {
            // The request may or may not have arrived. Nothing here can tell, which is
            // exactly why the request id is on disk.
            _log?.Invoke($"redeem transport failure: {error.Message}");
            return new Attempt(RedeemOutcome.Failed(RedeemFailure.Unreachable, true, error.Message));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new Attempt(RedeemOutcome.Failed(RedeemFailure.Unreachable, true, $"no answer within {_timeout}"));
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (RedeemClassification.IsRetryableStatus(reply.StatusCode))
            return new Attempt(RedeemOutcome.Failed(RedeemFailure.Unreachable, true, $"server answered {reply.StatusCode}"));

        if (!RedeemClassification.IsSuccessStatus(reply.StatusCode))
        {
            var reason = ReadReason(reply.Body);
            return new Attempt(Terminal(
                RedeemClassification.FromReason(reason),
                string.IsNullOrEmpty(reason) ? $"server answered {reply.StatusCode}" : $"{reply.StatusCode} {reason}"));
        }

        RedeemResponse? body;
        try
        {
            body = JsonSerializer.Deserialize<RedeemResponse>(reply.Body, _json);
        }
        catch (JsonException error)
        {
            return new Attempt(Terminal(RedeemFailure.MalformedResponse, error.Message));
        }

        if (body is null)
            return new Attempt(Terminal(RedeemFailure.MalformedResponse, "empty body"));

        if (!body.GrantsLicense)
            return new Attempt(Terminal(RedeemClassification.FromReason(body.Reason), $"status {body.Status}"));
        if (string.IsNullOrWhiteSpace(body.LicenseToken))
            return new Attempt(Terminal(RedeemFailure.MalformedResponse, $"{body.Status} carried no licence"));

        return new Attempt(null, body);
    }

    /// <summary>
    /// A failure the server has ruled on, and the end of this attempt's idempotency
    /// record.
    /// <para>
    /// The record exists to make a retry safe. Once the server has answered about this
    /// code there is nothing left to retry — the answer is final — so keeping it would
    /// be actively harmful: the next code the customer pastes would be refused as a
    /// <see cref="RedeemFailure.PendingMismatch"/> against a record nobody can use any
    /// more. Leaving a stale one on disk is how a paid customer ends up unable to
    /// redeem anything at all.
    /// </para>
    /// </summary>
    private RedeemOutcome Terminal(RedeemFailure failure, string detail)
    {
        try
        {
            _pending.Clear();
        }
        catch (IOException error)
        {
            _log?.Invoke($"redeem could not clear the pending record: {error.Message}");
        }

        return RedeemOutcome.Failed(failure, false, detail);
    }

    /// <summary>
    /// Verifies what came back, writes it, and closes the channel.
    /// <para>
    /// The order is the whole design. Verify before persisting, so a corrupt or
    /// foreign envelope never reaches <c>license.json</c> and can never be the reason a
    /// later redemption is refused. Persist before sealing, because a licence that is
    /// in memory but not on disk is a licence the next launch does not have. Seal
    /// before returning, with no await between the write and the seal, so there is no
    /// instant in which the licence is durable and the channel is still open.
    /// </para>
    /// </summary>
    private RedeemOutcome Accept(RedeemResponse response, string requestId)
    {
        if (!Base64Url.TryDecode(response.LicenseToken, out var tokenBytes))
            return Terminal(RedeemFailure.MalformedResponse, "licence token is not base64url");

        LicenseEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<LicenseEnvelope>(System.Text.Encoding.UTF8.GetString(tokenBytes), _json);
        }
        catch (JsonException error)
        {
            return Terminal(RedeemFailure.MalformedResponse, error.Message);
        }

        if (envelope is null)
            return Terminal(RedeemFailure.MalformedResponse, "empty licence token");

        // Offline, against the key compiled into this build. The server's signature is
        // the only thing standing between a customer's licence file and anyone with a
        // text editor, so it is checked before the file is written rather than after.
        var verification = _verifier.Verify(envelope, _clock(), _clockSkewTolerance);
        if (!verification.IsValid)
        {
            _log?.Invoke($"redeem produced an unverifiable licence: {verification.Failure}");
            return Terminal(RedeemFailure.LicenseUnverifiable, verification.Failure ?? "verification failed");
        }

        // The resolver binds on subject == local install id, so a licence for another
        // machine is refused here instead of being written and then failing every
        // later check with a message nobody can act on.
        if (_machine.CanBind && !string.Equals(envelope.Subject, _machine.InstallId, StringComparison.Ordinal))
            return Terminal(RedeemFailure.SubjectMismatch, "licence subject is not this install");

        _licenses.Save(envelope);
        try
        {
            // The answer is known now, so the record that existed only to make this
            // attempt recoverable has done its job. Best effort: a stale file is
            // harmless once the channel is sealed, and refusing to finish the
            // redemption over a delete would be absurd.
            _pending.Clear();
        }
        catch (IOException error)
        {
            _log?.Invoke($"redeem could not clear the pending record: {error.Message}");
        }

        // Durable licence, closed channel, in that order, with nothing in between.
        _transport.Seal();
        _log?.Invoke($"redeem succeeded with request {requestId} ({response.Status})");
        return RedeemOutcome.Success(envelope, response.RedeemedAt, response.Status);
    }

    /// <summary>
    /// The reason out of an error body, when the server sends one. Returns
    /// <c>null</c> rather than throwing for anything unreadable.
    /// </summary>
    private string? ReadReason(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("reason", out var reason)
                && reason.ValueKind == JsonValueKind.String
                    ? reason.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// One attempt's result: either a failure, or the body to act on. Exactly one is
    /// set, which is checked at the call site rather than being a union type the
    /// compiler cannot help with.
    /// </summary>
    private sealed record Attempt(RedeemOutcome? Outcome, RedeemResponse? Response = null);
}
