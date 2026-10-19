package token

import (
	"encoding/base64"
	"encoding/json"
	"errors"
	"strings"
	"testing"
	"time"
)

const testSecret = "0123456789abcdef0123456789abcdef0123456789abcdef"

func newTestIssuer(t *testing.T, now *time.Time) *Issuer {
	t.Helper()
	i, err := NewIssuer(testSecret, time.Hour, 30*24*time.Hour)
	if err != nil {
		t.Fatalf("NewIssuer: %v", err)
	}
	if now != nil {
		i.now = func() time.Time { return *now }
	}
	return i
}

func TestIssuerRefusesAWeakSecret(t *testing.T) {
	t.Parallel()

	// config.Load already enforces 32 bytes. Enforcing it again here means a
	// caller who wires this up by hand cannot get a token signed with
	// something guessable.
	if _, err := NewIssuer("short", time.Hour, 24*time.Hour); err == nil {
		t.Fatal("a secret under 32 bytes must be refused")
	}
}

func TestIssuerRefusesAnInvertedLifetime(t *testing.T) {
	t.Parallel()

	// A refresh token that expires before the access token it can replace
	// means a user is signed out while their access token is still valid —
	// the client would keep working until the moment it needed a refresh and
	// then discover it had none.
	if _, err := NewIssuer(testSecret, 24*time.Hour, time.Hour); err == nil {
		t.Fatal("a refresh lifetime shorter than the access lifetime must be refused")
	}
}

func TestAccessTokenRoundTrips(t *testing.T) {
	t.Parallel()

	i := newTestIssuer(t, nil)
	creds, err := i.Issue("acct-1", "")
	if err != nil {
		t.Fatalf("Issue: %v", err)
	}

	claims, err := i.Verify(creds.Access.Token)
	if err != nil {
		t.Fatalf("Verify: %v", err)
	}
	if claims.AccountID != "acct-1" {
		t.Errorf("subject = %q, want acct-1", claims.AccountID)
	}
	if claims.ChainID != creds.ChainID {
		t.Errorf("chain = %q, want %q", claims.ChainID, creds.ChainID)
	}
	if claims.SessionID != creds.SessionID {
		t.Errorf("session = %q, want %q", claims.SessionID, creds.SessionID)
	}
	if !claims.ExpiresAt.After(claims.IssuedAt) {
		t.Errorf("expiry %v is not after issue %v", claims.ExpiresAt, claims.IssuedAt)
	}
}

func TestAccessTokenHasThreeSegmentsAndPinsTheAlgorithm(t *testing.T) {
	t.Parallel()

	i := newTestIssuer(t, nil)
	creds, _ := i.Issue("acct-1", "")

	parts := strings.Split(creds.Access.Token, ".")
	if len(parts) != 3 {
		t.Fatalf("token has %d segments, want 3", len(parts))
	}
	hdrRaw, err := base64.RawURLEncoding.DecodeString(parts[0])
	if err != nil {
		t.Fatalf("header is not base64url: %v", err)
	}
	var hdr header
	if err := json.Unmarshal(hdrRaw, &hdr); err != nil {
		t.Fatalf("header is not JSON: %v", err)
	}
	if hdr.Alg != alg {
		t.Fatalf("alg = %q, want %q", hdr.Alg, alg)
	}
}

func TestAccessTokenExpires(t *testing.T) {
	t.Parallel()

	now := time.Date(2026, 10, 18, 12, 0, 0, 0, time.UTC)
	i := newTestIssuer(t, &now)
	creds, err := i.Issue("acct-1", "")
	if err != nil {
		t.Fatalf("Issue: %v", err)
	}

	now = now.Add(59 * time.Minute)
	if _, err := i.Verify(creds.Access.Token); err != nil {
		t.Fatalf("a token one minute short of expiry must verify: %v", err)
	}

	now = now.Add(2 * time.Minute)
	_, err = i.Verify(creds.Access.Token)
	if !errors.Is(err, ErrExpired) {
		t.Fatalf("error = %v, want ErrExpired", err)
	}
	// Expired is not the same as invalid, and the client responds to them
	// differently: one is worth a refresh, the other is not.
	if errors.Is(err, ErrInvalid) {
		t.Error("an expired token must not also report as invalid")
	}
}

