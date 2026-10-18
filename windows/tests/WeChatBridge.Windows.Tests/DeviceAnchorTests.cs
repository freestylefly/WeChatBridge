using System.Reflection;
using System.Runtime.Versioning;
using WeChatBridge.Windows.Core.Licensing;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// The hardware anchor: what goes into it, what comes out, and the three evidence tiers.
/// <para>
/// §10.11 is emphatic that the anchor is the <em>only</em> thing standing between a
/// copied Config directory and a second activated install, and equally emphatic that
/// it must never refuse a paying customer it simply cannot judge. These tests pin both
/// halves, because a change that helps either one at the cost of the other is the
/// failure this layer exists to avoid.
/// </para>
/// </summary>
public sealed class DeviceAnchorTests
{
    // ---- normalisation -----------------------------------------------------------

    [Fact]
    public void FirmwareFieldsAreAnUnorderedSet()
    {
        // OEM registry roles differ, and the enumeration order is not a contract.
        var forwards = DeviceAnchor.Create(["Dell Inc.", "XPS 15 9520", "0X2K3L"], ["5C-80-9F-11-22-33"]);
        var backwards = DeviceAnchor.Create(["0X2K3L", "XPS 15 9520", "Dell Inc."], ["5C-80-9F-11-22-33"]);

        Assert.Equal(forwards.Canonical, backwards.Canonical);
        Assert.Equal(AnchorDigest.Compute(forwards), AnchorDigest.Compute(backwards));
    }

    [Fact]
    public void FirmwareCasingAndPaddingDoNotChangeTheDigest()
    {
        var raw = DeviceAnchor.Create(["Dell Inc.", "XPS 15 9520"], ["5C-80-9F-11-22-33"]);
        var padded = DeviceAnchor.Create(["  dell inc. ", "xps  15   9520"], ["5C-80-9F-11-22-33"]);

        Assert.Equal(raw.Canonical, padded.Canonical);
    }

    [Fact]
    public void FirmwareKeepsItsInnerSpacesAsSingleSpaces()
    {
        var anchor = DeviceAnchor.Create(["XPS 15 9520"], []);

        Assert.Equal("XPS 15 9520", anchor.Firmware);
    }

    [Theory]
    [InlineData("5C-80-9F-11-22-33")]
    [InlineData("5C:80:9F:11:22:33")]
    [InlineData("5c809f112233")]
    [InlineData("5C.80.9F.11.22.33")]
    public void MacSeparatorAndCaseDoNotChangeTheAnchor(string mac)
    {
        var anchor = DeviceAnchor.Create(["Dell Inc."], [mac]);

        Assert.Equal(["5C809F112233"], anchor.Macs);
    }

    [Fact]
    public void MacOrderAndDuplicatesDoNotChangeTheAnchor()
    {
        var forwards = DeviceAnchor.Create(["Dell Inc."], ["5C809F112233", "A4B1C2D3E4F5", "5C809F112233"]);
        var backwards = DeviceAnchor.Create(["Dell Inc."], ["A4B1C2D3E4F5", "5C809F112233"]);

        Assert.Equal(forwards.Canonical, backwards.Canonical);
    }

