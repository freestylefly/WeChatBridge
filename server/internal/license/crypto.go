package license

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/x509"
	"encoding/base64"
	"encoding/pem"
	"fmt"
	"math/big"
	"os"
)

func base64Decode(s string) ([]byte, error) { return base64.StdEncoding.DecodeString(s) }
func base64Encode(b []byte) string          { return base64.StdEncoding.EncodeToString(b) }

func ellipticP256() elliptic.Curve { return elliptic.P256() }

// LoadPrivateKey reads the issuing key from a PEM file.
//
// Both PKCS#8 ("PRIVATE KEY") and SEC 1 ("EC PRIVATE KEY") are accepted,
// because `openssl ecparam -genkey` and `openssl pkcs8 -topk8` emit
// different ones and an operator will have used whichever their tutorial
// happened to show. The file is scanned block by block, because the
// ecparam command writes an "EC PARAMETERS" block ahead of the key and a
// loader that only reads the first block refuses the exact file that
// command produces. The curve must be P-256 — the one the desktop client
// embeds the matching public key for — and a key of any other curve is
// refused here rather than producing licences nobody can verify.
//
// The path, never the contents, belongs in a log line.
func LoadPrivateKey(path string) (*ecdsa.PrivateKey, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("license: read private key %s: %w", path, err)
	}

	var key *ecdsa.PrivateKey
	for block, rest := pem.Decode(raw); block != nil; block, rest = pem.Decode(rest) {
		switch block.Type {
		case "PRIVATE KEY":
			parsed, err := x509.ParsePKCS8PrivateKey(block.Bytes)
			if err != nil {
				return nil, fmt.Errorf("license: %s carries an unparseable PKCS#8 key: %w", path, err)
			}
			ec, ok := parsed.(*ecdsa.PrivateKey)
			if !ok {
				return nil, fmt.Errorf("license: %s carries a %T, want an EC key", path, parsed)
			}
			key = ec
		case "EC PRIVATE KEY":
			parsed, err := x509.ParseECPrivateKey(block.Bytes)
			if err != nil {
				return nil, fmt.Errorf("license: %s carries an unparseable SEC 1 key: %w", path, err)
			}
			key = parsed
		}
		if key != nil {
			break
		}
	}
	if key == nil {
		return nil, fmt.Errorf("license: %s carries no PRIVATE KEY or EC PRIVATE KEY block", path)
	}

	if key.Curve != elliptic.P256() {
		return nil, fmt.Errorf("license: %s is a %s key, want P-256 — the curve the client embeds", path, key.Curve.Params().Name)
	}
	return key, nil
}

// signRaw produces an IeeeP1363 fixed-field signature: r and s concatenated,
// each padded to 32 bytes, 64 bytes total.
//
// Go's default is ASN.1 DER, which is what every other part of the ecosystem
// emits. The client's verifier rejects anything that is not exactly 64 bytes,
// so this one call site is the entire difference between "every licence works"
// and "no licence works", and it is the reason the length check in Verify names
// DER as the usual cause.
func signRaw(key *ecdsa.PrivateKey, digest []byte) ([]byte, error) {
	r, s, err := ecdsa.Sign(nil, key, digest)
	if err != nil {
		return nil, err
	}
	out := make([]byte, SignatureLength)
	r.FillBytes(out[:32])
	s.FillBytes(out[32:])
	return out, nil
}

// verifyRaw checks a fixed-field signature. ecdsa.Verify expects the halves
// separately, so it is split back out here; it checks for malleability itself.
func verifyRaw(pub *ecdsa.PublicKey, digest, fixed []byte) bool {
	if len(fixed) != SignatureLength {
		return false
	}
	r := new(big.Int).SetBytes(fixed[:32])
	s := new(big.Int).SetBytes(fixed[32:])
	return ecdsa.Verify(pub, digest, r, s)
}

// marshalSPKI encodes a public key the way the client imports it: Subject
// Public Key Info, DER. This is the exact string the client pastes into
// LicensePublicKeys, so the encoding must not be "a valid encoding" — it must
// be this one.
func marshalSPKI(pub *ecdsa.PublicKey) ([]byte, error) {
	return x509.MarshalPKIXPublicKey(pub)
}

// ecdsaSignDER produces an ASN.1 DER signature — Go's default, and what
// `openssl dgst -sign` emits. Nothing in production calls it; the test suite
// uses it to prove the client would reject one, and that the failure says so
// by name instead of reporting a generic mismatch.
func ecdsaSignDER(key *ecdsa.PrivateKey, digest []byte) ([]byte, error) {
	return ecdsa.SignASN1(rand.Reader, key, digest)
}
