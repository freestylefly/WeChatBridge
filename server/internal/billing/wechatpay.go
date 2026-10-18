// Package billing is the subscription edition's money path: WeChat Pay Native
// orders, the callback that says they were paid, and the licence that a paid order
// is entitled to.
//
// Two things in here are load-bearing and easy to get wrong. The callback must be
// verified before anything is marked paid, and marking paid must be idempotent —
// WeChat re-delivers until it is answered with a success body, so a handler that
// issues on every delivery is a handler that issues several licences. See the
// design document, §9.8 and §15.5.
package billing

import (
	"bytes"
	"context"
	"crypto"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/json"
	"encoding/pem"
	"errors"
	"fmt"
	"io"
	"math/big"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"
)

// DefaultAPIBase is WeChat Pay's v3 endpoint. It is a field on the client rather
// than a constant so tests can point it at an httptest server.
const DefaultAPIBase = "https://api.mch.weixin.qq.com"

// nativePath is the Native transaction endpoint.
const nativePath = "/v3/pay/transactions/native"

// AuthorizationScheme is the APIv3 signature scheme identifier.
const AuthorizationScheme = "WECHATPAY2-SHA256-RSA2048"

// ErrPay is returned when WeChat answers an order request with an error body.
type ErrPay struct {
	Status  int
	Code    string
	Message string
}

func (e *ErrPay) Error() string {
	return fmt.Sprintf("wechat pay: %s (http %d): %s", e.Code, e.Status, e.Message)
}

// ClientOptions is everything the merchant side needs. The private key arrives as
// parsed key material, not as a path: loading it is main's job, and keeping file
// handling out of here means this package cannot log a key by accident.
type ClientOptions struct {
	// BaseURL is the API root. Defaults to DefaultAPIBase.
	BaseURL string

	MchID    string
	AppID    string
	SerialNo string

	PrivateKey *rsa.PrivateKey

	NotifyURL string

	HTTPClient *http.Client

	// Clock and Nonce are injectable so signatures are testable.
	Clock func() time.Time
	Nonce func() (string, error)
}

// Client talks to WeChat Pay's APIv3 as the merchant.
type Client struct {
	opts ClientOptions
	http *http.Client
}

// NewClient validates the merchant configuration. Half-configured payment is
// worse than absent payment — orders would be created and never confirmed — so
// every missing piece is named here rather than at the first callback.
func NewClient(opts ClientOptions) (*Client, error) {
	if strings.TrimSpace(opts.MchID) == "" {
		return nil, errors.New("billing: merchant id is empty")
	}
	if strings.TrimSpace(opts.AppID) == "" {
		return nil, errors.New("billing: app id is empty")
	}
	if strings.TrimSpace(opts.SerialNo) == "" {
		return nil, errors.New("billing: merchant certificate serial is empty")
	}
	if opts.PrivateKey == nil {
		return nil, errors.New("billing: merchant private key is nil")
	}
	if strings.TrimSpace(opts.NotifyURL) == "" {
		return nil, errors.New("billing: notify url is empty")
	}
	if opts.BaseURL == "" {
		opts.BaseURL = DefaultAPIBase
	}
	opts.BaseURL = strings.TrimRight(opts.BaseURL, "/")
	if opts.HTTPClient == nil {
		opts.HTTPClient = &http.Client{Timeout: 15 * time.Second}
	}
	if opts.Clock == nil {
		opts.Clock = func() time.Time { return time.Now() }
	}
	if opts.Nonce == nil {
		opts.Nonce = newNonce
	}
	return &Client{opts: opts, http: opts.HTTPClient}, nil
}

// Amount is money in integer minor units. A float here is a bug waiting for the
// one price that cannot be represented, so the type is an int64 and there is no
// conversion anywhere in this package.
type Amount struct {
	Total    int64  `json:"total"`
	Currency string `json:"currency"`
}

