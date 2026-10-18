using System.Security.Cryptography;

namespace WeChatBridge.Windows.Core.Licensing;

/// <summary>Outcome of one offline verification attempt.</summary>
/// <param name="IsValid">Whether every check passed.</param>
/// <param name="Failure">Machine-readable reason, for the log and the diagnostics screen.</param>
/// <param name="License">The envelope when it verified; <c>null</c> otherwise.</param>
/// <param name="Payload">The exact bytes that were signed, for reproducing a server-side mismatch.</param>
public sealed record LicenseVerification(
    bool IsValid,
    string? Failure,
    LicenseEnvelope? License,
    LicenseCanonicalPayload? Payload);

/// <summary>
/// Offline licence verification. No network, no files, no clock other than the one it
/// is handed — every input is a parameter so the interesting cases are testable.
/// </summary>
public sealed class LicenseVerifier
{
    private readonly Func<string, ECDsa?> _importPublicKey;
    private readonly IReadOnlyDictionary<int, string> _keysBySchemaVersion;

    /// <summary>Verifies against the keys compiled into <see cref="LicensePublicKeys"/>.</summary>
    public LicenseVerifier()
        : this(LicensePublicKeys.ImportSubjectPublicKeyInfo, LicensePublicKeys.PublicKeys)
    {
    }

    /// <param name="importPublicKey">
    /// Turns a base64url SubjectPublicKeyInfo into a key. Injectable so the tests can
    /// sign with a key pair they generated in memory: the production key is a
    /// placeholder until the server has one, and a test that depended on it could only
    /// ever assert failure.
    /// </param>
    /// <param name="keysBySchemaVersion">
    /// Format version to public key. Several versions may be present at once so a key
    /// rotation does not invalidate licences already in the field.
    /// </param>
    public LicenseVerifier(
        Func<string, ECDsa?> importPublicKey,
        IReadOnlyDictionary<int, string> keysBySchemaVersion)
    {
        _importPublicKey = importPublicKey;
        _keysBySchemaVersion = keysBySchemaVersion;
    }

    /// <param name="now">The current instant. Passed in so clock behaviour is testable.</param>
    /// <param name="clockSkewTolerance">
    /// How far the clock may be wrong before it counts against the licence. A client
    /// whose clock is behind must not lose a purchase it already paid for; the residual
    /// risk of a rolled-back clock is real but weak, and closing it needs a trusted
    /// time source at a cost that does not pay for itself (§10.13).
    /// </param>
    public LicenseVerification Verify(LicenseEnvelope? envelope, DateTimeOffset now, TimeSpan clockSkewTolerance)
    {
        if (envelope is null)
            return Fail("no license", null);

        if (!_keysBySchemaVersion.TryGetValue(envelope.SchemaVersion, out var encodedKey))
            return Fail($"unsupported format version {envelope.SchemaVersion}", LicenseCanonicalPayload.From(envelope));

        var payload = LicenseCanonicalPayload.From(envelope);
        if (string.IsNullOrWhiteSpace(payload.Subject))
            return Fail("empty subject", payload);

        if (!Base64Url.TryDecode(envelope.Signature, out var signature, LicenseToken.SignatureLength))
            return Fail("malformed signature", payload);

        // A key that cannot be imported is a failed verification, not an exception on
        // the startup path. The shipped factory returns null rather than throwing, but
        // the seam is injectable and a throwing implementation must not take the app
        // down with it.
        ECDsa? key;
        try
        {
            key = _importPublicKey(encodedKey);
        }
        catch (CryptographicException)
        {
            return Fail("no key for format version", payload);
        }
        catch (ArgumentException)
        {
            return Fail("no key for format version", payload);
        }

        if (key is null)
            return Fail("no key for format version", payload);

        using (key)
        {
            bool signatureValid;
            try
            {
                signatureValid = key.VerifyData(
                    payload.Bytes,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            catch (CryptographicException)
            {
                // A signature the key cannot even be asked about is a failed verification,
                // not a crash on the startup path.
                return Fail("signature not checkable", payload);
            }

            return signatureValid
                ? CheckWindow(envelope, payload, now, clockSkewTolerance)
                : Fail("signature mismatch", payload);
        }
    }

    private static LicenseVerification CheckWindow(
        LicenseEnvelope envelope,
        LicenseCanonicalPayload payload,
        DateTimeOffset now,
        TimeSpan clockSkewTolerance)
    {
        if (envelope.ExpiresAt is { } expires && now > expires)
            return Fail("expired", payload);

        // A not-before the client has not reached is treated as a clock that has not
        // caught up, not as an invalid licence.
        if (envelope.NotBefore is { } notBefore && notBefore - now > clockSkewTolerance)
            return Fail("not yet valid", payload);

        return new LicenseVerification(true, null, envelope, payload);
    }

    private static LicenseVerification Fail(string failure, LicenseCanonicalPayload? payload) =>
        new(false, failure, null, payload);
}
