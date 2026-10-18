package redeem

// The redemption code's shape and the normalisation table that goes with it.
//
// This table is a contract with the Windows client, not an implementation detail.
// The client normalises the same way before it burns anything locally and before it
// hashes what it writes to redeem-pending.json, and the server normalises the same
// way before it burns the code and before it looks it up. The two implementations
// are windows/src/WeChatBridge.Windows.Core/Redeem/RedeemCode.cs and this file, and
// the shared test vectors live in TestNormalisationMatchesTheClientTable below and in
// the client's RedeemCodeTests.cs.
//
// If they drift the symptom is "every code is invalid" with no signature error
// anywhere to point at — the request succeeds, the server simply cannot find the code
// it was sent. That is the same class of accident as a licence whose canonical
// payload moved (§10.2), which is why the rules are a few lines of hand-written
// switch rather than a dependency.

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"errors"
	"fmt"
	"strings"
	"unicode"
)

// Shape constants. These mirror the client's RedeemCode exactly; a test pins them
// together with the client's own so the two cannot drift.
const (
	// Prefix is the human-readable source marker at the head of every code.
	Prefix = "WCB"

	// PrefixLength is spelled out rather than derived because the client's tests
	// assert on the constant itself.
	PrefixLength = 3

	// GroupLength is the number of body characters between separators.
	GroupLength = 5

	// GroupCount is how many groups follow the prefix.
	GroupCount = 4

	// BodyLength is the random part, excluding the prefix. Twenty Crockford
	// Base32 characters is about 100 bits, which is what makes guessing a code
	// hopeless. The channel leak is the real risk, and that is answered by burning
	// the code and by the TTL, not by entropy.
	BodyLength = GroupLength * GroupCount

	// NormalizedLength is the canonical length with separators removed: prefix
	// plus body.
	NormalizedLength = PrefixLength + BodyLength

	// CanonicalLength is the length of the canonical string including its four
	// separators.
	CanonicalLength = NormalizedLength + GroupCount

	// Separator is the canonical group separator. A rune rather than a string
	// because normalisation walks the input one rune at a time, and comparing a
	// rune against a one-character string is how an off-by-one-width bug hides.
	Separator = '-'
)

// alphabet is Crockford Base32: the ten digits and twenty-two letters with I, L,
// O and U removed. Crockford's alphabet is what makes a code survive being read
// aloud over support chat — those four letters are the ones people mishear.
const alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"

// ErrNotACode is returned when the input cannot be a code at all, as opposed to
// being a well-formed code that no longer exists. The first is a client-visible
// typo; the second is a support conversation.
var ErrNotACode = errors.New("redeem: not a redemption code")

// ErrEntropy is returned when the system random source fails. It is separate from
// ErrNotACode because it is an outage, not a rejection.
var ErrEntropy = errors.New("redeem: random source failed")

// NormalizeBody strips separators and whitespace, upper-cases, and applies
// Crockford's anti-confusion folds. It reports false for input carrying a
// character no code can contain.
//
// The three folds are I and L to 1, and O to 0 — the substitutions people actually
// make copying a code by hand. U is deliberately not folded: it is not in the
// alphabet at all, and folding it to V would turn a typo into a different, valid
// looking code rather than into a rejection. The client's comment says the same
// thing, and this is the one line in the file where the two must not differ.
func NormalizeBody(input string) (string, bool) {
	if strings.TrimSpace(input) == "" {
		return "", false
	}

	var b strings.Builder
	b.Grow(len(input))
	for _, raw := range strings.ToUpper(strings.TrimSpace(input)) {
		// Hyphen is the canonical separator; whitespace is what a code becomes
		// after an email client; underscore is what a PDF text layer and a
		// spreadsheet cell produce, and rejecting it would be pedantry at the
		// customer's expense.
		if unicode.IsSpace(raw) || raw == Separator || raw == '_' {
			continue
		}

		var c rune
		switch raw {
		case 'I', 'L':
			c = '1'
		case 'O':
			c = '0'
		default:
			c = raw
		}

		if !strings.ContainsRune(alphabet, c) {
			return "", false
		}
		b.WriteRune(c)
	}
	return b.String(), true
}

// Canonical normalises any spelling a human might produce — pasted, typed,
// dictated over support, lower case, spaces instead of dashes, full-width — into
// the one string that is ever hashed, stored or compared.
//
// Full-width input is rejected rather than folded, which is what the client does:
// its TryParse returns false for a character outside the alphabet, so a
// full-width paste never reaches the server at all. Accepting it here would create
// a spelling the client cannot produce, which is worse than rejecting one it
// already refuses.
func Canonical(input string) (string, bool) {
	body, ok := NormalizeBody(input)
	if !ok || len(body) != NormalizedLength || !strings.HasPrefix(body, Prefix) {
		return "", false
	}

	var b strings.Builder
	b.Grow(CanonicalLength)
	b.WriteString(Prefix)
	b.WriteByte(byte(Separator))
	for i := 0; i < BodyLength; i++ {
		if i > 0 && i%GroupLength == 0 {
			b.WriteByte(byte(Separator))
		}
		b.WriteByte(body[PrefixLength+i])
	}
	return b.String(), true
}

// Digest is base64url over SHA-256 of the canonical form, and is the only thing
// the store ever holds. The plaintext code travels through email, an order page
// and support conversations, all three of which get screenshotted; the digest is
// what goes into a table row or a log line.
//
// It matches the client's RedeemCode.Digest byte for byte, which is what lets the
// client write a pending record before its first request and have the server agree
// about which code it is about.
func Digest(canonical string) string {
	sum := sha256.Sum256([]byte(canonical))
	return base64.RawURLEncoding.EncodeToString(sum[:])
}

// CodeDigest normalises and hashes in one step. It is the form every caller
// outside this file wants, because the plaintext is never needed again after it.
func CodeDigest(input string) (digest string, canonical string, err error) {
	canonical, ok := Canonical(input)
	if !ok {
		return "", "", ErrNotACode
	}
	return Digest(canonical), canonical, nil
}

// Generate mints one fresh code. Twenty characters drawn from the alphabet with a
// uniform index into it — a modulo over 36 would bias the last character, which is
// exactly the character a human retypes by hand.
//
// The returned plaintext is the only copy that will ever exist: the store keeps the
// digest only. Callers must deliver it to the customer and never log it.
func Generate() (canonical string, digest string, err error) {
	var b strings.Builder
	b.Grow(BodyLength)
	for i := 0; i < BodyLength; i++ {
		n, err := randomIndex(len(alphabet))
		if err != nil {
			return "", "", err
		}
		b.WriteByte(alphabet[n])
	}
	canonical = Prefix + string(Separator) + group(b.String())
	return canonical, Digest(canonical), nil
}

// group inserts the canonical separators every GroupLength body characters.
func group(body string) string {
	var b strings.Builder
	b.Grow(CanonicalLength)
	for i := 0; i < len(body); i++ {
		if i > 0 && i%GroupLength == 0 {
			b.WriteByte(byte(Separator))
		}
		b.WriteByte(body[i])
	}
	return b.String()
}

func randomIndex(n int) (int, error) {
	// One byte is enough for the 32-character alphabet, and rejection sampling on
	// 32 rather than 36 removes the bias without a loop that has to run long.
	var buf [1]byte
	for {
		if _, err := rand.Read(buf[:]); err != nil {
			return 0, fmt.Errorf("%w: %v", ErrEntropy, err)
		}
		if int(buf[0]) < 256-(256%n) {
			return int(buf[0]) % n, nil
		}
	}
}
