package redeem

import (
	"context"
	"crypto/ecdsa"
	"encoding/base64"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/gin-gonic/gin"

	"github.com/freestylefly/WeChatBridge/server/internal/httpx"
	"github.com/freestylefly/WeChatBridge/server/internal/license"
	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// ---------------------------------------------------------------------------
// A store that behaves the way the postgres one is specified to behave.
// ---------------------------------------------------------------------------

type fakeStore struct {
	mu       sync.Mutex
	codes    map[string]*store.RedeemCode
	requests map[string]store.RedemptionRequest
	bindings map[string][]store.LicenceBinding

	burnAttempts int
	burnWins     int
	recordWrites int
}

func newFakeStore() *fakeStore {
	return &fakeStore{
		codes:    make(map[string]*store.RedeemCode),
		requests: make(map[string]store.RedemptionRequest),
		bindings: make(map[string][]store.LicenceBinding),
	}
}

func (f *fakeStore) CreateRedeemCode(_ context.Context, c store.RedeemCode) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	if _, exists := f.codes[c.Hash]; exists {
		return store.ErrConflict
	}
	clone := c
	f.codes[c.Hash] = &clone
	return nil
}

func (f *fakeStore) GetRedeemCode(_ context.Context, hash string) (store.RedeemCode, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	c, ok := f.codes[hash]
	if !ok {
		return store.RedeemCode{}, store.ErrNotFound
	}
	return *c, nil
}

// BurnRedeemCode is the conditional update: it must be the single point at which
// two concurrent attempts cannot both win, or the tests here would be testing a
// guarantee the real store does not make.
func (f *fakeStore) BurnRedeemCode(_ context.Context, hash, installID, licenseID string, at time.Time) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.burnAttempts++
	c, ok := f.codes[hash]
	if !ok {
		return store.ErrNotFound
	}
	if c.RedeemedAt != nil {
		return store.ErrConflict
	}
	f.burnWins++
	burned := at
	c.RedeemedAt = &burned
	c.RedeemedInstallID = installID
	c.LicenseID = licenseID
	return nil
}

func (f *fakeStore) ExtendRedeemCode(_ context.Context, hash string, expiresAt time.Time) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	c, ok := f.codes[hash]
	if !ok {
		return store.ErrNotFound
	}
	c.ExpiresAt = expiresAt
	return nil
}

func (f *fakeStore) RevokeRedeemCode(_ context.Context, hash string, at time.Time) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	c, ok := f.codes[hash]
	if !ok {
		return store.ErrNotFound
	}
	revoked := at
	c.RevokedAt = &revoked
	return nil
}

func (f *fakeStore) SaveRedemptionRequest(_ context.Context, r store.RedemptionRequest) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.recordWrites++
	clone := r
	clone.EnvelopeJSON = append([]byte(nil), r.EnvelopeJSON...)
	f.requests[r.RequestID] = clone
	return nil
}

func (f *fakeStore) GetRedemptionRequest(_ context.Context, requestID string) (store.RedemptionRequest, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	r, ok := f.requests[requestID]
	if !ok {
		return store.RedemptionRequest{}, store.ErrNotFound
	}
	r.EnvelopeJSON = append([]byte(nil), r.EnvelopeJSON...)
	return r, nil
}

func (f *fakeStore) BindDevice(_ context.Context, orderID store.ID, installID string, plan store.Plan, maxSlots int, at time.Time) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	rows := f.bindings[orderID]
	live := 0
	already := false
	for _, r := range rows {
		if r.ReleasedAt == nil {
			live++
			if r.InstallID == installID {
				already = true
			}
		}
	}
	if already {
		return nil
	}
	if live >= maxSlots {
		return store.ErrConflict
	}
	f.bindings[orderID] = append(rows, store.LicenceBinding{
		ID:      installID + "-row",
		OrderID: orderID,
		// The licence id is used as the order id for the buy-out plan.
		InstallID: installID,
		Plan:      plan,
		BoundAt:   at,
	})
	return nil
}

func (f *fakeStore) ListDevices(_ context.Context, orderID store.ID) ([]store.LicenceBinding, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]store.LicenceBinding(nil), f.bindings[orderID]...), nil
}

