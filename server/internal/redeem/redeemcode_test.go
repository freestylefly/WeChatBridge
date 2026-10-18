package redeem

import (
	"encoding/base64"
	"strings"
	"testing"
)

// The normalisation table is the part of this flow the server and the client must
// share verbatim. If they drift, every code is invalid and nothing anywhere
// throws, so these vectors are mirrored from
// windows/tests/WeChatBridge.Windows.Tests/RedeemCodeTests.cs. Change one side and
// this file is the reminder to change the other.
//
// The sample is the client's own: WCB-7K3MQ-9XQ2H-4TB6P-WN3DP, four groups of
// five. The design document's illustrative example (WCB-7K3M-9XQ2-H4TB-6PWN) is
// four groups of four and is a shape no code has ever had — sixteen body
// characters is forty bits, and the hundred-bit argument in §3.3 is about twenty.
const sample = "WCB-7K3MQ-9XQ2H-4TB6P-WN3DP"

func TestShapeConstantsMatchTheClient(t *testing.T) {
	cases := []struct {
		name string
		got  int
		want int
	}{
		{"GroupCount", GroupCount, 4},
		{"GroupLength", GroupLength, 5},
		{"BodyLength", BodyLength, 20},
		{"NormalizedLength", NormalizedLength, 23},
		{"PrefixLength", PrefixLength, 3},
	}
	for _, c := range cases {
		if c.got != c.want {
			t.Errorf("%s = %d, want %d", c.name, c.got, c.want)
		}
	}
	if len(Prefix) != PrefixLength {
		t.Errorf("len(Prefix) = %d, want %d", len(Prefix), PrefixLength)
	}
	if CanonicalLength != len(sample) {
		t.Errorf("CanonicalLength = %d, want %d", CanonicalLength, len(sample))
	}
	if strings.Count(sample, string(Separator)) != GroupCount {
		t.Errorf("sample has %d separators, want %d", strings.Count(sample, string(Separator)), GroupCount)
	}
}

// The canonical form is the only spelling that is ever hashed.
func TestTheCanonicalFormIsTheDocumentedShape(t *testing.T) {
	got, ok := Canonical(sample)
	if !ok || got != sample {
		t.Fatalf("Canonical(%q) = %q, %v; want the input back", sample, got, ok)
	}
}

func TestNormalisationMatchesTheClientTable(t *testing.T) {
	// Every entry here is an InlineData from the client's RedeemCodeTests. They
	// are the shared vector, and the two files must be edited together.
	spellings := []string{
		"WCB-7K3MQ-9XQ2H-4TB6P-WN3DP",
		"wcb-7k3mq-9xq2h-4tb6p-wn3dp",
		"  WCB7K3MQ9XQ2H4TB6PWN3DP  ",
		"WCB 7K3MQ 9XQ2H 4TB6P WN3DP",
		"WCB_7K3MQ_9XQ2H_4TB6P_WN3DP",
		"wcb-7k3mq 9xq2h-4tb6p wn3dp",
		"WCB-7K3MQ-9XQ2H-4TB6P-WN3DP-",
		"WCB\t7K3MQ\n9XQ2H\r4TB6P WN3DP",
	}
	for _, spelling := range spellings {
		got, ok := Canonical(spelling)
		if !ok || got != sample {
			t.Errorf("Canonical(%q) = %q, %v; want %q", spelling, got, ok, sample)
		}
	}
}

func TestConfusableLettersFoldTheWayTheClientFoldsThem(t *testing.T) {
	cases := []struct{ canonical, typed string }{
		{"WCB-7K3MQ-9XQ20-4TB6P-WN3DP", "WCB-7K3MQ-9XQ20-4TB6P-WN3DP"}, // a digit is already canonical
		{"WCB-7K3MQ-9XQ20-4TB6P-WN3DP", "WCB-7K3MQ-9XQ2O-4TB6P-WN3DP"}, // O typed as zero
		{"WCB-7K3MQ-9XQ21-4TB6P-WN3DP", "WCB-7K3MQ-9XQ2I-4TB6P-WN3DP"}, // I typed as one
		{"WCB-7K3MQ-9XQ21-4TB6P-WN3DP", "WCB-7K3MQ-9XQ2L-4TB6P-WN3DP"}, // L typed as one
	}
	for _, c := range cases {
		got, ok := Canonical(c.typed)
		if !ok || got != c.canonical {
			t.Errorf("Canonical(%q) = %q, %v; want %q", c.typed, got, ok, c.canonical)
		}
	}
}

// U is rejected, not folded. Crockford's U is not in the alphabet, and folding it
// to V would turn a typo into a different, valid-looking code rather than into a
// rejection — the code the user typed would not be the code the server burns. The
// brief for this package asked for a U→V fold; the client does not have one, and a
// server that did would be the side that disagrees.
func TestUIsRejectedRatherThanFolded(t *testing.T) {
	if _, ok := Canonical("WCB-7K3MQ-9XQ2H-4TB6P-WN3UP"); ok {
		t.Fatal("a code containing U parsed; the client rejects it")
	}
	if _, ok := Canonical("WCB-7K3MQ-9XQ2H-4TB6P-WN3UV"); ok {
		t.Fatal("U→V folding is not in the contract")
	}
}

