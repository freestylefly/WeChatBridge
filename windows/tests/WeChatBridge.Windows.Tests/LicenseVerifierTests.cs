using System.Security.Cryptography;
using System.Text;
using WeChatBridge.Windows.Core.Licensing;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// Offline signature verification, including the format-version and clock rules.
/// <para>
/// Every case here is "somebody changed a byte". The layer's job is to refuse all of
/// them without an exception escaping onto the startup path.
/// </para>
/// </summary>
public sealed class LicenseVerifierTests
{
    private static readonly TimeSpan Skew = LicenseRuntimeOptions.Installed.ClockSkewTolerance;

    private static LicenseEnvelope Buyout(TestSigningKey keys, string subject = "install-subject") =>
        keys.Issue(subject);

    [Fact]
    public void AFreshlyIssuedBuyoutLicenseVerifies()
    {
        using var keys = new TestSigningKey();

        var verification = keys.Verifier().Verify(Buyout(keys), TestTime.Now, Skew);

        Assert.True(verification.IsValid, verification.Failure);
        Assert.Null(verification.Failure);
        Assert.Equal("install-subject", verification.License!.Subject);
    }

    [Fact]
    public void ThereIsNoLicenseAtAll()
    {
        using var keys = new TestSigningKey();

        var verification = keys.Verifier().Verify(null, TestTime.Now, Skew);

        Assert.False(verification.IsValid);
        Assert.Equal("no license", verification.Failure);
    }

    // ---- tampering ---------------------------------------------------------------

    [Fact]
    public void AFlippedBitInTheSignatureIsRejected()
    {
        using var keys = new TestSigningKey();
        var envelope = Buyout(keys);

        var signature = Base64Url.TryDecode(envelope.Signature, out var bytes, LicenseToken.SignatureLength)
            ? bytes
            : throw new InvalidOperationException("the harness produced a signature of the wrong length");
        bytes[0] ^= 0x01;

        var verification = keys.Verifier().Verify(envelope with { Signature = Base64Url.Encode(bytes) }, TestTime.Now, Skew);

        Assert.False(verification.IsValid);
        Assert.Equal("signature mismatch", verification.Failure);
    }

    [Fact]
    public void ALicenseSignedByAKeyTheClientDoesNotHoldIsRejected()
    {
        using var issuer = new TestSigningKey();
        using var other = new TestSigningKey();

        // Self-signed by an attacker with their own P-256 pair. Nothing in the envelope
        // distinguishes it, which is why the key has to be compiled in.
        var envelope = Buyout(issuer);

        var verification = other.Verifier().Verify(envelope, TestTime.Now, Skew);

        Assert.False(verification.IsValid);
        Assert.Equal("signature mismatch", verification.Failure);
    }

    [Theory]
    [InlineData("a different customer")]
    [InlineData("plan changed to a different product")]
    [InlineData("entitlement added")]
    [InlineData("entitlement removed")]
    [InlineData("issue time moved")]
    public void ChangingAnySignedFieldBreaksTheSignature(string scenario)
    {
        using var keys = new TestSigningKey();
        var envelope = Buyout(keys);

        LicenseEnvelope tampered = scenario switch
        {
            "a different customer" => envelope with { Subject = "someone-else" },
            "plan changed to a different product" => envelope with { Plan = "subscription" },
            "entitlement added" => envelope with { Entitlements = ["EXPORT", "SCENE", "CLOUD"] },
            "entitlement removed" => envelope with { Entitlements = ["EXPORT"] },
            _ => envelope with { IssuedAt = TestTime.IssuedAt.AddSeconds(1) },
        };

        var verification = keys.Verifier().Verify(tampered, TestTime.Now, Skew);

        Assert.False(verification.IsValid);
        Assert.Equal("signature mismatch", verification.Failure);
    }

