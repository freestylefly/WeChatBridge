// Command license-vectors writes the licence canonical-format test vectors that
// the server and the desktop client both consume.
//
// The vectors exist because of the failure they prevent. If the two sides
// disagree about canonicalisation by one byte — a trailing newline, a field
// ordered differently, an entitlement list left in map order — every licence
// already in the field stops verifying while every signature remains perfectly
// legal. There is no symptom that points at the cause.
//
// So the vectors pin the *bytes*, not the behaviour: every case below carries
// the exact canonical text and its hex, so a client implementation can diff its
// own output against this file without a server, a network, or a Go toolchain.
// Regenerating them changes the signatures (ECDSA draws a random k) and leaves
// the canonical text alone, which is why the text is what gets asserted.
//
// This is a development tool. It signs with a key that is published in this
// file on purpose: a key nobody holds is not a test key.
package main

import (
	"crypto/ecdsa"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"encoding/pem"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"time"

	"github.com/freestylefly/WeChatBridge/server/internal/license"
)

// testPrivateKey is a throwaway P-256 key. It signs nothing but these vectors,
// and its public half is written into the output file. A test key whose
// private half is secret tests nothing.
const testPrivateKey = `-----BEGIN EC PRIVATE KEY-----
MHcCAQEEIHx0lA9dO/bEV2SN2E6oOgBuVcQfHW+WBmYKC7AS+7vYoAoGCCqGSM49
AwEHoUQDQgAEJQIGWuHgDEI7QLKq0TsTQGbPV+1ptD4+OPTyrygF5o0KX+16G4fY
NCyNQR7OZBR+WcL1sVeP+qK4DbEo1yLPdg==
-----END EC PRIVATE KEY-----
`

// Fixed clock values. Every timestamp in this file is absolute so the vectors
// do not rot: "expired" has to mean expired next year too.
const (
	refIssued = int64(1767225600) // 2026-01-01T00:00:00Z
	refNotBef = int64(1767225600)
	refExpire = int64(1798761600) // 2027-01-01T00:00:00Z
)

type claimSet struct {
	Name string           `json:"name"`
	Why  string           `json:"why"`
	Env  license.Envelope `json:"-"`

	Mutate  string `json:"mutate,omitempty"`
	Subject string `json:"verifySubject,omitempty"`
	Now     string `json:"verifyNow,omitempty"`
	Skew    int    `json:"verifySkewSeconds,omitempty"`
}

// file is the shape written to disk. The field names are the contract with the
// C# reader; they are deliberately verbose rather than terse.
type file struct {
	Spec        string   `json:"spec"`
	GeneratedBy string   `json:"generatedBy"`
	HowToUse    []string `json:"howToUse"`

	Algorithm struct {
		Curve           string `json:"curve"`
		Digest          string `json:"digest"`
		SignatureFormat string `json:"signatureFormat"`
		PublicKeyFormat string `json:"publicKeyFormat"`
		Warning         string `json:"warning"`
	} `json:"algorithm"`

	Constants struct {
		DomainSeparator    string   `json:"domainSeparator"`
		Absent             string   `json:"absent"`
		CanonicalLineCount int      `json:"canonicalLineCount"`
		SignatureLength    int      `json:"signatureLength"`
		LineOrder          []string `json:"lineOrder"`
	} `json:"constants"`

	TestKey struct {
		Note                string `json:"note"`
		PrivateKeyPem       string `json:"privateKeyPem"`
		PublicKeySpkiDerB64 string `json:"publicKeySpkiDerBase64"`
		PublicKeyPem        string `json:"publicKeyPem"`
	} `json:"testKey"`

	Vectors []vector `json:"vectors"`
}

