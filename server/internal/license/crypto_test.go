package license

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/x509"
	"encoding/pem"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// writePEM puts a PEM block in a temp file and returns its path.
func writePEM(t *testing.T, blockType string, der []byte) string {
	t.Helper()
	path := filepath.Join(t.TempDir(), "key.pem")
	raw := pem.EncodeToMemory(&pem.Block{Type: blockType, Bytes: der})
	if err := os.WriteFile(path, raw, 0o600); err != nil {
		t.Fatalf("write key: %v", err)
	}
	return path
}

func TestLoadPrivateKeyAcceptsPKCS8(t *testing.T) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatalf("GenerateKey: %v", err)
	}
	der, err := x509.MarshalPKCS8PrivateKey(key)
	if err != nil {
		t.Fatalf("MarshalPKCS8PrivateKey: %v", err)
	}
	path := writePEM(t, "PRIVATE KEY", der)

	loaded, err := LoadPrivateKey(path)
	if err != nil {
		t.Fatalf("LoadPrivateKey: %v", err)
	}
	if loaded.D.Cmp(key.D) != 0 {
		t.Error("the loaded key is not the key that was written")
	}
	// And it signs: the whole point of loading it is the signer.
	signer, err := NewSigner(loaded)
	if err != nil {
		t.Fatalf("NewSigner: %v", err)
	}
	if _, err := signer.Build(IssueParams{Subject: "machine"}); err != nil {
		t.Fatalf("Build with the loaded key: %v", err)
	}
}

func TestLoadPrivateKeyAcceptsSEC1(t *testing.T) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatalf("GenerateKey: %v", err)
	}
	der, err := x509.MarshalECPrivateKey(key)
	if err != nil {
		t.Fatalf("MarshalECPrivateKey: %v", err)
	}
	path := writePEM(t, "EC PRIVATE KEY", der)

	loaded, err := LoadPrivateKey(path)
	if err != nil {
		t.Fatalf("LoadPrivateKey: %v", err)
	}
	if loaded.D.Cmp(key.D) != 0 {
		t.Error("the loaded key is not the key that was written")
	}
}

func TestLoadPrivateKeyAcceptsTheFileOpenSSLActuallyWrites(t *testing.T) {
	// `openssl ecparam -genkey -name prime256v1 -out key.pem` — the command
	// every tutorial shows — writes TWO PEM blocks: EC PARAMETERS first,
	// then the EC PRIVATE KEY. A loader that decodes only the first block
	// refuses the exact file an operator following that tutorial produced.
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatalf("GenerateKey: %v", err)
	}
	der, err := x509.MarshalECPrivateKey(key)
	if err != nil {
		t.Fatalf("MarshalECPrivateKey: %v", err)
	}
	path := filepath.Join(t.TempDir(), "key.pem")
	raw := pem.EncodeToMemory(&pem.Block{Type: "EC PARAMETERS", Bytes: []byte{0x06, 0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07}})
	raw = append(raw, pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: der})...)
	if err := os.WriteFile(path, raw, 0o600); err != nil {
		t.Fatalf("write key: %v", err)
	}

	loaded, err := LoadPrivateKey(path)
	if err != nil {
		t.Fatalf("LoadPrivateKey: %v", err)
	}
	if loaded.D.Cmp(key.D) != 0 {
		t.Error("the loaded key is not the key that was written")
	}
}

func TestLoadPrivateKeyRejectsWhatItCannotUse(t *testing.T) {
	p384, err := ecdsa.GenerateKey(elliptic.P384(), rand.Reader)
	if err != nil {
		t.Fatalf("GenerateKey: %v", err)
	}
	p384DER, err := x509.MarshalECPrivateKey(p384)
	if err != nil {
		t.Fatalf("MarshalECPrivateKey: %v", err)
	}

	dir := t.TempDir()
	notPEM := filepath.Join(dir, "not-pem.pem")
	if err := os.WriteFile(notPEM, []byte("this is not a key, just optimism"), 0o600); err != nil {
		t.Fatalf("write: %v", err)
	}
	wrongBlock := writePEM(t, "CERTIFICATE", p384DER)
	wrongCurve := writePEM(t, "EC PRIVATE KEY", p384DER)
	garbageDER := writePEM(t, "EC PRIVATE KEY", []byte{0x01, 0x02, 0x03})

	cases := []struct {
		name string
		path string
		want string
	}{
		{"not a PEM file", notPEM, "carries no PRIVATE KEY or EC PRIVATE KEY block"},
		{"wrong block type", wrongBlock, "carries no PRIVATE KEY or EC PRIVATE KEY block"},
		{"wrong curve", wrongCurve, "P-256"},
		{"unparseable DER", garbageDER, "unparseable"},
		{"missing file", filepath.Join(dir, "absent.pem"), "read private key"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			_, err := LoadPrivateKey(tc.path)
			if err == nil {
				t.Fatalf("LoadPrivateKey(%s) succeeded, want a refusal", tc.path)
			}
			if !strings.Contains(err.Error(), tc.want) {
				t.Errorf("error %q does not mention %q", err, tc.want)
			}
			// The error names the path, never the contents: a log line
			// with a private key in it is its own incident.
			if !strings.Contains(err.Error(), tc.path) {
				t.Errorf("error %q does not name the file %s", err, tc.path)
			}
		})
	}
}