// NativeOrderRequest is the body of a Native transaction request.
type NativeOrderRequest struct {
	Description string
	OutTradeNo  string
	Amount      Amount
	// TimeExpire is RFC 3339. WeChat shows the countdown; the server is what
	// actually closes the order.
	TimeExpire time.Time
}

// MaxOutTradeNoLength is WeChat's limit on out_trade_no.
const MaxOutTradeNoLength = 32

// NativeOrder creates a Native transaction and returns the code_url the client
// renders into a QR.
func (c *Client) NativeOrder(ctx context.Context, req NativeOrderRequest) (codeURL string, err error) {
	if req.Amount.Total <= 0 {
		return "", errors.New("billing: amount must be positive")
	}
	if req.Amount.Currency == "" {
		req.Amount.Currency = "CNY"
	}
	if req.OutTradeNo == "" || len(req.OutTradeNo) > MaxOutTradeNoLength {
		return "", fmt.Errorf("billing: out_trade_no must be 1..%d characters, got %d",
			MaxOutTradeNoLength, len(req.OutTradeNo))
	}
	if strings.TrimSpace(req.Description) == "" {
		return "", errors.New("billing: description is empty")
	}

	body := struct {
		AppID       string `json:"appid"`
		MchID       string `json:"mchid"`
		Description string `json:"description"`
		OutTradeNo  string `json:"out_trade_no"`
		NotifyURL   string `json:"notify_url"`
		Amount      Amount `json:"amount"`
		TimeExpire  string `json:"time_expire,omitempty"`
	}{
		AppID:       c.opts.AppID,
		MchID:       c.opts.MchID,
		Description: req.Description,
		OutTradeNo:  req.OutTradeNo,
		NotifyURL:   c.opts.NotifyURL,
		Amount:      req.Amount,
	}
	if !req.TimeExpire.IsZero() {
		body.TimeExpire = req.TimeExpire.UTC().Format(time.RFC3339)
	}

	raw, err := json.Marshal(body)
	if err != nil {
		return "", err
	}

	authorization, err := c.authorization(http.MethodPost, nativePath, raw)
	if err != nil {
		return "", err
	}

	req2, err := http.NewRequestWithContext(ctx, http.MethodPost, c.opts.BaseURL+nativePath, bytes.NewReader(raw))
	if err != nil {
		return "", err
	}
	req2.Header.Set("Content-Type", "application/json")
	req2.Header.Set("Accept", "application/json")
	req2.Header.Set("User-Agent", "WeChatBridge/1.0")
	req2.Header.Set("Authorization", authorization)

	resp, err := c.http.Do(req2)
	if err != nil {
		return "", fmt.Errorf("billing: native order: %w", err)
	}
	defer resp.Body.Close()

	payload, err := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if err != nil {
		return "", fmt.Errorf("billing: native order: %w", err)
	}
	if resp.StatusCode < 200 || resp.StatusCode > 299 {
		var pe struct {
			Code    string `json:"code"`
			Message string `json:"message"`
		}
		_ = json.Unmarshal(payload, &pe)
		if pe.Code == "" {
			pe.Code = "UNKNOWN"
		}
		return "", &ErrPay{Status: resp.StatusCode, Code: pe.Code, Message: pe.Message}
	}

	var out struct {
		CodeURL string `json:"code_url"`
	}
	if err := json.Unmarshal(payload, &out); err != nil {
		return "", fmt.Errorf("billing: native order decode: %w", err)
	}
	if out.CodeURL == "" {
		return "", errors.New("billing: native order returned no code_url")
	}
	return out.CodeURL, nil
}

