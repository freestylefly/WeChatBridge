package billing

import (
	"context"
	"crypto"
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/base64"
	"encoding/json"
	"encoding/pem"
	"io"
	"math/big"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/gin-gonic/gin"

	"github.com/freestylefly/WeChatBridge/server/internal/license"
	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

const (
	testMchID      = "1900000109"
	testAppID      = "wx1234567890abcdef"
	testAPIv3Key   = "0123456789abcdef0123456789abcdef"
	testMerchantNo = "SERIAL-1"
)

var fixedNow = time.Date(2026, 10, 18, 12, 0, 0, 0, time.UTC)

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

// wechatFake stands in for api.mch.weixin.qq.com.
type wechatFake struct {
	server   *httptest.Server
	requests []fakeOrderRequest
	bodies   [][]byte
	codeURLs []string
}

type fakeOrderRequest struct {
	OutTradeNo string `json:"out_trade_no"`
	Amount     struct {
		Total    int64  `json:"total"`
		Currency string `json:"currency"`
	} `json:"amount"`
	NotifyURL  string `json:"notify_url"`
	TimeExpire string `json:"time_expire"`
	Auth       string
}

func newWechatFake(t *testing.T) *wechatFake {
	t.Helper()
	w := &wechatFake{}
	mux := http.NewServeMux()
	mux.HandleFunc("/v3/pay/transactions/native", func(rw http.ResponseWriter, r *http.Request) {
		body, _ := io.ReadAll(r.Body)
		var req fakeOrderRequest
		if err := json.Unmarshal(body, &req); err != nil {
			rw.WriteHeader(http.StatusBadRequest)
			return
		}
		req.Auth = r.Header.Get("Authorization")
		w.requests = append(w.requests, req)
		w.bodies = append(w.bodies, append([]byte(nil), body...))
		rw.Header().Set("Content-Type", "application/json")
		_, _ = rw.Write([]byte(`{"code_url":"weixin://wxpay/bizpayurl?pr=` + base64.RawURLEncoding.EncodeToString([]byte(req.OutTradeNo)) + `"}`))
	})
	w.server = httptest.NewServer(mux)
	t.Cleanup(w.server.Close)
	return w
}

// platformCert makes the certificate WeChat's own would be: a self-signed RSA
// certificate on disk, which is exactly what CertDirectory is specified to read.
type platformCert struct {
	key      *rsa.PrivateKey
	serial   string
	dir      string
	notAfter time.Time
}

func newPlatformCert(t *testing.T) *platformCert {
	t.Helper()
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatalf("rsa key: %v", err)
	}
	template := &x509.Certificate{
		SerialNumber: big.NewInt(0x5157f09efdc096de),
		Subject:      pkix.Name{CommonName: "WeChatPay Platform"},
		NotBefore:    fixedNow.Add(-24 * time.Hour),
		NotAfter:     fixedNow.Add(20 * 24 * time.Hour),
	}
	der, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		t.Fatalf("certificate: %v", err)
	}
	dir := t.TempDir()
	path := filepath.Join(dir, "platform.pem")
	if err := os.WriteFile(path, pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}), 0o600); err != nil {
		t.Fatalf("write certificate: %v", err)
	}
	return &platformCert{
		key:      key,
		serial:   strings.ToUpper(strings.TrimLeft(template.SerialNumber.Text(16), "0")),
		dir:      dir,
		notAfter: template.NotAfter,
	}
}

func (p *platformCert) sign(t *testing.T, message []byte) string {
	t.Helper()
	digest := sha256.Sum256(message)
	sig, err := rsa.SignPKCS1v15(rand.Reader, p.key, crypto.SHA256, digest[:])
	if err != nil {
		t.Fatalf("sign: %v", err)
	}
	return base64.StdEncoding.EncodeToString(sig)
}

type fixture struct {
	svc     *Service
	store   *fakeStore
	wechat  *wechatFake
	plat    *platformCert
	verify  *CertDirectory
	signer  *license.Signer
	privKey *rsa.PrivateKey
}