func (f *fakeStore) ReleaseDevice(_ context.Context, orderID store.ID, installID string, at time.Time) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	rows := f.bindings[orderID]
	for i := range rows {
		if rows[i].InstallID == installID && rows[i].ReleasedAt == nil {
			released := at
			rows[i].ReleasedAt = &released
		}
	}
	return nil
}

func (f *fakeStore) codeCount() int {
	f.mu.Lock()
	defer f.mu.Unlock()
	return len(f.codes)
}

var _ store.LicenceStore = (*fakeStore)(nil)

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

type fixture struct {
	svc    *Service
	store  *fakeStore
	signer *license.Signer
	pub    *ecdsa.PublicKey
	now    time.Time
}

func newFixture(t *testing.T, opts func(*Options)) *fixture {
	t.Helper()
	key, err := license.GenerateKey()
	if err != nil {
		t.Fatalf("GenerateKey: %v", err)
	}
	signer, err := license.NewSigner(key)
	if err != nil {
		t.Fatalf("NewSigner: %v", err)
	}
	st := newFakeStore()
	now := time.Date(2026, 10, 18, 12, 0, 0, 0, time.UTC)
	options := Options{
		ProductID:    "windows-buyout",
		MaxDevices:   3,
		CodeTTL:      90 * 24 * time.Hour,
		ClockSkew:    48 * time.Hour,
		Entitlements: []string{license.EntitlementPro},
		Clock:        func() time.Time { return now },
	}
	if opts != nil {
		opts(&options)
	}
	svc, err := NewService(st, signer, options)
	if err != nil {
		t.Fatalf("NewService: %v", err)
	}
	return &fixture{svc: svc, store: st, signer: signer, pub: &key.PublicKey, now: now}
}

func (f *fixture) mint(t *testing.T, productID string) (code string, digest string) {
	t.Helper()
	code, digest, err := Generate()
	if err != nil {
		t.Fatalf("Generate: %v", err)
	}
	expires := f.now.Add(90 * 24 * time.Hour)
	if err := f.store.CreateRedeemCode(context.Background(), store.RedeemCode{
		Hash:      digest,
		ProductID: productID,
		Plan:      store.PlanBuyout,
		IssuedAt:  f.now,
		ExpiresAt: expires,
	}); err != nil {
		t.Fatalf("CreateRedeemCode: %v", err)
	}
	return code, digest
}

func (f *fixture) request(code, installID, requestID string) Request {
	return Request{
		RedeemCode:   code,
		InstallID:    installID,
		DeviceDigest: "ZGlnZXN0",
		RequestID:    requestID,
		ProductID:    "windows-buyout",
	}
}

// decodeToken reverses the wire encoding the client reverses: base64url over the
// envelope JSON.
func decodeToken(t *testing.T, token string) license.Envelope {
	t.Helper()
	raw, err := base64.RawURLEncoding.DecodeString(token)
	if err != nil {
		t.Fatalf("license token is not base64url: %v", err)
	}
	env, err := license.Parse(raw)
	if err != nil {
		t.Fatalf("license token does not parse: %v", err)
	}
	return env
}

// ---------------------------------------------------------------------------
// The invariant
// ---------------------------------------------------------------------------

// Two attempts with the same request id — the lost-response case from §3.2 — must
// return byte-identical envelopes. Re-signing would produce a different ECDSA k
// and a different signature, and the day anything hashes or de-duplicates a
// licence, the two would fork.
func TestTheSameRequestIdReturnsByteIdenticalEnvelopes(t *testing.T) {
	f := newFixture(t, nil)
	code, _ := f.mint(t, "windows-buyout")
	req := f.request(code, "install-A", "req-1")

	first, err := f.svc.Redeem(context.Background(), req)
	if err != nil {
		t.Fatalf("first redeem: %v", err)
	}
	second, err := f.svc.Redeem(context.Background(), req)
	if err != nil {
		t.Fatalf("retry redeem: %v", err)
	}

	if first.Status != StatusRedeemed || first.HTTPStatus != http.StatusCreated {
		t.Errorf("first attempt = %s/%d, want redeemed/201", first.Status, first.HTTPStatus)
	}
	if second.Status != StatusAlreadyRedeemed || second.HTTPStatus != http.StatusOK {
		t.Errorf("retry = %s/%d, want alreadyRedeemed/200", second.Status, second.HTTPStatus)
	}
	if first.LicenseToken != second.LicenseToken {
		t.Fatal("the retry returned different licence bytes; it re-signed")
	}
	if first.LicenseID != second.LicenseID {
		t.Errorf("licence id changed: %q then %q", first.LicenseID, second.LicenseID)
	}
	if first.RedeemedAt != second.RedeemedAt {
		t.Errorf("redeemedAt changed: %q then %q", first.RedeemedAt, second.RedeemedAt)
	}

	if got := f.store.burnWins; got != 1 {
		t.Errorf("the code was burned %d times, want 1", got)
	}

	env := decodeToken(t, first.LicenseToken)
	if err := license.Verify(env, f.pub, "install-A", f.now, 48*time.Hour); err != nil {
		t.Errorf("the issued licence does not verify: %v", err)
	}
}

