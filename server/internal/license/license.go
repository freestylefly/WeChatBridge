// Package license issues and verifies the signed licences the desktop client
// validates offline.
//
// # Why this package is written the way it is
//
// The signature is never taken over the JSON text. Field order, casing and
// whitespace all change when a serialiser changes, and every licence in the
// field would fail at once — with a signature that is perfectly valid, which
// is close to undiagnosable out there. Instead the payload is canonicalised:
// a fixed domain separator, a fixed line order, one field per line, a
// sentinel for every absent value so the line count never moves, and the
// entitlement bits sorted before they are joined.
//
// The Windows client canonicalises identically in
// windows/src/WeChatBridge.Windows.Core/Licensing/LicenseEnvelope.cs. The two
// implementations must produce byte-identical output or every customer loses
// access at the same moment, so TestSharedCanonicalVector pins a literal and
// its digest, and both test suites read the same constant.
//
// TestSharedCanonicalVector is the contract. If you change the format, the
// client changes with you in the same commit, and the version below goes up.
package license

import (
	"crypto/ecdsa"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"sort"
	"strconv"
	"strings"
	"time"
)

// Format constants. Changing any of these is a breaking change to every
// licence already issued, and requires a matching client change.
const (
	// CurrentVersion selects the verification key on the client side.
	CurrentVersion = 1

	// DomainSeparator is also a tripwire: a payload built by the wrong format
	// version produces a different first line and fails the signature.
	DomainSeparator = "wechatbridge.license.v1"

	// Absent is written for any unset field so the line count is constant.
	Absent = "-"

	// CanonicalLineCount includes the domain separator line. The client asserts
	// this too; a mismatch means one side changed the shape.
	CanonicalLineCount = 8

	// SignatureLength is two 32-byte integers, fixed field concatenation
	// (IeeeP1363). A DER signature — what `openssl dgst -sign` emits by default —
	// is not this length and must be rejected rather than accepted loosely.
	SignatureLength = 64

	// PlanBuyout is the perpetual product. A buyout licence has no expiry: that
	// absence is the definition of the product, not an oversight.
	PlanBuyout = "buyout"

	// PlanSubscription is the cloud product, which does expire and is renewed
	// online.
	PlanSubscription = "subscription"
)

// Entitlements recognised by the client. A licence carrying none of these
// unlocks nothing.
const (
	EntitlementPro     = "PRO"
	EntitlementPDF     = "PDF"
	EntitlementExport  = "EXPORT"
	EntitlementScene   = "SCENE"
	EntitlementHistory = "HISTORY"
)

// Envelope is the signed licence as it sits on disk and as it travels over the
// wire.
//
// The field names are pinned lowercase and short, not camelCase by convention:
// this is a wire contract with a client written in another language, and a
// naming-policy change on either side would silently break every licence
// already in the field. `sig` is deliberately outside the signed payload —
// it is the signature itself.
type Envelope struct {
	Version      int      `json:"v"`
	Subject      string   `json:"sub"`
	Plan         string   `json:"plan"`
	IssuedAt     int64    `json:"iat"`
	NotBefore    *int64   `json:"nbf"`
	ExpiresAt    *int64   `json:"exp"`
	Entitlements []string `json:"ent"`
	Signature    string   `json:"sig"`
}

// Claims is the decoded, signature-checked content. Nothing outside this
// package should read the Envelope fields directly after issuance.
type Claims struct {
	Version      int
	Subject      string
	Plan         string
	IssuedAt     time.Time
	NotBefore    *time.Time
	ExpiresAt    *time.Time
	Entitlements []string
}

// Canonical renders the exact bytes the signature covers: the domain
// separator, then one fixed field per line, no trailing newline.
//
// A trailing newline is the classic off-by-one here — it makes the two sides
// differ by exactly one byte and the signature fails with no other symptom.
func Canonical(e Envelope) string {
	lines := make([]string, 0, CanonicalLineCount)
	lines = append(lines, DomainSeparator)
	lines = append(lines, "v="+strconv.Itoa(e.Version))
	lines = append(lines, "sub="+e.Subject)
	lines = append(lines, "plan="+e.Plan)
	lines = append(lines, "iat="+strconv.FormatInt(e.IssuedAt, 10))
	lines = append(lines, "nbf="+optional(e.NotBefore))
	lines = append(lines, "exp="+optional(e.ExpiresAt))
	lines = append(lines, "ent="+normalizeEntitlements(e.Entitlements))
	return strings.Join(lines, "\n")
}

