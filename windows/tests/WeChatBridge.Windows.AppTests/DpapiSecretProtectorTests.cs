using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using WeChatBridge.Windows.Core.Licensing;
using WeChatBridge.Windows.Services;

namespace WeChatBridge.Windows.AppTests;

/// <summary>
/// The licence layer's secret protection, against the real Windows DPAPI.
/// <para>
/// Core's suite runs the whole recovery state machine against a fake protector that
/// fails the way DPAPI fails, and that is the right place for it — it is fast, it can
/// pretend to be another machine, and it makes the state machine's cases cheap. But a
/// fake proves only that the machine handles the shape of the exception. These tests
/// are here for the two claims that cannot be checked any other way: that the real
/// DPAPI really does refuse a wrong-entropy blob, and that the two lines the whole
/// layer exists to defend hold when the bytes really were sealed by Windows rather
/// than by xUnit's stand-in.
/// </para>
/// <para>
/// Everything writes under <see cref="Path.GetTempPath()"/> and deletes itself. A real
/// DPAPI blob is a real sealed secret: dropping one into the developer's actual
/// <c>%LOCALAPPDATA%</c> would leave a working install identity behind for whatever
/// runs the app next.
/// </para>
/// </summary>
public sealed class DpapiSecretProtectorTests
{
    /// <summary>
    /// The entropy <see cref="InstallIdentityStore"/> passes. Copied here rather than
    /// reflected because it is deliberately not public: a caller that could read it
    /// would not need it, and DPAPI already requires the Windows account. If this
    /// literal is ever wrong, the round-trip tests still pass and the real store
    /// returns null for everything — which is why
    /// <see cref="TheRealInstallIdentityStoreRoundTripsThroughDpapi"/> exists.
    /// </summary>
    private static readonly byte[] InstallEntropy = Encoding.UTF8.GetBytes("wechatbridge.install-identity.v1");

    private static readonly byte[] UnrelatedEntropy = Encoding.UTF8.GetBytes("wechatbridge.something-else.v1");

    // ---- the protector ------------------------------------------------------------

    [Fact]
    public void RealDpapiRoundTripsTheBytes()
    {
        var protector = new DpapiSecretProtector();
        var plain = Bytes.Utf8("{\"installId\":\"abc\",\"firstSeenAt\":\"2026-03-01T00:00:00+00:00\"}");

        var blob = protector.Protect(plain, InstallEntropy);

        Assert.NotEqual(plain, blob);
        Assert.Equal(plain, protector.Unprotect(blob, InstallEntropy));
    }

