// Package token issues and verifies the credentials the desktop client carries.
//
// The two credentials are deliberately different animals:
//
//   - The access token is short-lived and self-contained. It carries the
//     account and chain identifiers in a signed envelope, so the common case —
//     "which account is this?" — is a signature check and nothing else. It is
//     signed, not encrypted, and it says so in its own header: a client can
//     read it, and must not pretend otherwise.
//
//   - The refresh token is an opaque random string. Only its SHA-256 goes into
//     the database. It is not a JWT, and making it one would be a mistake with
//     teeth: a self-contained refresh token can only be invalidated by
//     refusing to honour it, which means it is never actually invalid — the
//     client keeps presenting it until its own expiry passes. Refresh tokens
//     exist so they can be revoked at a moment's notice, so the state has to
//     live on this side.
//
// Signing and verification happen entirely here. The client never signs
// anything and holds no key.
package token

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"strings"
	"time"
)

var (
	// ErrInvalid means the token is not one we issued, or was altered.
	ErrInvalid = errors.New("token: invalid")

	// ErrExpired means the token was ours and its time is up. It is separate
	// from ErrInvalid because the client's response differs: an expired access
	// token is worth a refresh, a malformed one is not.
	ErrExpired = errors.New("token: expired")
)

// alg is fixed, not read from the incoming token. A verifier that takes the
// algorithm from the thing it is verifying has an algorithm-confusion bug
// waiting for the first attacker; this one only ever checks HS256 and rejects
// everything else, including "none".
const alg = "HS256"

// refreshPrefix versions the refresh token's shape. It costs 5 bytes and makes
// a leaked string greppable in a log-scraping incident and rotatable later
// without ambiguity about which generation it was.
const refreshPrefix = "wcb1_"

// Claims is what the access token asserts.
type Claims struct {
	AccountID string
	SessionID string
	// ChainID is the family this token descends from. It is why an access
	// token is enough to revoke a chain: the client never needs to hold a
	// refresh token to log out.
	ChainID   string
	IssuedAt  time.Time
	ExpiresAt time.Time
}

type header struct {
	Alg string `json:"alg"`
	Typ string `json:"typ"`
}

type payload struct {
	Subject   string `json:"sub"`
	SessionID string `json:"sid"`
	ChainID   string `json:"cid"`
	IssuedAt  int64  `json:"iat"`
	ExpiresAt int64  `json:"exp"`
	Type      string `json:"typ"`
}

// Issuer mints both kinds of credential. It is safe for concurrent use.
type Issuer struct {
	secret     []byte
	accessTTL  time.Duration
	refreshTTL time.Duration
	now        func() time.Time
	rand       io.Reader
}

// NewIssuer builds an issuer. A secret shorter than 32 bytes is rejected here
// rather than at the first token, because config.Load already enforces that
// and a silent short key is a key nobody notices is weak.
func NewIssuer(secret string, accessTTL, refreshTTL time.Duration) (*Issuer, error) {
	if len(secret) < 32 {
		return nil, fmt.Errorf("token: signing secret must be at least 32 bytes, got %d", len(secret))
	}
	if accessTTL <= 0 {
		return nil, fmt.Errorf("token: access TTL must be positive")
	}
	if refreshTTL <= accessTTL {
		return nil, fmt.Errorf("token: refresh TTL must outlast the access TTL")
	}
	return &Issuer{
		secret:     []byte(secret),
		accessTTL:  accessTTL,
		refreshTTL: refreshTTL,
		now:        func() time.Time { return time.Now().UTC() },
		rand:       rand.Reader,
	}, nil
}

// Issued is one credential plus the facts about it the store records.
type Issued struct {
	Token     string
	Hash      string
	ExpiresAt time.Time
}

// NewCredentials is one signed access token plus the claims it asserts. The
// hashes and the TTLs are carried alongside so the caller does not recompute
// anything the issuer already decided.
type NewCredentials struct {
	Access           Issued
	AccessClaims     Claims
	AccessTokenHash  string
	Refresh          Issued
	RefreshTokenHash string
	RefreshExpiresAt time.Time
	ChainID          string
	SessionID        string
}

