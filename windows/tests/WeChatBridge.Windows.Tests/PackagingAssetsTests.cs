using System.Buffers.Binary;
using System.Xml.Linq;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// A share target only reaches the system and Weixin share sheets when the shell can load
/// the logos the manifest points at, at the geometry it declares. Declaring a single
/// oversized PNG as both the 44x44 and the 150x150 logo compiles cleanly and even registers
/// cleanly, but the target then never shows up in enumeration — a failure that is invisible
/// unless the declared asset is checked directly, so it is asserted here.
/// </summary>
public sealed class PackagingAssetsTests
{
    private static readonly XNamespace Foundation =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

    private static readonly XNamespace Uap =
        "http://schemas.microsoft.com/appx/manifest/uap/windows10";

    [Fact]
    public void ManifestLogosResolveToFilesOfTheDeclaredSize()
    {
        var sparsePackage = Path.Combine(PackagingRoot(), "SparsePackage");
        var manifest = XDocument.Load(Path.Combine(sparsePackage, "AppxManifest.xml"));
        var applications = manifest.Root!
            .Element(Foundation + "Applications")!
            .Elements(Foundation + "Application")
            .ToList();
        Assert.NotEmpty(applications);

        foreach (var application in applications)
        {
            var visualElements = application.Element(Uap + "VisualElements")!;
            AssertLogo(sparsePackage, "Square44x44Logo", visualElements.Attribute("Square44x44Logo")!.Value, 44);
            AssertLogo(sparsePackage, "Square150x150Logo", visualElements.Attribute("Square150x150Logo")!.Value, 150);
        }

        // The store logo is what the shell falls back to when no scaled variant is installed.
        var storeLogo = manifest.Root
            .Element(Foundation + "Properties")!
            .Element(Foundation + "Logo")!.Value;
        Assert.True(
            File.Exists(Resolve(sparsePackage, storeLogo)),
            $"Properties/Logo points at {storeLogo}, which is not in the sparse package.");
    }

    /// <summary>
    /// Weixin hides share targets whose display name contains the contiguous brand
    /// substrings 微信 / WeChat / Weixin, but does not normalise zero-width characters.
    /// The manifest therefore spells the product name with a U+2060 WORD JOINER between
    /// 微 and 信 so it renders as 微信流 without containing the filtered substring. Any
    /// tooling that rewrites these strings (normalisation, pretty-printing, code-gen)
    /// can silently strip the joiner and make the target vanish from the share menu —
    /// which is exactly the failure this assertion exists to catch.
    /// </summary>
    [Fact]
    public void DisplayStringsAvoidContiguousBrandSubstrings()
    {
        var manifest = XDocument.Load(
            Path.Combine(PackagingRoot(), "SparsePackage", "AppxManifest.xml"));

        var applications = manifest.Root!
            .Element(Foundation + "Applications")!
            .Elements(Foundation + "Application")
            .ToList();
        Assert.NotEmpty(applications);

        // The product name is the package identity's string: the one place the
        // U+2060 spelling must survive so the menu renders 微信流.
        Assert.Contains("微⁠信流",
            manifest.Root.Element(Foundation + "Properties")!.Element(Foundation + "DisplayName")!.Value);

        // Every entry's user-visible strings — DisplayName, app Description and
        // the ShareTarget description — go through the same filter. Entry titles
        // (发给 Codex, 复制到剪贴板…) carry no brand words at all.
        var visibleStrings = new List<string>();
        foreach (var application in applications)
        {
            var visualElements = application.Element(Uap + "VisualElements")!;
            visibleStrings.Add(visualElements.Attribute("DisplayName")!.Value);
            visibleStrings.Add(visualElements.Attribute("Description")!.Value);
            visibleStrings.Add(application
                .Element(Foundation + "Extensions")!
                .Element(Uap + "Extension")!
                .Element(Uap + "ShareTarget")!
                .Attribute("Description")!.Value);
        }

        // Ordinal comparison mirrors Weixin's raw substring check; culture-aware
        // comparison would treat U+2060 as ignorable and report a false positive.
        foreach (var text in visibleStrings)
        {
            Assert.DoesNotContain("微信", text, StringComparison.Ordinal);
            Assert.DoesNotContain("WeChat", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Weixin", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The manifest registers a single façade entry — Share.Hub, the 「微信流」
    /// row WeChat shows — whose Application Id the helper resolves through
    /// <see cref="Core.ShareActions.ShareEntryId"/> just like the retired
    /// per-app ids. Asserting the singleton rather than <c>All</c> keeps this
    /// a contract test: adding an Application back must be a deliberate act.
    /// </summary>
    [Fact]
    public void EveryShareEntryMapsToAKnownAction()
    {
        var manifest = XDocument.Load(
            Path.Combine(PackagingRoot(), "SparsePackage", "AppxManifest.xml"));
        var ids = manifest.Root!
            .Element(Foundation + "Applications")!
            .Elements(Foundation + "Application")
            .Select(a => a.Attribute("Id")!.Value)
            .ToList();

        Assert.Equal([Core.ShareActions.ShareEntryId(Core.ShareAction.Hub)], ids);

        // The Windows schema rejects Ids with underscores; keep the pattern pinned.
        foreach (var id in ids)
        {
            Assert.Matches(@"^([A-Za-z][A-Za-z0-9]*)(\.[A-Za-z][A-Za-z0-9]*)*$", id);
        }
    }

    private static void AssertLogo(string sparsePackage, string field, string declared, int size)
    {
        var file = Resolve(sparsePackage, declared);
        Assert.True(File.Exists(file), $"{field} points at {declared}, which is not in the sparse package.");
        Assert.Equal((size, size), ReadPngSize(file));
    }

    private static string Resolve(string sparsePackage, string declared) =>
        Path.Combine(sparsePackage, declared.Replace('\\', Path.DirectorySeparatorChar));

    /// <summary>
    /// Reads PNG dimensions straight from the IHDR chunk so the check does not depend on a
    /// Windows-only imaging API in a test that also has to run on the CI agent.
    /// </summary>
    private static (int Width, int Height) ReadPngSize(string path)
    {
        Span<byte> header = stackalloc byte[24];
        using (var stream = File.OpenRead(path))
        {
            stream.ReadExactly(header);
        }

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, header[..4].ToArray());
        return (
            BinaryPrimitives.ReadInt32BigEndian(header[16..20]),
            BinaryPrimitives.ReadInt32BigEndian(header[20..24]));
    }

    private static string PackagingRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "windows", "packaging");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            $"windows/packaging was not found above the test output directory {AppContext.BaseDirectory}.");
    }
}