func newFixture(t *testing.T) *fixture {
	t.Helper()
	st := newFakeStore()
	wechat := newWechatFake(t)
	plat := newPlatformCert(t)
	verifier, err := LoadCertDirectory(plat.dir)
	if err != nil {
		t.Fatalf("LoadCertDirectory: %v", err)
	}

	merchantKey, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatalf("merchant key: %v", err)
	}
	client, err := NewClient(ClientOptions{
		BaseURL:    wechat.server.URL,
		MchID:      testMchID,
		AppID:      testAppID,
		SerialNo:   testMerchantNo,
		PrivateKey: merchantKey,
		NotifyURL:  "https://api.example.com/v1/billing/notify",
		Clock:      func() time.Time { return fixedNow },
		Nonce:      func() (string, error) { return strings.Repeat("n", 32), nil },
		HTTPClient: wechat.server.Client(),
	})
	if err != nil {
		t.Fatalf("NewClient: %v", err)
	}

	licenceKey, err := license.GenerateKey()
	if err != nil {
		t.Fatalf("license key: %v", err)
	}
	signer, err := license.NewSigner(licenceKey)
	if err != nil {
		t.Fatalf("NewSigner: %v", err)
	}

	svc, err := NewService(st, st, signer, client, verifier, testAPIv3Key, Options{
		ProductID:       "windows-subscription",
		MonthlyFen:      3800,
		YearlyFen:       39800,
		OrderTTL:        30 * time.Minute,
		LicenseLifetime: 31 * 24 * time.Hour,
		MaxDevices:      3,
		ClockSkew:       48 * time.Hour,
		Entitlements:    []string{license.EntitlementPro},
		PublicBaseURL:   "https://api.example.com",
		Clock:           func() time.Time { return fixedNow },
	})
	if err != nil {
		t.Fatalf("NewService: %v", err)
	}
	return &fixture{svc: svc, store: st, wechat: wechat, plat: plat, verify: verifier, signer: signer, privKey: merchantKey}
}

// callback builds a signed, encrypted WeChat callback.
type callback struct {
	headers http.Header
	body    []byte
}

func (f *fixture) callback(t *testing.T, outTradeNo string, amountFen int64, transactionID string) callback {
	t.Helper()
	return f.callbackAt(t, outTradeNo, amountFen, transactionID, fixedNow)
}

func (f *fixture) callbackAt(t *testing.T, outTradeNo string, amountFen int64, transactionID string, at time.Time) callback {
	t.Helper()
	resource := map[string]any{
		"appid":            testAppID,
		"mchid":            testMchID,
		"out_trade_no":     outTradeNo,
		"transaction_id":   transactionID,
		"trade_state":      "SUCCESS",
		"trade_state_desc": "支付成功",
		"success_time":     at.Format(time.RFC3339),
		"amount": map[string]any{
			"total":          amountFen,
			"payer_total":    amountFen,
			"currency":       "CNY",
			"payer_currency": "CNY",
		},
	}
	plaintext, err := json.Marshal(resource)
	if err != nil {
		t.Fatal(err)
	}
	envelope := encryptResource(t, []byte(testAPIv3Key), plaintext)
	body, err := json.Marshal(map[string]any{
		"id":            "EV-TEST-0001",
		"create_time":   at.Format(time.RFC3339),
		"event_type":    "TRANSACTION.SUCCESS",
		"resource_type": "encrypt-resource",
		"summary":       "支付成功",
		"resource":      envelope,
	})
	if err != nil {
		t.Fatal(err)
	}
	return f.sign(t, body, at)
}

// sign adds the four signature headers over the exact body.
func (f *fixture) sign(t *testing.T, body []byte, at time.Time) callback {
	t.Helper()
	timestamp := strconv.FormatInt(at.Unix(), 10)
	nonce := "NONCE-0123456789abcdef"
	message := timestamp + "\n" + nonce + "\n" + string(body) + "\n"
	headers := http.Header{}
	headers.Set(HeaderTimestamp, timestamp)
	headers.Set(HeaderNonce, nonce)
	headers.Set(HeaderSerial, f.plat.serial)
	headers.Set(HeaderSignature, f.plat.sign(t, []byte(message)))
	return callback{headers: headers, body: body}
}

