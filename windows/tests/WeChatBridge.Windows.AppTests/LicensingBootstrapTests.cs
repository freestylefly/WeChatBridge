using System.Security.Cryptography;
using System.Text;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Licensing;
using WeChatBridge.Windows.Services;

namespace WeChatBridge.Windows.AppTests;

/// <summary>
/// The application-side wiring of the licence layer: which directory it reads, which
/// secret protection it uses, and — the one that matters commercially — that an
/// unlicensed install still forwards.
/// </summary>
public sealed class LicensingBootstrapTests
{
    // ---- layout -------------------------------------------------------------------

    [Fact]
    public void AnInstalledLayoutKeepsItsLicenceFilesUnderTheLayoutConfigDirectory()
    {
        using var layout = new TempLayout();
        var bootstrap = LicensingBootstrap.Create(layout.Installed);

        Assert.False(layout.Installed.IsPortable);
        Assert.False(bootstrap.IsPortable);
        Assert.Equal(layout.Installed.Config, Path.GetDirectoryName(bootstrap.Store.FilePath));

        // Nothing is read on construction: a missing licence is not Pro, and the
        // identity is minted on the first resolve, not at startup wiring time.
        Assert.False(File.Exists(bootstrap.Store.FilePath));
    }

    [Fact]
    public void AFirstResolveMintsAnInstallIdentityAndLeavesTheUserUnlicensed()
    {
        using var layout = new TempLayout();
        var bootstrap = LicensingBootstrap.Create(layout.Installed);

        var resolution = bootstrap.Resolution;

        Assert.False(resolution.IsPro);
        Assert.Equal(LicenseActivation.None, resolution.Activation);
        Assert.False(string.IsNullOrWhiteSpace(resolution.InstallId));
        Assert.True(File.Exists(Path.Combine(layout.Installed.Config, InstallIdentityStore.FileName)));
        // The first launch is the moment a purchase gets something to bind to.
        Assert.True(File.Exists(Path.Combine(layout.Installed.Config, InstallIdentityStore.AnchorFileName)));
    }

    [Fact]
    public void TheResolutionIsAnsweredOnceAndOnlyRefreshMovesIt()
    {
        using var layout = new TempLayout();
        var bootstrap = LicensingBootstrap.Create(layout.Installed);
        var first = bootstrap.Resolution;

        // Read it again the way a binding would: same object, no second pass through
        // the state machine, no new identity.
        Assert.Same(first, bootstrap.Resolution);
        Assert.Same(first, bootstrap.Resolution);

        var refreshed = bootstrap.Refresh();
        Assert.NotSame(first, refreshed);
        Assert.Equal(first.InstallId, refreshed.InstallId);
    }

    // ---- portable -----------------------------------------------------------------

    [Fact]
    public void APortableLayoutIsTheFreeTierAndLeavesTheDriveUnmarked()
    {
        using var layout = new TempLayout();
        var bootstrap = LicensingBootstrap.Create(layout.Portable);

        Assert.True(bootstrap.IsPortable);
        var resolution = bootstrap.Resolution;

        Assert.False(resolution.IsPro);
        Assert.Equal(LicenseActivation.None, resolution.Activation);
        Assert.Equal("portable", resolution.Detail);
        // No identity: a stick that mints one on every machine it visits eventually
        // hands a real licence to the wrong one.
        Assert.Null(resolution.InstallId);
        Assert.Empty(Entries(layout.Installed.Config));
    }

    [Fact]
    public void PortableWritesNothingEvenAfterSeveralResolves()
    {
        using var layout = new TempLayout();
        var bootstrap = LicensingBootstrap.Create(layout.Portable);

        _ = bootstrap.Resolution;
        bootstrap.Refresh();
        bootstrap.Refresh();

        // Not even the directories: portable mode never had a reason to create them.
        Assert.Empty(Entries(layout.Installed.DataRoot));
    }

