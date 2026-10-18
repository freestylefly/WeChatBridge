package license

import (
	"bytes"
	"crypto/ecdsa"
	"crypto/sha256"
	"encoding/hex"
	"strings"
	"testing"
	"time"
)

// SharedCanonicalVector is the contract between this server and the Windows
// client. The identical constant lives in
// windows/tests/WeChatBridge.Windows.Tests/LicensingCanonicalPayloadTests.cs.
//
// One test suite reading a copy proves nothing, which is why the digest is
// also pinned: a server can check its canonicaliser against that single hex
// string without linking to the client, or to this package.
const SharedCanonicalVector = "wechatbridge.license.v1\n" +
	"v=1\n" +
	"sub=Zx8Kq3nT7pQ1mR4vW9yB0\n" +
	"plan=buyout\n" +
	"iat=1772323200\n" +
	"nbf=-\n" +
	"exp=-\n" +
	"ent=EXPORT,SCENE"

// SharedCanonicalVectorSha256 is SHA-256 of the vector's UTF-8 bytes, with no
// trailing newline. A trailing newline would make the two sides differ by
// exactly one byte, and the signature would fail with no other symptom.
const SharedCanonicalVectorSha256 = "0897d84321f6816e2600528641592e309729460e332c8922bbbc03dbdc920306"

func sharedVectorEnvelope() Envelope {
	return Envelope{
		Version:      1,
		Subject:      "Zx8Kq3nT7pQ1mR4vW9yB0",
		Plan:         PlanBuyout,
		IssuedAt:     1772323200,
		NotBefore:    nil,
		ExpiresAt:    nil,
		Entitlements: []string{"EXPORT", "SCENE"},
	}
}

// TestSharedCanonicalVector is the one test that must never be edited without
// the client changing in the same commit.
func TestSharedCanonicalVector(t *testing.T) {
	got := Canonical(sharedVectorEnvelope())
	if got != SharedCanonicalVector {
		t.Fatalf("canonical payload drifted from the shared vector.\n got: %q\nwant: %q", got, SharedCanonicalVector)
	}
}

func TestSharedCanonicalVectorDigest(t *testing.T) {
	digest := sha256.Sum256([]byte(SharedCanonicalVector))
	if hex.EncodeToString(digest[:]) != SharedCanonicalVectorSha256 {
		t.Fatalf("vector digest mismatch: got %s want %s", hex.EncodeToString(digest[:]), SharedCanonicalVectorSha256)
	}
}

// The client asserts 109 bytes and asserts the last one is not a newline.
// Both facts are load-bearing and both are cheap to keep true.
func TestCanonicalShapeMatchesTheClientAssertions(t *testing.T) {
	b := CanonicalBytes(sharedVectorEnvelope())
	if len(b) != 109 {
		t.Errorf("canonical payload is %d bytes, want 109 (the client's constant)", len(b))
	}
	if b[len(b)-1] == '\n' {
		t.Error("canonical payload ends in a newline; that is a one-byte difference the client will reject")
	}
	if got := strings.Count(SharedCanonicalVector, "\n") + 1; got != CanonicalLineCount {
		t.Errorf("canonical payload has %d lines, want %d", got, CanonicalLineCount)
	}
}

// Any set iteration order — a map, a SELECT without ORDER BY — must produce
// the same signed bytes.
func TestEntitlementOrderDoesNotChangeTheSignedBytes(t *testing.T) {
	base := sharedVectorEnvelope()

	for _, variant := range [][]string{
		{"SCENE", "EXPORT"},
		{"scene", "export"},
		{"  EXPORT  ", "SCENE"},
		{"EXPORT", "EXPORT", "SCENE"},
		{"scene", "Scene", "EXPORT", " scene "},
	} {
		shuffled := base
		shuffled.Entitlements = variant
		if got := Canonical(shuffled); got != SharedCanonicalVector {
			t.Errorf("entitlement order %v changed the payload:\n got: %q", variant, got)
		}
	}
}

func TestEmptyEntitlementsUseTheSentinel(t *testing.T) {
	for _, bits := range [][]string{nil, {}, {""}, {"  "}, {"", "   "}} {
		e := sharedVectorEnvelope()
		e.Entitlements = bits
		if !strings.HasSuffix(Canonical(e), "ent=-") {
			t.Errorf("entitlements %v should canonicalise to the sentinel", bits)
		}
	}
}