func encryptResource(t *testing.T, key, plaintext []byte) map[string]string {
	t.Helper()
	block, err := aes.NewCipher(key)
	if err != nil {
		t.Fatal(err)
	}
	gcm, err := cipher.NewGCM(block)
	if err != nil {
		t.Fatal(err)
	}
	nonce := []byte("123456789012")
	aad := []byte("transaction")
	sealed := gcm.Seal(nil, nonce, plaintext, aad)
	return map[string]string{
		"algorithm":       "AEAD_AES_256_GCM",
		"ciphertext":      base64.StdEncoding.EncodeToString(sealed),
		"associated_data": string(aad),
		"nonce":           string(nonce),
		"original_type":   "TRANSACTION.SUCCESS",
	}
}

// newPaidOrder drives the whole purchase up to a paid callback.
func (f *fixture) newPaidOrder(t *testing.T, accountID, clientRequestID string, period Period) store.Order {
	t.Helper()
	view, err := f.svc.CreateOrder(context.Background(), accountID, CreateOrderRequest{
		ClientRequestID: clientRequestID,
		ProductID:       "windows-subscription",
		Period:          period,
	})
	if err != nil {
		t.Fatalf("CreateOrder: %v", err)
	}
	order, err := f.store.GetOrder(context.Background(), view.OrderID)
	if err != nil {
		t.Fatalf("GetOrder: %v", err)
	}
	cb := f.callback(t, order.OutTradeNo, order.AmountFen, "42000012342026101"+order.ID[:6])
	outcome, _ := f.svc.HandleNotify(context.Background(), cb.headers, cb.body)
	if outcome.Status != http.StatusOK {
		t.Fatalf("callback answered %d, want 200", outcome.Status)
	}
	paid, err := f.store.GetOrder(context.Background(), order.ID)
	if err != nil {
		t.Fatalf("GetOrder after payment: %v", err)
	}
	return paid
}

// ---------------------------------------------------------------------------
// The callback
// ---------------------------------------------------------------------------

func TestACallbackMarksTheOrderPaidAndAnswersSuccess(t *testing.T) {
	f := newFixture(t)
	view, err := f.svc.CreateOrder(context.Background(), "acct-1", CreateOrderRequest{
		ClientRequestID: "cr-1", ProductID: "windows-subscription", Period: PeriodMonthly,
	})
	if err != nil {
		t.Fatalf("CreateOrder: %v", err)
	}
	order, _ := f.store.GetOrder(context.Background(), view.OrderID)
	if order.Status != store.OrderPending {
		t.Fatalf("status = %s, want pending", order.Status)
	}

	cb := f.callback(t, order.OutTradeNo, order.AmountFen, "txn-1")
	outcome, err := f.svc.HandleNotify(context.Background(), cb.headers, cb.body)
	if err != nil {
		t.Fatalf("HandleNotify: %v", err)
	}
	if outcome.Status != http.StatusOK {
		t.Fatalf("status = %d, want 200 — anything else and WeChat redelivers forever", outcome.Status)
	}
	if outcome.Body.Code != "SUCCESS" || outcome.Body.Message != "成功" {
		t.Errorf("body = %+v, want the exact success body", outcome.Body)
	}

	paid, _ := f.store.GetOrder(context.Background(), order.ID)
	if paid.Status != store.OrderPaid {
		t.Errorf("status = %s, want paid", paid.Status)
	}
	if paid.TransactionID != "txn-1" {
		t.Errorf("transaction id = %q", paid.TransactionID)
	}
	if paid.PaidAt == nil {
		t.Fatal("paidAt was not recorded")
	}
}