func optional(unix *int64) string {
	if unix == nil {
		return Absent
	}
	return strconv.FormatInt(*unix, 10)
}

// normalizeEntitlements sorts, upper-cases and de-duplicates the bits.
//
// The sort is what makes the format survive ordinary server code: any map
// iteration or `SELECT` without ORDER BY hands the list back in a different
// order each time, and two servers that build the same list differently would
// otherwise produce different signed bytes for the same licence.
func normalizeEntitlements(bits []string) string {
	seen := make(map[string]struct{}, len(bits))
	out := make([]string, 0, len(bits))
	for _, bit := range bits {
		trimmed := strings.ToUpper(strings.TrimSpace(bit))
		if trimmed == "" {
			continue
		}
		if _, dup := seen[trimmed]; dup {
			continue
		}
		seen[trimmed] = struct{}{}
		out = append(out, trimmed)
	}
	if len(out) == 0 {
		return Absent
	}
	sort.Strings(out) // byte order, matching the client's StringComparer.Ordinal
	return strings.Join(out, ",")
}

// CanonicalBytes is what Sign actually signs.
func CanonicalBytes(e Envelope) []byte {
	return []byte(Canonical(e))
}

// Signer holds the issuing private key. The desktop client embeds the matching
// public key as a compile-time constant, so this key must live somewhere the
// client cannot reach — the server's secret store, never this repository.
type Signer struct {
	key *ecdsa.PrivateKey
}

// NewSigner wraps a P-256 key. A key of any other curve is rejected here rather
// than producing a licence the client cannot verify.
func NewSigner(key *ecdsa.PrivateKey) (*Signer, error) {
	if key == nil {
		return nil, errors.New("license: signer key is nil")
	}
	if key.Curve.Params().Name != "P-256" {
		return nil, fmt.Errorf("license: want a P-256 key, got %s", key.Curve.Params().Name)
	}
	return &Signer{key: key}, nil
}

// PublicBase64 returns the SPKI DER, base64 — the exact string the client
// embeds in LicensePublicKeys. It is safe to log and to print.
func (s *Signer) PublicBase64() (string, error) {
	pub, ok := s.key.Public().(*ecdsa.PublicKey)
	if !ok {
		return "", errors.New("license: key does not carry an ECDSA public key")
	}
	der, err := marshalSPKI(pub)
	if err != nil {
		return "", err
	}
	return base64.StdEncoding.EncodeToString(der), nil
}

// Issue signs a licence. It does not validate the claims for you: the caller
// is the only party that knows whether a subject is entitled to a plan, and a
// signing function that second-guesses it would only be a place to get that
// wrong quietly.
func (s *Signer) Issue(e Envelope) (Envelope, error) {
	if e.Version == 0 {
		e.Version = CurrentVersion
	}
	if e.Version != CurrentVersion {
		return Envelope{}, fmt.Errorf("license: refusing to issue format v%d, this build knows v%d", e.Version, CurrentVersion)
	}
	if e.Entitlements == nil {
		e.Entitlements = []string{}
	}

	digest := sha256.Sum256(CanonicalBytes(e))
	sig, err := signRaw(s.key, digest[:])
	if err != nil {
		return Envelope{}, err
	}
	e.Signature = base64.StdEncoding.EncodeToString(sig)
	return e, nil
}

// JSON renders the licence the way the client parses it. Indented, and with
// nulls written rather than omitted: the client deserialises with
// DefaultIgnoreCondition.Never, so a missing `exp` and a null `exp` are the
// same to it, but a *missing key* is a different code path and one fewer
// reason to be surprised.
func (e Envelope) JSON() ([]byte, error) {
	return json.MarshalIndent(e, "", "  ")
}

// Parse reads a licence without checking the signature. Callers that care
// about authenticity must go through Verify; this exists for the issuer's own
// bookkeeping and for tests.
func Parse(raw []byte) (Envelope, error) {
	var e Envelope
	dec := json.NewDecoder(strings.NewReader(string(raw)))
	if err := dec.Decode(&e); err != nil {
		return Envelope{}, fmt.Errorf("license: decode: %w", err)
	}
	if e.Subject == "" {
		return Envelope{}, errors.New("license: subject is empty")
	}
	if e.Version == 0 {
		return Envelope{}, errors.New("license: version is missing")
	}
	return e, nil
}

