using System.Text;
using WeChatBridge.Windows.Core.Licensing;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// The device-binding state machine — the part of this layer a paying customer feels.
/// <para>
/// Two cases decide whether the design is worth anything at all: a reinstall must be
/// invisible to the customer, and a copied Config directory must not buy a second
/// activation. Both are pinned here as named tests, because both are the kind of
/// regression that only surfaces in someone's support inbox.
/// </para>
/// </summary>
public sealed class LicenseResolverTests
{
    // ---- 生死线 1: reinstall ------------------------------------------------------

    /// <summary>
    /// The accident this layer exists to prevent: a customer paid a permanent price,
    /// reinstalled Windows, and lost Pro.
    /// </summary>
    [Fact]
    public void RecoverySucceedsWhenTheIdentityBlobIsDeletedButTheLicenseRemains()
    {
        using var keys = new TestSigningKey();
        using var config = new TempLicenseConfig();
        var machine = LicensingHarness.MachineAReader();
        var resolver = config.Resolver(keys, machine);

        // First launch mints the identity that a purchase will be bound to.
        var fresh = resolver.Resolve();
        Assert.False(fresh.IsPro);
        Assert.Equal(LicenseActivation.None, fresh.Activation);
        var originalInstallId = fresh.InstallId;
        Assert.False(string.IsNullOrWhiteSpace(originalInstallId));

        // The purchase lands, bound to that id.
        new LicenseStore(config.Root).Save(keys.Issue(originalInstallId!));
        Assert.True(resolver.Resolve().IsPro);

        // Reinstall: the protected blob is gone, the licence and the anchor are not.
        File.Delete(config.Paths.InstallIdentityFile);
        Assert.Null(new InstallIdentityStore(config.Root, config.Protector).Load());

        var recovered = resolver.Resolve();

        Assert.True(recovered.IsPro);
        Assert.Equal(LicenseActivation.Recovered, recovered.Activation);
        Assert.Equal(originalInstallId, recovered.InstallId);
        Assert.False(recovered.Degraded);
        Assert.Equal(AnchorEvidence.SameMachine, recovered.Evidence);

        // And the recovered id is the one on disk, not just the one in the return value.
        var store = new InstallIdentityStore(config.Root, config.Protector);
        Assert.Equal(originalInstallId, store.Load()!.InstallId);
    }

