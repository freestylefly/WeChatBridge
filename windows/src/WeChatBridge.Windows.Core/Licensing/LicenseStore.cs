using System.Text.Json;
using WeChatBridge.Windows.Core.Activation;

namespace WeChatBridge.Windows.Core.Licensing;

/// <summary>
/// Where the licence layer keeps its files. The constructor takes the directory rather
/// than defaulting to <see cref="AppLayout.Current"/>: a default would pull the
/// process-wide layout into the licence layer, and every test would then be writing
/// into the developer's real profile.
/// </summary>
public sealed class LicensePaths
{
    public LicensePaths(string configDirectory) => ConfigDirectory = configDirectory;

    public string ConfigDirectory { get; }

    /// <summary>The signed envelope, in the clear — its signature is what protects it.</summary>
    public string LicenseFile => Path.Combine(ConfigDirectory, LicenseStore.FileName);

    /// <summary>The protected install identity. Binary, and never readable as text.</summary>
    public string InstallIdentityFile => Path.Combine(ConfigDirectory, InstallIdentityStore.FileName);

    /// <summary>The hardware anchor recorded when the licence was accepted, in the clear.</summary>
    public string DeviceAnchorFile => Path.Combine(ConfigDirectory, InstallIdentityStore.AnchorFileName);
}

/// <summary>
/// Reads and writes <c>license.json</c>. Plaintext on purpose: the licence is signed,
/// and keeping it readable means a user whose licence is rejected can hand it to
/// support without extracting anything.
/// </summary>
public sealed class LicenseStore
{
    public const string FileName = "license.json";

    /// <summary>Written next to the file so a future format can be recognised without guessing.</summary>
    public const int CurrentSchemaVersion = LicenseToken.CurrentSchemaVersion;

    private readonly string _directory;

    public LicenseStore(string configDirectory) => _directory = configDirectory;

    public string FilePath => Path.Combine(_directory, FileName);

    /// <summary>Returns <c>null</c> when the file is missing, unreadable, or malformed.</summary>
    public LicenseEnvelope? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;
            return JsonSerializer.Deserialize<LicenseEnvelope>(File.ReadAllText(FilePath), JsonOptions);
        }
        catch (JsonException)
        {
            // A truncated or hand-edited licence is not a licence.
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

    public void Save(LicenseEnvelope envelope)
    {
        Directory.CreateDirectory(_directory);
        var staging = FilePath + ".tmp";
        File.WriteAllText(staging, JsonSerializer.Serialize(envelope, JsonOptions));
        File.Move(staging, FilePath, overwrite: true);
    }

    public void Clear()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);
    }

    /// <summary>
    /// Field names are pinned on the type; the only thing left to configure here is
    /// relaxed escaping, so a subject or plan containing non-ASCII survives a
    /// round trip instead of arriving as escape soup.
    /// <para>
    /// C-1 (契约登记册 §11.7): the Go issuer writes <c>iat</c>/<c>nbf</c>/<c>exp</c> as
    /// unix seconds (<c>license.go:91-93</c>), and the default reader refuses a JSON
    /// number for a <see cref="DateTimeOffset"/> — which made every real envelope read
    /// back as "no licence at all". The converter reads both dialects; the write path
    /// is RFC 3339 either way, so files this build wrote stay readable.
    /// </para>
    /// </summary>
    private static JsonSerializerOptions JsonOptions { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new TimestampConverter());
        return options;
    }
}
