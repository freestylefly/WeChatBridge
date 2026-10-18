package license

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/x509"
	"encoding/base64"
	"math/big"
)

func base64Decode(s string) ([]byte, error) { return base64.StdEncoding.DecodeString(s) }
func base64Encode(b []byte) string          { return base64.StdEncoding.EncodeToString(b) }

func ellipticP256() elliptic.Curve { return elliptic.P256() }

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
