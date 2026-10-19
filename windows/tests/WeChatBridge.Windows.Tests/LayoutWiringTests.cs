using System.IO;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// Contract tests over the repository's own source, in the spirit of
/// <c>PackagingAssetsTests</c>: they assert how things are wired rather than
/// exercising runtime behaviour, which is what stops the two failures that are
/// invisible until a user is holding a USB stick.
/// </summary>
public sealed class LayoutWiringTests
{
    /// <summary>
    /// Files allowed to name LocalApplicationData directly.
    /// <para>
    /// Everything else must go through <c>AppLayout</c>, or a portable build
    /// writes some of its state to the stick and some to the machine — and the
    /// split is invisible: the app looks fine until the drive is pulled.
    /// </para>
    /// </summary>
    private static readonly string[] AllowedLocalAppDataFiles =
    [
        "WeChatBridge.Windows.Core/AppLayout.cs",
        // A file-dialog start folder, not a place user data is written.
        "WeChatBridge.Windows/Components/CollectionDialogs.cs",
        // Reads the installed copy's presence so the UI can warn that taking over
        // the share entry disables it.
        "WeChatBridge.Windows/Services/ShareRegistrationService.cs",
    ];

    [Fact]
    public void NoSourceFileResolvesLocalAppDataOutsideTheAllowlist()
    {
        var offenders = new List<string>();
        foreach (var file in SourceFiles())
        {
            var relative = Relative(file);
            if (AllowedLocalAppDataFiles.Any(a => relative.EndsWith(a, StringComparison.Ordinal)))
                continue;
            if (File.ReadAllText(file).Contains("SpecialFolder.LocalApplicationData", StringComparison.Ordinal))
                offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0,
            "These files resolve %LOCALAPPDATA% directly instead of through AppLayout, "
            + "which sends that data to the stick in a portable build:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The portable sentinel is deliberately not a constant that could be
    /// duplicated. Two definitions would let the build script's file and the
    /// resolver's file drift, and the symptom is a build that silently behaves
    /// as installed.
    /// </summary>
    [Fact]
    public void ThePortableMarkerNameIsDefinedExactlyOnce()
    {
        var definitions = SourceFiles()
            .Where(file => File.ReadAllText(file).Contains(AppLayout.PortableMarkerFileName, StringComparison.Ordinal))
            .Select(Relative)
            .Where(relative => relative.EndsWith("AppLayout.cs", StringComparison.Ordinal) == false)
            .ToList();

        Assert.True(definitions.Count == 0,
            "portable.flag is named outside AppLayout, where it can drift from the resolver:\n  "
            + string.Join("\n  ", definitions));
    }

    /// <summary>
    /// Every machine-wide coordination name carries the layout suffix. Without
    /// it a portable copy and an installed copy evict each other in silence: the
    /// second launch creates its Inbox, loses the single-instance mutex, and
    /// exits with no window.
    /// </summary>
    [Theory]
    [InlineData("WeChatBridge.Windows/App.xaml.cs")]
    [InlineData("WeChatBridge.ShareTarget/Program.cs")]
    [InlineData("WeChatBridge.Windows.Core/PrefetchHint.cs")]
    [InlineData("WeChatBridge.Windows.Core/Collections/CollectionImportProgress.cs")]
    [InlineData("WeChatBridge.Windows/LaunchAtLogin.cs")]
    public void CoordinationNamesAreSuffixedByLayout(string relativePath)
    {
        var text = File.ReadAllText(SourceFile(relativePath));

        Assert.Contains("CoordinationSuffix", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePortableRegistrationScriptShipsWithTheBuild()
    {
        var script = Path.Combine(PortableRoot(), "Register-ShareTarget.ps1");

        Assert.True(File.Exists(script), $"{script} is missing; the portable build cannot register a share entry.");
        // Registration is launched by the app with -SkipProcessKill, because the
        // script's Stop-Process would otherwise kill the app whose button ran it.
        Assert.Contains("SkipProcessKill", File.ReadAllText(script), StringComparison.Ordinal);
    }

    [Fact]
    public void TheFirstRunScriptShipsWithAByteOrderMark()
    {
        var script = Path.Combine(PortableRoot(), "FirstRun.ps1");

        Assert.True(File.Exists(script), $"{script} is missing.");
        // Windows PowerShell 5.1 reads a BOM-less .ps1 as the system ANSI code
        // page — GBK on a Chinese Windows — which silently corrupts the Chinese
        // strings instead of failing.
        var bytes = File.ReadAllBytes(script);
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "FirstRun.ps1 must be UTF-8 with a BOM.");
    }

    /// <summary>
    /// The portable build is the free tier of the buy-out edition, so it is
    /// built from the buy-out source set only.
    /// <para>
    /// A comment in the script is not enough of a guard: adding an
    /// <c>-Edition</c> parameter to it is a one-line change that looks
    /// harmless, compiles clean, and lets a subscription build reach a USB
    /// stick — where the licence travels with the drive and the account does
    /// not. Nothing else in the build would catch it.
    /// </para>
    /// </summary>
    [Fact]
    public void ThePortableBuildOnlyCompilesTheBuyoutEdition()
    {
        var script = Path.Combine(PortableRoot(), "build-portable.ps1");
        Assert.True(File.Exists(script), $"{script} is missing.");

        var text = File.ReadAllText(script);

        Assert.Contains("WeChatBridgeEdition", text, StringComparison.Ordinal);
        Assert.Contains("'Perpetual'", text, StringComparison.Ordinal);
        // ValidateSet is what an -Edition parameter needs, so its absence is
        // the thing being asserted: there is no parameter to pass.
        Assert.DoesNotContain("ValidateSet", text, StringComparison.Ordinal);
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(SrcRoot(), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            // obj\ holds generated markup that legitimately references the source.
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static string SourceFile(string relativePath) =>
        Path.Combine(SrcRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string Relative(string path) =>
        Path.GetRelativePath(SrcRoot(), path).Replace('\\', '/');

    private static string SrcRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "windows", "src");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException(
            $"windows/src was not found above the test output directory {AppContext.BaseDirectory}.");
    }

    private static string PortableRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "windows", "portable");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException(
            $"windows/portable was not found above the test output directory {AppContext.BaseDirectory}.");
    }
}