// Issue mints a matched access/refresh pair for one session. The two always
// come from the same call because they always belong to the same chain, and a
// caller that could issue them separately could eventually put a token from one
// chain next to a session row from another.
//
// The returned Token strings go on the wire and never touch a log; the hashes
// are what the store keeps.
func (i *Issuer) Issue(accountID, existingChainID string) (NewCredentials, error) {
	now := i.now()

	chainID := existingChainID
	if chainID == "" {
		id, err := i.NewChainID()
		if err != nil {
			return NewCredentials{}, err
		}
		chainID = id
	}
	sessionID, err := i.NewID()
	if err != nil {
		return NewCredentials{}, err
	}
	refresh, err := i.IssueRefresh()
	if err != nil {
		return NewCredentials{}, err
	}

	claims := Claims{
		AccountID: accountID,
		SessionID: sessionID,
		ChainID:   chainID,
		IssuedAt:  now,
		ExpiresAt: now.Add(i.accessTTL),
	}
	raw, err := i.sign(payload{
		Subject:   accountID,
		SessionID: sessionID,
		ChainID:   chainID,
		IssuedAt:  now.Unix(),
		ExpiresAt: claims.ExpiresAt.Unix(),
		Type:      "access",
	})
	if err != nil {
		return NewCredentials{}, err
	}
	accessHash := HashAccess(raw)
	return NewCredentials{
		Access:           Issued{Token: raw, Hash: accessHash, ExpiresAt: claims.ExpiresAt},
		AccessClaims:     claims,
		AccessTokenHash:  accessHash,
		Refresh:          refresh,
		RefreshTokenHash: refresh.Hash,
		RefreshExpiresAt: refresh.ExpiresAt,
		ChainID:          chainID,
		SessionID:        sessionID,
	}, nil
}

// SetClock replaces the issuer's clock. It exists so a test can age a token
// out without sleeping, and it is the only way the clock is not the wall
// clock. It is not safe to call while tokens are being issued.
func (i *Issuer) SetClock(now func() time.Time) {
	if now == nil {
		now = func() time.Time { return time.Now().UTC() }
	}
	i.now = now
}

// AccessTTL and RefreshTTL report the configured lifetimes, for callers that
// need to show the client something before a token exists.
func (i *Issuer) AccessTTL() time.Duration  { return i.accessTTL }
func (i *Issuer) RefreshTTL() time.Duration { return i.refreshTTL }

// Now is the issuer's clock in UTC.
func (i *Issuer) Now() time.Time { return i.now() }

// IssueRefresh mints an opaque refresh token and the hash of it. The token is
// 256 bits from crypto/rand with no structure to guess at.
func (i *Issuer) IssueRefresh() (Issued, error) {
	var buf [32]byte
	if _, err := io.ReadFull(i.rand, buf[:]); err != nil {
		return Issued{}, fmt.Errorf("token: read random: %w", err)
	}
	raw := refreshPrefix + base64.RawURLEncoding.EncodeToString(buf[:])
	return Issued{Token: raw, Hash: HashRefresh(raw), ExpiresAt: i.now().Add(i.refreshTTL)}, nil
}

// NewChainID mints the identifier that groups every token descended from one
// sign-in. It is opaque to the client, which is the point: a client that knew
// it could guess the neighbours.
func (i *Issuer) NewChainID() (string, error) {
	var buf [16]byte
	if _, err := io.ReadFull(i.rand, buf[:]); err != nil {
		return "", fmt.Errorf("token: read random: %w", err)
	}
	return base64.RawURLEncoding.EncodeToString(buf[:]), nil
}

// NewID mints an identifier for a session or a ticket.
func (i *Issuer) NewID() (string, error) {
	var buf [24]byte
	if _, err := io.ReadFull(i.rand, buf[:]); err != nil {
		return "", fmt.Errorf("token: read random: %w", err)
	}
	return base64.RawURLEncoding.EncodeToString(buf[:]), nil
}

func (i *Issuer) sign(p payload) (string, error) {
	h, err := json.Marshal(header{Alg: alg, Typ: "JWT"})
	if err != nil {
		return "", fmt.Errorf("token: marshal header: %w", err)
	}
	b, err := json.Marshal(p)
	if err != nil {
		return "", fmt.Errorf("token: marshal payload: %w", err)
	}
	unsigned := b64(h) + "." + b64(b)
	mac := hmac.New(sha256.New, i.secret)
	mac.Write([]byte(unsigned))
	return unsigned + "." + b64(mac.Sum(nil)), nil
}

