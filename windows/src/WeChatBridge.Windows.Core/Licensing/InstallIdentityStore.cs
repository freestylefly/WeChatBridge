using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeChatBridge.Windows.Core.Licensing;

/// <summary>
/// The per-machine install identity: the id a licence is bound to, and when this
/// installation was first seen.
/// </summary>
public sealed record InstallIdentity(string InstallId, DateTimeOffset FirstSeenAt);

/// <summary>
/// The hardware anchor as it was recorded when the licence was accepted.
/// <para>
/// It is a separate file from the protected identity on purpose. The identity is
/// unreadable after a reinstall — that is the whole point of it — and if the anchor
/// went with it, recovery would have nothing left to compare against and every
/// reinstall would look like a machine swap.
/// </para>
/// </summary>
public sealed record DeviceAnchorRecord(
    [property: JsonPropertyName("v")] int SchemaVersion,
    [property: JsonPropertyName("fw")] string Firmware,
    [property: JsonPropertyName("mac")] IReadOnlyList<string> Macs,
    [property: JsonPropertyName("os")] string Os)
{
    public const int CurrentSchemaVersion = 1;

    public static DeviceAnchorRecord From(DeviceAnchor anchor) =>
        new(CurrentSchemaVersion, anchor.Firmware, anchor.Macs, anchor.Os);

    public DeviceAnchor ToAnchor() => new(Firmware, Macs, Os);
}

/// <summary>
/// Owns the two files that answer "which machine is this, and what did this machine
/// look like when the licence was accepted".
/// <para>
/// <c>install-identity.dat</c> holds the install id <em>protected</em>. That is the
/// single most important line in this layer: with the id in the clear, copying the
/// whole Config directory to another machine satisfies both checks at once — the
/// signature verifies and the binding matches — and the signature layer would be
/// decorative. Protected, the copy cannot be opened on the other machine, the local
/// store regenerates an id, and the licence bound to the old one stops applying.
/// </para>
/// <para>
/// The id is 16 CSPRNG bytes in base64url rather than a GUID: a v4 GUID carries
/// structure that survives in the version nibble and the variant bits, and nothing
/// here needs a GUID shape.
/// </para>
/// </summary>
public sealed class InstallIdentityStore
{
    public const string FileName = "install-identity.dat";
    public const string AnchorFileName = "device-anchor.json";
    public const int InstallIdByteLength = 16;

    /// <summary>
    /// Application-specific entropy. Not a secret — it exists so a blob protected for
    /// some unrelated purpose is not mistaken for an install identity.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("wechatbridge.install-identity.v1");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _directory;
    private readonly ISecretProtector _protector;

    public InstallIdentityStore(string configDirectory, ISecretProtector protector)
    {
        _directory = configDirectory;
        _protector = protector;
    }

    public string FilePath => Path.Combine(_directory, FileName);

    public string AnchorPath => Path.Combine(_directory, AnchorFileName);

    public bool Exists => File.Exists(FilePath);

    /// <summary>22 characters of base64url over 16 CSPRNG bytes.</summary>
    public static string NewInstallId()
    {
        Span<byte> bytes = stackalloc byte[InstallIdByteLength];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.Encode(bytes);
    }

    /// <summary>
    /// The local identity, or <c>null</c> when there is none that can be opened.
    /// <para>
    /// Missing and unopenable collapse to the same answer on purpose. Both mean "this
    /// directory carries no identity for this machine" — which is exactly the state a
    /// directory copied to another machine is in — and the resolver then decides from
    /// the hardware anchor instead. Distinguishing them would buy nothing and would
    /// put a "this file looks tampered with" branch in front of a paid user.
    /// </para>
    /// </summary>
    public InstallIdentity? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;
            var plain = _protector.Unprotect(File.ReadAllBytes(FilePath), Entropy);
            var identity = JsonSerializer.Deserialize<InstallIdentity>(plain, JsonOptions);
            return identity is null || string.IsNullOrWhiteSpace(identity.InstallId) ? null : identity;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
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
    }

    /// <summary>
    /// Writes the identity through <see cref="ISecretProtector"/> and swaps the file
    /// in atomically. The id is never written in the clear, not even transiently:
    /// the payload goes from memory straight into the protector.
    /// </summary>
    public void Save(InstallIdentity identity)
    {
        Directory.CreateDirectory(_directory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(identity, JsonOptions);
        var blob = _protector.Protect(plain, Entropy);
        var staging = FilePath + ".tmp";
        File.WriteAllBytes(staging, blob);
        File.Move(staging, FilePath, overwrite: true);
    }

    /// <summary>
    /// The anchor recorded earlier, or <c>null</c> when there is none this build can
    /// trust.
    /// <para>
    /// A file written by a <em>newer</em> format is treated as absent rather than
    /// parsed on a guess. That is deliberately the same answer as a deleted file:
    /// an unparseable anchor is no evidence, and no evidence is not guilt
    /// (see <see cref="DeviceAnchor.Compare"/>). Reading half of a format we do not
    /// understand would make a downgrade look like a hardware swap.
    /// </para>
    /// </summary>
    public DeviceAnchor? LoadAnchor()
    {
        try
        {
            if (!File.Exists(AnchorPath))
                return null;
            var record = JsonSerializer.Deserialize<DeviceAnchorRecord>(File.ReadAllText(AnchorPath), JsonOptions);
            return record is null || record.SchemaVersion != DeviceAnchorRecord.CurrentSchemaVersion
                ? null
                : record.ToAnchor();
        }
        catch (JsonException)
        {
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
    }

    public void SaveAnchor(DeviceAnchor anchor)
    {
        Directory.CreateDirectory(_directory);
        var record = DeviceAnchorRecord.From(anchor);
        var staging = AnchorPath + ".tmp";
        File.WriteAllText(staging, JsonSerializer.Serialize(record, JsonOptions));
        File.Move(staging, AnchorPath, overwrite: true);
    }
}