    [Fact]
    public void RecoveryIsRepeatableSoACrashMidwayCostsNothing()
    {
        using var keys = new TestSigningKey();
        using var config = new TempLicenseConfig();
        var resolver = config.Resolver(keys, LicensingHarness.MachineAReader());
        var original = resolver.Resolve().InstallId;
        new LicenseStore(config.Root).Save(keys.Issue(original!));
        File.Delete(config.Paths.InstallIdentityFile);

        // No transaction log and no half-state: run it as many times as you like.
        // The first run recovers; every later run finds the blob it wrote and matches.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var resolution = resolver.Resolve();
            Assert.True(resolution.IsPro);
            Assert.Equal(original, resolution.InstallId);
            Assert.Equal(
                attempt == 0 ? LicenseActivation.Recovered : LicenseActivation.Matched,
                resolution.Activation);
        }
    }

    [Fact]
    public void RecoveryNeedsNothingTheDeletedBlobHeld()
    {
        using var keys = new TestSigningKey();
        using var config = new TempLicenseConfig();
        var firstLaunch = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
        var afterReinstall = firstLaunch.AddMonths(8);
        var store = new InstallIdentityStore(config.Root, config.Protector);

        var installId = config.Resolver(keys, LicensingHarness.MachineAReader(), clock: () => firstLaunch)
            .Resolve().InstallId;
        Assert.Equal(firstLaunch, store.Load()!.FirstSeenAt);

        new LicenseStore(config.Root).Save(keys.Issue(installId!));
        File.Delete(config.Paths.InstallIdentityFile);

        // The original first-seen stamp lived in the deleted blob and is simply gone;
        // recovery reads only license.json and the anchor computed now, so it restamps
        // from the current clock. That is the cost of deleting the transaction log, and
        // §10.11 buys the idempotence with it.
        Assert.True(config.Resolver(keys, LicensingHarness.MachineAReader(), clock: () => afterReinstall)
            .Resolve().IsPro);
        Assert.Equal(afterReinstall, store.Load()!.FirstSeenAt);
    }

    // ---- 生死线 2: the copied Config directory ------------------------------------

    /// <summary>
    /// The attack this layer exists to stop: copy the whole Config directory to
    /// another machine and both checks — signature and binding — would pass at once.
    /// </summary>
    [Fact]
    public void TheLicenseFailsOnAnotherMachineWhenTheWholeConfigIsCopied()
    {
        using var keys = new TestSigningKey();
        using var original = new TempLicenseConfig();
        var ownerId = original.Resolver(keys, LicensingHarness.MachineAReader()).Resolve().InstallId;
        new LicenseStore(original.Root).Save(keys.Issue(ownerId!));

        using var stolen = original.CloneInto(LicensingHarness.MachineB);
        var log = new List<string>();
        var resolution = stolen.Resolver(keys, LicensingHarness.MachineBReader(), log: log).Resolve();

        Assert.False(resolution.IsPro);
        Assert.Equal(AnchorEvidence.DifferentMachine, resolution.Evidence);
        Assert.Equal(LicenseActivation.Rejected, resolution.Activation);
        Assert.True(resolution.NeedsManualUnbind);
        Assert.Contains(log, line => line.Contains("hardware anchor", StringComparison.Ordinal));

        // The foreign subject was never written onto this machine. It must not be: a
        // relabelled machine is a machine the copy can simply come back to.
        var identity = new InstallIdentityStore(stolen.Root, stolen.Protector).Load();
        Assert.NotNull(identity);
        Assert.NotEqual(ownerId, identity!.InstallId);
    }

    /// <summary>
    /// The same attack, one launch later. A refusal that only holds for the launch in
    /// which it happens is not a refusal.
    /// </summary>
    [Fact]
    public void ARefusedCopyStaysRefusedOnEverySubsequentLaunch()
    {
        using var keys = new TestSigningKey();
        using var original = new TempLicenseConfig();
        var ownerId = original.Resolver(keys, LicensingHarness.MachineAReader()).Resolve().InstallId;
        new LicenseStore(original.Root).Save(keys.Issue(ownerId!));

        using var stolen = original.CloneInto(LicensingHarness.MachineB);
        var resolver = stolen.Resolver(keys, LicensingHarness.MachineBReader());

        for (var launch = 1; launch <= 4; launch++)
        {
            var resolution = resolver.Resolve();
            Assert.False(resolution.IsPro, $"launch {launch} let the copied licence through");
            Assert.Equal(LicenseActivation.Rejected, resolution.Activation);
            Assert.Equal(AnchorEvidence.DifferentMachine, resolution.Evidence);
        }

        var identity = new InstallIdentityStore(stolen.Root, stolen.Protector).Load();
        Assert.NotEqual(ownerId, identity!.InstallId);
    }

    /// <summary>
    /// The copied blob is unreadable here, so this machine mints an identity of its own
    /// and it stays of its own — but the anchor that says "not this machine" must
    /// survive, or the next launch finds two matching anchors and adopts.
    /// </summary>
    [Fact]
    public void ARefusalLeavesTheForeignAnchorInPlaceForTheNextLaunch()
    {
        using var keys = new TestSigningKey();
        using var original = new TempLicenseConfig();
        var ownerId = original.Resolver(keys, LicensingHarness.MachineAReader()).Resolve().InstallId;
        new LicenseStore(original.Root).Save(keys.Issue(ownerId!));
        var foreignAnchor = File.ReadAllText(original.Paths.DeviceAnchorFile);

        using var stolen = original.CloneInto(LicensingHarness.MachineB);
        stolen.Resolver(keys, LicensingHarness.MachineBReader()).Resolve();

        Assert.Equal(foreignAnchor, File.ReadAllText(stolen.Paths.DeviceAnchorFile));
    }

    /// <summary>After a manual unbind the machine is usable again and reclaims its own anchor.</summary>
    [Fact]
    public void ClearingTheCopiedLicenseLetsTheMachineReclaimItsOwnAnchor()
    {
        using var keys = new TestSigningKey();
        using var original = new TempLicenseConfig();
        var ownerId = original.Resolver(keys, LicensingHarness.MachineAReader()).Resolve().InstallId;
        new LicenseStore(original.Root).Save(keys.Issue(ownerId!));

        using var stolen = original.CloneInto(LicensingHarness.MachineB);
        var reader = LicensingHarness.MachineBReader();
        var resolver = stolen.Resolver(keys, reader);
        Assert.Equal(LicenseActivation.Rejected, resolver.Resolve().Activation);

        // The support-scripted unbind: drop the licence that was not bought here.
        new LicenseStore(stolen.Root).Clear();

        var after = resolver.Resolve();
        Assert.False(after.IsPro);
        Assert.Equal(LicenseActivation.None, after.Activation);
        Assert.Equal(AnchorDigest.Compute(LicensingHarness.AnchorB()), AnchorDigest.Compute(reader.Read()));
    }

    // ---- invariant 1: the blob opening is proof enough ----------------------------

    [Fact]
    public void AMatchingSubjectIsNotVetoedByTheHardwareAnchor()
    {
        using var keys = new TestSigningKey();
        using var config = new TempLicenseConfig();
        var resolver = config.Resolver(keys, LicensingHarness.MachineAReader());
        var installId = resolver.Resolve().InstallId;
        new LicenseStore(config.Root).Save(keys.Issue(installId!));
        Assert.True(resolver.Resolve().IsPro);

        // The machine reports a completely different machine now. DPAPI still opened,
        // which is itself a machine-binding proof, so the anchor is never read.
        var reader = LicensingHarness.MachineBReader();
        var resolution = config.Resolver(keys, reader).Resolve();

        Assert.True(resolution.IsPro);
        Assert.Equal(LicenseActivation.Matched, resolution.Activation);
        Assert.Equal(0, reader.ReadCount);
    }

    [Fact]
    public void AMotherboardSwapWithoutAReinstallKeepsPro()
    {
        using var keys = new TestSigningKey();
        using var config = new TempLicenseConfig();
        var resolver = config.Resolver(keys, LicensingHarness.MachineAReader());
        var installId = resolver.Resolve().InstallId;
        new LicenseStore(config.Root).Save(keys.Issue(installId!));

        // Same Windows install, new board: different firmware and different adapters,
        // but the DPAPI blob opens, so the customer keeps the product they paid for.
        var resolution = config.Resolver(keys, LicensingHarness.MachineBReader()).Resolve();

        Assert.True(resolution.IsPro);
        Assert.Equal(installId, resolution.InstallId);
    }

    // ---- invariant 3: no evidence is not guilt -------------------------------------

    [Fact]
    public void AnUnreadableMachineWithNoIdentityRecoversTheLicenceAndLogsTheDegradation()
    {
        using var keys = new TestSigningKey();
        using var config = new TempLicenseConfig();
        var installId = config.Resolver(keys, LicensingHarness.MachineAReader()).Resolve().InstallId;
        new LicenseStore(config.Root).Save(keys.Issue(installId!));
        File.Delete(config.Paths.InstallIdentityFile);

        var log = new List<string>();
        var resolution = config.Resolver(keys, FakeMachineFactsReader.Unreadable(), log: log).Resolve();

        Assert.True(resolution.IsPro);
        Assert.Equal(LicenseActivation.Recovered, resolution.Activation);
        Assert.Equal(installId, resolution.InstallId);
        Assert.Equal(AnchorEvidence.NoEvidence, resolution.Evidence);
        Assert.True(resolution.Degraded);
        Assert.Contains(log, line => line.Contains("without hardware evidence", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingAnchorFileRecoversRatherThanRefusing()
    {
        using var keys = new TestSigningKey();
        using var config = new TempLicenseConfig();
        var installId = config.Resolver(keys, LicensingHarness.MachineAReader()).Resolve().InstallId;
        new LicenseStore(config.Root).Save(keys.Issue(installId!));
        File.Delete(config.Paths.InstallIdentityFile);
        File.Delete(config.Paths.DeviceAnchorFile);

        var resolution = config.Resolver(keys, LicensingHarness.MachineBReader()).Resolve();

        Assert.True(resolution.IsPro);
        Assert.Equal(AnchorEvidence.NoEvidence, resolution.Evidence);
        Assert.True(resolution.Degraded);
    }

    // ---- adoption ----------------------------------------------------------------

    [Fact]
    public void ADifferentIdentityOnMatchingHardwareIsAdoptedOver()
    {
        using var keys = new TestSigningKey();
        using var config = new TempLicenseConfig();
        var resolver = config.Resolver(keys, LicensingHarness.MachineAReader());
        var original = resolver.Resolve().InstallId;
        new LicenseStore(config.Root).Save(keys.Issue(original!));

        // The user profile was rebuilt, so a new id was minted; the hardware is the
        // same machine, so the licence subject wins and the local id is rewritten.
        var store = new InstallIdentityStore(config.Root, config.Protector);
        store.Save(new InstallIdentity(InstallIdentityStore.NewInstallId(), TestTime.Now));

        var resolution = resolver.Resolve();

        Assert.True(resolution.IsPro);
        Assert.Equal(LicenseActivation.Adopted, resolution.Activation);
        Assert.Equal(original, resolution.InstallId);
        Assert.Equal(original, store.Load()!.InstallId);
    }

    // ---- refusals that are not hardware --------------------------------------------

    [Fact]
    public void ATamperedLicenseIsNeverProAndIsNeverRepaired()
    {
        using var keys = new TestSigningKey();
        using var config = new TempLicenseConfig();
        var resolver = config.Resolver(keys, LicensingHarness.MachineAReader());
        var installId = resolver.Resolve().InstallId;
        var good = keys.Issue(installId!);
        new LicenseStore(config.Root).Save(good);
        Assert.True(resolver.Resolve().IsPro);

        // Someone edits plan in license.json. The local identity must survive the
        // refusal untouched, so a support call can restore the real file.
        new LicenseStore(config.Root).Save(good with { Plan = "enterprise" });

        var resolution = resolver.Resolve();

        Assert.False(resolution.IsPro);
        Assert.Equal(LicenseActivation.Unverifiable, resolution.Activation);
        Assert.Equal("signature mismatch", resolution.Detail);
        Assert.Equal(installId, new InstallIdentityStore(config.Root, config.Protector).Load()!.InstallId);
    }

    [Fact]
    public void AMalformedLicenseFileIsTreatedAsNoLicense()
    {
        using var keys = new TestSigningKey();
        using var config = new TempLicenseConfig();
        File.WriteAllText(config.Paths.LicenseFile, "{ this is not json");

        var resolution = config.Resolver(keys, LicensingHarness.MachineAReader()).Resolve();

        Assert.False(resolution.IsPro);
        Assert.NotNull(resolution.InstallId);
    }

    // ---- portable ------------------------------------------------------------------

    [Fact]
    public void ThePortableBuildNeverMintsAnIdentity()
    {
        using var keys = new TestSigningKey();
        using var config = new TempLicenseConfig();
        var reader = LicensingHarness.MachineAReader();

        var resolution = config.Resolver(keys, reader, LicenseRuntimeOptions.Portable).Resolve();

        Assert.False(resolution.IsPro);
        Assert.Equal(LicenseActivation.None, resolution.Activation);
        Assert.Equal("portable", resolution.Detail);
        Assert.Null(resolution.InstallId);
        Assert.False(File.Exists(config.Paths.InstallIdentityFile));
        Assert.False(File.Exists(config.Paths.DeviceAnchorFile));
        Assert.Equal(0, reader.ReadCount);
    }

    [Fact]
    public void ThePortableBuildIgnoresAValidLicenceOnTheStick()
    {
        using var keys = new TestSigningKey();
        using var config = new TempLicenseConfig();
        new LicenseStore(config.Root).Save(keys.Issue("someone-elses-install-id"));

        var resolution = config.Resolver(keys, LicensingHarness.MachineAReader(), LicenseRuntimeOptions.Portable).Resolve();

        // §10.4: portable is the free tier of the buyout product. A stick that carries
        // an identity from every machine it visits is a stick that eventually hands a
        // real licence to the wrong one.
        Assert.False(resolution.IsPro);
        Assert.Equal("portable", resolution.Detail);
        Assert.False(File.Exists(config.Paths.InstallIdentityFile));
    }

    [Fact]
    public void ThePortableBuildNeverOverwritesALicenceItFindsOnTheStick()
    {
        using var keys = new TestSigningKey();
        using var config = new TempLicenseConfig();
        var store = new LicenseStore(config.Root);
        store.Save(keys.Issue("someone-elses-install-id"));
        var before = File.ReadAllBytes(config.Paths.LicenseFile);

        config.Resolver(keys, LicensingHarness.MachineAReader(), LicenseRuntimeOptions.Portable).Resolve();

        Assert.Equal(before, File.ReadAllBytes(config.Paths.LicenseFile));
    }

    // ---- the install id never touches the disk in the clear -------------------------

    [Fact]
    public void TheInstallIdIsNeverWrittenToDiskInTheClear()
    {
        using var keys = new TestSigningKey();
        using var config = new TempLicenseConfig();
        var installId = config.Resolver(keys, LicensingHarness.MachineAReader()).Resolve().InstallId;

        var bytes = File.ReadAllBytes(config.Paths.InstallIdentityFile);
        var id = Encoding.UTF8.GetBytes(installId!);

        // §10.5. If the id were readable here it could be copied next to license.json
        // and both checks would pass at once, leaving the signature layer decorative.
        Assert.False(bytes.AsSpan().IndexOf(id) >= 0, "install-identity.dat contains the install id in the clear");
        Assert.DoesNotContain(
            installId!,
            Encoding.UTF8.GetString(bytes),
            StringComparison.Ordinal);
        Assert.False(File.ReadAllText(config.Paths.DeviceAnchorFile).Contains(installId!, StringComparison.Ordinal));
    }

    [Fact]
    public void TheInstallIdIsTwentyTwoBase64UrlCharacters()
    {
        var id = InstallIdentityStore.NewInstallId();

        Assert.Equal(InstallIdentityStore.InstallIdByteLength, id.Length * 6 / 8);
        Assert.DoesNotContain('=', id);
        Assert.True(Base64Url.TryDecode(id, out var bytes, InstallIdentityStore.InstallIdByteLength));
        Assert.NotEqual(id, InstallIdentityStore.NewInstallId());
    }

    // ---- schema versioning ----------------------------------------------------------

    [Fact]
    public void AnAnchorFileFromAnUnknownSchemaVersionIsIgnoredRatherThanTrusted()
    {
        using var keys = new TestSigningKey();
        using var config = new TempLicenseConfig();
        var store = new InstallIdentityStore(config.Root, config.Protector);
        store.SaveAnchor(LicensingHarness.AnchorA());

        // A format this build cannot parse is exactly "no evidence". It must not be
        // read as agreement, and it must not be read as disagreement either.
        var written = File.ReadAllText(config.Paths.DeviceAnchorFile).Replace(
            "\"v\": 1", $"\"v\": {DeviceAnchorRecord.CurrentSchemaVersion + 1}", StringComparison.Ordinal);
        File.WriteAllText(config.Paths.DeviceAnchorFile, written);

        Assert.Null(store.LoadAnchor());
    }

    [Fact]
    public void AMalformedAnchorFileIsIgnoredRatherThanTrusted()
    {
        using var config = new TempLicenseConfig();
        var store = new InstallIdentityStore(config.Root, config.Protector);
        File.WriteAllText(config.Paths.DeviceAnchorFile, "not json at all");

        Assert.Null(store.LoadAnchor());
    }

    [Fact]
    public void AnAnchorRecordRoundTripsThroughTheStore()
    {
        using var config = new TempLicenseConfig();
        var store = new InstallIdentityStore(config.Root, config.Protector);
        var anchor = LicensingHarness.AnchorA();

        store.SaveAnchor(anchor);

        Assert.Equal(anchor.Canonical, store.LoadAnchor()!.Canonical);
    }
}