    [Fact]
    public void PortableIsWhereTheLicenceStateLivesToo()
    {
        using var layout = new TempLayout();
        // The stick is the installation, so a licence redeemed here has to persist
        // here — not under %LOCALAPPDATA% on whichever machine is plugged in.
        Assert.StartsWith(layout.PortableRoot, layout.Portable.Config, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the product rule ---------------------------------------------------------

    [Fact]
    public void AnUnlicensedInstallStillForwards()
    {
        using var layout = new TempLayout();
        var installed = LicensingBootstrap.Create(layout.Installed);

        Assert.False(installed.IsPro);
        Assert.True(installed.IsShareDeliveryAvailable);
    }

    [Fact]
    public void PortableStillForwards()
    {
        using var layout = new TempLayout();
        var portable = LicensingBootstrap.Create(layout.Portable);

        Assert.False(portable.IsPro);
        Assert.True(portable.IsShareDeliveryAvailable);
    }

    [Fact]
    public void ARefusedLicenceStillForwardsAndAsksForSupport()
    {
        using var layout = new TempLayout();
        var bootstrap = LicensingBootstrap.Create(layout.Installed);
        // A licence that cannot be verified is the worst shape an unlicensed install
        // can be in, and it is still an install that forwards.
        new LicenseStore(layout.Installed.Config).Save(new LicenseEnvelope
        {
            Subject = "some-other-machine",
            Signature = new string('A', 128),
        });

        var resolution = bootstrap.Refresh();

        Assert.False(resolution.IsPro);
        Assert.Equal(LicenseActivation.Unverifiable, resolution.Activation);
        Assert.True(bootstrap.IsShareDeliveryAvailable);
        // Unverifiable is not the same as Rejected: nothing is being asked of support.
        Assert.False(bootstrap.NeedsManualUnbind);
    }

    // ---- edition ------------------------------------------------------------------

    [Fact]
    public void TheBuildKnowsWhichEditionItIsWithoutReadingAnything()
    {
        using var layout = new TempLayout();
        var bootstrap = LicensingBootstrap.Create(layout.Installed);

        Assert.Equal(Edition.Name, bootstrap.EditionName);
#if WECHATBRIDGE_SUBSCRIPTION
        Assert.Equal("Subscription", bootstrap.EditionName);
        Assert.True(bootstrap.HasAccount);
#else
        Assert.Equal("Perpetual", bootstrap.EditionName);
        Assert.False(bootstrap.HasAccount);
#endif
    }

    /// <summary>Enumerate a directory that the code under test may legitimately never have created.</summary>
    private static string[] Entries(string path) =>
        Directory.Exists(path) ? Directory.EnumerateFileSystemEntries(path).ToArray() : [];
}

/// <summary>
/// A temp directory laid out like an installation, and one like a USB stick.
/// <para>
/// Everything the licence layer can write lives under
/// <see cref="Layout.Config"/>, so both layouts below are pointed at temp paths and
/// never at the developer's real <c>%LOCALAPPDATA%</c>. Constructing
/// <see cref="LicensingBootstrap"/> over <see cref="AppLayout.Current"/> in a test
/// would create directories and write real DPAPI ciphertext into the machine's actual
/// profile, and the tests would then be asserting on whatever the developer last ran.
/// </para>
/// </summary>
internal sealed class TempLayout : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "WeChatBridgeLicensingTests", Guid.NewGuid().ToString("N"));

    public TempLayout()
    {
        var appDirectory = Path.Combine(_root, "app");
        var portableRoot = Path.Combine(_root, "stick");
        PortableRoot = portableRoot;
        Directory.CreateDirectory(appDirectory);
        Directory.CreateDirectory(portableRoot);

        Installed = AppLayout.Resolve(appDirectory, Path.Combine(_root, "data"));
        // The marker is written through AppLayout's own constant. Its literal belongs
        // to AppLayout.cs alone (LayoutWiringTests), and a test that spelled it out
        // would drift from the resolver for no reason.
        File.WriteAllText(Path.Combine(portableRoot, AppLayout.PortableMarkerFileName), "");
        var shareTargetDirectory = Path.Combine(portableRoot, AppLayout.ShareTargetDirectoryName);
        Directory.CreateDirectory(shareTargetDirectory);
        Portable = AppLayout.Resolve(shareTargetDirectory, Path.Combine(_root, "unused"));
    }

    public AppLayout Installed { get; }