type vector struct {
	Name            string           `json:"name"`
	Why             string           `json:"why"`
	Mutate          string           `json:"mutate,omitempty"`
	Claims          license.Envelope `json:"claims"`
	CanonicalText   string           `json:"canonicalText"`
	CanonicalHex    string           `json:"canonicalBytesHex"`
	CanonicalSHA256 string           `json:"canonicalSha256Hex"`
	Envelope        json.RawMessage  `json:"envelopeJSON"`
	// The inputs Verify was called with. A negative case is meaningless
	// without them: "expired" is only expired relative to a clock.
	VerifySubject  string      `json:"verifySubject"`
	VerifyNowUnix  int64       `json:"verifyNowUnix"`
	VerifySkewSecs int         `json:"verifySkewSeconds"`
	Expect         expectation `json:"expect"`
}

type expectation struct {
	Verify string `json:"verify"`
	Reason string `json:"reason"`
	// WhyThisCaseFails explains the case in prose, so a client that fails it
	// can tell "my canonical form is wrong" apart from "my verifier rejects
	// what it should accept".
	Note string `json:"note"`
}

func main() {
	out := flag.String("out", "", "path to write (default ../shared/License/license-test-vectors.json)")
	flag.Parse()

	key, err := parseKey(testPrivateKey)
	if err != nil {
		fatal("parse test key: %v", err)
	}
	signer, err := license.NewSigner(key)
	if err != nil {
		fatal("new signer: %v", err)
	}

	cases := buildCases()

	var f file
	f.Spec = "wechatbridge.license.canonical.v1"
	f.GeneratedBy = "server/cmd/license-vectors (go run ./cmd/license-vectors)"
	f.HowToUse = []string{
		"canonicalText is the exact byte sequence the signature covers. Diff your own canonical builder against it first; every other failure is downstream of it.",
		"canonicalBytesHex is the same bytes hex-encoded, for clients that would rather compare bytes than strings.",
		"canonicalSha256Hex is what gets fed to ECDSA. Sign or verify THAT, never the JSON text.",
		"envelopeJSON is the licence exactly as a verifier receives it, signature included.",
		"Every vector is consumed the same way: canonicalise `claims`, diff against `canonicalText`, verify `envelopeJSON`. No per-case special casing, including the tampered ones — `mutate` records how a case was built, it is not an instruction.",
		"Read canonicalText first. Every other disagreement is downstream of it.",
		"PublicKeySpkiDerBase64 under testKey is exactly the string the client embeds as its compile-time constant: base64 of a SubjectPublicKeyInfo DER, which is what .NET's ImportSubjectPublicKeyInfo expects.",
		"Each vector's expect.verify is what the reference implementation returns. 'fail' vectors carry the reason string verbatim.",
		"verifySubject / verifyNowUnix / verifySkewSeconds are the inputs Verify was called with. A negative case means nothing without them — 'expired' is only expired relative to a clock.",
		"Regenerating rewrites every signature (ECDSA picks a random k each time) and leaves canonicalText untouched. That asymmetry is deliberate: the canonical form is the contract, the signature is an instance of it.",
	}
	f.Algorithm.Curve = "P-256 (prime256v1 / secp256r1)"
	f.Algorithm.Digest = "SHA-256 over the canonical bytes"
	f.Algorithm.SignatureFormat = "IeeeP1363 fixed-field: r||s, 64 bytes, big-endian, then base64 std encoding"
	f.Algorithm.PublicKeyFormat = "SubjectPublicKeyInfo (SPKI) DER, then base64 std encoding"
	f.Algorithm.Warning = "`openssl dgst -sign` emits a DER signature, not IeeeP1363. Handing one to a fixed-field verifier throws a CryptographicException that points at nothing useful. See the der-signature vector."

	f.Constants.DomainSeparator = license.DomainSeparator
	f.Constants.Absent = license.Absent
	f.Constants.CanonicalLineCount = license.CanonicalLineCount
	f.Constants.SignatureLength = license.SignatureLength
	f.Constants.LineOrder = []string{"<domain separator>", "v", "sub", "plan", "iat", "nbf", "exp", "ent"}

	f.TestKey.Note = "TEST KEY ONLY. Its private half is published here deliberately so the vectors are reproducible by anyone. It signs nothing that matters."
	f.TestKey.PrivateKeyPem = testPrivateKey

	pubDER, err := x509.MarshalPKIXPublicKey(&key.PublicKey)
	if err != nil {
		fatal("marshal public key: %v", err)
	}
	f.TestKey.PublicKeySpkiDerB64 = base64.StdEncoding.EncodeToString(pubDER)
	pemBytes, err := marshalPublicPEM(pubDER)
	if err != nil {
		fatal("marshal public pem: %v", err)
	}
	f.TestKey.PublicKeyPem = string(pemBytes)

	for _, c := range cases {
		signed, err := signer.Issue(c.Env)
		if err != nil {
			fatal("issue %s: %v", c.Name, err)
		}
		// A mutated vector is signed first and broken afterwards; that is the
		// only way to produce a signature that is genuinely valid-looking and
		// genuinely wrong for the payload beside it.
		subject := c.Subject
		if subject == "" {
			subject = signed.Subject
		}
		probe := signed
		if c.Mutate != "" {
			probe = applyMutation(signed, c.Mutate)
		}

		// The file carries the envelope *as it will be presented to the
		// verifier* — post-mutation for the tampered cases, signature
		// untouched. A consumer therefore does exactly three things with every
		// vector and needs no per-case special casing: canonicalise `claims`,
		// compare against `canonicalText`, verify `envelopeJSON`. `mutate`
		// records how the case was built; it is documentation, not something a
		// reader has to execute.
		body, err := probe.JSON()
		if err != nil {
			fatal("render %s: %v", c.Name, err)
		}

		verifyErr := license.Verify(probe, &key.PublicKey, subject, c.now(), time.Duration(c.Skew)*time.Second)

		v := vector{
			Name:     c.Name,
			Why:      c.Why,
			Mutate:   c.Mutate,
			Claims:   probe,
			Envelope: body,
		}
		v.VerifySubject = subject
		v.VerifyNowUnix = c.now().Unix()
		v.VerifySkewSecs = c.Skew
		v.CanonicalText = license.Canonical(probe)
		v.CanonicalHex = hex.EncodeToString(license.CanonicalBytes(probe))
		digest := sha256.Sum256(license.CanonicalBytes(probe))
		v.CanonicalSHA256 = hex.EncodeToString(digest[:])

		if verifyErr == nil {
			v.Expect.Verify = "ok"
		} else {
			v.Expect.Verify = "fail"
			v.Expect.Reason = verifyErr.Error()
		}
		v.Expect.Note = c.expectNote

		f.Vectors = append(f.Vectors, v)
	}

	path := *out
	if path == "" {
		path = filepath.Join("..", "shared", "License", "license-test-vectors.json")
	}
	blob, err := json.MarshalIndent(f, "", "  ")
	if err != nil {
		fatal("marshal: %v", err)
	}
	blob = append(blob, '\n')
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		fatal("mkdir: %v", err)
	}
	if err := os.WriteFile(path, blob, 0o644); err != nil {
		fatal("write %s: %v", path, err)
	}
	fmt.Printf("wrote %s (%d vectors, %d bytes)\n", path, len(f.Vectors), len(blob))
}