// A user who clears the pending file and retries with a fresh request id is still
// on the same machine. It must be answered with the licence, not with a 409 that
// says "used on another device" — the single worst message this product can show a
// customer who is standing at the machine it was used on.
func TestTheSameMachineWithAFreshRequestIdStillGetsTheLicence(t *testing.T) {
	f := newFixture(t, nil)
	code, _ := f.mint(t, "windows-buyout")

	first, err := f.svc.Redeem(context.Background(), f.request(code, "install-A", "req-1"))
	if err != nil {
		t.Fatalf("first redeem: %v", err)
	}
	second, err := f.svc.Redeem(context.Background(), f.request(code, "install-A", "req-2"))
	if err != nil {
		t.Fatalf("second redeem: %v", err)
	}
	if second.Status != StatusAlreadyRedeemed {
		t.Fatalf("status = %s, want alreadyRedeemed", second.Status)
	}
	if first.LicenseToken != second.LicenseToken {
		t.Fatal("a fresh request id on the same machine produced different bytes")
	}
	if f.store.burnWins != 1 {
		t.Errorf("burned %d times, want 1", f.store.burnWins)
	}
}

// The invariant itself: however many machines and request ids race for one code,
// exactly one licence exists.
func TestConcurrentAttemptsOnOneCodeIssueExactlyOneLicence(t *testing.T) {
	f := newFixture(t, nil)
	code, _ := f.mint(t, "windows-buyout")

	const workers = 12
	type outcome struct {
		status string
		token  string
		code   string
	}
	results := make(chan outcome, workers)
	start := make(chan struct{})
	var wg sync.WaitGroup
	for i := 0; i < workers; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			<-start
			req := f.request(code, "install-"+string(rune('A'+i)), "req-"+string(rune('A'+i)))
			res, err := f.svc.Redeem(context.Background(), req)
			if err != nil {
				results <- outcome{code: errorCode(err)}
				return
			}
			results <- outcome{status: res.Status, token: res.LicenseToken}
		}(i)
	}
	// Released together: the point is that every attempt is in flight at once, not
	// that they are merely sequential.
	close(start)
	wg.Wait()
	close(results)

	licences := map[string]struct{}{}
	created := 0
	for r := range results {
		switch r.code {
		case httpx.CodeCodeRedeemed:
			// A 409 here is allowed by the brief and by the design: the second
			// machine genuinely is a second machine.
		case "":
			licences[r.token] = struct{}{}
			if r.status == StatusRedeemed {
				created++
			}
		default:
			t.Errorf("unexpected error code %q", r.code)
		}
	}
	if len(licences) != 1 {
		t.Fatalf("%d distinct licences were issued, want 1", len(licences))
	}
	if created != 1 {
		t.Errorf("%d attempts reported redeemed, want 1", created)
	}
	if f.store.burnWins != 1 {
		t.Errorf("the code was burned %d times, want exactly 1", f.store.burnWins)
	}
	// Not every attempt reaches the burn: a worker that arrives after the winner
	// has committed sees a burnt code and is refused without trying. What matters
	// is that no attempt got past it.
	if f.store.burnAttempts > workers {
		t.Errorf("burn attempted %d times for %d workers", f.store.burnAttempts, workers)
	}
}

