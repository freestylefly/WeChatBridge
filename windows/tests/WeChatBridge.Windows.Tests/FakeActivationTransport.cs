using System.Text.Json;
using WeChatBridge.Windows.Core.Activation;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// A stand-in for the one seam every outbound call in the buy-out flow goes through.
/// It records every request — verb, path, body, bearer — and answers whatever the
/// test scripted, in order.
/// <para>
/// The failure modes are reproduced faithfully rather than conveniently: a transport
/// failure raises <see cref="ApiTransportException"/> (no response, so whether the
/// server acted is unknowable), which is the exception the services' retry paths are
/// written against. A fake that answered 500 instead would let the tests pass for the
/// wrong reason.
/// </para>
/// </summary>
internal sealed class FakeActivationTransport : IActivationTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Queue<Func<ApiRequest, ApiReply>> _script = new();
    private readonly Queue<Func<string, byte[]>> _bytes = new();
    private readonly List<ApiRequest> _requests = [];
    private readonly List<string> _bytePaths = [];

    /// <summary>Every request the transport was asked to send, in order.</summary>
    public IReadOnlyList<ApiRequest> Requests
    {
        get { lock (_requests) return _requests.ToArray(); }
    }

    /// <summary>Every image path fetched, in order.</summary>
    public IReadOnlyList<string> BytePaths
    {
        get { lock (_bytePaths) return _bytePaths.ToArray(); }
    }

    public int SendCount { get; private set; }

    public bool Disposed { get; private set; }

    public void Reply(int statusCode, string body) =>
        _script.Enqueue(_ => new ApiReply(statusCode, body));

    /// <summary>Answers from a function of the request, for bodies that echo it.</summary>
    public void ReplyFunc(Func<ApiRequest, ApiReply> answer) => _script.Enqueue(answer);

    /// <summary>The request never produced a response: refused, reset, DNS failure.</summary>
    public void Throw(string message = "the connection was reset") =>
        _script.Enqueue(_ => throw new ApiTransportException(message, new IOException(message)));

    public void ReplyBytes(byte[] bytes) => _bytes.Enqueue(_ => bytes);

    public void ThrowBytes(string message = "the connection was reset") =>
        _bytes.Enqueue(_ => throw new ApiTransportException(message, new IOException(message)));

    public Task<ApiReply> SendAsync(ApiRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        SendCount++;
        lock (_requests)
            _requests.Add(request);

        var next = _script.Count > 0 ? _script.Dequeue() : _ => new ApiReply(500, "{}");
        return Task.FromResult(next(request));
    }

    public Task<byte[]> GetBytesAsync(string path, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_bytePaths)
            _bytePaths.Add(path);

        var next = _bytes.Count > 0 ? _bytes.Dequeue() : _ => Array.Empty<byte>();
        return Task.FromResult(next(path));
    }

    public void Dispose() => Disposed = true;

    /// <summary>The request body, parsed — for asserting what was actually sent.</summary>
    public static JsonElement BodyOf(ApiRequest request) =>
        JsonDocument.Parse(request.Json ?? "{}").RootElement;

    /// <summary>The single request the transport was asked to send, or a loud failure.</summary>
    public ApiRequest Single()
    {
        Assert.Single(Requests);
        return Requests[0];
    }
}