    [Fact]
    public void TheCiphertextIsNotThePlaintextInAnyReading()
    {
        var protector = new DpapiSecretProtector();
        var plain = Bytes.Utf8("install-id-A");

        var blob = protector.Protect(plain, InstallEntropy);

        // A reader who opens install-identity.dat as text must see nothing useful.
        Assert.DoesNotContain("install-id-A", Encoding.UTF8.GetString(blob), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("install", Encoding.UTF8.GetString(blob), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheWrongEntropyIsRefused()
    {
        var protector = new DpapiSecretProtector();
        var blob = protector.Protect(Bytes.Utf8("install-id-A"), InstallEntropy);

        Assert.Throws<CryptographicException>(() => protector.Unprotect(blob, UnrelatedEntropy));
    }

    [Fact]
    public void OmittingTheEntropyIsRefusedToo()
    {
        var protector = new DpapiSecretProtector();
        var blob = protector.Protect(Bytes.Utf8("install-id-A"), InstallEntropy);

        // The entropy is not the secret — DPAPI is — but it is what stops a blob sealed
        // for some unrelated purpose being opened here and parsed as an identity.
        Assert.Throws<CryptographicException>(() => protector.Unprotect(blob, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void AnEditedByteBreaksTheSeal()
    {
        var protector = new DpapiSecretProtector();
        var blob = protector.Protect(Bytes.Utf8("install-id-A"), InstallEntropy);
        blob[blob.Length / 2] ^= 0xFF;

        Assert.Throws<CryptographicException>(() => protector.Unprotect(blob, InstallEntropy));
    }

    [Fact]
    public void AGarbageFileIsRefusedRatherThanParsed()
    {
        var protector = new DpapiSecretProtector();

        Assert.Throws<CryptographicException>(() => protector.Unprotect(Bytes.Utf8("{\"installId\":\"forged\"}"), InstallEntropy));
    }

    /// <summary>
    /// What <see cref="DataProtectionScope.CurrentUser"/> does and does not buy,
    /// measured rather than assumed.
    /// <para>
    /// DPAPI records the scope inside the blob, and it is tempting to assume that
    /// therefore a <c>CurrentUser</c> blob refuses a <c>LocalMachine</c> read. It does
    /// not: on the machine that sealed it, under the same Windows account, both scopes
    /// decrypt. So the scope buys nothing against a curious process running as this same
    /// user — which was never the threat — and everything against a <em>different</em>
    /// account on the same machine, which is the one that matters and is the one this
    /// suite cannot run, because it has exactly one Windows account to run under.
    /// </para>
    /// </summary>
    [Fact]
    public void TheScopeIsRecordedInTheBlobButDoesNotStopThisSameUser()
    {
        var protector = new DpapiSecretProtector();
        var sealedForThisUser = protector.Protect(Bytes.Utf8("install-id-A"), InstallEntropy);
        var sealedForTheMachine = ProtectedData.Protect(
            Bytes.Utf8("install-id-A"), InstallEntropy, DataProtectionScope.LocalMachine);

        // Two different seals over the same plaintext: the scope really is part of the blob.
        Assert.NotEqual(sealedForThisUser, sealedForTheMachine);

        // And under this account, both open. Written down so the choice of scope rests
        // on the argument about other local accounts, which is the real one, and not on
        // a belief that CurrentUser defends against this process.
        Assert.Equal("install-id-A", Encoding.UTF8.GetString(
            ProtectedData.Unprotect(sealedForThisUser, InstallEntropy, DataProtectionScope.LocalMachine)));
        Assert.Equal("install-id-A", Encoding.UTF8.GetString(protector.Unprotect(sealedForThisUser, InstallEntropy)));
    }

    // ---- the store on top of it ----------------------------------------------------

    [Fact]
    public void TheRealInstallIdentityStoreRoundTripsThroughDpapi()
    {
        using var config = new TempConfigDirectory();
        var store = config.Identity(new DpapiSecretProtector());
        var identity = new InstallIdentity(InstallIdentityStore.NewInstallId(), DateTimeOffset.UtcNow);

        store.Save(identity);

        Assert.True(store.Exists);
        // The file on disk is sealed: the id is not readable as text.
        Assert.DoesNotContain(identity.InstallId, Encoding.UTF8.GetString(File.ReadAllBytes(store.FilePath)), StringComparison.Ordinal);
        var loaded = config.Identity(new DpapiSecretProtector()).Load();
        Assert.NotNull(loaded);
        Assert.Equal(identity.InstallId, loaded!.InstallId);
    }

    [Fact]
    public void ABlobSealedForSomethingElseReadsAsNoIdentityRatherThanAsAnError()
    {
        using var config = new TempConfigDirectory();
        // What a Config directory copied to another machine looks like from here: the
        // file is present, well-formed in size, and unopenable. The store has to answer
        // "no identity here" so the resolver can fall back to the anchor — not throw,
        // because a throw on the startup path takes the app down over a licence file.
        File.WriteAllBytes(
            Path.Combine(config.Root, InstallIdentityStore.FileName),
            new DpapiSecretProtector().Protect(Bytes.Utf8("{\"installId\":\"elsewhere\"}"), UnrelatedEntropy));

        Assert.Null(config.Identity(new DpapiSecretProtector()).Load());
    }

    // ---- 生死线 1: a reinstall must be invisible to a paying customer ----------------

    [Fact]
    public void AReinstallStillOpensTheLicenceThroughRealDpapi()
    {
        using var keys = new TestLicenseIssuer();
        using var config = new TempConfigDirectory();
        var machine = new FixedMachineFacts(FixedMachineFacts.MachineA());
        var resolver = new LicenseResolver(
            config.Store, config.Identity(new DpapiSecretProtector()), keys.Verifier(), machine);

        // First launch mints the identity a purchase will bind to.
        var fresh = resolver.Resolve();
        Assert.False(fresh.IsPro);
        var installId = fresh.InstallId;
        Assert.False(string.IsNullOrWhiteSpace(installId));

        config.Store.Save(keys.Issue(installId!));
        Assert.True(resolver.Resolve().IsPro);

        // Reinstall: DPAPI's blob goes with the profile, the licence and the anchor do not.
        File.Delete(config.Paths.InstallIdentityFile);
        Assert.Null(config.Identity(new DpapiSecretProtector()).Load());

        var recovered = resolver.Resolve();

        Assert.True(recovered.IsPro);
        Assert.Equal(LicenseActivation.Recovered, recovered.Activation);
        Assert.Equal(installId, recovered.InstallId);
        Assert.False(recovered.Degraded);
        Assert.Equal(AnchorEvidence.SameMachine, recovered.Evidence);
        // Recovered onto disk, sealed again by DPAPI for this machine.
        Assert.Equal(installId, config.Identity(new DpapiSecretProtector()).Load()!.InstallId);
    }

    [Fact]
    public void RecoveryRunsAgainWithoutChangingTheAnswer()
    {
        using var keys = new TestLicenseIssuer();
        using var config = new TempConfigDirectory();
        var resolver = new LicenseResolver(
            config.Store, config.Identity(new DpapiSecretProtector()), keys.Verifier(),
            new FixedMachineFacts(FixedMachineFacts.MachineA()));
        var installId = resolver.Resolve().InstallId!;
        config.Store.Save(keys.Issue(installId));
        File.Delete(config.Paths.InstallIdentityFile);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var resolution = resolver.Resolve();
            Assert.True(resolution.IsPro);
            Assert.Equal(installId, resolution.InstallId);
            Assert.Equal(attempt == 0 ? LicenseActivation.Recovered : LicenseActivation.Matched, resolution.Activation);
        }
    }

    // ---- 生死线 2: a copied Config directory must not buy a second activation -------

    [Fact]
    public void ACopiedConfigDirectoryCannotUnlockTheLicence()
    {
        using var keys = new TestLicenseIssuer();
        using var original = new TempConfigDirectory();
        var protector = new DpapiSecretProtector();
        var ownerMachine = new FixedMachineFacts(FixedMachineFacts.MachineA());
        var onTheOwner = new LicenseResolver(
            original.Store, original.Identity(protector), keys.Verifier(), ownerMachine);

        var installId = onTheOwner.Resolve().InstallId!;
        original.Store.Save(keys.Issue(installId));
        Assert.True(onTheOwner.Resolve().IsPro);

        // The whole directory moves, which is the attack: signature and binding would
        // both pass, because the licence is intact and the identity id is the same one.
        using var copy = original.Clone();
        Assert.Equal(installId, copy.Store.Load()!.Subject);

        // What the copy actually looks like on the machine it was carried to. The two
        // halves cannot both be faked on one test machine, so each is put in place
        // separately: the identity blob becomes one this account cannot open — exactly
        // what DPAPI does for a different Windows user or a different machine — and
        // the anchor is the foreign hardware recorded when the licence was accepted.
        File.WriteAllBytes(copy.Paths.InstallIdentityFile, protector.Protect(Bytes.Utf8("{\"installId\":\"elsewhere\"}"), UnrelatedEntropy));

        var foreignMachine = new FixedMachineFacts(FixedMachineFacts.MachineB());
        var onTheOtherMachine = new LicenseResolver(
            copy.Store, copy.Identity(protector), keys.Verifier(), foreignMachine);

        var refused = onTheOtherMachine.Resolve();

        Assert.False(refused.IsPro);
        Assert.Equal(LicenseActivation.Rejected, refused.Activation);
        Assert.Equal(AnchorEvidence.DifferentMachine, refused.Evidence);
        Assert.True(refused.NeedsManualUnbind);
    }

    [Fact]
    public void SupportClearingTheLicenceReclaimsTheDirectoryWithoutRewritingTheAnchor()
    {
        using var keys = new TestLicenseIssuer();
        using var original = new TempConfigDirectory();
        var protector = new DpapiSecretProtector();
        var onTheOwner = new LicenseResolver(
            original.Store, original.Identity(protector), keys.Verifier(), new FixedMachineFacts(FixedMachineFacts.MachineA()));
        original.Store.Save(keys.Issue(onTheOwner.Resolve().InstallId!));
        onTheOwner.Resolve();

        using var copy = original.Clone();
        File.WriteAllBytes(copy.Paths.InstallIdentityFile, protector.Protect(Bytes.Utf8("{\"installId\":\"elsewhere\"}"), UnrelatedEntropy));
        var onTheOtherMachine = new LicenseResolver(
            copy.Store, copy.Identity(protector), keys.Verifier(), new FixedMachineFacts(FixedMachineFacts.MachineB()));

        Assert.Equal(LicenseActivation.Rejected, onTheOtherMachine.Resolve().Activation);

        // The refusal has to end, or a support ticket resolves into a permanently
        // dead install. Clearing the licence is the whole remedy: the next resolve
        // reads no licence, adopts this machine's anchor, and the copy is now simply
        // an unlicensed install — which is what it is.
        copy.Store.Clear();
        var afterSupport = onTheOtherMachine.Resolve();

        Assert.False(afterSupport.IsPro);
        Assert.Equal(LicenseActivation.None, afterSupport.Activation);
        Assert.False(afterSupport.NeedsManualUnbind);
        Assert.Equal(FixedMachineFacts.MachineB().Canonical, copy.Identity(protector).LoadAnchor()!.Canonical);
    }

    // ---- the registry reader on real hardware --------------------------------------

    [Fact]
    public void TheRegistryAnswersTheAnchorsValuesAndSurvivesAKeyThatIsNotThere()
    {
        // The key RegistryMachineFacts reads. MachineGuid is deliberately not here —
        // it is the OS identity, and it is the first thing a reinstall destroys.
        using var bios = Registry.LocalMachine.OpenSubKey(RegistryMachineFacts.BiosKeyPath);

        Assert.NotNull(bios);
        foreach (var name in new[] { "SystemManufacturer", "SystemProductName", "BaseBoardProduct" })
            Assert.False(string.IsNullOrWhiteSpace(bios!.GetValue(name) as string), $"{name} came back empty");

        // MachineGuid is here instead, under the key Windows actually puts it in. Read
        // so the suite knows the registry is openable at all, and because a smoke test
        // that only ever reads a key that exists proves nothing about a missing one.
        using var cryptography = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        Assert.NotNull(cryptography);
        Assert.False(string.IsNullOrWhiteSpace(cryptography!.GetValue("MachineGuid") as string));

        // A missing key is null, not an exception: the anchor has to be readable on a
        // machine where the reader cannot see it, and NoEvidence is a legal answer.
        using var missing = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\WeChatBridgeNoSuchKey");
        Assert.Null(missing);
    }

    [Fact]
    public void TheProductionMachineFactsReaderAnswersAndIsStable()
    {
        var reader = new RegistryMachineFacts();

        var first = reader.Read();
        var second = reader.Read();

        Assert.NotNull(first);
        // Normalised, sorted and de-duplicated, so two enumerations of one machine
        // produce byte-identical anchors — that is what makes a stored anchor
        // comparable after a reinstall.
        Assert.Equal(first.Canonical, second.Canonical);
    }

    [Fact]
    public void TheProductionReaderAnswersRatherThanComplaining()
    {
        // The reader's contract is that it answers, not that it reports: a locked-down
        // registry view, a missing key, or an adapter set that cannot be enumerated all
        // come back as an anchor with whatever was readable. Nothing above it carries a
        // try/catch on the startup path, because there is nothing to catch.
        var anchor = new RegistryMachineFacts().Read();

        Assert.NotNull(anchor);
        Assert.NotNull(anchor.Macs);
        Assert.False(string.IsNullOrEmpty(anchor.Firmware));
    }
}