// VerifyError distinguishes the failures a support conversation needs to
// separate. "Your licence is invalid" is the worst thing to tell a paying
// customer, and the reason is usually knowable.
type VerifyError struct {
	Reason string
	Err    error
}

func (v *VerifyError) Error() string {
	if v.Err != nil {
		return "license: " + v.Reason + ": " + v.Err.Error()
	}
	return "license: " + v.Reason
}

func (v *VerifyError) Unwrap() error { return v.Err }

func verifyFail(reason string, err error) error { return &VerifyError{Reason: reason, Err: err} }

// Verify checks a licence against a public key and the local identity.
//
// The clock window exists because a machine whose clock is off must not lose a
// perpetual licence — and because the alternative, asking a customer to fix
// their clock before we will honour a purchase, is not a conversation worth
// having. It also means the window is an attack surface; see the threat model
// in the design document rather than widening it casually.
func Verify(e Envelope, pub *ecdsa.PublicKey, localSubject string, now time.Time, skew time.Duration) error {
	if e.Version != CurrentVersion {
		return verifyFail(fmt.Sprintf("format v%d is not supported by this client", e.Version), nil)
	}
	if e.Subject != localSubject {
		return verifyFail("licence belongs to another machine", nil)
	}

	sig, err := base64.StdEncoding.DecodeString(e.Signature)
	if err != nil {
		return verifyFail("signature is not base64", err)
	}
	if len(sig) != SignatureLength {
		// A DER signature lands here. That is the `openssl dgst -sign` default
		// and the single most likely server-side mistake; say so rather than
		// reporting a bare mismatch.
		return verifyFail(fmt.Sprintf("signature is %d bytes, want %d (IeeeP1363; a DER signature is the usual cause)", len(sig), SignatureLength), nil)
	}

	digest := sha256.Sum256(CanonicalBytes(e))
	if !verifyRaw(pub, digest[:], sig) {
		return verifyFail("signature does not verify", nil)
	}

	if e.NotBefore != nil && now.Add(skew).Unix() < *e.NotBefore {
		return verifyFail("licence is not valid yet", nil)
	}
	if e.ExpiresAt != nil && now.Add(-skew).Unix() >= *e.ExpiresAt {
		return verifyFail("licence has expired", nil)
	}
	if normalizeEntitlements(e.Entitlements) == Absent {
		return verifyFail("licence carries no entitlement", nil)
	}
	return nil
}

// HasEntitlement reports whether a verified licence unlocks a capability.
func HasEntitlement(e Envelope, want string) bool {
	for _, bit := range e.Entitlements {
		if strings.EqualFold(strings.TrimSpace(bit), want) {
			return true
		}
	}
	return false
}

// IssueParams is the issuer's view of one licence. It exists so that callers
// cannot accidentally leave a field unset that the format requires.
type IssueParams struct {
	Subject      string
	Plan         string
	Entitlements []string
	Lifetime     time.Duration // zero means perpetual; a buy-out licence has none
	IssuedAt     time.Time
	ClockSkew    time.Duration // clock tolerance the client will apply
}

// Build assembles and signs. Lifetime zero yields a perpetual licence, which is
// the buy-out product's definition rather than a missing-field accident.
func (s *Signer) Build(p IssueParams) (Envelope, error) {
	now := p.IssuedAt
	if now.IsZero() {
		now = time.Now().UTC()
	}
	e := Envelope{
		Version:      CurrentVersion,
		Subject:      p.Subject,
		Plan:         p.Plan,
		IssuedAt:     now.Unix(),
		NotBefore:    ptr(now.Unix()),
		Entitlements: p.Entitlements,
	}
	if p.Lifetime > 0 {
		e.ExpiresAt = ptr(now.Add(p.Lifetime).Unix())
	}
	return s.Issue(e)
}

func ptr(v int64) *int64 { return &v }

// GenerateKey makes a fresh P-256 issuing key. Callers must load the real key
// from a secret store; this exists for local runs and for tests, and calling it
// in a production path means every restart invalidates every licence in the
// field.
func GenerateKey() (*ecdsa.PrivateKey, error) {
	return ecdsa.GenerateKey(ellipticP256(), rand.Reader)
}