    /// <summary>
    /// The §10.11 measurement: 31 adapters, 10 with empty addresses, and one real
    /// <c>5E:…</c> alongside the genuine <c>5C:…</c>. Bit 1 of the first octet is the
    /// locally administered flag; keeping such addresses would let one Wi-Fi Direct
    /// connection invent a new anchor.
    /// </summary>
    [Theory]
    [InlineData("5E:80:9F:11:22:33", "bit 1 set — locally administered, i.e. randomised or virtual")]
    [InlineData("02:00:00:00:00:01", "bit 1 set — Wi-Fi Direct soft AP")]
    [InlineData("01:00:5E:00:00:FB", "bit 0 set — multicast")]
    [InlineData("03:00:00:00:00:01", "both flags set")]
    public void LocallyAdministeredAndMulticastAddressesAreDropped(string mac, string because)
    {
        var anchor = DeviceAnchor.Create(["Dell Inc."], [mac, "5C809F112233"]);

        Assert.True(anchor.Macs.Count == 1, because);
        Assert.Equal(["5C809F112233"], anchor.Macs);
        Assert.DoesNotContain(
            mac.Replace(":", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal),
            anchor.Canonical,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("00-00-00-00-00-00-00")]  // 14 hex digits is not a MAC
    [InlineData("5C-80-9F-11-22")]         // too short
    [InlineData("5C-80-9F-11-22-33-44")]
    [InlineData("5C-80-9F-11-22-ZZ")]
    public void EmptyAndMalformedAddressesAreDropped(string mac)
    {
        Assert.Empty(DeviceAnchor.Create(["Dell Inc."], [mac]).Macs);
    }

    [Fact]
    public void AnEmptyAdapterListIsTheSentinelNotAnEmptyString()
    {
        var anchor = DeviceAnchor.Create(["Dell Inc."], []);

        Assert.Equal("mac=-", anchor.Canonical.Split('\n')[1]);
    }

    [Fact]
    public void AnUnreadableRegistryLeavesTheFirmwareSectionAbsent()
    {
        var anchor = DeviceAnchor.Create(null, ["5C809F112233"]);

        Assert.Equal(DeviceAnchor.Absent, anchor.Firmware);
        Assert.StartsWith("fw=-\n", anchor.Canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOsNameDropsItsVersionSoAFeatureUpdateDoesNotChurnTheFingerprint()
    {
        var versioned = DeviceAnchor.Create(["Dell Inc."], ["5C809F112233"], "Microsoft Windows 11 Pro 24H2");
        var plain = DeviceAnchor.Create(["Dell Inc."], ["5C809F112233"], "microsoft windows 11 pro");

        Assert.Equal("MICROSOFT WINDOWS", versioned.Os);
        Assert.Equal(AnchorDigest.Compute(versioned), AnchorDigest.Compute(plain));
    }

    [Fact]
    public void TheCanonicalFormIsStableAcrossASerialisationRoundTrip()
    {
        var anchor = LicensingHarness.AnchorA();
        var record = DeviceAnchorRecord.From(anchor);
        var restored = new DeviceAnchorRecord(1, record.Firmware, record.Macs, record.Os).ToAnchor();

        Assert.Equal(anchor.Canonical, restored.Canonical);
        Assert.Equal(AnchorDigest.Compute(anchor), AnchorDigest.Compute(restored));
    }

    [Fact]
    public void TheDigestIsNotTheBareCanonicalTextHash()
    {
        // The domain separator is what stops a device digest and a licence payload from
        // being the same preimage.
        var anchor = LicensingHarness.AnchorA();
        var bare = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(anchor.Canonical)));

        Assert.NotEqual(bare, AnchorDigest.Compute(anchor));
    }

    // ---- evidence tiers ----------------------------------------------------------

    [Fact]
    public void MatchingFirmwareAndAnIntersectingAdapterSetIsSameMachine()
    {
        var stored = LicensingHarness.AnchorA();

        // A USB NIC and a Wi-Fi Direct session were added since; the original card is
        // still there, so this is the same machine. An aggregate digest would fail here.
        var observed = DeviceAnchor.Create(
            LicensingHarness.FirmwareA,
            ["5C809F112233", "001A2B3C4D5E"]);

        Assert.Equal(AnchorEvidence.SameMachine, DeviceAnchor.Compare(stored, observed));
    }

    [Fact]
    public void DifferentFirmwareIsCounterEvidenceOnItsOwn()
    {
        Assert.Equal(
            AnchorEvidence.DifferentMachine,
            DeviceAnchor.Compare(LicensingHarness.AnchorA(), LicensingHarness.AnchorB()));
    }

    [Fact]
    public void TwoPopulatedAdapterSetsThatDoNotIntersectAreCounterEvidence()
    {
        // Same firmware string, but none of the original adapters is present — a disk
        // image moved onto different hardware.
        var stored = DeviceAnchor.Create(["Dell Inc."], ["5C809F112233"]);
        var observed = DeviceAnchor.Create(["Dell Inc."], ["A4B1C2D3E4F5"]);

        Assert.Equal(AnchorEvidence.DifferentMachine, DeviceAnchor.Compare(stored, observed));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingMaterialOnOneSideIsNoEvidenceRatherThanGuilt(bool storedHasFirmware)
    {
        // Firmware agrees wherever both sides can see it, but the observed machine
        // reports no adapter at all. The one hardware fact available agrees and nothing
        // contradicts it, so this cannot become a verdict.
        var stored = DeviceAnchor.Create(
            storedHasFirmware ? LicensingHarness.FirmwareA : null,
            LicensingHarness.MacsA);
        var observed = DeviceAnchor.Create(LicensingHarness.FirmwareA, []);

        Assert.Equal(AnchorEvidence.NoEvidence, DeviceAnchor.Compare(stored, observed));
    }

    [Fact]
    public void FirmwareThatAgreesWithoutAnyAdapterMaterialOnEitherSideIsNoEvidence()
    {
        var stored = DeviceAnchor.Create(["Dell Inc."], []);
        var observed = DeviceAnchor.Create(["Dell Inc."], null);

        Assert.Equal(AnchorEvidence.NoEvidence, DeviceAnchor.Compare(stored, observed));
    }

    [Fact]
    public void AnUnreadableMachineFallsIntoNoEvidenceRatherThanGuilt()
    {
        var unreadable = FakeMachineFactsReader.Unreadable().Read();

        Assert.Equal(DeviceAnchor.Absent, unreadable.Firmware);
        Assert.Empty(unreadable.Macs);
        Assert.Equal(AnchorEvidence.NoEvidence, DeviceAnchor.Compare(LicensingHarness.AnchorA(), unreadable));
    }

    [Fact]
    public void NoRecordedAnchorIsNoEvidence()
    {
        Assert.Equal(
            AnchorEvidence.NoEvidence,
            DeviceAnchor.Compare(null, LicensingHarness.AnchorA()));
    }

    [Fact]
    public void AnUnrecordedAnchorOnBothSidesIsNoEvidence()
    {
        Assert.Equal(
            AnchorEvidence.NoEvidence,
            DeviceAnchor.Compare(DeviceAnchor.Create(null, null, null), DeviceAnchor.Create(null, null, null)));
    }

    // ---- source-level contracts --------------------------------------------------

    /// <summary>
    /// §10.6 and §10.11 name the three firmware fields and rule two out by name. A
    /// silent reordering or re-adding of that list is invisible in review and would make
    /// a BIOS update read as a hardware swap, so it is pinned here.
    /// </summary>
    [Theory]
    [InlineData("SystemManufacturer", true)]
    [InlineData("BaseBoardProduct", true)]
    [InlineData("SystemProductName", true)]
    [InlineData("BIOSVersion", false)]
    [InlineData("BaseBoardVersion", false)]
    [InlineData("MachineGuid", false)]
    [InlineData("Win32_ComputerSystemProduct", false)]
    public void OnlyTheThreeModelFieldsAreReadAndMachineGuidIsNeverUsed(string name, bool expected)
    {
        var text = File.ReadAllText(SourceFile("Licensing/IMachineFactsReader.cs"));

        Assert.Equal(expected, text.Contains($"\"{name}\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>Registry.LocalMachine</c> under net10.0 is analyzer error CA1416, and
    /// TreatWarningsAsErrors would turn dropping the annotation into a build break —
    /// or, worse, into a build break for whoever adds the next registry read.
    /// </summary>
    [Fact]
    public void TheRegistryReaderIsAnnotatedForWindowsSoCa1416CannotFire()
    {
        // Looked up by name rather than named directly: a static reference to a
        // windows-only type is itself a platform-compat call site.
        var type = typeof(LicenseResolver).Assembly
            .GetType("WeChatBridge.Windows.Core.Licensing.RegistryMachineFacts", throwOnError: true)!;

        var annotation = type.GetCustomAttribute<SupportedOSPlatformAttribute>();

        Assert.NotNull(annotation);
        Assert.Equal("windows", annotation!.PlatformName);
    }

    [Fact]
    public void NoLicensingStoreHasAnOptionalConstructorParameter()
    {
        // A defaulted directory would put tests in the developer's real profile and, in
        // a portable build, the licence state on the stick.
        foreach (var type in new[] { typeof(LicenseStore), typeof(InstallIdentityStore), typeof(LicensePaths) })
        {
            foreach (var constructor in type.GetConstructors())
            {
                var optional = constructor.GetParameters().Where(p => p.HasDefaultValue).ToList();
                Assert.True(optional.Count == 0,
                    $"{type.Name} has optional parameters: {string.Join(", ", optional.Select(p => p.Name))}");
            }
        }
    }

    private static string SourceFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "windows", "src", "WeChatBridge.Windows.Core",
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException(
            $"windows/src/WeChatBridge.Windows.Core/{relativePath} was not found above {AppContext.BaseDirectory}.");
    }
}