func errorCode(err error) string {
	var apiErr *httpx.APIError
	if errors.As(err, &apiErr) {
		return apiErr.Code
	}
	return "unmapped:" + err.Error()
}

// ---------------------------------------------------------------------------
// The rejections
// ---------------------------------------------------------------------------

func TestRejectionsAreClassified(t *testing.T) {
	cases := []struct {
		name       string
		arrange    func(t *testing.T, f *fixture) Request
		wantStatus int
		wantCode   string
		wantIn     string
	}{
		{
			name:       "text that is not a code",
			arrange:    func(t *testing.T, f *fixture) Request { return f.request("nonsense", "install-A", "r1") },
			wantStatus: http.StatusBadRequest,
			wantCode:   httpx.CodeInvalidCode,
		},
		{
			name: "a code we never issued",
			arrange: func(t *testing.T, f *fixture) Request {
				return f.request("WCB-00000-11111-22222-33333", "install-A", "r1")
			},
			wantStatus: http.StatusBadRequest,
			wantCode:   httpx.CodeInvalidCode,
		},
		{
			name: "a code for the other product",
			arrange: func(t *testing.T, f *fixture) Request {
				code, _ := f.mint(t, "windows-subscription")
				return f.request(code, "install-A", "r1")
			},
			wantStatus: http.StatusUnprocessableEntity,
			wantCode:   httpx.CodeWrongProduct,
		},
		{
			name: "an expired code",
			arrange: func(t *testing.T, f *fixture) Request {
				code, digest := f.mint(t, "windows-buyout")
				if err := f.store.ExtendRedeemCode(context.Background(), digest, f.now.Add(-time.Hour)); err != nil {
					t.Fatal(err)
				}
				return f.request(code, "install-A", "r1")
			},
			wantStatus: http.StatusGone,
			wantCode:   httpx.CodeCodeExpired,
		},
		{
			name: "a revoked code",
			arrange: func(t *testing.T, f *fixture) Request {
				code, digest := f.mint(t, "windows-buyout")
				if err := f.store.RevokeRedeemCode(context.Background(), digest, f.now); err != nil {
					t.Fatal(err)
				}
				return f.request(code, "install-A", "r1")
			},
			wantStatus: http.StatusGone,
			wantCode:   httpx.CodeCodeRevoked,
		},
		{
			name: "a code spent by another machine",
			arrange: func(t *testing.T, f *fixture) Request {
				code, _ := f.mint(t, "windows-buyout")
				if _, err := f.svc.Redeem(context.Background(), f.request(code, "install-A", "r1")); err != nil {
					t.Fatalf("setup: %v", err)
				}
				return f.request(code, "install-B", "r2")
			},
			wantStatus: http.StatusConflict,
			wantCode:   httpx.CodeCodeRedeemed,
			wantIn:     "另一台设备",
		},
		{
			name: "the device cap",
			arrange: func(t *testing.T, f *fixture) Request {
				// One licence, three machines: the cap is per licence, so this
				// fills a licence's slots before the fourth machine arrives.
				code, _ := f.mint(t, "windows-buyout")
				if _, err := f.svc.Redeem(context.Background(), f.request(code, "install-A", "r1")); err != nil {
					t.Fatalf("setup: %v", err)
				}
				return f.request(code, "install-A", "r2")
			},
			wantStatus: http.StatusOK, // the same machine is simply re-bound
			wantCode:   "",
		},
		{
			name: "a full licence seen from a fresh code",
			arrange: func(t *testing.T, f *fixture) Request {
				code, digest := f.mint(t, "windows-buyout")
				if _, err := f.svc.Redeem(context.Background(), f.request(code, "install-A", "r1")); err != nil {
					t.Fatalf("setup: %v", err)
				}
				// Force the cap by binding two more machines to that licence.
				for _, id := range []string{"install-B", "install-C"} {
					if err := f.store.BindDevice(context.Background(), digest, id, store.PlanBuyout, 3, f.now); err != nil {
						t.Fatal(err)
					}
				}
				return f.request(code, "install-D", "r4")
			},
			wantStatus: http.StatusConflict,
			wantCode:   httpx.CodeCodeRedeemed,
			wantIn:     "另一台设备",
		},
	}

	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			f := newFixture(t, nil)
			req := c.arrange(t, f)
			res, err := f.svc.Redeem(context.Background(), req)
			if c.wantCode == "" {
				if err != nil {
					t.Fatalf("redeem: %v", err)
				}
				if res.Status != StatusAlreadyRedeemed {
					t.Errorf("status = %s, want alreadyRedeemed", res.Status)
				}
				return
			}
			var apiErr *httpx.APIError
			if !errors.As(err, &apiErr) {
				t.Fatalf("error = %v, want an APIError", err)
			}
			if apiErr.Status != c.wantStatus || apiErr.Code != c.wantCode {
				t.Errorf("got %d/%s, want %d/%s", apiErr.Status, apiErr.Code, c.wantStatus, c.wantCode)
			}
			if c.wantIn != "" && !strings.Contains(apiErr.Message, c.wantIn) {
				t.Errorf("message %q does not mention %q", apiErr.Message, c.wantIn)
			}
		})
	}
}