func TestTamperedAccessTokenIsRejected(t *testing.T) {
	t.Parallel()

	i := newTestIssuer(t, nil)
	creds, _ := i.Issue("acct-1", "")

	parts := strings.Split(creds.Access.Token, ".")
	// Re-sign nothing; swap the payload for one claiming a different account.
	forged, err := base64.RawURLEncoding.DecodeString(parts[1])
	if err != nil {
		t.Fatalf("payload is not base64url: %v", err)
	}
	var p payload
	if err := json.Unmarshal(forged, &p); err != nil {
		t.Fatalf("payload is not JSON: %v", err)
	}
	p.Subject = "someone-else"
	edited, _ := json.Marshal(p)

	if _, err := i.Verify(parts[0] + "." + base64.RawURLEncoding.EncodeToString(edited) + "." + parts[2]); !errors.Is(err, ErrInvalid) {
		t.Fatalf("a re-pointed payload must not verify, got %v", err)
	}
}

// The classic confusion bug: a verifier that reads the algorithm from the
// token will accept an unsigned one that says {"alg":"none"}.
func TestAlgorithmNoneIsRejected(t *testing.T) {
	t.Parallel()

	i := newTestIssuer(t, nil)
	noneHdr := base64.RawURLEncoding.EncodeToString([]byte(`{"alg":"none","typ":"JWT"}`))
	body := base64.RawURLEncoding.EncodeToString([]byte(
		`{"sub":"admin","sid":"s","cid":"c","iat":1,"exp":9999999999,"typ":"access"}`))

	for _, forged := range []string{
		noneHdr + "." + body + ".",
		noneHdr + "." + body,
	} {
		if _, err := i.Verify(forged); err == nil {
			t.Fatalf("an unsigned token was accepted: %q", forged)
		}
	}
}

func TestATokenSignedWithAnotherSecretIsRejected(t *testing.T) {
	t.Parallel()

	other, err := NewIssuer("ffffffffffffffffffffffffffffffffffffffff", time.Hour, 24*time.Hour)
	if err != nil {
		t.Fatalf("NewIssuer: %v", err)
	}
	creds, _ := other.Issue("acct-1", "")

	mine := newTestIssuer(t, nil)
	if _, err := mine.Verify(creds.Access.Token); !errors.Is(err, ErrInvalid) {
		t.Fatalf("a token signed with someone else's key must not verify, got %v", err)
	}
}

func TestGarbageIsInvalidNotExpired(t *testing.T) {
	t.Parallel()

	i := newTestIssuer(t, nil)
	for _, raw := range []string{"", "not-a-token", "a.b", "a.b.c.d", "....", "a.b.c"} {
		if _, err := i.Verify(raw); !errors.Is(err, ErrInvalid) {
			t.Errorf("Verify(%q) = %v, want ErrInvalid", raw, err)
		}
	}
}

// The whole reason a refresh token is opaque: the store keeps a hash, and
// nothing anywhere can turn that hash back into the credential.
func TestRefreshTokenIsOpaqueAndOnlyItsHashIsKept(t *testing.T) {
	t.Parallel()

	i := newTestIssuer(t, nil)
	refresh, err := i.IssueRefresh()
	if err != nil {
		t.Fatalf("IssueRefresh: %v", err)
	}

	if !strings.HasPrefix(refresh.Token, refreshPrefix) {
		t.Errorf("refresh token %q has no version prefix", refresh.Token)
	}
	// Three dot-separated segments would mean somebody made it a JWT.
	if strings.Contains(refresh.Token, ".") {
		t.Fatalf("refresh token looks like a JWT: %q", refresh.Token)
	}
	if refresh.Hash == refresh.Token {
		t.Fatal("the stored value is the token itself")
	}
	if !strings.HasPrefix(refresh.Hash, HashRefresh(refresh.Token)) {
		t.Fatal("the stored value is not the hash of the token")
	}
	// And the hash does not contain the token, so a database dump does not
	// hand out sessions.
	if strings.Contains(refresh.Hash, refresh.Token) {
		t.Fatal("the hash embeds the plaintext token")
	}
	// A refresh token is also not accepted where an access token belongs.
	if _, err := i.Verify(refresh.Token); err == nil {
		t.Fatal("a refresh token verified as an access token")
	}
}

func TestRefreshTokensAreUnpredictable(t *testing.T) {
	t.Parallel()

	i := newTestIssuer(t, nil)
	seen := make(map[string]bool, 200)
	for n := 0; n < 200; n++ {
		refresh, err := i.IssueRefresh()
		if err != nil {
			t.Fatalf("IssueRefresh: %v", err)
		}
		if seen[refresh.Token] {
			t.Fatal("the issuer produced the same refresh token twice")
		}
		seen[refresh.Token] = true
	}
}

