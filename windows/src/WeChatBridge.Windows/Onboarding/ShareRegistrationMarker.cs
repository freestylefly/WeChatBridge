using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Onboarding;

/// <summary>
/// Records where the share package was registered from.
/// <para>
/// The package is registered with <c>Add-AppxPackage -ExternalLocation</c>,
/// which binds it to whatever path the drive had at that moment. Move the drive
/// to a different letter and the record still exists while pointing at nothing.
/// Nothing in the package itself reports that, and reading
/// <c>Package.InstalledLocation</c> back was not reliable enough to depend on,
/// so the root is written here instead — on the drive, so it travels with the
/// registration it describes.
/// </para>
/// </summary>
internal sealed record ShareRegistrationMarker(
    [property: JsonPropertyName("portableRoot")] string PortableRoot,
    [property: JsonPropertyName("registeredAtUtc")] DateTimeOffset RegisteredAtUtc)
{
    private const string FileName = "ShareRegistration.json";

    /// <summary>Where the marker lives when this copy is portable.</summary>
    public static string? Path => AppLayout.Current.InstallLogs is { } logs
        ? System.IO.Path.Combine(logs, FileName)
        : null;

    /// <summary>
    /// True when a marker exists for this exact root, or when this copy is not
    /// portable and therefore has nothing to compare.
    /// </summary>
    public static bool IsCurrent()
    {
        if (!AppLayout.Current.IsPortable)
            return true;
        var path = Path;
        if (path is null || !File.Exists(path))
            // No marker: either registration predates this build, or the user
            // cleared Data\. Either way we have no evidence it moved.
            return true;
        return Read()?.PortableRoot is { } root && SameRoot(root, AppLayout.Current.PortableRoot!);
    }

    public static void Write()
    {
        var path = Path;
        if (path is null)
            return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(
                new ShareRegistrationMarker(AppLayout.Current.PortableRoot!, DateTimeOffset.UtcNow),
                BatchManifest.JsonOptions));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing the marker costs a re-registration prompt, not a feature.
        }
    }

    public static void Clear()
    {
        try
        {
            if (Path is { } path && File.Exists(path))
                File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static ShareRegistrationMarker? Read()
    {
        try
        {
            return JsonSerializer.Deserialize<ShareRegistrationMarker>(
                File.ReadAllText(Path!), BatchManifest.JsonOptions);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool SameRoot(string left, string right) =>
        string.Equals(
            System.IO.Path.GetFullPath(left).TrimEnd('\\'),
            System.IO.Path.GetFullPath(right).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
}