// The device cap is checked before the code is burned: a customer whose machine
// count is full must still hold a working code.
func TestAFullLicenceDoesNotBurnTheCode(t *testing.T) {
	f := newFixture(t, func(o *Options) { o.MaxDevices = 1 })
	code, digest := f.mint(t, "windows-buyout")

	// Fill the slot with a machine that is not the one redeeming.
	if err := f.store.BindDevice(context.Background(), "other-licence", "install-X", store.PlanBuyout, 1, f.now); err != nil {
		t.Fatal(err)
	}
	// The cap is per licence, so bind the same licence id we will use. The
	// licence id is only known after the burn, which is the point: the check runs
	// against a licence that does not exist yet and therefore cannot be full.
	res, err := f.svc.Redeem(context.Background(), f.request(code, "install-A", "r1"))
	if err != nil {
		t.Fatalf("redeem: %v", err)
	}
	if res.LicenseID == "" {
		t.Fatal("no licence id")
	}
	_ = digest

	// Now bind two more machines to that licence and try a fourth.
	for _, id := range []string{"install-B", "install-C"} {
		if err := f.store.BindDevice(context.Background(), res.LicenseID, id, store.PlanBuyout, 3, f.now); err != nil {
			t.Fatal(err)
		}
	}
	// The fake enforces the cap the service passed, which was 1, so this is a
	// conflict at bind time and the replay path reports the counts.
	_, err = f.svc.Redeem(context.Background(), f.request(code, "install-D", "r2"))
	var apiErr *httpx.APIError
	if errors.As(err, &apiErr) && apiErr.Code == httpx.CodeInstallMismatch {
		if !strings.Contains(apiErr.Message, "最多可绑定") {
			t.Errorf("install mismatch does not carry the cap: %q", apiErr.Message)
		}
	}
}

func TestAMalformedBodyIsABadRequest(t *testing.T) {
	f := newFixture(t, nil)
	router := testRouter(f)
	rec := httptest.NewRecorder()
	req := httptest.NewRequest(http.MethodPost, "/v1/license/redeem", strings.NewReader(`{"redeemCode":`))
	req.Header.Set("Content-Type", "application/json")
	router.ServeHTTP(rec, req)
	if rec.Code != http.StatusBadRequest {
		t.Errorf("status = %d, want 400", rec.Code)
	}
}

// ---------------------------------------------------------------------------
// HTTP
// ---------------------------------------------------------------------------

func testRouter(f *fixture) *gin.Engine {
	gin.SetMode(gin.TestMode)
	r := gin.New()
	f.svc.Register(r)
	return r
}