// Full-width input is rejected too, which is what the client's TryParse does: a
// full-width character is outside the alphabet, so it never reaches the server
// from that client. Folding it here would create a spelling the client cannot
// produce, which is a worse failure than refusing one it already refuses.
func TestFullWidthIsRejectedLikeTheClientRejectsIt(t *testing.T) {
	full := "ＷＣＢ－７Ｋ３ＭＱ－９ＸＱ２Ｈ－４ＴＢ６Ｐ－ＷＮ３ＤＰ"
	if _, ok := Canonical(full); ok {
		t.Fatal("full-width input parsed; the client cannot produce one")
	}
}

func TestTextThatIsNotACodeIsRejected(t *testing.T) {
	cases := []string{
		"",
		"   ",
		"WCB-7K3MQ-9XQ2H-4TB6P",        // three groups
		"WCB-7K3MQ-9XQ2H-4TB6P-WN3DPX", // one character too many
		"WCB-7K3M-9XQ2H-4TB6P-WN3",     // one character too few
		"XYZ-7K3MQ-9XQ2H-4TB6P-WN3DP",  // wrong prefix
		"WCB-7K3MQ-9XQ2H-4TB6P-WN3D$",  // a character the alphabet lacks
		"WCB-7K3M-9XQ2-H4TB-6PWN",      // the document's four-by-four example
	}
	for _, in := range cases {
		if _, ok := Canonical(in); ok {
			t.Errorf("Canonical(%q) parsed", in)
		}
	}
}

// The digest is what lands on disk, so its inputs are pinned rather than "some hash
// of the canonical string". It must equal the client's RedeemCode.DigestOf:
// base64url, unpadded, over SHA-256 of the canonical UTF-8 text.
func TestTheDigestIsTakenOverTheCanonicalForm(t *testing.T) {
	digest, canonical, err := CodeDigest("wcb 7k3mq 9xq2h 4tb6p wn3dp")
	if err != nil {
		t.Fatalf("CodeDigest: %v", err)
	}
	if canonical != sample {
		t.Fatalf("canonical = %q, want %q", canonical, sample)
	}
	if digest != Digest(sample) {
		t.Fatalf("CodeDigest disagreed with Digest")
	}
	if strings.ContainsAny(digest, "=+/") {
		t.Errorf("digest %q is not unpadded base64url", digest)
	}
	if len(digest) != 43 {
		t.Errorf("digest length = %d, want 43 for SHA-256 in base64url", len(digest))
	}

	// Pinned value, so a change to the digest's input or encoding is a test
	// failure rather than a silent disagreement with the client's pending record.
	// The client computes the same thing in RedeemCode.DigestOf.
	const want = "2PKbVoqaJTWbevbcyivjllo2rTqWuJIHpqDGRBikLQk"
	if digest != want {
		t.Fatalf("Digest(%q) = %q, want %q", sample, digest, want)
	}
	raw, err := base64.RawURLEncoding.DecodeString(digest)
	if err != nil || len(raw) != 32 {
		t.Fatalf("digest is not 32 bytes of base64url: %v", err)
	}
}

func TestGenerateProducesCodesThatNormaliseToThemselves(t *testing.T) {
	seen := make(map[string]struct{}, 200)
	for i := 0; i < 200; i++ {
		code, digest, err := Generate()
		if err != nil {
			t.Fatalf("Generate: %v", err)
		}
		if len(code) != CanonicalLength {
			t.Fatalf("generated %q has length %d, want %d", code, len(code), CanonicalLength)
		}
		got, ok := Canonical(code)
		if !ok || got != code {
			t.Fatalf("generated %q does not normalise to itself (got %q, %v)", code, got, ok)
		}
		if Digest(code) != digest {
			t.Fatalf("Generate's digest disagrees with Digest for %q", code)
		}
		for _, r := range code {
			if r != Separator && !strings.ContainsRune(alphabet, r) {
				t.Fatalf("generated %q contains %q, which is outside Crockford's alphabet", code, r)
			}
		}
		if _, dup := seen[code]; dup {
			t.Fatalf("Generate produced %q twice in 200 draws", code)
		}
		seen[code] = struct{}{}
	}
}

// The generated code must survive the same confusions a typed one does, in the
// same direction: a canonical code never contains a character that folds.
func TestGeneratedCodesAreAlreadyCanonical(t *testing.T) {
	for i := 0; i < 50; i++ {
		code, _, err := Generate()
		if err != nil {
			t.Fatalf("Generate: %v", err)
		}
		if strings.ContainsAny(strings.ToUpper(code), "ILOU") {
			t.Fatalf("generated %q contains a letter that folds on the way back in", code)
		}
	}
}
