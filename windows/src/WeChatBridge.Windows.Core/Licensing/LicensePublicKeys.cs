using System.Security.Cryptography;

namespace WeChatBridge.Windows.Core.Licensing;

/// <summary>
/// The verification keys, compiled in.
/// <para>
/// They are constants rather than a file on purpose. A key read from
/// <c>AppLayout.Config</c> would be a key a user can replace — and in a portable
/// build that directory is a plaintext folder on a USB stick. An embedded resource
/// would only add a way for the file to be missing.
/// </para>
/// <para>
/// Keys are selected by licence format version so a rotation does not empty the field:
/// a licence signed under the old version keeps verifying under its own key while new
/// licences are issued under the new one.
/// </para>
/// </summary>
public static class LicensePublicKeys
{
    /// <summary>
    /// The issuing key for licence format version 1: the base64url
    /// SubjectPublicKeyInfo the server signs with.
    /// <para>
    /// Paired with the private key in the server's signing pipeline, which is
    /// where the key is generated and the only place it lives. It is not in
    /// this repository and is not on any developer machine.
    /// </para>
    /// <para>
    /// base64url, unpadded — <see cref="Base64Url.TryDecode"/> is what reads
    /// this, and the same encoding the canonical payload carries, so a key
    /// written in standard base64 would import as different bytes here than
    /// the server derived them from.
    /// </para>
    /// </summary>
    public const string PlaceholderKeyV1 = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEhVxpyiTAh_oFRSPEUU7S1HLOYzjwOb1W5VF3Skp_-IH7yIInku28_WVWDtis0xuBAmCaHFycB3aWeQUDxkb5_g";

    /// <summary>Format version to base64url SubjectPublicKeyInfo.</summary>
    public static IReadOnlyDictionary<int, string> PublicKeys { get; } =
        new Dictionary<int, string> { [LicenseToken.CurrentSchemaVersion] = PlaceholderKeyV1 };

    /// <summary>The key for a format version, or <c>null</c> when the version is unknown.</summary>
    public static string? ForSchemaVersion(int schemaVersion) =>
        PublicKeys.TryGetValue(schemaVersion, out var key) ? key : null;

    /// <summary>
    /// The key factory the verifier is given. Returns <c>null</c> rather than throwing
    /// for anything unusable, because "this licence has no key" and "this licence is
    /// not signed" are the same answer for the caller: it does not verify. The
    /// placeholder above is deliberately not importable, and the test suite injects a
    /// factory over its own throwaway key pair instead of borrowing this one.
    /// </summary>
    public static ECDsa? ImportSubjectPublicKeyInfo(string? base64UrlSubjectPublicKeyInfo)
    {
        if (!Base64Url.TryDecode(base64UrlSubjectPublicKeyInfo, out var der))
            return null;
        try
        {
            var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(der, out _);
            return key;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