func TestACallbackWithABadSignatureMarksNothing(t *testing.T) {
	f := newFixture(t)
	order := f.newPendingOrder(t, "acct-1", "cr-1")

	cb := f.callback(t, order.OutTradeNo, order.AmountFen, "txn-1")
	// Sign a different body than the one delivered — a forgery, not a retry.
	// The callback helper is deterministic, so the body must be varied (here:
	// one extra fen inside the encrypted resource) or this is just a replay of
	// the identical delivery, which the service is right to accept.
	forged := f.callback(t, order.OutTradeNo, order.AmountFen+1, "txn-1")
	forged.headers.Set(HeaderSignature, cb.headers.Get(HeaderSignature))

	outcome, err := f.svc.HandleNotify(context.Background(), forged.headers, forged.body)
	if err == nil {
		t.Fatal("a forged callback was accepted")
	}
	if outcome.Status == http.StatusOK {
		t.Error("a forged callback was answered with success; WeChat would stop retrying a payment we never recorded")
	}
	assertStillPending(t, f, order.ID)
}

func TestACallbackWithATamperedAmountMarksNothing(t *testing.T) {
	f := newFixture(t)
	order := f.newPendingOrder(t, "acct-1", "cr-1")

	// Correctly signed, correctly decrypted, and the amount is one fen short.
	cb := f.callback(t, order.OutTradeNo, order.AmountFen-1, "txn-1")
	outcome, err := f.svc.HandleNotify(context.Background(), cb.headers, cb.body)
	if err == nil {
		t.Fatal("a callback for the wrong amount was accepted")
	}
	if outcome.Status == http.StatusOK {
		t.Error("an amount mismatch was answered with success")
	}
	assertStillPending(t, f, order.ID)
}

func TestACallbackForATradeNumberWeNeverIssuedMarksNothing(t *testing.T) {
	f := newFixture(t)
	cb := f.callback(t, "WCBNOTAREALTRADENUMBER0000000000", 3800, "txn-1")
	outcome, err := f.svc.HandleNotify(context.Background(), cb.headers, cb.body)
	if err == nil {
		t.Fatal("a callback for an unknown order was accepted")
	}
	if outcome.Status == http.StatusOK {
		t.Error("an unknown trade number was answered with success")
	}
}

// WeChat re-delivers until it gets a success body. Two deliveries, one licence —
// this is the difference between a payment and a customer's complaint.
func TestARedeliveredCallbackIssuesOneLicence(t *testing.T) {
	f := newFixture(t)
	order := f.newPaidOrder(t, "acct-1", "cr-1", PeriodMonthly)

	cb := f.callback(t, order.OutTradeNo, order.AmountFen, "txn-1")
	for i := 0; i < 3; i++ {
		outcome, err := f.svc.HandleNotify(context.Background(), cb.headers, cb.body)
		if err != nil {
			t.Fatalf("delivery %d: %v", i, err)
		}
		if outcome.Status != http.StatusOK || outcome.Body.Code != "SUCCESS" {
			t.Fatalf("delivery %d answered %d/%s; a redelivery must still be acknowledged",
				i, outcome.Status, outcome.Body.Code)
		}
	}
	if f.store.markPaidCalls != 1 {
		t.Errorf("MarkPaid was called %d times, want 1", f.store.markPaidCalls)
	}

	// And the licence, however many times it is collected, is one licence.
	first, err := f.svc.Redeem(context.Background(), "acct-1", order.ID, "install-A")
	if err != nil {
		t.Fatalf("Redeem: %v", err)
	}
	second, err := f.svc.Redeem(context.Background(), "acct-1", order.ID, "install-A")
	if err != nil {
		t.Fatalf("Redeem again: %v", err)
	}
	if first.LicenseToken != second.LicenseToken {
		t.Fatal("a second redeem returned different licence bytes; it re-signed")
	}
	if len(f.store.envelope) != 1 {
		t.Errorf("%d envelopes were stored for one order, want 1", len(f.store.envelope))
	}
}

func TestACallbackOutsideTheClockWindowIsRefused(t *testing.T) {
	f := newFixture(t)
	order := f.newPendingOrder(t, "acct-1", "cr-1")
	cb := f.callbackAt(t, order.OutTradeNo, order.AmountFen, "txn-1", fixedNow.Add(-time.Hour))
	if _, err := f.svc.HandleNotify(context.Background(), cb.headers, cb.body); err == nil {
		t.Fatal("a stale callback was accepted")
	}
	assertStillPending(t, f, order.ID)
}

