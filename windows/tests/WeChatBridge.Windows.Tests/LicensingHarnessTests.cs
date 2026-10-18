using System.Security.Cryptography;
using System.Text;
using WeChatBridge.Windows.Core.Licensing;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// Test doubles for the licence layer: a protector that fails the way DPAPI fails, a
/// machine-facts reader that reports whatever machine the test is pretending to be, a
/// throwaway signing key pair, and a throwaway config directory.
/// <para>
/// None of these touch DPAPI, the registry, the network, or the real
/// <c>%LOCALAPPDATA%</c>. Every store is handed an explicit directory.
/// </para>
/// </summary>
internal static class LicensingHarness
{
    /// <summary>Machine A in every test that needs two.</summary>
    public const string MachineA = "machine-A";

    /// <summary>Machine B: a different model and a disjoint adapter set.</summary>
    public const string MachineB = "machine-B";

    /// <summary>Reads like a real BIOS key on real hardware.</summary>
    public static readonly string[] FirmwareA = ["Dell Inc.", "XPS 15 9520", "0X2K3L"];

    /// <summary>A different model, and no adapter in common with <see cref="MachineA"/>.</summary>
    public static readonly string[] FirmwareB = ["Lenovo", "ThinkPad X1 Carbon Gen 11", "PF3XK9Q"];

    /// <summary>
    /// One locally administered address — the shape the §10.11 measurement found, a
    /// <c>5E:…</c> first octet next to a real <c>5C:…</c> wireless card.
    /// </summary>
    public static readonly string[] MacsA = ["5C-80-9F-11-22-33", "A4-B1-C2-D3-E4-F5", "02:00:00:00:00:01"];

    public static readonly string[] MacsB = ["00-15-5D-77-88-99", "F0-E1-D2-C3-B4-A5"];

    public static DeviceAnchor AnchorA() => DeviceAnchor.Create(FirmwareA, MacsA, "Microsoft Windows 11 Pro");

    public static DeviceAnchor AnchorB() => DeviceAnchor.Create(FirmwareB, MacsB, "Microsoft Windows 11 Home");

    public static FakeMachineFactsReader MachineAReader() => new(AnchorA());

    public static FakeMachineFactsReader MachineBReader() => new(AnchorB());
}

/// <summary>A protector whose blobs only open under the key that made them.</summary>
internal sealed class FakeSecretProtector : ISecretProtector
{
    private const byte Magic0 = 0xFB;
    private const byte Magic1 = 0x52;
    private const byte Magic2 = 0x50;
    private const byte Magic3 = 0x01;
    private const int TagLength = 16;

    private readonly byte[] _key;

    /// <param name="machineAndUser">
    /// Stands in for the machine key plus the user SID that DPAPI binds a blob to.
    /// Two different values are two different machines as far as this protector is
    /// concerned, which is the whole property the copy attack turns on.
    /// </param>
    public FakeSecretProtector(string machineAndUser) =>
        _key = SHA256.HashData(Encoding.UTF8.GetBytes("fake-dpapi:" + machineAndUser));

    public byte[] Protect(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> entropy)
    {
        var framed = new byte[4 + plain.Length + TagLength];
        framed[0] = Magic0;
        framed[1] = Magic1;
        framed[2] = Magic2;
        framed[3] = Magic3;
        plain.CopyTo(framed.AsSpan(4));
        Tag(framed.AsSpan(4, plain.Length), entropy).AsSpan(0, TagLength).CopyTo(framed.AsSpan(4 + plain.Length));
        return Transform(framed, entropy);
    }

    public byte[] Unprotect(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> entropy)
    {
        var framed = Transform(blob, entropy);
        if (framed.Length < 4 + TagLength
            || framed[0] != Magic0 || framed[1] != Magic1 || framed[2] != Magic2 || framed[3] != Magic3)
        {
            throw new CryptographicException("blob was not produced under this machine's key");
        }

        var body = framed.AsSpan(4, framed.Length - 4 - TagLength);
        var tag = framed.AsSpan(framed.Length - TagLength, TagLength);
        if (!CryptographicOperations.FixedTimeEquals(tag, Tag(body, entropy).AsSpan(0, TagLength)))
            throw new CryptographicException("blob integrity check failed");

        return body.ToArray();
    }

    private byte[] Tag(ReadOnlySpan<byte> body, ReadOnlySpan<byte> entropy)
    {
        var buffer = new byte[_key.Length + entropy.Length + body.Length];
        _key.CopyTo(buffer, 0);
        entropy.CopyTo(buffer.AsSpan(_key.Length));
        body.CopyTo(buffer.AsSpan(_key.Length + entropy.Length));
        return SHA256.HashData(buffer);
    }

    /// <summary>Counter-mode keystream. The cipher does not matter; only the key binding does.</summary>
    private byte[] Transform(ReadOnlySpan<byte> input, ReadOnlySpan<byte> entropy)
    {
        var output = new byte[input.Length];
        var block = new byte[_key.Length + entropy.Length + 8];
        _key.CopyTo(block, 0);
        entropy.CopyTo(block.AsSpan(_key.Length));

        for (var offset = 0; offset < input.Length; offset += 32)
        {
            BitConverter.TryWriteBytes(block.AsSpan(_key.Length + entropy.Length), (long)(offset / 32));
            var stream = SHA256.HashData(block);
            var take = Math.Min(32, input.Length - offset);
            for (var index = 0; index < take; index++)
                output[offset + index] = (byte)(input[offset + index] ^ stream[index]);
        }
        return output;
    }
}