    public AppLayout Portable { get; }

    /// <summary>The removable drive's root, as <see cref="AppLayout"/> resolved it.</summary>
    public string PortableRoot { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// A throwaway config directory holding only what the licence layer owns.
/// <para>
/// Under <see cref="Path.GetTempPath()"/>, deleted recursively on dispose. Never
/// <c>ConfigStore.DefaultDirectory</c> and never <c>AppLayout.Current</c>: those point
/// at the real profile, and a test that writes DPAPI ciphertext there leaves state
/// behind that a later run would inherit.
/// </para>
/// </summary>
internal sealed class TempConfigDirectory : IDisposable
{
    public TempConfigDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "WeChatBridgeLicenseAppTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public LicensePaths Paths => new(Root);

    public LicenseStore Store => new(Root);

    public InstallIdentityStore Identity(ISecretProtector protector) => new(Root, protector);

    /// <summary>Copies every file the licence layer owns, the way a user moves a Config directory.</summary>
    public TempConfigDirectory Clone()
    {
        var clone = new TempConfigDirectory();
        foreach (var name in new[] { LicenseStore.FileName, InstallIdentityStore.FileName, InstallIdentityStore.AnchorFileName })
        {
            var source = Path.Combine(Root, name);
            if (File.Exists(source))
                File.Copy(source, Path.Combine(clone.Root, name), overwrite: true);
        }
        return clone;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Reports a fixed machine, so the recovery state machine can be exercised without
/// the developer's real hardware deciding the outcome.
/// </summary>
internal sealed class FixedMachineFacts : IMachineFactsReader
{
    public FixedMachineFacts(DeviceAnchor anchor) => Anchor = anchor;

    /// <summary>A desktop with a plausible BIOS key and two administered adapters.</summary>
    public static DeviceAnchor MachineA() => DeviceAnchor.Create(
        ["Dell Inc.", "XPS 15 9520", "0X2K3L"],
        ["5C-80-9F-11-22-33", "A4-B1-C2-D3-E4-F5"],
        "Microsoft Windows 11 Pro");

    /// <summary>A different vendor, and not one adapter in common with <see cref="MachineA"/>.</summary>
    public static DeviceAnchor MachineB() => DeviceAnchor.Create(
        ["Lenovo", "ThinkPad X1 Carbon Gen 11", "PF3XK9Q"],
        ["00-15-5D-77-88-99", "F0-E1-D2-C3-B4-A5"],
        "Microsoft Windows 11 Home");

    public DeviceAnchor Anchor { get; }

    public int ReadCount { get; private set; }

    public DeviceAnchor Read()
    {
        ReadCount++;
        return Anchor;
    }
}

/// <summary>
/// A signing key pair generated at run time.
/// <para>
/// The compiled-in key is a placeholder until the server has one, so a test that
/// leaned on <see cref="LicensePublicKeys"/> could only ever assert failure — which
/// would make the licence layer look broken rather than untested. This pair is created
/// in memory and never written down: the real private key belongs in the server's CI
/// secret store and must never enter this repository or a developer's machine.
/// </para>
/// </summary>
internal sealed class TestLicenseIssuer : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string PublicKeyBase64Url => Base64Url.Encode(_key.ExportSubjectPublicKeyInfo());

    public LicenseVerifier Verifier() =>
        new(LicensePublicKeys.ImportSubjectPublicKeyInfo,
            new Dictionary<int, string> { [LicenseToken.CurrentSchemaVersion] = PublicKeyBase64Url });

    /// <summary>Issues a buy-out licence bound to <paramref name="subject"/>.</summary>
    public LicenseEnvelope Issue(string subject, DateTimeOffset? issuedAt = null)
    {
        var envelope = new LicenseEnvelope
        {
            Subject = subject,
            Plan = "buyout",
            IssuedAt = issuedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            Entitlements = ["EXPORT", "SCENE"],
        };
        return envelope with { Signature = Base64Url.Encode(_key.SignData(
            LicenseCanonicalPayload.From(envelope).Bytes,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) };
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>UTF-8 text as a byte span, for protector round trips.</summary>
internal static class Bytes
{
    public static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
