using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Redeem;

namespace WeChatBridge.Windows.Redeem;

/// <summary>
/// The buy-out edition's only network path, and the only file in either assembly
/// allowed to name <c>System.Net.Http</c>.
/// <para>
/// <b>Why it lives here and not in Core.</b> Core is the assembly
/// <c>EditionIsolationTests</c> certifies as naming no networking type at all, by exact
/// equality against a three-line allow-list — including the <c>catch</c> clause
/// problem, since an exception filter is a type reference. A redeem client that lived
/// there would not merely add an exception to the rule; it would make the rule
/// unfalsifiable. So Core keeps the logic and this file keeps the verbs.
/// </para>
/// <para>
/// <b>Why it seals itself.</b> The buy-out promise is "after redeeming, this build
/// stops talking to the network". Compilation cannot express that — this class is
/// permitted to name <c>HttpClient</c>, or the redemption could not happen at all —
/// so the promise is enforced here, at runtime, by a process-wide flag plus a disposal
/// of the handler that tears down the connection pool. Disposing matters on its own:
/// a keep-alive socket with nothing to send on it still sits there being visible to
/// anything watching the network, and "we are done" should mean the connection is gone
/// rather than idle.
/// </para>
/// </summary>
internal sealed class HttpRedeemTransport : IRedeemTransport
{
    /// <summary>
    /// Process-wide, because the promise is about the build rather than about this
    /// object: once one licence is on disk, no instance — including one built later by
    /// a caller that has not heard — may put a byte on the wire.
    /// </summary>
    private static int _sealed;

    private readonly HttpMessageHandler _handler;
    private readonly HttpClient _client;
    private int _closed;

    internal HttpRedeemTransport(HttpMessageHandler? handler = null)
    {
        _handler = handler ?? new SocketsHttpHandler
        {
            // Nothing here needs a cookie jar, and a cookie sent to a third-party host
            // is a third-party identifier this product does not otherwise have.
            UseCookies = false,
            // Bounded so a socket cannot outlive the redemption it was opened for even
            // if the seal path is somehow skipped.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };

        _client = new HttpClient(_handler, disposeHandler: false)
        {
            // Infinite on purpose. HttpClient's own timeout raises the same
            // TaskCanceledException as the user pressing Cancel, and after the fact no
            // catch clause can tell the two apart — which would make "the server never
            // answered" indistinguishable from "the user gave up", and those two
            // deserve different buttons. The deadline is a linked token source in
            // RedeemService instead, where the two tokens can be told apart.
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WeChatBridge-Windows", Edition.Name));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>The connection pool is closed for the rest of this process.</summary>
    internal static bool IsSealedProcessWide => Volatile.Read(ref _sealed) != 0;

    public bool IsSealed => IsSealedProcessWide;

    /// <summary>The client, for the test that pins the timeout and the user agent.</summary>
    internal HttpClient Client => _client;

    public async Task<RedeemHttpReply> PostAsync(string json, CancellationToken cancellationToken)
    {
        // Before the handler, before the request object, before anything. A sealed
        // channel must not be able to open a socket even by accident.
        if (IsSealedProcessWide)
            throw new RedeemChannelSealedException();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);

        using var request = new HttpRequestMessage(HttpMethod.Post, RedeemEndpoint.AbsoluteUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        try
        {
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new RedeemHttpReply((int)response.StatusCode, body);
        }
        catch (HttpRequestException error)
        {
            // Refused, reset, DNS failure, TLS failure: the request produced no
            // response, so whether the server burned the code is unknowable here.
            throw new RedeemTransportException(error.Message, error);
        }
        catch (IOException error)
        {
            // A body that stops mid-stream arrives as an IOException rather than an
            // HttpRequestException, and it is the *more* interesting case: the status
            // line may already have said 200.
            throw new RedeemTransportException(error.Message, error);
        }
    }

    public void Seal()
    {
        Interlocked.Exchange(ref _sealed, 1);
        Close();
    }

    /// <summary>
    /// Tears down the connection pool without sealing the channel.
    /// <para>
    /// The distinction is the point. Disposing is what a caller does when it is done
    /// with this transport — closing the dialog, for instance — and it must not take
    /// the promise with it: a user who opened the redemption window, read the wording
    /// and closed it has not redeemed anything, and a later attempt from the same
    /// process must still be allowed to reach the network. Sealing is a decision
    /// about the licence, and only <see cref="RedeemService"/> makes it.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        _client.Dispose();
        _handler.Dispose();
    }

    /// <summary>
    /// Test host only: clears the process-wide seal.
    /// <para>
    /// The flag has to be process-wide for the promise to mean anything, and a
    /// process-wide flag cannot be reset from a test without exactly this method. It
    /// is internal rather than public so that no production caller can reach it, and
    /// the application never does: the only thing that clears the channel is the app
    /// exiting, which is the behaviour the privacy wording describes.
    /// </para>
    /// </summary>
    internal static void ResetSealForTests() => Interlocked.Exchange(ref _sealed, 0);
}