func TestACallbackWithTheWrongAPIv3KeyCannotDecrypt(t *testing.T) {
	f := newFixture(t)
	order := f.newPendingOrder(t, "acct-1", "cr-1")

	// A body encrypted under a different key but otherwise valid: decryption must
	// fail after the signature passes.
	other := encryptResource(t, []byte("ffffffffffffffffffffffffffffffff"), []byte(`{"out_trade_no":"x"}`))
	body, _ := json.Marshal(map[string]any{
		"id": "EV-2", "event_type": "TRANSACTION.SUCCESS", "resource_type": "encrypt-resource",
		"resource": other,
	})
	cb := f.sign(t, body, fixedNow)
	if _, err := f.svc.HandleNotify(context.Background(), cb.headers, cb.body); err == nil {
		t.Fatal("a body encrypted under another key was accepted")
	}
	assertStillPending(t, f, order.ID)
}

func TestThePlatformVerifierReportsExpiryBeforeItBreaks(t *testing.T) {
	f := newFixture(t)
	soon := f.verify.ExpiringSoon(fixedNow, 30*24*time.Hour)
	if len(soon) != 1 {
		t.Fatalf("a certificate expiring in 20 days is not on the 30-day list: %v", soon)
	}
	if none := f.verify.ExpiringSoon(fixedNow, 24*time.Hour); len(none) != 0 {
		t.Errorf("a certificate expiring in 20 days is on the 24-hour list: %v", none)
	}
}

func TestAnUnknownPlatformSerialIsRefusedNotIgnored(t *testing.T) {
	f := newFixture(t)
	cb := f.callback(t, "WCBX", 100, "txn-1")
	cb.headers.Set(HeaderSerial, "DEADBEEF")
	_, err := f.svc.HandleNotify(context.Background(), cb.headers, cb.body)
	if err == nil {
		t.Fatal("an unknown platform serial verified")
	}
}

// ---------------------------------------------------------------------------
// Orders
// ---------------------------------------------------------------------------

func (f *fixture) newPendingOrder(t *testing.T, accountID, clientRequestID string) store.Order {
	t.Helper()
	view, err := f.svc.CreateOrder(context.Background(), accountID, CreateOrderRequest{
		ClientRequestID: clientRequestID, ProductID: "windows-subscription", Period: PeriodMonthly,
	})
	if err != nil {
		t.Fatalf("CreateOrder: %v", err)
	}
	order, err := f.store.GetOrder(context.Background(), view.OrderID)
	if err != nil {
		t.Fatalf("GetOrder: %v", err)
	}
	return order
}

func assertStillPending(t *testing.T, f *fixture, orderID store.ID) {
	t.Helper()
	o, err := f.store.GetOrder(context.Background(), orderID)
	if err != nil {
		t.Fatalf("GetOrder: %v", err)
	}
	if o.Status != store.OrderPending {
		t.Errorf("order %s became %s; a rejected callback must change nothing", orderID, o.Status)
	}
}

func TestTheSameClientRequestIDReturnsTheSameOrder(t *testing.T) {
	f := newFixture(t)
	req := CreateOrderRequest{ClientRequestID: "cr-1", ProductID: "windows-subscription", Period: PeriodMonthly}

	first, err := f.svc.CreateOrder(context.Background(), "acct-1", req)
	if err != nil {
		t.Fatalf("first: %v", err)
	}
	second, err := f.svc.CreateOrder(context.Background(), "acct-1", req)
	if err != nil {
		t.Fatalf("second: %v", err)
	}

	if first.OrderID != second.OrderID {
		t.Errorf("two orders for one purchase attempt: %q and %q", first.OrderID, second.OrderID)
	}
	if first.HTTPStatus != http.StatusCreated || second.HTTPStatus != http.StatusOK {
		t.Errorf("statuses = %d then %d, want 201 then 200", first.HTTPStatus, second.HTTPStatus)
	}
	if f.store.orderCount() != 1 {
		t.Errorf("the store holds %d orders, want 1", f.store.orderCount())
	}
}