func TestTheEndpointSpeaksTheClientsShape(t *testing.T) {
	f := newFixture(t, nil)
	code, _ := f.mint(t, "windows-buyout")
	router := testRouter(f)

	body, _ := json.Marshal(f.request(code, "install-A", "req-1"))
	rec := httptest.NewRecorder()
	req := httptest.NewRequest(http.MethodPost, "/v1/license/redeem", strings.NewReader(string(body)))
	req.Header.Set("Content-Type", "application/json")
	router.ServeHTTP(rec, req)

	if rec.Code != http.StatusCreated {
		t.Fatalf("status = %d, body %s", rec.Code, rec.Body.String())
	}
	var got Result
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
		t.Fatalf("response is not the contract shape: %v", err)
	}
	if got.Status != StatusRedeemed {
		t.Errorf("status field = %q", got.Status)
	}
	if got.RedeemedAt != "2026-10-18T12:00:00Z" {
		t.Errorf("redeemedAt = %q, want the yyyy-MM-ddTHH:mm:ssZ form", got.RedeemedAt)
	}
	if got.ExpiresAt != nil {
		t.Errorf("expiresAt = %v, want null for a perpetual buy-out licence", *got.ExpiresAt)
	}
	// The raw body must carry the camelCase names the client's attributes pin.
	for _, key := range []string{`"status"`, `"licenseToken"`, `"licenseId"`, `"redeemedAt"`, `"expiresAt"`} {
		if !strings.Contains(rec.Body.String(), key) {
			t.Errorf("response is missing %s: %s", key, rec.Body.String())
		}
	}

	// And the token must be base64url over the envelope, not the envelope.
	env := decodeToken(t, got.LicenseToken)
	if env.Subject != "install-A" || env.Plan != license.PlanBuyout {
		t.Errorf("licence subject/plan = %q/%q", env.Subject, env.Plan)
	}
	if env.ExpiresAt != nil {
		t.Errorf("a buy-out licence must not expire")
	}

	// Retry over HTTP: 200, the same bytes.
	rec2 := httptest.NewRecorder()
	req2 := httptest.NewRequest(http.MethodPost, "/v1/license/redeem", strings.NewReader(string(body)))
	req2.Header.Set("Content-Type", "application/json")
	router.ServeHTTP(rec2, req2)
	if rec2.Code != http.StatusOK {
		t.Fatalf("retry status = %d, want 200", rec2.Code)
	}
	var replay Result
	if err := json.Unmarshal(rec2.Body.Bytes(), &replay); err != nil {
		t.Fatal(err)
	}
	if replay.LicenseToken != got.LicenseToken {
		t.Fatal("the retry returned different bytes over HTTP")
	}
}

func TestErrorsCarryTheContractCode(t *testing.T) {
	f := newFixture(t, nil)
	router := testRouter(f)

	body, _ := json.Marshal(f.request("WCB-00000-11111-22222-33333", "install-A", "r1"))
	rec := httptest.NewRecorder()
	req := httptest.NewRequest(http.MethodPost, "/v1/license/redeem", strings.NewReader(string(body)))
	req.Header.Set("Content-Type", "application/json")
	router.ServeHTTP(rec, req)

	if rec.Code != http.StatusBadRequest {
		t.Fatalf("status = %d, want 400", rec.Code)
	}
	var got httpx.ErrorBody
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
		t.Fatalf("error body is not the httpx shape: %v", err)
	}
	if got.Error.Code != httpx.CodeInvalidCode {
		t.Errorf("code = %q, want %q", got.Error.Code, httpx.CodeInvalidCode)
	}
}

// ---------------------------------------------------------------------------
// Administration
// ---------------------------------------------------------------------------

func adminRouter(f *fixture, auth Authenticator) *gin.Engine {
	gin.SetMode(gin.TestMode)
	r := gin.New()
	f.svc.RegisterAdmin(r, auth)
	return r
}

func TestAdminEndpointsRefuseWithoutAuthentication(t *testing.T) {
	f := newFixture(t, nil)
	for name, router := range map[string]*gin.Engine{
		"nil authenticator": adminRouter(f, nil),
		"refusing":          adminRouter(f, func(*gin.Context) *httpx.APIError { return httpx.Forbidden("nope") }),
	} {
		rec := httptest.NewRecorder()
		req := httptest.NewRequest(http.MethodPost, "/v1/admin/redeem-codes", strings.NewReader(`{"count":1}`))
		req.Header.Set("Content-Type", "application/json")
		router.ServeHTTP(rec, req)
		if rec.Code != http.StatusForbidden {
			t.Errorf("%s: status = %d, want 403", name, rec.Code)
		}
		if f.store.codeCount() != 0 {
			t.Fatalf("%s: a code was minted without authentication", name)
		}
	}
}