// Verify checks the signature and the expiry, and returns what the token
// asserts. It never touches the database, which is why the lifetime is short.
func (i *Issuer) Verify(raw string) (Claims, error) {
	claims, err := i.Peek(raw)
	if err != nil {
		return Claims{}, err
	}
	if !i.now().Before(claims.ExpiresAt) {
		return Claims{}, ErrExpired
	}
	return claims, nil
}

// Peek verifies the signature and the structure and returns the claims,
// ignoring only the clock.
//
// It exists for one caller: signing out with an access token that has already
// expired. That is the moment a user is most likely to press the button, and a
// server that answered "your token is invalid, so I cannot sign you out"
// leaves them stuck with a credential they are trying to throw away.
//
// The signature check is not skipped and must not be: reading the chain id out
// of an unverified payload would let anyone revoke any chain they could name.
// An expired token is a genuine token; a forged one is still a forgery.
func (i *Issuer) Peek(raw string) (Claims, error) {
	parts := strings.Split(raw, ".")
	if len(parts) != 3 {
		return Claims{}, ErrInvalid
	}
	mac := hmac.New(sha256.New, i.secret)
	mac.Write([]byte(parts[0] + "." + parts[1]))
	want, err := unb64(parts[2])
	if err != nil {
		return Claims{}, ErrInvalid
	}
	if !hmac.Equal(want, mac.Sum(nil)) {
		return Claims{}, ErrInvalid
	}

	hdrRaw, err := unb64(parts[0])
	if err != nil {
		return Claims{}, ErrInvalid
	}
	var hdr header
	if err := json.Unmarshal(hdrRaw, &hdr); err != nil || hdr.Alg != alg {
		// An "alg" we did not choose is not a token. Including "none" here is
		// the point.
		return Claims{}, ErrInvalid
	}

	pRaw, err := unb64(parts[1])
	if err != nil {
		return Claims{}, ErrInvalid
	}
	var p payload
	if err := json.Unmarshal(pRaw, &p); err != nil {
		return Claims{}, ErrInvalid
	}
	if p.Type != "access" || p.Subject == "" {
		// A refresh token, or something signed by us for another purpose,
		// presented as an access token.
		return Claims{}, ErrInvalid
	}
	claims := Claims{
		AccountID: p.Subject,
		SessionID: p.SessionID,
		ChainID:   p.ChainID,
		IssuedAt:  time.Unix(p.IssuedAt, 0).UTC(),
		ExpiresAt: time.Unix(p.ExpiresAt, 0).UTC(),
	}
	return claims, nil
}

// HashAccess is what the store keeps for the access token. It exists so a
// session row can be correlated and so a token found in a database dump can be
// tested without being a usable credential.
func HashAccess(raw string) string { return sha256Hex("access\x00" + raw) }

// HashRefresh is what the store keeps for the refresh token. The plaintext
// leaves this process exactly once, in a JSON response to the client.
//
// The two hash functions are domain-separated. They land in different columns
// and are never compared across kinds today, but a shared namespace means one
// careless join or one future lookup that forgets which column it is reading
// silently succeeds instead of returning nothing.
func HashRefresh(raw string) string { return sha256Hex("refresh\x00" + raw) }

// Fingerprint reduces a secret to eight hex characters of its SHA-256 so a log
// line can show "which token" without showing the token. It is a prefix of the
// same hash the store keeps, so an operator can match a log line against a
// database row. It is not a redaction of the token's own prefix — a prefix of
// the value itself would confirm a guess as readily as the whole thing.
func Fingerprint(secret string) string {
	if secret == "" {
		return ""
	}
	return sha256Hex(secret)[:8]
}

func sha256Hex(s string) string {
	sum := sha256.Sum256([]byte(s))
	return hex.EncodeToString(sum[:])
}

func b64(b []byte) string { return base64.RawURLEncoding.EncodeToString(b) }

func unb64(s string) ([]byte, error) { return base64.RawURLEncoding.DecodeString(s) }