// authorization builds the APIv3 Authorization header.
//
// The signed message is method, path-with-query, timestamp, nonce and body, each
// followed by a newline and the whole thing followed by one more — the trailing
// newline is part of the definition and its absence is a signature failure with no
// other symptom. The merchant's private key signs; the platform certificate's
// public key verifies.
func (c *Client) authorization(method, path string, body []byte) (string, error) {
	timestamp := strconv.FormatInt(c.opts.Clock().Unix(), 10)
	nonce, err := c.opts.Nonce()
	if err != nil {
		return "", fmt.Errorf("billing: nonce: %w", err)
	}
	message := strings.Join([]string{method, path, timestamp, nonce, string(body)}, "\n") + "\n"
	sum := sha256.Sum256([]byte(message))
	signature, err := rsa.SignPKCS1v15(rand.Reader, c.opts.PrivateKey, crypto.SHA256, sum[:])
	if err != nil {
		return "", fmt.Errorf("billing: sign: %w", err)
	}
	return fmt.Sprintf(
		`%s mchid="%s",nonce_str="%s",signature="%s",timestamp="%s",serial_no="%s"`,
		AuthorizationScheme, c.opts.MchID, nonce,
		base64.StdEncoding.EncodeToString(signature), timestamp, c.opts.SerialNo,
	), nil
}

// newNonce returns WeChat's 32-character alphanumeric nonce.
func newNonce() (string, error) {
	const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"
	out := make([]byte, 32)
	for i := range out {
		n, err := randomIndex(len(alphabet))
		if err != nil {
			return "", err
		}
		out[i] = alphabet[n]
	}
	return string(out), nil
}

func randomIndex(n int) (int, error) {
	var buf [1]byte
	for {
		if _, err := rand.Read(buf[:]); err != nil {
			return 0, err
		}
		if int(buf[0]) < 256-(256%n) {
			return int(buf[0]) % n, nil
		}
	}
}

// ---------------------------------------------------------------------------
// Platform certificates
// ---------------------------------------------------------------------------

// Errors a callback can fail with. They are separate types rather than one because
// the operational response differs: an unknown serial means WeChat rotated and we
// have not caught up, an expired one means the refresh job is broken, and both are
// the silent-failure mode §15.5 warns about.
var (
	ErrPlatformCertUnknown = errors.New("billing: unknown platform certificate serial")
	ErrPlatformCertExpired = errors.New("billing: platform certificate has expired")
	ErrNoVerifier          = errors.New("billing: no platform verifier configured")
)

// PlatformVerifier checks a signature made by WeChat Pay.
//
// Automatic download from GET /v3/certificates is deliberately not implemented
// here; CertDirectory.Refresh is the seam where it goes. What matters today is
// that the seam exists and that expiry is detectable — a platform certificate
// that lapses makes every API call and every callback fail verification *silently*,
// which is a payment that stops working with nothing in the logs but a signature
// error.
type PlatformVerifier interface {
	Verify(ctx context.Context, serial string, message, signature []byte) error

	// ExpiringSoon reports the certificates that lapse within the window, for
	// the alert that should fire weeks before it happens.
	ExpiringSoon(now time.Time, within time.Duration) []string
}

// CertDirectory verifies against the PEM certificates WeChat publishes at
// /v3/certificates. It reloads from disk when the directory's contents change, so
// a job that drops the freshly downloaded certificate in is enough to recover from
// a rotation without a restart.
type CertDirectory struct {
	dir string

	mu      sync.RWMutex
	certs   map[string]*x509.Certificate
	loaded  time.Time
	refresh func(context.Context) error
}

// LoadCertDirectory reads every PEM certificate in dir.
func LoadCertDirectory(dir string) (*CertDirectory, error) {
	d := &CertDirectory{dir: dir}
	if err := d.Reload(context.Background()); err != nil {
		return nil, err
	}
	return d, nil
}

// SetRefresh installs the hook that downloads a fresh certificate set. It is
// called by Verify on an unknown serial before giving up, so a service that has
// wired it recovers from a rotation on its own. The download is not implemented
// here: it needs the APIv3 key and the merchant key in one place, which is
// main's job.
func (d *CertDirectory) SetRefresh(fn func(context.Context) error) {
	d.mu.Lock()
	defer d.mu.Unlock()
	d.refresh = fn
}