func TestIssuingWithAnExistingChainKeepsTheFamily(t *testing.T) {
	t.Parallel()

	// A rotation must not start a new family. If it did, a leaked refresh
	// token would escape the revocation meant for it.
	i := newTestIssuer(t, nil)
	first, _ := i.Issue("acct-1", "")
	second, err := i.Issue("acct-1", first.ChainID)
	if err != nil {
		t.Fatalf("Issue: %v", err)
	}
	if second.ChainID != first.ChainID {
		t.Fatalf("chain changed on rotation: %q -> %q", first.ChainID, second.ChainID)
	}
	if second.Refresh.Token == first.Refresh.Token {
		t.Fatal("a rotation must mint a new refresh token")
	}
	if second.SessionID == first.SessionID {
		t.Fatal("a rotation must mint a new session id")
	}
}

func TestFreshIssuesGetDistinctChains(t *testing.T) {
	t.Parallel()

	i := newTestIssuer(t, nil)
	a, _ := i.Issue("acct-1", "")
	b, _ := i.Issue("acct-1", "")
	if a.ChainID == b.ChainID {
		t.Fatal("two separate sign-ins shared a chain id")
	}
}

// Signing out has to work with an access token that has already expired. That
// is the moment people press the button. The signature check is not skipped —
// a forged token still fails — only the clock is.
func TestPeekIgnoresExpiryButNotTheSignature(t *testing.T) {
	t.Parallel()

	now := time.Date(2026, 10, 18, 12, 0, 0, 0, time.UTC)
	i := newTestIssuer(t, &now)
	creds, _ := i.Issue("acct-1", "")

	now = now.Add(48 * time.Hour)
	if _, err := i.Verify(creds.Access.Token); !errors.Is(err, ErrExpired) {
		t.Fatalf("Verify should report expiry, got %v", err)
	}

	claims, err := i.Peek(creds.Access.Token)
	if err != nil {
		t.Fatalf("Peek on an expired but genuine token: %v", err)
	}
	if claims.ChainID != creds.ChainID {
		t.Errorf("chain = %q, want %q", claims.ChainID, creds.ChainID)
	}

	other, err := NewIssuer("ffffffffffffffffffffffffffffffffffffffff", time.Hour, 24*time.Hour)
	if err != nil {
		t.Fatalf("NewIssuer: %v", err)
	}
	if _, err := other.Peek(creds.Access.Token); !errors.Is(err, ErrInvalid) {
		t.Fatalf("Peek must still verify the signature, got %v", err)
	}
	if _, err := i.Peek("garbage"); err == nil {
		t.Fatal("Peek accepted garbage")
	}
}

// A non-"access" token signed with our key is not an access token. If some
// future feature issued signed blobs of another kind, this is the line that
// stops them being presented at the API.
func TestOnlyAccessTypedTokensVerify(t *testing.T) {
	t.Parallel()

	i := newTestIssuer(t, nil)
	other, err := i.sign(payload{
		Subject:   "acct-1",
		SessionID: "s",
		ChainID:   "c",
		IssuedAt:  time.Now().Unix(),
		ExpiresAt: time.Now().Add(time.Hour).Unix(),
		Type:      "receipt",
	})
	if err != nil {
		t.Fatalf("sign: %v", err)
	}
	if _, err := i.Verify(other); !errors.Is(err, ErrInvalid) {
		t.Fatalf("a token of another type verified, got %v", err)
	}
}

func TestFingerprintDoesNotRevealTheSecret(t *testing.T) {
	t.Parallel()

	const secret = "wcb1_abcdefghijklmnopqrstuvwxyz0123456789ABCDE"
	fp := Fingerprint(secret)
	if len(fp) != 8 {
		t.Fatalf("fingerprint is %d characters, want 8: %q", len(fp), fp)
	}
	if strings.Contains(secret, fp) {
		t.Fatal("the fingerprint is a substring of the secret, so it confirms a guess")
	}
	// Stable, so an operator can match a log line to a database row.
	if Fingerprint(secret) != fp {
		t.Fatal("fingerprint is not stable")
	}
	if Fingerprint("other") == fp {
		t.Fatal("two different secrets produced the same fingerprint")
	}
	if Fingerprint("") != "" {
		t.Fatal("an empty secret should fingerprint as empty")
	}
}

func TestHashesAreDeterministicAndDistinctPerInput(t *testing.T) {
	t.Parallel()

	if HashRefresh("a") != HashRefresh("a") {
		t.Fatal("HashRefresh is not deterministic")
	}
	if HashRefresh("a") == HashRefresh("b") {
		t.Fatal("two refresh tokens hashed the same")
	}
	if HashAccess("a") == HashRefresh("a") {
		t.Fatal("the two token kinds share a hash namespace")
	}
	if len(HashRefresh("a")) != 64 {
		t.Fatalf("hash is %d characters, want a full SHA-256 hex digest", len(HashRefresh("a")))
	}
}