type vectorCase struct {
	Name       string
	Why        string
	Env        license.Envelope
	Mutate     string
	Subject    string
	Now        time.Time
	Skew       int
	expectNote string
}

func (c vectorCase) now() time.Time {
	if c.Now.IsZero() {
		return time.Unix(refIssued, 0).UTC()
	}
	return c.Now
}

func iptr(v int64) *int64 { return &v }

// buildCases is the whole suite. The buy-out cases describe the product as it
// actually ships; the rest exist to pin a decision, not to reach a coverage
// number.
func buildCases() []vectorCase {
	return []vectorCase{
		{
			Name: "buyout-perpetual",
			Why:  "The product as it ships: a perpetual licence with no validity window and one entitlement. If this case disagrees, every paid user is broken.",
			Env: license.Envelope{
				Subject:      "kR7dQm2ZpX9vLbTn4HsYc1WjE6uF0aA3G",
				Plan:         "buyout",
				IssuedAt:     refIssued,
				Entitlements: []string{"pro"},
			},
			expectNote: "nbf and exp are both absent. Both canonical lines carry the '-' sentinel so the line count stays at 8.",
		},
		{
			Name: "entitlement-normalisation",
			Why:  "Server code hands back entitlement lists in whatever order a map or an unordered SELECT produced. Unsorted, lowercase, padded and duplicated input must canonicalise to the same bytes as the clean form.",
			Env: license.Envelope{
				Subject:      "kR7dQm2ZpX9vLbTn4HsYc1WjE6uF0aA3G",
				Plan:         "buyout",
				IssuedAt:     refIssued,
				Entitlements: []string{"  pro ", "pdf-deep-parse", "PRO", "pdf-deep-parse", "obsidian-sync"},
			},
			expectNote: "Trims, upper-cases, de-duplicates, then sorts by byte order. The input list order is irrelevant and must not survive into the signature.",
		},
		{
			Name: "entitlement-order-does-not-matter",
			Why:  "The same entitlement set in a different input order must produce byte-identical canonical text. Two servers that build the list differently would otherwise sign different bytes for the same licence.",
			Env: license.Envelope{
				Subject:      "kR7dQm2ZpX9vLbTn4HsYc1WjE6uF0aA3G",
				Plan:         "buyout",
				IssuedAt:     refIssued,
				Entitlements: []string{"pro", "obsidian-sync", "pdf-deep-parse"},
			},
			expectNote: "Compare canonicalText here against entitlement-normalisation: the two are byte-identical, entitlement line included. That vector's input is padded, mixed-case and duplicated, this one's is a different order — normalisation absorbs all of it, so none of it survives into the signed bytes. Identical text, not merely matching lines.",
		},
		{
			Name: "subscription-windowed",
			Why:  "The windowed shape. Both timestamps present, so both canonical lines carry real numbers and the '-' sentinel goes unused.",
			Env: license.Envelope{
				Subject:      "u9Kp4Zn8XvC2mLbTqR7hY0wS3dF6gH0a",
				Plan:         "subscription",
				IssuedAt:     refIssued,
				NotBefore:    iptr(refNotBef),
				ExpiresAt:    iptr(refExpire),
				Entitlements: []string{"pro", "cloud-parse"},
			},
			Skew:       300,
			expectNote: "Unix seconds, no milliseconds, no timezone suffix. Skew is 300s on both edges of the window.",
		},
		{
			Name: "no-entitlement",
			Why:  "A licence with no entitlement is rejected. The ent line still occupies its slot with '-', so the line count is unchanged.",
			Env: license.Envelope{
				Subject:      "kR7dQm2ZpX9vLbTn4HsYc1WjE6uF0aA3G",
				Plan:         "buyout",
				IssuedAt:     refIssued,
				Entitlements: []string{},
			},
			expectNote: "Signature is valid over this payload; the rejection is a policy check, not a crypto failure. A client that verifies the signature first and then checks entitlements will report this differently — that is fine.",
		},
		{
			Name: "entitlement-blank-strings-only",
			Why:  "Whitespace-only entitlement strings normalise to the same absent form as an empty list.",
			Env: license.Envelope{
				Subject:      "kR7dQm2ZpX9vLbTn4HsYc1WjE6uF0aA3G",
				Plan:         "buyout",
				IssuedAt:     refIssued,
				Entitlements: []string{"", "   ", "\t"},
			},
			expectNote: "canonicalText should be byte-identical to no-entitlement.",
		},
		{
			Name: "tampered-plan",
			Why:  "Someone edits the licence file and upgrades their own plan. The signature covers plan, so this must fail.",
			Env: license.Envelope{
				Subject:      "kR7dQm2ZpX9vLbTn4HsYc1WjE6uF0aA3G",
				Plan:         "buyout",
				IssuedAt:     refIssued,
				Entitlements: []string{"pro"},
			},
			Mutate:     `set plan="subscription"`,
			expectNote: "Mutate the envelope after signing. canonicalText is the mutated text, so a client that rebuilds canonical form from claims will see the same disagreement here.",
		},
		{
			Name: "tampered-entitlement-added",
			Why:  "The classic upgrade attack: keep a valid signature and append an entitlement to the list.",
			Env: license.Envelope{
				Subject:      "kR7dQm2ZpX9vLbTn4HsYc1WjE6uF0aA3G",
				Plan:         "buyout",
				IssuedAt:     refIssued,
				Entitlements: []string{"pro"},
			},
			Mutate:     `add entitlement="cloud-parse"`,
			expectNote: "The ent line is part of the signed payload. Adding to it after signing is exactly what the signature is there to catch.",
		},
		{
			Name: "tampered-subject",
			Why:  "The licence is lifted onto another machine. The subject no longer matches the local installId.",
			Env: license.Envelope{
				Subject:      "kR7dQm2ZpX9vLbTn4HsYc1WjE6uF0aA3G",
				Plan:         "buyout",
				IssuedAt:     refIssued,
				Entitlements: []string{"pro"},
			},
			Mutate:     `set sub="tR2pWq9LmX4kYbN7vHc0sJdE3uF8gH1a"`,
			expectNote: "Fails at the signature as well as at the binding check. The subject is signed, so this cannot be made to pass by editing alone.",
		},
		{
			Name: "der-signature",
			Why:  "The single most likely server-side mistake, pinned as a case: openssl emits a DER ECDSA signature and a fixed-field verifier needs 64 raw bytes. The failure looks like nothing at all unless you catch it here.",
			Env: license.Envelope{
				Subject:      "kR7dQm2ZpX9vLbTn4HsYc1WjE6uF0aA3G",
				Plan:         "buyout",
				IssuedAt:     refIssued,
				Entitlements: []string{"pro"},
			},
			Mutate:     `signature=der`,
			expectNote: "The reason string names the length and says a DER signature is the usual cause. If your client throws an opaque crypto exception instead, it has not reproduced this check.",
		},
		{
			Name: "signature-not-base64",
			Why:  "A signature that is not base64 at all — a hand-edited file, a truncated copy.",
			Env: license.Envelope{
				Subject:      "kR7dQm2ZpX9vLbTn4HsYc1WjE6uF0aA3G",
				Plan:         "buyout",
				IssuedAt:     refIssued,
				Entitlements: []string{"pro"},
			},
			Mutate:     `set sig="not valid base64!!"`,
			expectNote: "Decoding happens before length is checked, so this fails earlier than der-signature does.",
		},
		{
			Name: "expired",
			Why:  "A windowed licence checked after it lapsed.",
			Env: license.Envelope{
				Subject:      "u9Kp4Zn8XvC2mLbTqR7hY0wS3dF6gH0a",
				Plan:         "subscription",
				IssuedAt:     refIssued,
				NotBefore:    iptr(refNotBef),
				ExpiresAt:    iptr(refExpire),
				Entitlements: []string{"pro"},
			},
			Now:        time.Unix(refExpire+3600, 0).UTC(),
			Skew:       300,
			expectNote: "Expiry is inclusive at the boundary: now-skew >= exp rejects.",
		},
		{
			Name: "not-yet-valid",
			Why:  "A windowed licence checked before its not-before.",
			Env: license.Envelope{
				Subject:      "u9Kp4Zn8XvC2mLbTqR7hY0wS3dF6gH0a",
				Plan:         "subscription",
				IssuedAt:     refIssued,
				NotBefore:    iptr(refNotBef),
				ExpiresAt:    iptr(refExpire),
				Entitlements: []string{"pro"},
			},
			Now:        time.Unix(refIssued-3600, 0).UTC(),
			Skew:       300,
			expectNote: "Not-before is exclusive: now+skew < nbf rejects.",
		},
		{
			Name: "wrong-local-subject",
			Why:  "A structurally perfect licence presented to the wrong machine. Signature verifies; the binding check is what refuses it.",
			Env: license.Envelope{
				Subject:      "kR7dQm2ZpX9vLbTn4HsYc1WjE6uF0aA3G",
				Plan:         "buyout",
				IssuedAt:     refIssued,
				Entitlements: []string{"pro"},
			},
			Subject:    "someOtherMachineInstallId",
			expectNote: "No mutation: the payload and signature are intact. Only the local subject differs, which is what the second defence layer is for.",
		},
		{
			Name: "unsupported-version",
			Why:  "A future format version must be refused rather than parsed on a guess.",
			Env: license.Envelope{
				Subject:      "kR7dQm2ZpX9vLbTn4HsYc1WjE6uF0aA3G",
				Plan:         "buyout",
				IssuedAt:     refIssued,
				Entitlements: []string{"pro"},
			},
			Mutate:     `set v=2`,
			expectNote: "Signed at v1, then the version is bumped to v2. The issuer refuses to produce this at all — Issue rejects an unknown version — so the only way to build the case is to sign first and mutate after. That is also the shape of the attack it pins: a file edited on disk, not one the server produced.",
		},
	}
}

