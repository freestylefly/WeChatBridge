using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Activation;

namespace WeChatBridge.Windows.Activation;

/// <summary>
/// The licence service's verbs, and the second file in this repository allowed to
/// name <c>System.Net.Http</c>.
/// <para>
/// <b>Why it is here and not in Core.</b> Core is the assembly
/// <c>EditionIsolationTests</c> certifies as naming no networking type at all, by
/// exact equality against a three-line allow-list. The redemption client solved this
/// the same way — decisions in Core, verbs here — and the reason to keep solving it
/// that way is that the other arrangement makes the promise unfalsifiable. A test
/// that has been widened to permit today's exception will be widened again tomorrow,
/// and the day it stops meaning anything is the day a user learns it from a packet
/// capture.
/// </para>
/// <para>
/// <b>What it will not do.</b> No cookies, no automatic redirect, no retry, no
/// redirect to a login page on another host. Every one of those is a way for a
/// request to reach a place the user did not name, and the buy-out privacy wording
/// says this build talks to exactly the licence service and nothing else. The
/// address is fixed at construction for the same reason: a base URL a settings file
/// can change is a base URL that can be changed to somewhere else.
/// </para>
/// </summary>
internal sealed class HttpActivationTransport : IActivationTransport
{
    private readonly HttpMessageHandler _handler;
    private readonly HttpClient _client;
    private readonly Uri _baseAddress;
    private int _closed;

    internal HttpActivationTransport(Uri baseAddress, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);

        // A base address without a trailing slash silently replaces its last
        // segment: new Uri("https://host/api", "v1/auth/session") is
        // https://host/v1/auth/session. Normalising here is cheaper than finding out
        // in production.
        _baseAddress = baseAddress.AbsoluteUri.EndsWith('/')
            ? baseAddress
            : new Uri(baseAddress.AbsoluteUri + "/", UriKind.Absolute);

        _handler = handler ?? new SocketsHttpHandler
        {
            // Nothing here needs a cookie jar, and a cookie sent to a third-party
            // host is a third-party identifier this product does not otherwise have.
            UseCookies = false,
            // Bounded so a socket cannot outlive the purchase it was opened for.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            // A redirect to another host would take the bearer token with it.
            AllowAutoRedirect = false,
        };

        _client = new HttpClient(_handler, disposeHandler: false)
        {
            // Infinite on purpose. HttpClient's own timeout raises the same
            // TaskCanceledException as the user pressing Cancel, and after the fact
            // no catch clause can tell the two apart — which would make "the server
            // never answered" indistinguishable from "the user gave up". The deadline
            // is a linked token source in AccountService and ActivationService.
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            BaseAddress = _baseAddress,
        };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WeChatBridge-Windows", Edition.Name));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>The client, for the test that pins the timeout, the agent and the address.</summary>
    internal HttpClient Client => _client;

    public async Task<ApiReply> SendAsync(ApiRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);

        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Path);
        if (request.Json is not null)
            message.Content = new StringContent(request.Json, Encoding.UTF8, "application/json");
        if (!string.IsNullOrWhiteSpace(request.BearerToken))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.BearerToken);

        try
        {
            using var response = await _client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new ApiReply((int)response.StatusCode, body);
        }
        catch (HttpRequestException error)
        {
            // Refused, reset, DNS failure, TLS failure: no response, so whether the
            // server acted on it is unknowable here.
            throw new ApiTransportException(error.Message, error);
        }
        catch (IOException error)
        {
            // A body that stops mid-stream arrives as an IOException rather than an
            // HttpRequestException, and it is the more interesting case: the status
            // line may already have said 200.
            throw new ApiTransportException(error.Message, error);
        }
    }

    /// <summary>
    /// Fetches an image from the licence service host.
    /// <para>
    /// The same client, the same deadline and the same failure mapping as a JSON
    /// call, so a QR code cannot become a second, unexamined network path just
    /// because it is not text.
    /// </para>
    /// </summary>
    public async Task<byte[]> GetBytesAsync(string path, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);

        using var message = new HttpRequestMessage(HttpMethod.Get, path);
        try
        {
            using var response = await _client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ApiException((int)response.StatusCode, $"图片获取失败（HTTP {(int)response.StatusCode}）。", ApiErrorCodes.Internal, "");

            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            throw new ApiTransportException(error.Message, error);
        }
        catch (IOException error)
        {
            throw new ApiTransportException(error.Message, error);
        }
    }

    /// <summary>
    /// Closes the connection pool. Separate from sealing, because this flow has no
    /// seal to set: unlike redemption, signing in and buying are repeated actions,
    /// and a closed window is not a promise never to speak to the service again.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        _client.Dispose();
        _handler.Dispose();
        GC.SuppressFinalize(this);
    }
}