    [Fact]
    public void ReorderingTheEntitlementBitsIsNotTampering()
    {
        using var keys = new TestSigningKey();
        var envelope = Buyout(keys);

        // Set iteration order is not a contract; the canonical payload sorts.
        var verification = keys.Verifier().Verify(envelope with { Entitlements = ["SCENE", "EXPORT"] }, TestTime.Now, Skew);

        Assert.True(verification.IsValid, verification.Failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64url!!")]
    [InlineData("AAAA")]
    public void AMalformedSignatureIsRejectedWithoutThrowing(string signature)
    {
        using var keys = new TestSigningKey();

        var verification = keys.Verifier().Verify(Buyout(keys) with { Signature = signature }, TestTime.Now, Skew);

        Assert.False(verification.IsValid);
        Assert.Equal("malformed signature", verification.Failure);
    }

    [Fact]
    public void AnEmptySubjectIsRejected()
    {
        using var keys = new TestSigningKey();

        var verification = keys.Verifier().Verify(Buyout(keys, subject: "   "), TestTime.Now, Skew);

        Assert.False(verification.IsValid);
        Assert.Equal("empty subject", verification.Failure);
    }

    // ---- the DER trap from §10.14 ------------------------------------------------

    /// <summary>
    /// <c>openssl dgst -sign</c> emits a DER SEQUENCE. It is a mathematically valid
    /// ECDSA signature of the right key over the right bytes, and it is 70–72 bytes
    /// rather than 64. The client must refuse it — and refuse it as a malformed
    /// signature, because "the server is using the wrong encoding" is otherwise
    /// undiagnosable from the symptom, which is every customer failing at once.
    /// </summary>
    [Fact]
    public void ADerEncodedSignatureIsRejected()
    {
        using var keys = new TestSigningKey();
        var envelope = Buyout(keys);

        // The harness's own key, signing with the DER format the openssl pipeline emits.
        var der = Base64Url.Encode(keys.SignDataDer(LicenseCanonicalPayload.From(envelope), fixedField: false));

        var verification = keys.Verifier().Verify(envelope with { Signature = der }, TestTime.Now, Skew);

        Assert.False(verification.IsValid);
        Assert.Equal("malformed signature", verification.Failure);
        Assert.True(Base64Url.TryDecode(der, out var derBytes));
        Assert.True(derBytes.Length != LicenseToken.SignatureLength);
    }

    // ---- key selection -----------------------------------------------------------

    [Fact]
    public void AnUnknownFormatVersionIsRefusedRatherThanGuessedAt()
    {
        using var keys = new TestSigningKey();

        // Signed over v=2 and correctly signed — the only thing wrong is that this
        // build holds no v=2 key, and it must not fall back to the v=1 one.
        var envelope = keys.Issue("install-subject", schemaVersion: LicenseToken.CurrentSchemaVersion + 1);

        var verification = keys.Verifier().Verify(envelope, TestTime.Now, Skew);

        Assert.False(verification.IsValid);
        Assert.Equal($"unsupported format version {LicenseToken.CurrentSchemaVersion + 1}", verification.Failure);
    }

    [Fact]
    public void AnOlderLicenceKeepsVerifyingAfterARotation()
    {
        using var oldKey = new TestSigningKey();
        using var newKey = new TestSigningKey();

        // Both keys present at once, the shape §10.3 asks for. A rotation must not empty
        // the field: licences already in customers' hands stay valid.
        var rotated = new LicenseVerifier(
            LicensePublicKeys.ImportSubjectPublicKeyInfo,
            new Dictionary<int, string>
            {
                [LicenseToken.CurrentSchemaVersion - 1] = oldKey.PublicKeyBase64Url,
                [LicenseToken.CurrentSchemaVersion] = newKey.PublicKeyBase64Url,
            });

        var legacy = oldKey.Issue("install-subject", schemaVersion: LicenseToken.CurrentSchemaVersion - 1);
        var current = oldKey.Issue("install-subject");

        Assert.True(rotated.Verify(legacy, TestTime.Now, Skew).IsValid);
        // A v=1 licence signed under the old key is a forgery, not a straggler: the
        // rotation replaced the v=1 key, it did not add to it.
        Assert.False(rotated.Verify(current, TestTime.Now, Skew).IsValid);
        Assert.NotNull(newKey.PublicKeyBase64Url);
    }

    [Fact]
    public void TheCompiledInKeyIsUsableAndAnUnknownVersionIsNot()
    {
        // The compiled-in key used to be a placeholder that no import could
        // read, and this test asserted that — encoding "an unreleased build
        // must not be able to unlock itself". The key is real now, so that
        // failure mode is gone and the assertion had to change with it.
        //
        // What survives the change is the half that still matters: an unknown
        // format version has no key, so a licence claiming a version this
        // build does not know cannot verify — including one signed by the very
        // key that every other version trusts.
        Assert.NotNull(LicensePublicKeys.ImportSubjectPublicKeyInfo(LicensePublicKeys.PlaceholderKeyV1));
        Assert.Null(LicensePublicKeys.ForSchemaVersion(999));

        var verifier = new LicenseVerifier();
        using var keys = new TestSigningKey();

        // Signed under the current version, but claiming a version the
        // shipped keyring has no key for. The version is checked before the
        // key is looked up, so this is a rejection on its own terms rather
        // than a signature failure — and it is signed with a key that every
        // other version trusts, which is what makes it worth asserting.
        var forged = keys.Issue("install-subject", schemaVersion: LicenseToken.CurrentSchemaVersion + 1);
        var verification = verifier.Verify(forged, TestTime.Now, Skew);

        Assert.False(verification.IsValid);
        Assert.Equal(
            $"unsupported format version {LicenseToken.CurrentSchemaVersion + 1}",
            verification.Failure);
    }

    [Fact]
    public void ALicenceSignedByAnotherKeyDoesNotVerify()
    {
        // The other direction, and the one that matters for the compiled-in
        // key being real: it must verify the issuing pipeline's signatures and
        // nothing else. TestSigningKey is a throwaway pair the harness owns,
        // so a licence it signs has to fail against the shipped keyring —
        // otherwise a build would accept any key that happened to be linked in.
        var verifier = new LicenseVerifier();
        using var keys = new TestSigningKey();

        var verification = verifier.Verify(keys.Issue("install-subject"), TestTime.Now, Skew);

        Assert.False(verification.IsValid);
        Assert.Equal("signature mismatch", verification.Failure);
    }

    [Fact]
    public void AnUnimportableKeyIsARefusalRatherThanAnException()
    {
        var verifier = new LicenseVerifier(
            _ => throw new CryptographicException("bad key material"),
            new Dictionary<int, string> { [LicenseToken.CurrentSchemaVersion] = "irrelevant" });

        using var keys = new TestSigningKey();

        var verification = verifier.Verify(keys.Issue("install-subject"), TestTime.Now, Skew);

        Assert.False(verification.IsValid);
        Assert.Equal("no key for format version", verification.Failure);
    }

    // ---- the validity window -----------------------------------------------------

    [Fact]
    public void ABuyoutLicenseHasNoExpirySoTheClockCannotRevokeIt()
    {
        using var keys = new TestSigningKey();
        var envelope = Buyout(keys);

        Assert.Null(envelope.ExpiresAt);

        // Ten years on, or the clock rolled back a decade: §10.10 says a buyout is
        // never revoked locally.
        Assert.True(keys.Verifier().Verify(envelope, TestTime.Now.AddYears(10), Skew).IsValid);
        Assert.True(keys.Verifier().Verify(envelope, TestTime.Now.AddYears(-10), Skew).IsValid);
    }

    [Fact]
    public void AnExpiredLicenseIsRefused()
    {
        using var keys = new TestSigningKey();
        var envelope = keys.Issue("install-subject", expiresAt: TestTime.Now.AddDays(-1));

        var verification = keys.Verifier().Verify(envelope, TestTime.Now, Skew);

        Assert.False(verification.IsValid);
        Assert.Equal("expired", verification.Failure);
    }

    [Fact]
    public void ALicenseExpiresExactlyAtItsExpiryInstant()
    {
        using var keys = new TestSigningKey();
        var envelope = keys.Issue("install-subject", expiresAt: TestTime.Now);

        Assert.True(keys.Verifier().Verify(envelope, TestTime.Now, Skew).IsValid);
        Assert.False(keys.Verifier().Verify(envelope, TestTime.Now.AddSeconds(1), Skew).IsValid);
    }

    [Fact]
    public void AClockThatHasNotCaughtUpWithTheNotBeforeIsTolerated()
    {
        using var keys = new TestSigningKey();
        // The clock is a month behind; the tolerance is 30 days, so the difference is
        // right on the boundary.
        var envelope = keys.Issue("install-subject", notBefore: TestTime.Now.AddDays(-29));

        Assert.True(keys.Verifier().Verify(envelope, TestTime.Now, Skew).IsValid);
    }

    [Fact]
    public void ALicenseWhoseNotBeforeIsFarInTheFutureIsRefused()
    {
        using var keys = new TestSigningKey();
        var envelope = keys.Issue("install-subject", notBefore: TestTime.Now.AddDays(90));

        var verification = keys.Verifier().Verify(envelope, TestTime.Now, Skew);

        Assert.False(verification.IsValid);
        Assert.Equal("not yet valid", verification.Failure);
    }

    [Fact]
    public void ARolledBackClockDoesNotVoidAPurchasedLicense()
    {
        using var keys = new TestSigningKey();
        var envelope = Buyout(keys);

        // §10.13 accepts the residual risk of a rolled-back clock explicitly; what it
        // must not do is cost a customer their purchase. A buyout carries neither an
        // expiry nor a not-before, so no clock value can reach it.
        Assert.True(keys.Verifier().Verify(envelope, TestTime.Now.AddYears(-10), Skew).IsValid);
        Assert.True(keys.Verifier().Verify(envelope, TestTime.Now.AddYears(10), Skew).IsValid);
    }

    [Fact]
    public void TheVerifierTakesEveryInputAsAParameterAndTouchesNothingElse()
    {
        // No clock of its own, no files, no network: the same envelope verifies
        // identically no matter what the machine looks like.
        using var keys = new TestSigningKey();
        var envelope = Buyout(keys);
        var verifier = keys.Verifier();

        var first = verifier.Verify(envelope, TestTime.Now, Skew);
        var second = verifier.Verify(envelope, TestTime.Now, Skew);

        Assert.True(first.IsValid);
        Assert.Equal(first.Payload!.Bytes, second.Payload!.Bytes);
        Assert.Equal(8, first.Payload.Canonical.Split('\n').Length);
        Assert.Equal(LicenseCanonicalPayload.DomainSeparator, Encoding.UTF8.GetString(first.Payload.Bytes[..23]));
    }
}
