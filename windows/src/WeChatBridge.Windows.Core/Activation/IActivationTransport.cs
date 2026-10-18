namespace WeChatBridge.Windows.Core.Activation;

/// <summary>
/// One request to the licence service, described without naming an HTTP type.
/// <para>
/// <c>Method</c> is a string and <see cref="ApiReply.StatusCode"/> is an int on
/// purpose. Core is the assembly <c>EditionIsolationTests</c> certifies as naming no
/// networking type at all, by exact equality — and an exception filter is a type
/// reference too, which is why the mapping out of <c>HttpRequestException</c> lives
/// in the WPF assembly rather than here. Everything in Core that reaches the network
/// goes through this interface, and the verbs live in one file in the app assembly.
/// </para>
/// </summary>
/// <param name="Method">An uppercase HTTP verb.</param>
/// <param name="Path">A path relative to the base address, without a leading slash.</param>
/// <param name="Json">The request body, or null for no body.</param>
/// <param name="BearerToken">The access token to authorise with, or null for an anonymous call.</param>
public sealed record ApiRequest(string Method, string Path, string? Json, string? BearerToken);

/// <summary>
/// What came back: the status code and the body, both untyped. Status codes are
/// compared against the <c>ApiErrorCodes</c> / literal numbers in this namespace so
/// that Core never has to know what an <c>HttpStatusCode</c> is.
/// </summary>
public sealed record ApiReply(int StatusCode, string Body)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}

/// <summary>
/// The seam every outbound call in this flow goes through.
/// <para>
/// It is not a general HTTP client. It has no cookies, no redirect following and no
/// retry of its own, because the buy-out promise is that this build reaches exactly
/// the endpoints named in <see cref="ActivationEndpoint"/> and nothing else. A
/// transport that could quietly retry would break that promise invisibly — the user
/// would have paid for one request and sent three.
/// </para>
/// </summary>
public interface IActivationTransport : IDisposable
{
    Task<ApiReply> SendAsync(ApiRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches a same-host image — today, the QR code.
    /// <para>
    /// Deliberately part of the seam rather than left to the UI's own image loader.
    /// A window that loads an <c>http</c> URI on its own gets a fetch with no
    /// deadline, no user agent and no audit trail, and the privacy wording this
    /// product ships says every byte it sends goes through one place. It would also
    /// be a second network path that the allow-list test does not see, which is
    /// exactly how an allow-list stops meaning anything.
    /// </para>
    /// </summary>
    Task<byte[]> GetBytesAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// The request never produced a response: refused, reset, DNS failure, TLS failure,
/// or a body that stopped mid-stream.
/// <para>
/// Whether the server acted on it is unknowable from here, which is the whole reason
/// this is a distinct type. The login and activation flows are written to be safe to
/// repeat after one of these — they carry a ticket and a request id — so the UI can
/// offer "重试" honestly instead of guessing.
/// </para>
/// </summary>
public sealed class ApiTransportException : Exception
{
    public ApiTransportException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The call exceeded its deadline, as distinct from the user cancelling it.</summary>
public sealed class ApiTimeoutException : Exception
{
    public ApiTimeoutException(string message, Exception? inner = null) : base(message, inner) { }
}
