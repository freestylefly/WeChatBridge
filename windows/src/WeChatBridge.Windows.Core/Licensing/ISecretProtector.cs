namespace WeChatBridge.Windows.Core.Licensing;

/// <summary>
/// Secret storage capability. Core (net10.0) has no <c>ProtectedData</c> — that is a
/// compile error (CS0103), not a runtime one — so Core only declares the capability and
/// the single implementation lives in the WPF layer.
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> entropy);

    /// <summary>Throws <see cref="System.Security.Cryptography.CryptographicException"/> when the blob cannot be opened, which callers translate into "no identity here".</summary>
    byte[] Unprotect(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> entropy);
}