/// <summary>A machine facts reader that reports a fixed machine, or refuses to.</summary>
internal sealed class FakeMachineFactsReader : IMachineFactsReader
{
    private readonly DeviceAnchor? _anchor;

    public FakeMachineFactsReader(DeviceAnchor? anchor) => _anchor = anchor;

    /// <summary>A reader that cannot answer at all — a locked-down registry view.</summary>
    public static FakeMachineFactsReader Unreadable() => new(null);

    public int ReadCount { get; private set; }

    public DeviceAnchor Read()
    {
        ReadCount++;
        return _anchor ?? DeviceAnchor.Create(null, null, null);
    }
}

/// <summary>
/// A signing key pair generated at run time.
/// <para>
/// The production key is a placeholder until the server has one, so any test that
/// leaned on <see cref="LicensePublicKeys"/> could only ever assert failure. The
/// private key is created in memory and never written to disk — the real one belongs
/// to the server's CI secret store and must never enter this repository or a
/// developer's machine.
/// </para>
/// </summary>
internal sealed class TestSigningKey : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string PublicKeyBase64Url => Base64Url.Encode(_key.ExportSubjectPublicKeyInfo());

    public IReadOnlyDictionary<int, string> KeysByVersion =>
        new Dictionary<int, string> { [LicenseToken.CurrentSchemaVersion] = PublicKeyBase64Url };

    public LicenseVerifier Verifier() =>
        new(LicensePublicKeys.ImportSubjectPublicKeyInfo, KeysByVersion);

    /// <summary>Signs exactly the bytes the client will reconstruct.</summary>
    public string Sign(LicenseCanonicalPayload payload) => Base64Url.Encode(SignDataDer(payload, fixedField: true));

    /// <summary>
    /// Raw signature over the canonical bytes. <paramref name="fixedField"/> selects
    /// the encoding: <c>true</c> is the 64-byte IeeeP1363 concatenation the client
    /// requires, <c>false</c> is the DER SEQUENCE that <c>openssl dgst -sign</c> emits.
    /// </summary>
    public byte[] SignDataDer(LicenseCanonicalPayload payload, bool fixedField) =>
        _key.SignData(
            payload.Bytes,
            HashAlgorithmName.SHA256,
            fixedField ? DSASignatureFormat.IeeeP1363FixedFieldConcatenation : DSASignatureFormat.Rfc3279DerSequence);

    public LicenseEnvelope Issue(
        string subject,
        string plan = "buyout",
        DateTimeOffset? issuedAt = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? expiresAt = null,
        IReadOnlyList<string>? entitlements = null,
        int? schemaVersion = null) =>
        IssueSigned(
            subject, plan, issuedAt, notBefore, expiresAt, entitlements, schemaVersion,
            payload => Sign(payload));

    /// <summary>As <see cref="Issue"/>, but signs with a caller-supplied signer — for the DER case.</summary>
    public LicenseEnvelope IssueSigned(
        string subject,
        string plan,
        DateTimeOffset? issuedAt,
        DateTimeOffset? notBefore,
        DateTimeOffset? expiresAt,
        IReadOnlyList<string>? entitlements,
        int? schemaVersion,
        Func<LicenseCanonicalPayload, string> signer)
    {
        var envelope = new LicenseEnvelope
        {
            SchemaVersion = schemaVersion ?? LicenseToken.CurrentSchemaVersion,
            Subject = subject,
            Plan = plan,
            IssuedAt = issuedAt ?? TestTime.IssuedAt,
            NotBefore = notBefore,
            ExpiresAt = expiresAt,
            Entitlements = entitlements ?? ["EXPORT", "SCENE"],
        };
        return envelope with { Signature = signer(LicenseCanonicalPayload.From(envelope)) };
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>The fixed instants the shared test vectors are built on.</summary>
internal static class TestTime
{
    /// <summary>2026-03-01T00:00:00Z — the issue time the canonical vector pins.</summary>
    public static readonly DateTimeOffset IssuedAt = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    public static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>A throwaway config directory, deleted on dispose.</summary>
internal sealed class TempLicenseConfig : IDisposable
{
    public TempLicenseConfig()
    {
        Root = Path.Combine(Path.GetTempPath(), "WeChatBridgeLicenseTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public LicensePaths Paths => new(Root);

    /// <summary>Copies every file this layer owns, the way a user moves a Config directory.</summary>
    public TempLicenseConfig CloneInto(string machineAndUser)
    {
        var clone = new TempLicenseConfig();
        foreach (var name in new[] { LicenseStore.FileName, InstallIdentityStore.FileName, InstallIdentityStore.AnchorFileName })
        {
            var source = Path.Combine(Root, name);
            if (File.Exists(source))
                File.Copy(source, Path.Combine(clone.Root, name), overwrite: true);
        }
        clone.Protector = new FakeSecretProtector(machineAndUser);
        return clone;
    }

    public FakeSecretProtector Protector { get; set; } = new(LicensingHarness.MachineA);

    /// <summary>The whole stack over this directory, wired to a specific machine.</summary>
    public LicenseResolver Resolver(
        TestSigningKey keys,
        IMachineFactsReader machineFacts,
        LicenseRuntimeOptions? options = null,
        Func<DateTimeOffset>? clock = null,
        List<string>? log = null) =>
        new(
            new LicenseStore(Root),
            new InstallIdentityStore(Root, Protector),
            keys.Verifier(),
            machineFacts,
            options ?? LicenseRuntimeOptions.Installed,
            clock ?? (() => TestTime.Now),
            log is null ? null : new Action<string>(log.Add));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