// out_trade_no is what WeChat enforces uniqueness on, and the derivation is what
// makes it an idempotency key rather than a random string.
func TestOutTradeNoIsUniquePerAttemptAndFitsTheLimit(t *testing.T) {
	a := OutTradeNo("acct-1", "cr-1")
	b := OutTradeNo("acct-1", "cr-2")
	c := OutTradeNo("acct-2", "cr-1")

	if len(a) > MaxOutTradeNoLength {
		t.Errorf("out_trade_no is %d characters, WeChat's limit is %d", len(a), MaxOutTradeNoLength)
	}
	if a == b {
		t.Error("two request ids produced one trade number")
	}
	if a == c {
		t.Error("two accounts produced one trade number")
	}
	if a != OutTradeNo("acct-1", "cr-1") {
		t.Error("the derivation is not deterministic")
	}
	if strings.ContainsAny(a, "abcdef") {
		t.Errorf("out_trade_no %q should be uppercase hex after the prefix", a)
	}
}

func TestTwoDevicesOfOneAccountShareTheOrder(t *testing.T) {
	f := newFixture(t)
	first, err := f.svc.CreateOrder(context.Background(), "acct-1", CreateOrderRequest{
		ClientRequestID: "cr-from-laptop", ProductID: "windows-subscription", Period: PeriodMonthly,
	})
	if err != nil {
		t.Fatalf("laptop: %v", err)
	}
	second, err := f.svc.CreateOrder(context.Background(), "acct-1", CreateOrderRequest{
		ClientRequestID: "cr-from-desktop", ProductID: "windows-subscription", Period: PeriodMonthly,
	})
	if err != nil {
		t.Fatalf("desktop: %v", err)
	}

	// Not a conflict: both machines are the same person's, and a 409 here is the
	// one error in this flow the customer can do nothing about.
	if second.OrderID != first.OrderID {
		t.Fatalf("the second device got its own order (%q, %q)", first.OrderID, second.OrderID)
	}
	if second.HTTPStatus != http.StatusOK {
		t.Errorf("status = %d, want 200 for an existing order", second.HTTPStatus)
	}
	if len(f.wechat.requests) != 1 {
		t.Errorf("WeChat was called %d times, want 1", len(f.wechat.requests))
	}
}

func TestTheOrderRequestWeSendWeChat(t *testing.T) {
	f := newFixture(t)
	order := f.newPendingOrder(t, "acct-1", "cr-1")

	if len(f.wechat.requests) != 1 {
		t.Fatalf("WeChat saw %d requests", len(f.wechat.requests))
	}
	req := f.wechat.requests[0]
	if req.OutTradeNo != order.OutTradeNo {
		t.Errorf("out_trade_no = %q, want %q", req.OutTradeNo, order.OutTradeNo)
	}
	if req.Amount.Total != 3800 {
		t.Errorf("amount = %d, want 3800 fen", req.Amount.Total)
	}
	if req.Amount.Currency != "CNY" {
		t.Errorf("currency = %q", req.Amount.Currency)
	}
	if req.NotifyURL == "" || !strings.HasPrefix(req.NotifyURL, "https://") {
		t.Errorf("notify_url = %q", req.NotifyURL)
	}
	// The Authorization header is the contract WeChat verifies before it does
	// anything at all.
	auth := req.Auth
	for _, part := range []string{
		AuthorizationScheme,
		`mchid="` + testMchID + `"`,
		`serial_no="` + testMerchantNo + `"`,
		`signature="`,
		`timestamp="`,
		`nonce_str="`,
	} {
		if !strings.Contains(auth, part) {
			t.Errorf("Authorization %q is missing %q", auth, part)
		}
	}
}

