namespace WeChatBridge.Windows.Core.Delivery;

/// <summary>
/// Which representation of shared content WeChatBridge accepts from the host.
/// Ported from <c>Representation.swift</c>.
///
/// On macOS the share provider offers UTIs and the code picks the least lossy
/// one. On Windows the share contract offers <c>StandardDataFormats</c> names;
/// the meaningful decision is the same: take real files when they exist, and
/// never let a URL stand in for a file — the same reason macOS refused a file
/// representation of <c>public.url</c>, which would stage a 40-byte link file
/// instead of the attachment.
/// </summary>
public enum LoadStrategy
{
    /// <summary>Real files (StorageItems / a shell file list).</summary>
    File,
    /// <summary>Plain text — a link or a title, never mistaken for an attachment.</summary>
    Text,
}

public sealed record Representation(string Format, LoadStrategy Strategy)
{
    /// <summary><c>StandardDataFormats.StorageItems</c> — real StorageFile objects.</summary>
    public const string StorageItemsFormat = "StorageItems";
    /// <summary>Explorer-style file list, offered by hosts that expose the share as a drop.</summary>
    public const string FileDropFormat = "FileDrop";
    public const string UriFormat = "Uri";
    public const string TextFormat = "Text";

    /// <summary>
    /// Picks the representation that loses the least information.
    /// Returns null when only a link or text is offered: the share helper treats
    /// a file-less share as a failure to report, not something to fabricate from.
    /// </summary>
    public static Representation? Choose(IEnumerable<string> offeredFormats)
    {
        var formats = offeredFormats as IReadOnlyCollection<string> ?? offeredFormats.ToList();
        if (formats.Contains(StorageItemsFormat))
            return new Representation(StorageItemsFormat, LoadStrategy.File);
        if (formats.Contains(FileDropFormat))
            return new Representation(FileDropFormat, LoadStrategy.File);
        return null;
    }

    /// <summary>
    /// The name the user should see in history.
    ///
    /// macOS measured that the temporary file is named after the *type*
    /// ("Zip归档.zip") — a reliable extension source and a terrible name source.
    /// The Windows equivalent: the provider's preferred name wins, the staged
    /// file only fills in a missing extension.
    /// </summary>
    public static string DisplayName(string? preferredName, string stagedFileName)
    {
        var stagedLeaf = Path.GetFileName(stagedFileName);
        var raw = string.IsNullOrEmpty(preferredName) ? stagedLeaf : preferredName!;
        if (Path.GetExtension(raw).Length == 0 && Path.GetExtension(stagedLeaf) is { Length: > 0 } fallback)
            raw += fallback;
        return raw;
    }
}