// applyMutation breaks an already-signed envelope. Only the mutations named in
// buildCases exist; an unknown name is a programming error, not a no-op.
func applyMutation(e license.Envelope, mutate string) license.Envelope {
	switch mutate {
	case `set plan="subscription"`:
		e.Plan = "subscription"
	case `add entitlement="cloud-parse"`:
		e.Entitlements = append(append([]string{}, e.Entitlements...), "cloud-parse")
	case `set sub="tR2pWq9LmX4kYbN7vHc0sJdE3uF8gH1a"`:
		e.Subject = "tR2pWq9LmX4kYbN7vHc0sJdE3uF8gH1a"
	case `set v=2`:
		e.Version = 2
	case `set sig="not valid base64!!"`:
		e.Signature = "not valid base64!!"
	case `signature=der`:
		e.Signature = derShaped(e.Signature)
	default:
		panic("unknown mutation: " + mutate)
	}
	return e
}

// derShaped re-wraps a fixed-field signature in a minimal DER SEQUENCE so the
// verifier sees a plausible but wrong length. It does not need to be a
// cryptographically valid DER blob — the point is the length check firing
// before any arithmetic happens.
func derShaped(sig string) string {
	raw, err := base64.StdEncoding.DecodeString(sig)
	if err != nil || len(raw) != license.SignatureLength {
		panic("derShaped: unexpected input")
	}
	body := append([]byte{0x02}, 0x21)
	body = append(body, raw[:32]...)
	body = append(body, 0x02, 0x21)
	body = append(body, raw[32:]...)
	der := append([]byte{0x30, byte(len(body))}, body...)
	return base64.StdEncoding.EncodeToString(der)
}

func parseKey(pemText string) (*ecdsa.PrivateKey, error) {
	block, _ := pem.Decode([]byte(pemText))
	if block == nil {
		return nil, fmt.Errorf("no PEM block")
	}
	if k, err := x509.ParseECPrivateKey(block.Bytes); err == nil {
		return k, nil
	}
	parsed, err := x509.ParsePKCS8PrivateKey(block.Bytes)
	if err != nil {
		return nil, err
	}
	k, ok := parsed.(*ecdsa.PrivateKey)
	if !ok {
		return nil, fmt.Errorf("not an ECDSA key")
	}
	return k, nil
}

func marshalPublicPEM(der []byte) ([]byte, error) {
	return pem.EncodeToMemory(&pem.Block{Type: "PUBLIC KEY", Bytes: der}), nil
}

func fatal(format string, args ...any) {
	fmt.Fprintf(os.Stderr, "license-vectors: "+format+"\n", args...)
	os.Exit(1)
}
