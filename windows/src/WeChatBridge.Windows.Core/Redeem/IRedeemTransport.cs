namespace WeChatBridge.Windows.Core.Redeem;

/// <summary>
/// The one network door in the buy-out edition, expressed as an interface.
/// <para>
/// Core names no HTTP type — not even in a <c>catch</c> clause, because an exception
/// filter is a type reference and <c>EditionIsolationTests</c> asserts by exact equality
/// that this assembly names no networking type at all. So the classification of an
/// unreachable network arrives here as <see cref="RedeemTransportException"/>, and the
/// HTTP verbs stay on the other side of the seam, in the WPF assembly that is allowed
/// to have them.
/// </para>
/// <para>
/// It is also the seam that makes the sealing testable: <see cref="Seal"/> is what
/// closes the channel, and <see cref="IsSealed"/> is what the rest of the app is
/// allowed to ask.
/// </para>
/// </summary>
public interface IRedeemTransport : IDisposable
{
    /// <summary>
    /// Whether the channel is closed. Process-wide rather than per instance: the
    /// promise is about this build, not about this object.
    /// </summary>
    bool IsSealed { get; }

    /// <summary>
    /// Posts a JSON body to <see cref="RedeemEndpoint.AbsoluteUrl"/> and returns the
    /// status and body verbatim. Classification is <see cref="RedeemService"/>'s job,
    /// not the transport's.
    /// </summary>
    /// <exception cref="RedeemChannelSealedException">
    /// The channel is closed. Thrown before any handler is touched, so a call after
    /// sealing cannot open a socket even in principle.
    /// </exception>
    /// <exception cref="RedeemTransportException">The request never produced a response.</exception>
    Task<RedeemHttpReply> PostAsync(string json, CancellationToken cancellationToken);

    /// <summary>
    /// Closes the channel for the rest of the process. Called by
    /// <see cref="RedeemService"/> the moment the licence is on disk.
    /// </summary>
    void Seal();
}

/// <summary>What came back, unclassified.</summary>
/// <param name="StatusCode">HTTP status. Kept as an int so this assembly names no HTTP enum.</param>
/// <param name="Body">The response body, empty for a body-less error.</param>
public sealed record RedeemHttpReply(int StatusCode, string Body);

/// <summary>
/// The request failed in a way that leaves the outcome unknown: a refused connection,
/// a reset mid-body, a dropped tunnel.
/// <para>
/// A dedicated type rather than <c>HttpRequestException</c> so that this assembly never
/// names an HTTP type, and so that "the transport could not tell" is one thing the
/// service can catch rather than a set of framework exceptions it must enumerate.
/// </para>
/// </summary>
public sealed class RedeemTransportException : Exception
{
    public RedeemTransportException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The channel is sealed. Raised by every entry point into the redeem path once a
/// licence has been persisted.
/// <para>
/// An exception rather than a failed outcome, because after this point there is no
/// action a user could take that would help: the promise is "no further network
/// requests", and a returned <c>RedeemFailure</c> would look like a code problem the
/// user could fix.
/// </para>
/// </summary>
public sealed class RedeemChannelSealedException : InvalidOperationException
{
    public RedeemChannelSealedException()
        : base("The redemption channel is sealed: this build has already redeemed its licence and makes no further network requests.")
    {
    }
}