func TestSignatureRoundTrip(t *testing.T) {
	key, err := GenerateKey()
	if err != nil {
		t.Fatalf("generate key: %v", err)
	}
	signer, err := NewSigner(key)
	if err != nil {
		t.Fatalf("new signer: %v", err)
	}

	issued, err := signer.Issue(sharedVectorEnvelope())
	if err != nil {
		t.Fatalf("issue: %v", err)
	}

	sig, err := base64Decode(issued.Signature)
	if err != nil {
		t.Fatalf("signature is not base64: %v", err)
	}
	if len(sig) != SignatureLength {
		t.Fatalf("signature is %d bytes, want %d — this is the IeeeP1363/Go-DER trap", len(sig), SignatureLength)
	}

	if err := Verify(issued, &key.PublicKey, issued.Subject, time.Unix(issued.IssuedAt, 0), time.Hour); err != nil {
		t.Fatalf("a freshly issued licence failed to verify: %v", err)
	}
}

// Tampering any signed field must fail, and the failure must name the tampering
// rather than reporting a bare mismatch — "your licence is invalid" is the worst
// thing to tell a paying customer when the cause was a server bug.
func TestTamperingAnySignedFieldFails(t *testing.T) {
	signer, key := testSigner(t)

	for _, tc := range []struct {
		name    string
		mutate  func(*Envelope)
		wantSub string
	}{
		{"subject", func(e *Envelope) { e.Subject = "someone-elses-id" }, "another machine"},
		{"whitespace", func(e *Envelope) { e.Subject = " Zx8Kq3nT7pQ1mR4vW9yB0 " }, "another machine"},
		{"plan", func(e *Envelope) { e.Plan = PlanSubscription }, "signature does not verify"},
		{"issued at", func(e *Envelope) { e.IssuedAt += 1 }, "signature does not verify"},
		{"add entitlement", func(e *Envelope) { e.Entitlements = append(e.Entitlements, EntitlementPro) }, "signature does not verify"},
		{"remove entitlement", func(e *Envelope) { e.Entitlements = []string{"EXPORT"} }, "signature does not verify"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			// Sign the honest licence first, then tamper the signed artefact.
			// Tampering before signing and then verifying proves nothing — of
			// course that verifies.
			issued, err := signer.Issue(sharedVectorEnvelope())
			if err != nil {
				t.Fatalf("issue: %v", err)
			}
			// The local identity is whatever this machine says it is, and it does
			// not move when the licence's subject is edited. Reading it back out
			// of the tampered envelope would let the identity check pass and
			// report the wrong reason.
			localSubject := issued.Subject
			tc.mutate(&issued)

			err = Verify(issued, &key.PublicKey, localSubject, time.Unix(1772323200, 0), time.Hour)
			if err == nil {
				t.Fatal("a tampered licence verified")
			}
			if !strings.Contains(err.Error(), tc.wantSub) {
				t.Errorf("error should explain %q, got: %v", tc.wantSub, err)
			}
		})
	}
}

// Go's ecdsa.Sign emits ASN.1 DER by default; that is the single most likely
// server-side mistake and it must be named in the failure, not reported as a
// generic mismatch.
func TestDERSignatureIsRejectedAndNamed(t *testing.T) {
	signer, key := testSigner(t)
	issued, err := signer.Issue(sharedVectorEnvelope())
	if err != nil {
		t.Fatalf("issue: %v", err)
	}

	// A real DER signature over the same payload, as `openssl dgst -sign` emits.
	der, err := ecdsaSignDER(key, CanonicalBytes(sharedVectorEnvelope()))
	if err != nil {
		t.Fatalf("der sign: %v", err)
	}
	issued.Signature = base64Encode(der)

	err = Verify(issued, &key.PublicKey, issued.Subject, time.Unix(1772323200, 0), time.Hour)
	if err == nil {
		t.Fatal("a DER signature verified; the client would reject it")
	}
	if !strings.Contains(err.Error(), "DER") {
		t.Errorf("the failure should name DER as the usual cause, got: %v", err)
	}
}

func TestPerpetualLicenceNeverExpires(t *testing.T) {
	signer, key := testSigner(t)
	issued, err := signer.Build(IssueParams{
		Subject:      "local-subject",
		Plan:         PlanBuyout,
		Entitlements: []string{EntitlementPro},
		IssuedAt:     time.Unix(1772323200, 0),
	})
	if err != nil {
		t.Fatalf("build: %v", err)
	}
	if issued.ExpiresAt != nil {
		t.Fatal("a buy-out licence must carry no expiry; that absence is the product")
	}
	if err := Verify(issued, &key.PublicKey, "local-subject", time.Now().AddDate(30, 0, 0), time.Hour); err != nil {
		t.Fatalf("a perpetual licence expired in thirty years: %v", err)
	}
}

