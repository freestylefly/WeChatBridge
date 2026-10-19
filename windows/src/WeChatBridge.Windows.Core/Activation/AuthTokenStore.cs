using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WeChatBridge.Windows.Core.Licensing;

namespace WeChatBridge.Windows.Core.Activation;

/// <summary>
/// The signed-in session, kept across launches so a customer is not made to scan a
/// QR code every morning.
/// <para>
/// Stored through <see cref="Licensing.ISecretProtector"/> (DPAPI, current user)
/// rather than in the clear, for the same reason the install identity is: the access
/// token is a bearer credential that authorises the buy-out purchase, and an
/// access token that sits in a readable JSON file next to the licence is a licence
/// someone else can buy.
/// </para>
/// <para>
/// The refresh token outlives the access token and is the reason this file exists at
/// all — 90-day refresh TTL is what makes "log in once" true. The flip side is that
/// it is worth protecting, which is the paragraph above.
/// </para>
/// </summary>
public sealed class AuthTokenStore
{
    public const string FileName = "auth-session.dat";

    /// <summary>
    /// DPAPI entropy. Binds the blob to this product's purpose, so another program
    /// running as the same user cannot unprotect it and reuse the session: it would
    /// have to guess this string too.
    /// </summary>
    public static ReadOnlySpan<byte> Entropy => "wechatbridge.auth-session.v1"u8;

    private readonly string _path;
    private readonly ISecretProtector _protector;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public AuthTokenStore(string configDirectory, ISecretProtector protector)
    {
        ArgumentException.ThrowIfNullOrEmpty(configDirectory);
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _path = Path.Combine(configDirectory, FileName);
    }

    public string FilePath => _path;

    /// <summary>
    /// The stored session, or null when there is none we can use.
    /// <para>
    /// Fails closed, in every direction: no file, an unreadable file, a blob that
    /// another user wrote, a future schema. All of them mean "not signed in", which
    /// costs one QR scan — the alternative, carrying a half-trusted token, would cost
    /// a stranger a purchase.
    /// </para>
    /// </summary>
    public AuthTokens? Load()
    {
        try
        {
            if (!File.Exists(_path))
                return null;

            var blob = File.ReadAllBytes(_path);
            var plain = _protector.Unprotect(blob, Entropy);
            return JsonSerializer.Deserialize<AuthTokens>(Encoding.UTF8.GetString(plain), JsonOptions);
        }
        catch (CryptographicException)
        {
            // Written by another user, another machine, or another product. Same
            // answer as a missing file.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Replaces the stored session. The write is staged, then moved into place.</summary>
    public void Save(AuthTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokens, JsonOptions));
        var blob = _protector.Protect(plain, Entropy);

        // Staged so that a crash mid-write leaves the previous session intact
        // rather than a half-file that reads as "signed out" — or worse, as a
        // parseable session with a truncated refresh token.
        var staging = _path + ".tmp";
        File.WriteAllBytes(staging, blob);
        File.Move(staging, _path, overwrite: true);
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch (IOException)
        {
            // A session that cannot be deleted is a session the server's TTL will
            // end anyway. Failing here would strand the user with no way to sign out.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