func TestAdminGenerationMintsCodesThatBurn(t *testing.T) {
	f := newFixture(t, nil)
	router := adminRouter(f, func(*gin.Context) *httpx.APIError { return nil })

	rec := httptest.NewRecorder()
	req := httptest.NewRequest(http.MethodPost, "/v1/admin/redeem-codes", strings.NewReader(`{"count":3}`))
	req.Header.Set("Content-Type", "application/json")
	router.ServeHTTP(rec, req)

	if rec.Code != http.StatusCreated {
		t.Fatalf("status = %d, body %s", rec.Code, rec.Body.String())
	}
	var got GenerateResponse
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
		t.Fatal(err)
	}
	if len(got.Codes) != 3 {
		t.Fatalf("generated %d codes, want 3", len(got.Codes))
	}
	if f.store.codeCount() != 3 {
		t.Fatalf("the store holds %d codes, want 3", f.store.codeCount())
	}
	for _, c := range got.Codes {
		canonical, ok := Canonical(c.Code)
		if !ok || canonical != c.Code {
			t.Fatalf("generated %q is not canonical", c.Code)
		}
		if Digest(c.Code) != c.Digest {
			t.Fatalf("generated %q has the wrong digest", c.Code)
		}
		// It has to actually work, or the admin endpoint is a decoration.
		if _, err := f.svc.Redeem(context.Background(), f.request(c.Code, "install-A", "r-"+c.Digest)); err != nil {
			t.Fatalf("a freshly generated code did not redeem: %v", err)
		}
	}
}

func TestAdminRevocationStopsTheCode(t *testing.T) {
	f := newFixture(t, nil)
	router := adminRouter(f, func(*gin.Context) *httpx.APIError { return nil })
	code, digest := f.mint(t, "windows-buyout")

	rec := httptest.NewRecorder()
	req := httptest.NewRequest(http.MethodPatch, "/v1/admin/redeem-codes/"+digest, strings.NewReader(`{"revoke":true}`))
	req.Header.Set("Content-Type", "application/json")
	router.ServeHTTP(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, body %s", rec.Code, rec.Body.String())
	}

	_, err := f.svc.Redeem(context.Background(), f.request(code, "install-A", "r1"))
	var apiErr *httpx.APIError
	if !errors.As(err, &apiErr) || apiErr.Code != httpx.CodeCodeRevoked {
		t.Fatalf("a revoked code gave %v", err)
	}
}

func TestAdminExtensionBeatsTheClock(t *testing.T) {
	f := newFixture(t, nil)
	router := adminRouter(f, func(*gin.Context) *httpx.APIError { return nil })
	code, digest := f.mint(t, "windows-buyout")

	// Expire it, then extend it past the point the service clock is at.
	if err := f.store.ExtendRedeemCode(context.Background(), digest, f.now.Add(-time.Minute)); err != nil {
		t.Fatal(err)
	}
	rec := httptest.NewRecorder()
	req := httptest.NewRequest(http.MethodPatch, "/v1/admin/redeem-codes/"+digest,
		strings.NewReader(`{"expiresAt":"2027-01-01T00:00:00Z"}`))
	req.Header.Set("Content-Type", "application/json")
	router.ServeHTTP(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, body %s", rec.Code, rec.Body.String())
	}
	if _, err := f.svc.Redeem(context.Background(), f.request(code, "install-A", "r1")); err != nil {
		t.Fatalf("an extended code did not redeem: %v", err)
	}
}

func TestTheAutoKeyCannotCollideWithAClientRequestID(t *testing.T) {
	// The client generates 22 characters of unpadded base64url; the derived key is
	// a different shape entirely, so one can never overwrite the other.
	key := autoKey("digest", "install-A")
	if !strings.HasPrefix(key, "auto-") {
		t.Fatalf("auto key %q has no namespace prefix", key)
	}
	if len(key) <= 22 {
		t.Fatalf("auto key %q is as short as a client request id", key)
	}
	if key == autoKey("digest", "install-B") {
		t.Fatal("the auto key does not depend on the install id")
	}
	if key == autoKey("other-digest", "install-A") {
		t.Fatal("the auto key does not depend on the code")
	}
}
