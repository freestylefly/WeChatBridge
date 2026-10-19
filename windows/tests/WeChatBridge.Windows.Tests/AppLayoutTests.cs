using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// Exercises <see cref="AppLayout.Resolve"/> rather than
/// <see cref="AppLayout.Current"/>: every other suite in this repo passes its
/// directories explicitly, and resolving the singleton here would mean the first
/// test ever to touch it — in a test host whose base directory is the test bin
/// folder, creating real directories on the developer's machine.
/// </summary>
public sealed class AppLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "WeChatBridgeTests", Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string InstallRoot => Path.Combine(_root, "App");
    private string InstalledData => Path.Combine(_root, "LocalAppData", "WeChatBridge");

    private void Touch(params string[] directories)
    {
        foreach (var directory in directories)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, AppLayout.PortableMarkerFileName), "");
        }
    }

    private AppLayout ResolveFrom(string appDirectory) =>
        AppLayout.Resolve(appDirectory, InstalledData);

    [Fact]
    public void WithoutTheMarkerTheAppUsesLocalAppData()
    {
        Directory.CreateDirectory(InstallRoot);

        var layout = ResolveFrom(InstallRoot);

        Assert.False(layout.IsPortable);
        Assert.Null(layout.PortableRoot);
        Assert.Null(layout.Vault);
        Assert.Null(layout.InstallLogs);
        Assert.Equal(InstalledData, layout.DataRoot);
        // Installed builds keep the coordination names they have always had, so
        // an upgrade cannot orphan an already-running instance's mutex.
        Assert.Equal("", layout.CoordinationSuffix);
    }

    [Fact]
    public void TheMarkerBesideTheExecutableSelectsPortableMode()
    {
        Touch(InstallRoot);

        var layout = ResolveFrom(InstallRoot);

        Assert.True(layout.IsPortable);
        Assert.Equal(Path.GetFullPath(InstallRoot), layout.PortableRoot);
        Assert.Equal(Path.Combine(Path.GetFullPath(InstallRoot), "Data"), layout.DataRoot);
    }

    [Fact]
    public void TheHelperResolvesTheSameRootFromItsShareTargetDirectory()
    {
        Touch(InstallRoot);
        var helper = Path.Combine(InstallRoot, AppLayout.ShareTargetDirectoryName);
        Directory.CreateDirectory(helper);

        Assert.Equal(ResolveFrom(InstallRoot).DataRoot, ResolveFrom(helper).DataRoot);
    }

    /// <summary>
    /// The guard that keeps an installed build installed. Probing the parent
    /// unconditionally — the shape <c>AppLogos.PathFor</c> uses, where a second
    /// probe only costs a file check — would let a stray marker anywhere above
    /// the payload redirect an installed build's Inbox and Config onto a drive
    /// that is not there.
    /// </summary>
    [Fact]
    public void AMarkerAboveADifferentlyNamedDirectoryIsIgnored()
    {
        Touch(InstallRoot);
        var unrelated = Path.Combine(InstallRoot, "Resources");
        Directory.CreateDirectory(unrelated);

        var layout = ResolveFrom(unrelated);

        Assert.False(layout.IsPortable);
        Assert.Equal(InstalledData, layout.DataRoot);
    }

    [Fact]
    public void EveryDerivedRootSitsUnderOneDataRoot()
    {
        Touch(InstallRoot);

        var layout = ResolveFrom(InstallRoot);

        foreach (var root in new[] { layout.Inbox, layout.Config, layout.Skills, layout.SkillBackups })
        {
            Assert.StartsWith(layout.DataRoot, root, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(layout.PortableRoot!, root, StringComparison.OrdinalIgnoreCase);
        }
        // The vault travels with the drive rather than the data tree: it is the
        // user's notes, not our state.
        Assert.Equal(Path.Combine(layout.PortableRoot!, "Vault"), layout.Vault);
        Assert.StartsWith(layout.PortableRoot!, layout.InstallLogs!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Two sticks must not collide on one set of mutexes, pipes and events —
    /// otherwise a share handed to the other drive's resident process.
    /// </summary>
    [Fact]
    public void SeparateDrivesGetSeparateCoordinationNames()
    {
        var first = Path.Combine(_root, "StickE");
        var second = Path.Combine(_root, "StickF");
        Touch(first, second);

        Assert.NotEqual(
            ResolveFrom(first).CoordinationSuffix,
            ResolveFrom(second).CoordinationSuffix);
        Assert.NotEqual(
            ResolveFrom(first).CoordinationSuffix,
            ResolveFrom(first).CoordinationSuffix + "x");
    }

    [Fact]
    public void CoordinationSuffixIsStableAcrossResolutions()
    {
        Touch(InstallRoot);

        Assert.Equal(
            ResolveFrom(InstallRoot).CoordinationSuffix,
            ResolveFrom(InstallRoot).CoordinationSuffix);
    }
}