// Reload re-reads the directory.
func (d *CertDirectory) Reload(ctx context.Context) error {
	entries, err := os.ReadDir(d.dir)
	if err != nil {
		return fmt.Errorf("billing: platform certificate directory: %w", err)
	}
	certs := make(map[string]*x509.Certificate, len(entries))
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(strings.ToLower(e.Name()), ".pem") {
			continue
		}
		raw, err := os.ReadFile(filepath.Join(d.dir, e.Name()))
		if err != nil {
			return fmt.Errorf("billing: platform certificate %s: %w", e.Name(), err)
		}
		block, _ := pem.Decode(raw)
		if block == nil {
			return fmt.Errorf("billing: platform certificate %s is not PEM", e.Name())
		}
		cert, err := x509.ParseCertificate(block.Bytes)
		if err != nil {
			return fmt.Errorf("billing: platform certificate %s: %w", e.Name(), err)
		}
		certs[normaliseSerial(cert.SerialNumber)] = cert
	}
	if len(certs) == 0 {
		return fmt.Errorf("billing: no platform certificates in %s", d.dir)
	}

	d.mu.Lock()
	defer d.mu.Unlock()
	d.certs = certs
	d.loaded = time.Now()
	return nil
}

// Verify checks one APIv3 signature.
func (d *CertDirectory) Verify(ctx context.Context, serial string, message, signature []byte) error {
	key := normaliseSerialString(serial)
	d.mu.RLock()
	cert, ok := d.certs[key]
	refresh := d.refresh
	d.mu.RUnlock()

	if !ok {
		// One reload before giving up: a certificate dropped in since start-up
		// should not need a restart to be believed.
		if err := d.Reload(ctx); err == nil {
			d.mu.RLock()
			cert, ok = d.certs[key]
			d.mu.RUnlock()
		}
	}
	if !ok && refresh != nil {
		if err := refresh(ctx); err == nil {
			d.mu.RLock()
			cert, ok = d.certs[key]
			d.mu.RUnlock()
		}
	}
	if !ok {
		return fmt.Errorf("%w: %s", ErrPlatformCertUnknown, key)
	}

	now := time.Now()
	if now.After(cert.NotAfter) {
		return fmt.Errorf("%w: %s expired at %s", ErrPlatformCertExpired, key, cert.NotAfter.UTC().Format(time.RFC3339))
	}

	pub, ok := cert.PublicKey.(*rsa.PublicKey)
	if !ok {
		return fmt.Errorf("billing: platform certificate %s does not carry an RSA key", key)
	}
	digest := sha256.Sum256(message)
	if err := rsa.VerifyPKCS1v15(pub, crypto.SHA256, digest[:], signature); err != nil {
		return fmt.Errorf("billing: platform signature does not verify (cert %s): %w", key, err)
	}
	return nil
}

// ExpiringSoon lists the serials that lapse inside the window. A service should
// alert on it: the failure mode otherwise is that payments stop working.
func (d *CertDirectory) ExpiringSoon(now time.Time, within time.Duration) []string {
	d.mu.RLock()
	defer d.mu.RUnlock()
	var out []string
	for key, cert := range d.certs {
		if cert.NotAfter.Sub(now) <= within {
			out = append(out, key)
		}
	}
	return out
}

func normaliseSerial(n *big.Int) string { return normaliseSerialString(n.Text(16)) }

// normaliseSerial puts a serial in the form WeChat sends: uppercase hex without
// leading zeros. A certificate's own rendering and the header's disagree on both
// counts often enough that a lookup keyed on either alone misses.
func normaliseSerialString(s string) string {
	s = strings.ToUpper(strings.TrimSpace(s))
	s = strings.TrimPrefix(s, "0X")
	s = strings.TrimLeft(s, "0")
	if s == "" {
		return "0"
	}
	return s
}