func TestTheMerchantSignatureActuallyVerifies(t *testing.T) {
	f := newFixture(t)
	f.newPendingOrder(t, "acct-1", "cr-1")

	auth := f.wechat.requests[0].Auth
	fields := parseAuthorization(t, auth)
	timestamp := fields["timestamp"]
	nonce := fields["nonce_str"]
	signature := fields["signature"]

	// The signed message is the exact method, path, timestamp, nonce and body.
	// Rebuilding it here — rather than trusting the header — is the test.
	body := f.wechat.bodies[0]
	message := http.MethodPost + "\n" + nativePath + "\n" + timestamp + "\n" + nonce + "\n" + string(body) + "\n"
	digest := sha256.Sum256([]byte(message))
	sig, err := base64.StdEncoding.DecodeString(signature)
	if err != nil {
		t.Fatalf("signature is not base64: %v", err)
	}
	pub, ok := f.privKey.Public().(*rsa.PublicKey)
	if !ok {
		t.Fatal("not an RSA key")
	}
	if err := rsa.VerifyPKCS1v15(pub, crypto.SHA256, digest[:], sig); err != nil {
		t.Fatalf("the merchant signature does not verify: %v", err)
	}
}

func TestRedeemRefusesAnUnpaidOrder(t *testing.T) {
	f := newFixture(t)
	order := f.newPendingOrder(t, "acct-1", "cr-1")
	if _, err := f.svc.Redeem(context.Background(), "acct-1", order.ID, "install-A"); err == nil {
		t.Fatal("an unpaid order produced a licence")
	}
}

func TestRedeemIsIdempotentAndBindsTheMachine(t *testing.T) {
	f := newFixture(t)
	order := f.newPaidOrder(t, "acct-1", "cr-1", PeriodYearly)

	first, err := f.svc.Redeem(context.Background(), "acct-1", order.ID, "install-A")
	if err != nil {
		t.Fatalf("Redeem: %v", err)
	}
	if first.LicenseKey == "" || first.LicenseID == "" {
		t.Errorf("redeem returned %+v, want both ids", first)
	}

	raw, err := base64.RawURLEncoding.DecodeString(first.LicenseToken)
	if err != nil {
		t.Fatalf("the token is not base64url: %v", err)
	}
	env, err := license.Parse(raw)
	if err != nil {
		t.Fatalf("the token does not carry an envelope: %v", err)
	}
	if env.Plan != license.PlanSubscription {
		t.Errorf("plan = %q", env.Plan)
	}
	if env.ExpiresAt == nil {
		t.Error("a subscription licence must expire; that is the whole product")
	}

	// A second device of the same account gets the same licence, which is what
	// makes "both machines have a working authorisation" true.
	second, err := f.svc.Redeem(context.Background(), "acct-1", order.ID, "install-B")
	if err != nil {
		t.Fatalf("Redeem from the second machine: %v", err)
	}
	if second.LicenseToken != first.LicenseToken {
		t.Error("the second machine got different bytes for the same order")
	}

	devices, _ := f.store.ListDevices(context.Background(), order.ID)
	if len(devices) != 2 {
		t.Errorf("%d devices bound, want 2", len(devices))
	}
}

func TestAnotherAccountsOrderIsInvisible(t *testing.T) {
	f := newFixture(t)
	order := f.newPaidOrder(t, "acct-1", "cr-1", PeriodMonthly)
	if _, err := f.svc.Redeem(context.Background(), "acct-2", order.ID, "install-X"); err == nil {
		t.Fatal("another account redeemed somebody's order")
	}
}

func TestTheQREndpointServesPNG(t *testing.T) {
	f := newFixture(t)
	order := f.newPendingOrder(t, "acct-1", "cr-1")

	png, err := f.svc.QRPNG(context.Background(), order.ID)
	if err != nil {
		t.Fatalf("QRPNG: %v", err)
	}
	// The client checks the magic bytes and refuses anything over 4 MB.
	if len(png) < 8 || string(png[1:4]) != "PNG" {
		t.Fatalf("the body is not a PNG: % x", png[:8])
	}
	if len(png) > 4<<20 {
		t.Errorf("the PNG is %d bytes; the client refuses anything over 4 MB", len(png))
	}
}

