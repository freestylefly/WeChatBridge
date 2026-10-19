using System.Text;

namespace WeChatBridge.Windows.Core.Licensing;

/// <summary>
/// Unpadded base64url, the encoding the licence format uses for ids, digests,
/// signatures and public keys.
/// <para>
/// Unpadded matters: <c>Convert.ToBase64String</c> would append <c>=</c>, and the
/// value travels inside JSON and inside the canonical signature payload, where a
/// padding character that one side writes and the other strips turns every licence
/// into a signature failure.
/// </para>
/// </summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
            return string.Empty;
        var text = Convert.ToBase64String(bytes);
        var end = text.Length;
        while (end > 0 && text[end - 1] == '=')
            end--;
        return text.AsSpan(0, end).ToString().Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Returns <c>false</c> for anything that is not valid base64url of the expected
    /// byte length. A malformed signature and a wrong signature are the same answer to
    /// the caller: the licence does not verify.
    /// </summary>
    public static bool TryDecode(string? text, out byte[] bytes, int? expectedLength = null)
    {
        bytes = [];
        if (string.IsNullOrEmpty(text))
            return false;

        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => "=" };
        try
        {
            var decoded = Convert.FromBase64String(padded);
            if (expectedLength is { } length && decoded.Length != length)
                return false;
            bytes = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string EncodeUtf8(string text) => Encode(Encoding.UTF8.GetBytes(text));
}