func TestClockSkewIsToleratedButNotIndefinitely(t *testing.T) {
	signer, key := testSigner(t)
	issued, err := signer.Build(IssueParams{
		Subject:      "local-subject",
		Plan:         PlanSubscription,
		Entitlements: []string{EntitlementPro},
		Lifetime:     time.Hour,
		IssuedAt:     time.Unix(1772323200, 0),
	})
	if err != nil {
		t.Fatalf("build: %v", err)
	}

	const skew = 48 * time.Hour
	if err := Verify(issued, &key.PublicKey, "local-subject", time.Unix(*issued.ExpiresAt, 0).Add(skew-time.Second), skew); err != nil {
		t.Errorf("a licence inside the skew window should still verify: %v", err)
	}
	if err := Verify(issued, &key.PublicKey, "local-subject", time.Unix(*issued.ExpiresAt, 0).Add(skew+time.Second), skew); err == nil {
		t.Error("a licence past the skew window should not verify")
	}
}

func TestLicenceForAnotherMachineIsRejectedByName(t *testing.T) {
	signer, key := testSigner(t)
	issued, err := signer.Issue(sharedVectorEnvelope())
	if err != nil {
		t.Fatalf("issue: %v", err)
	}
	err = Verify(issued, &key.PublicKey, "a-different-install", time.Unix(1772323200, 0), time.Hour)
	if err == nil || !strings.Contains(err.Error(), "another machine") {
		t.Fatalf("expected an 'another machine' failure, got: %v", err)
	}
}

func TestUnknownFormatVersionIsRejected(t *testing.T) {
	signer, key := testSigner(t)
	issued, err := signer.Issue(sharedVectorEnvelope())
	if err != nil {
		t.Fatalf("issue: %v", err)
	}
	issued.Version = CurrentVersion + 1
	if err := Verify(issued, &key.PublicKey, issued.Subject, time.Unix(1772323200, 0), time.Hour); err == nil {
		t.Fatal("a future format version verified against today's key table")
	}
}

func TestSignerRefusesANonP256Key(t *testing.T) {
	if _, err := NewSigner(nil); err == nil {
		t.Error("a nil key should be refused")
	}
}

// The public key string is what the client embeds. The client decodes it with
// Convert.FromBase64String, i.e. standard base64 — `+` and `/` are legitimate
// in that alphabet, so the assertion is that it decodes and is the right size
// for a P-256 SPKI, not that it happens to avoid two characters.
func TestPublicKeyIsSPKIForTheClientToEmbed(t *testing.T) {
	_, key := testSigner(t)
	signer, err := NewSigner(key)
	if err != nil {
		t.Fatalf("new signer: %v", err)
	}
	b64, err := signer.PublicBase64()
	if err != nil {
		t.Fatalf("public key: %v", err)
	}
	der, err := base64Decode(b64)
	if err != nil {
		t.Fatalf("the client would not be able to decode this: %v", err)
	}
	// A P-256 SPKI is 91 bytes; 124 base64 characters. Pin the shape so a change
	// of encoding — to raw coordinates, or to base64url — fails here rather than
	// at a customer's machine.
	if len(der) != 91 || len(b64) != 124 {
		t.Errorf("P-256 SPKI should be 91 DER bytes / 124 base64 chars, got %d / %d", len(der), len(b64))
	}
	if !strings.HasPrefix(b64, "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE") {
		t.Errorf("SPKI should start with the standard id-ecPublicKey-with-P-256 prefix, got %.40q", b64)
	}
}

func TestJSONRoundTripPreservesEverythingSigned(t *testing.T) {
	signer, _ := testSigner(t)
	issued, err := signer.Issue(sharedVectorEnvelope())
	if err != nil {
		t.Fatalf("issue: %v", err)
	}
	raw, err := issued.JSON()
	if err != nil {
		t.Fatalf("json: %v", err)
	}
	for _, field := range []string{`"v"`, `"sub"`, `"plan"`, `"iat"`, `"nbf"`, `"exp"`, `"ent"`, `"sig"`} {
		if !bytes.Contains(raw, []byte(field)) {
			t.Errorf("serialised licence is missing %s: %s", field, raw)
		}
	}
	back, err := Parse(raw)
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	if Canonical(back) != Canonical(issued) {
		t.Error("a licence changed its signed bytes through a JSON round trip")
	}
}

func testSigner(t *testing.T) (*Signer, *ecdsa.PrivateKey) {
	t.Helper()
	key, err := GenerateKey()
	if err != nil {
		t.Fatalf("generate key: %v", err)
	}
	signer, err := NewSigner(key)
	if err != nil {
		t.Fatalf("new signer: %v", err)
	}
	return signer, key
}