// ---------------------------------------------------------------------------
// HTTP surface
// ---------------------------------------------------------------------------

func TestTheBillingRoutes(t *testing.T) {
	f := newFixture(t)
	gin.SetMode(gin.TestMode)
	r := gin.New()
	f.svc.Register(r, principalFunc(func(*gin.Context) (string, bool) { return "acct-1", true }))

	body, _ := json.Marshal(CreateOrderRequest{
		ClientRequestID: "cr-http", ProductID: "windows-subscription", Period: PeriodMonthly,
	})
	rec := httptest.NewRecorder()
	req := httptest.NewRequest(http.MethodPost, "/v1/billing/orders", strings.NewReader(string(body)))
	req.Header.Set("Content-Type", "application/json")
	r.ServeHTTP(rec, req)
	if rec.Code != http.StatusCreated {
		t.Fatalf("status = %d, body %s", rec.Code, rec.Body.String())
	}
	var view OrderView
	if err := json.Unmarshal(rec.Body.Bytes(), &view); err != nil {
		t.Fatalf("response shape: %v", err)
	}
	if view.QRImageURL == "" || !strings.HasSuffix(view.QRImageURL, "/qr.png") {
		t.Errorf("qrImageUrl = %q", view.QRImageURL)
	}
	if view.AmountFen != 3800 {
		t.Errorf("amount = %d fen", view.AmountFen)
	}
	if view.ExpiresAt != "2026-10-18T12:30:00Z" {
		t.Errorf("expiresAt = %q", view.ExpiresAt)
	}

	// The QR is served without a session: the client must not put a bearer token
	// on a URL that might be a CDN.
	qr := httptest.NewRecorder()
	r.ServeHTTP(qr, httptest.NewRequest(http.MethodGet, "/v1/billing/orders/"+view.OrderID+"/qr.png", nil))
	if qr.Code != http.StatusOK || qr.Header().Get("Content-Type") != "image/png" {
		t.Errorf("qr: status %d, type %q", qr.Code, qr.Header().Get("Content-Type"))
	}

	// Without a session, everything else is 401.
	denied := gin.New()
	f.svc.Register(denied, nil)
	drec := httptest.NewRecorder()
	dreq := httptest.NewRequest(http.MethodPost, "/v1/billing/orders", strings.NewReader(string(body)))
	dreq.Header.Set("Content-Type", "application/json")
	denied.ServeHTTP(drec, dreq)
	if drec.Code != http.StatusUnauthorized {
		t.Errorf("unauthenticated order creation answered %d, want 401", drec.Code)
	}
}

func TestTheNotifyRouteAnswersWeChatNotOurOwnErrorShape(t *testing.T) {
	f := newFixture(t)
	order := f.newPendingOrder(t, "acct-1", "cr-1")
	cb := f.callback(t, order.OutTradeNo, order.AmountFen, "txn-1")

	gin.SetMode(gin.TestMode)
	r := gin.New()
	f.svc.Register(r, DenyAll)

	req := httptest.NewRequest(http.MethodPost, "/v1/billing/notify", strings.NewReader(string(cb.body)))
	for k, v := range cb.headers {
		req.Header[k] = v
	}
	rec := httptest.NewRecorder()
	r.ServeHTTP(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, body %s", rec.Code, rec.Body.String())
	}
	var got NotifyResult
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
		t.Fatalf("body is not the WeChat shape: %v", err)
	}
	if got.Code != "SUCCESS" || got.Message != "成功" {
		t.Errorf("body = %+v", got)
	}
}

// ---------------------------------------------------------------------------
// helpers
// ---------------------------------------------------------------------------

func parseAuthorization(t *testing.T, header string) map[string]string {
	t.Helper()
	fields := map[string]string{}
	rest := strings.TrimPrefix(header, AuthorizationScheme+" ")
	for _, part := range strings.Split(rest, ",") {
		kv := strings.SplitN(strings.TrimSpace(part), "=", 2)
		if len(kv) == 2 {
			fields[kv[0]] = strings.Trim(kv[1], `"`)
		}
	}
	return fields
}
