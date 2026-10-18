package license

import (
	"crypto/ecdsa"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// This file exists so shared/License/license-test-vectors.json is consumed by
// the repository that generated it, rather than only by the Windows client that
// reads it. A vector file nobody on the server side ever opens drifts silently,
// and the moment anyone notices is the moment every licence in the field has
// already stopped verifying.
//
// What is pinned here is the *canonical bytes*, not the signatures. ECDSA draws
// a random k per signature, so regenerating the file rewrites every `sig` and
// leaves canonicalText untouched — that asymmetry is why the text and its
// digest are the assertion targets and the signature is not.
//
// Path note: the test binary runs with its working directory set to the
// package directory, so it is three levels up from here (internal/license ->
// internal -> server -> repo root), not two. `server/shared` does not exist.
const vectorsPath = "../../../shared/License/license-test-vectors.json"

// vectorFile mirrors the on-disk schema. Only the fields this test asserts on
// are decoded; `why`, `mutate`, `note` and `howToUse` are prose for the C#
// reader and are deliberately not part of any contract.
type vectorFile struct {
	Spec      string `json:"spec"`
	Constants struct {
		DomainSeparator    string   `json:"domainSeparator"`
		Absent             string   `json:"absent"`
		CanonicalLineCount int      `json:"canonicalLineCount"`
		SignatureLength    int      `json:"signatureLength"`
		LineOrder          []string `json:"lineOrder"`
	} `json:"constants"`
	TestKey struct {
		PrivateKeyPem       string `json:"privateKeyPem"`
		PublicKeySpkiDerB64 string `json:"publicKeySpkiDerBase64"`
	} `json:"testKey"`
	Vectors []struct {
		Name            string          `json:"name"`
		Claims          Envelope        `json:"claims"`
		CanonicalText   string          `json:"canonicalText"`
		CanonicalHex    string          `json:"canonicalBytesHex"`
		CanonicalSHA256 string          `json:"canonicalSha256Hex"`
		Envelope        json.RawMessage `json:"envelopeJSON"`
		VerifySubject   string          `json:"verifySubject"`
		VerifyNowUnix   int64           `json:"verifyNowUnix"`
		VerifySkewSecs  int             `json:"verifySkewSeconds"`
		Expect          struct {
			Verify string `json:"verify"`
			Reason string `json:"reason"`
		} `json:"expect"`
	} `json:"vectors"`
}

func loadVectorFile(t *testing.T) *vectorFile {
	t.Helper()
	raw, err := os.ReadFile(filepath.Clean(vectorsPath))
	if err != nil {
		t.Fatalf("read shared vectors: %v", err)
	}
	var f vectorFile
	if err := json.Unmarshal(raw, &f); err != nil {
		t.Fatalf("decode shared vectors: %v", err)
	}
	if len(f.Vectors) == 0 {
		t.Fatalf("%s carries no vectors; a truncated regeneration looks exactly like a passing test if this is skipped", vectorsPath)
	}
	return &f
}

// vectorPub returns the public half of the published test key, loaded through
// the production loader so this test cannot pass against a key the server
// itself would refuse.
func vectorPub(t *testing.T, f *vectorFile) *ecdsa.PublicKey {
	t.Helper()
	path := filepath.Join(t.TempDir(), "vectors-test-key.pem")
	if err := os.WriteFile(path, []byte(f.TestKey.PrivateKeyPem), 0o600); err != nil {
		t.Fatalf("write test key: %v", err)
	}
	key, err := LoadPrivateKey(path)
	if err != nil {
		t.Fatalf("load vector test key: %v", err)
	}
	return &key.PublicKey
}

func vectorByName(t *testing.T, f *vectorFile, name string) string {
	t.Helper()
	for _, v := range f.Vectors {
		if v.Name == name {
			return v.CanonicalText
		}
	}
	t.Fatalf("vector %q is gone; a case cannot be quietly deleted to make the suite pass", name)
	return ""
}

// TestVectorCanonicalBytesMatch pins the exact byte sequence every other
// failure is downstream of. A client that rebuilds canonical form and diffs it
// against canonicalText before it touches the signature gets a one-line
// diagnosis; a client that goes straight to ECDSA gets a boolean and no idea.
func TestVectorCanonicalBytesMatch(t *testing.T) {
	f := loadVectorFile(t)
	for _, v := range f.Vectors {
		t.Run(v.Name, func(t *testing.T) {
			if got := Canonical(v.Claims); got != v.CanonicalText {
				t.Errorf("canonical text drifted from the shared vector\n got: %q\nwant: %q", got, v.CanonicalText)
			}
			if got := hex.EncodeToString(CanonicalBytes(v.Claims)); got != v.CanonicalHex {
				t.Errorf("canonical hex drifted\n got: %s\nwant: %s", got, v.CanonicalHex)
			}
			digest := sha256.Sum256(CanonicalBytes(v.Claims))
			if got := hex.EncodeToString(digest[:]); got != v.CanonicalSHA256 {
				t.Errorf("canonical digest drifted\n got: %s\nwant: %s", got, v.CanonicalSHA256)
			}
			// The sentinel for an absent field is what keeps this at 8. A case
			// that drops a line is byte-identical to nothing, so assert the
			// shape too — CanonicalLineCount is otherwise an unused constant
			// here and would drift unnoticed.
			if n := strings.Count(v.CanonicalText, "\n") + 1; n != CanonicalLineCount {
				t.Errorf("canonical text has %d lines, want %d", n, CanonicalLineCount)
			}
		})
	}
}

// TestVectorVerifyOutcome runs each case through Verify exactly as a client
// would, using the inputs the file records with it — "expired" means nothing
// without the clock it was checked against.
func TestVectorVerifyOutcome(t *testing.T) {
	f := loadVectorFile(t)
	pub := vectorPub(t, f)

	for _, v := range f.Vectors {
		t.Run(v.Name, func(t *testing.T) {
			var env Envelope
			if err := json.Unmarshal(v.Envelope, &env); err != nil {
				t.Fatalf("decode envelopeJSON: %v", err)
			}

			now := time.Unix(v.VerifyNowUnix, 0).UTC()
			err := Verify(env, pub, v.VerifySubject, now, time.Duration(v.VerifySkewSecs)*time.Second)

			switch v.Expect.Verify {
			case "ok":
				if err != nil {
					t.Fatalf("want ok, got %v", err)
				}
			case "fail":
				if err == nil {
					t.Fatal("want fail, got ok")
				}
				if v.Expect.Reason == "" {
					t.Fatal("a fail vector with no reason tells a support conversation nothing")
				}
				// The reason strings are the payload for anyone reading a bug
				// report from a paying customer, so compare verbatim rather
				// than merely asserting non-empty.
				if err.Error() != v.Expect.Reason {
					t.Errorf("reason mismatch\n got: %q\nwant: %q", err.Error(), v.Expect.Reason)
				}
			default:
				t.Fatalf("unknown expect.verify %q; the schema grew and this test did not", v.Expect.Verify)
			}
		})
	}
}

// TestVectorEntitlementInvariants states in Go the two properties the file only
// asserts in prose. Both are the reason the entitlement list is normalised
// before it is signed at all.
func TestVectorEntitlementInvariants(t *testing.T) {
	f := loadVectorFile(t)

	// Empty and "present but blank" are the same licence. If they were not,
	// the two texts would differ and one of the two cases would be describing
	// a licence the server cannot actually issue.
	empty := vectorByName(t, f, "no-entitlement")
	blank := vectorByName(t, f, "entitlement-blank-strings-only")
	if empty != blank {
		t.Errorf("no-entitlement and entitlement-blank-strings-only must canonicalise identically\n%s\n%s", empty, blank)
	}

	// Input order, padding, case and duplicates must not survive into the
	// signed bytes. Two servers that build the same list differently would
	// otherwise produce different signatures for the same purchase. The
	// canonical texts are byte-identical, entitlement line included — that is
	// stronger than "everything but the ent line matches", and it is the
	// stronger statement that is true.
	normalised := vectorByName(t, f, "entitlement-normalisation")
	reordered := vectorByName(t, f, "entitlement-order-does-not-matter")
	if normalised != reordered {
		t.Errorf("entitlement normalisation must not depend on input order\n%s\n%s", normalised, reordered)
	}
}

// TestVectorConstantsMatchImplementation keeps the documented constants and the
// compiled ones from drifting apart. The file is what the C# client reads, so a
// constant changed here without regenerating it leaves the client pinned to a
// format this build no longer produces — every licence invalid, every signature
// legal.
func TestVectorConstantsMatchImplementation(t *testing.T) {
	c := loadVectorFile(t).Constants

	if c.DomainSeparator != DomainSeparator {
		t.Errorf("domain separator: file has %q, code has %q", c.DomainSeparator, DomainSeparator)
	}
	if c.Absent != Absent {
		t.Errorf("absent sentinel: file has %q, code has %q", c.Absent, Absent)
	}
	if c.CanonicalLineCount != CanonicalLineCount {
		t.Errorf("canonical line count: file has %d, code has %d", c.CanonicalLineCount, CanonicalLineCount)
	}
	if c.SignatureLength != SignatureLength {
		t.Errorf("signature length: file has %d, code has %d", c.SignatureLength, SignatureLength)
	}
	if len(c.LineOrder) != CanonicalLineCount {
		t.Errorf("line order lists %d lines, want %d", len(c.LineOrder), CanonicalLineCount)
	}
}
