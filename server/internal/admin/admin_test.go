package admin

import (
	"context"
	"crypto/ecdsa"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"sort"
	"strings"
	"testing"
	"time"

	"github.com/gin-gonic/gin"

	"github.com/freestylefly/WeChatBridge/server/internal/license"
	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

func init() { gin.SetMode(gin.TestMode) }

const (
	adminAccountID = "acc-admin"
	adminOpenID    = "open-admin"
	buyerAccountID = "acc-buyer"
	buyerOpenID    = "open-buyer"

	testInstallID = "b64install000000000000"
)

// testNow is fixed so a licence's timestamps are assertable without sleeping.
var testNow = time.Date(2026, 10, 18, 14, 30, 0, 0, time.UTC)

// ---------------------------------------------------------------------------
// Harness — the same shape the activation package's tests use: a test
// middleware stands in for auth.RequireAuth, and the account reader agrees
// with it about where the account id lives.
// ---------------------------------------------------------------------------

const testAccountKey = "test_account"

func testAccountReader(accountID string) func(*gin.Context) (string, bool) {
	return func(c *gin.Context) (string, bool) {
		v, ok := c.Get(testAccountKey)
		if !ok {
			return "", false
		}
		id, ok := v.(string)
		return id, ok && id != ""
	}
}

// testAuth is the stand-in for auth.RequireAuth. An empty accountID models a
// request that reached the handler without the middleware ever running.
func testAuth(accountID string) gin.HandlerFunc {
	return func(c *gin.Context) {
		if accountID != "" {
			c.Set(testAccountKey, accountID)
		}
		c.Next()
	}
}

func testSigner(t *testing.T) (*license.Signer, *ecdsa.PublicKey) {
	t.Helper()
	key, err := license.GenerateKey()
	if err != nil {
		t.Fatalf("GenerateKey: %v", err)
	}
	signer, err := license.NewSigner(key)
	if err != nil {
		t.Fatalf("NewSigner: %v", err)
	}
	pub, ok := key.Public().(*ecdsa.PublicKey)
	if !ok {
		t.Fatal("generated key does not carry an ECDSA public key")
	}
	return signer, pub
}

func newTestEngine(t *testing.T, st *fakeStore, accountID string) *gin.Engine {
	t.Helper()
	engine, _ := newTestEngineWithSigner(t, st, accountID)
	return engine
}

// newTestEngineWithSigner is newTestEngine plus the public key of the signer
// the engine was built with — the key the stored envelopes must verify
// against.
func newTestEngineWithSigner(t *testing.T, st *fakeStore, accountID string) (*gin.Engine, *ecdsa.PublicKey) {
	t.Helper()
	signer, pub := testSigner(t)
	svc, err := NewService(Options{
		Auth:         st,
		Activation:   st,
		Signer:       signer,
		AdminOpenIDs: []string{adminOpenID},
		AccountOf:    testAccountReader(accountID),
		Now:          func() time.Time { return testNow },
	})
	if err != nil {
		t.Fatalf("NewService: %v", err)
	}
	engine := gin.New()
	svc.RegisterRoutes(engine, testAuth(accountID))
	return engine, pub
}

// seedSellerAccount is the signed-in seller the whitelist names.
func seedSellerAccount(t *testing.T, st *fakeStore) {
	t.Helper()
	st.seedAccount(store.Account{ID: adminAccountID, OpenID: adminOpenID, Nickname: "卖家"})
}

// seedBuyerAccount is a signed-in buyer who is not on the whitelist.
func seedBuyerAccount(t *testing.T, st *fakeStore) {
	t.Helper()
	st.seedAccount(store.Account{ID: buyerAccountID, OpenID: buyerOpenID, Nickname: "买家"})
}

func doJSON(t *testing.T, engine *gin.Engine, method, path string, body any) (*httptest.ResponseRecorder, map[string]any) {
	t.Helper()

	var reader *strings.Reader
	if body != nil {
		raw, err := json.Marshal(body)
		if err != nil {
			t.Fatalf("marshal request: %v", err)
		}
		reader = strings.NewReader(string(raw))
	} else {
		reader = strings.NewReader("")
	}
	req := httptest.NewRequest(method, path, reader)
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}

	rec := httptest.NewRecorder()
	engine.ServeHTTP(rec, req)

	out := map[string]any{}
	if rec.Body.Len() > 0 && strings.HasPrefix(rec.Header().Get("Content-Type"), "application/json") {
		if err := json.Unmarshal(rec.Body.Bytes(), &out); err != nil {
			t.Fatalf("%s %s: response is not JSON: %s", method, path, rec.Body.String())
		}
	}
	return rec, out
}

func keysOf(m map[string]any) []string {
	out := make([]string, 0, len(m))
	for k := range m {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}

// seedPendingRequest is the row the seller's confirm button acts on.
func seedPendingRequest(t *testing.T, st *fakeStore, requestID, installID string, amountFen *int64) store.ActivationRequest {
	t.Helper()
	return st.seedRequest(store.ActivationRequest{
		RequestID:    requestID,
		AccountID:    buyerAccountID,
		InstallID:    installID,
		AnchorDigest: "sha256:anchor",
		AmountFen:    amountFen,
		Status:       store.ActivationPending,
		CreatedAt:    testNow,
	})
}

// ---------------------------------------------------------------------------
// Wiring
// ---------------------------------------------------------------------------

func TestNewServiceRequiresItsWiring(t *testing.T) {
	signer, _ := testSigner(t)
	base := Options{
		Auth:         newFakeStore(),
		Activation:   newFakeStore(),
		Signer:       signer,
		AdminOpenIDs: []string{adminOpenID},
		AccountOf:    testAccountReader(adminAccountID),
	}

	cases := []struct {
		name   string
		mutate func(*Options)
	}{
		{"no auth store", func(o *Options) { o.Auth = nil }},
		{"no activation store", func(o *Options) { o.Activation = nil }},
		{"no signer", func(o *Options) { o.Signer = nil }},
		{"no account reader", func(o *Options) { o.AccountOf = nil }},
		{"empty whitelist", func(o *Options) { o.AdminOpenIDs = nil }},
		{"whitespace-only whitelist", func(o *Options) { o.AdminOpenIDs = []string{"  ", ""} }},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			opts := base
			tc.mutate(&opts)
			if _, err := NewService(opts); err == nil {
				t.Fatalf("NewService accepted a %s", tc.name)
			}
		})
	}

	if _, err := NewService(base); err != nil {
		t.Fatalf("NewService rejected a complete wiring: %v", err)
	}
}

func TestWhitelistMatchesExactlyAfterTrimming(t *testing.T) {
	signer, _ := testSigner(t)
	svc, err := NewService(Options{
		Auth:         newFakeStore(),
		Activation:   newFakeStore(),
		Signer:       signer,
		AdminOpenIDs: []string{" open-a ", "", "open-b"},
		AccountOf:    testAccountReader(adminAccountID),
	})
	if err != nil {
		t.Fatalf("NewService: %v", err)
	}
	for _, openID := range []string{"open-a", "open-b", " open-a"} {
		if !svc.isAdmin(openID) {
			t.Errorf("isAdmin(%q) = false, want true", openID)
		}
	}
	for _, openID := range []string{"open-ax", "pen-a", "", "OPEN-A"} {
		if svc.isAdmin(openID) {
			t.Errorf("isAdmin(%q) = true, want false", openID)
		}
	}
}

// ---------------------------------------------------------------------------
// Admin identity: every endpoint sits behind the same two gates
// ---------------------------------------------------------------------------

func TestEveryEndpointRefusesAnAnonymousCaller(t *testing.T) {
	st := newFakeStore()
	seedSellerAccount(t, st)
	engine := newTestEngine(t, st, "")

	calls := []struct {
		method, path string
		body         any
	}{
		{http.MethodGet, "/v1/admin/requests", nil},
		{http.MethodPost, "/v1/admin/confirm", map[string]string{"requestId": "act_x"}},
		{http.MethodPost, "/v1/admin/reissue", map[string]string{"installId": testInstallID}},
		{http.MethodPost, "/v1/admin/revoke", map[string]string{"licenseId": "lic_x"}},
	}
	for _, call := range calls {
		rec, out := doJSON(t, engine, call.method, call.path, call.body)
		if rec.Code != http.StatusUnauthorized {
			t.Errorf("%s %s without a token: status %d, want 401", call.method, call.path, rec.Code)
		}
		if code, _ := out["error"].(map[string]any)["code"].(string); code != "unauthorized" {
			t.Errorf("%s %s without a token: code %q, want unauthorized", call.method, call.path, code)
		}
	}
}

func TestEveryEndpointRefusesANonWhitelistedSeller(t *testing.T) {
	st := newFakeStore()
	seedBuyerAccount(t, st)
	engine := newTestEngine(t, st, buyerAccountID)

	calls := []struct {
		method, path string
		body         any
	}{
		{http.MethodGet, "/v1/admin/requests", nil},
		{http.MethodPost, "/v1/admin/confirm", map[string]string{"requestId": "act_x"}},
		{http.MethodPost, "/v1/admin/reissue", map[string]string{"installId": testInstallID}},
		{http.MethodPost, "/v1/admin/revoke", map[string]string{"licenseId": "lic_x"}},
	}
	for _, call := range calls {
		rec, out := doJSON(t, engine, call.method, call.path, call.body)
		if rec.Code != http.StatusForbidden {
			t.Errorf("%s %s as a buyer: status %d, want 403", call.method, call.path, rec.Code)
		}
		if code, _ := out["error"].(map[string]any)["code"].(string); code != "forbidden" {
			t.Errorf("%s %s as a buyer: code %q, want forbidden", call.method, call.path, code)
		}
	}
}

func TestATokenForADeletedAccountIsForbiddenNotUnauthorized(t *testing.T) {
	st := newFakeStore()
	seedSellerAccount(t, st) // the whitelist exists; this token's account does not
	engine := newTestEngine(t, st, "acc-ghost")

	rec, _ := doJSON(t, engine, http.MethodGet, "/v1/admin/requests", nil)
	if rec.Code != http.StatusForbidden {
		t.Fatalf("status %d, want 403 — the credential verified, the identity behind it did not", rec.Code)
	}
}

func TestAnUnreadableAccountIsAnInternalError(t *testing.T) {
	st := newFakeStore()
	seedSellerAccount(t, st)
	st.getAccountErr = errors.New("database is having a moment")
	engine := newTestEngine(t, st, adminAccountID)

	rec, _ := doJSON(t, engine, http.MethodGet, "/v1/admin/requests", nil)
	if rec.Code != http.StatusInternalServerError {
		t.Fatalf("status %d, want 500", rec.Code)
	}
}

// ---------------------------------------------------------------------------
// GET /v1/admin/requests
// ---------------------------------------------------------------------------

func TestRequestsListsEveryStateWhenNoFilterIsGiven(t *testing.T) {
	st := newFakeStore()
	seedSellerAccount(t, st)
	seedBuyerAccount(t, st)
	seedPendingRequest(t, st, "act_1", testInstallID, nil)
	st.seedRequest(store.ActivationRequest{RequestID: "act_2", AccountID: buyerAccountID, InstallID: "inst-2", Status: store.ActivationConfirmed, LicenseID: "lic_2"})
	st.seedRequest(store.ActivationRequest{RequestID: "act_3", AccountID: buyerAccountID, InstallID: "inst-3", Status: store.ActivationRejected})
	engine := newTestEngine(t, st, adminAccountID)

	rec, out := doJSON(t, engine, http.MethodGet, "/v1/admin/requests", nil)
	if rec.Code != http.StatusOK {
		t.Fatalf("status %d, want 200", rec.Code)
	}
	items, _ := out["items"].([]any)
	if len(items) != 3 {
		t.Fatalf("got %d items, want 3 — an absent filter means every state", len(items))
	}
	// Newest first, the way the console's table reads.
	first, _ := items[0].(map[string]any)
	if got, _ := first["requestId"].(string); got != "act_3" {
		t.Errorf("first item is %q, want act_3 (newest first)", got)
	}
}

func TestRequestsFiltersByStatus(t *testing.T) {
	st := newFakeStore()
	seedSellerAccount(t, st)
	seedBuyerAccount(t, st)
	seedPendingRequest(t, st, "act_1", testInstallID, nil)
	st.seedRequest(store.ActivationRequest{RequestID: "act_2", AccountID: buyerAccountID, InstallID: "inst-2", Status: store.ActivationConfirmed, LicenseID: "lic_2"})
	engine := newTestEngine(t, st, adminAccountID)

	rec, out := doJSON(t, engine, http.MethodGet, "/v1/admin/requests?status=pending", nil)
	if rec.Code != http.StatusOK {
		t.Fatalf("status %d, want 200", rec.Code)
	}
	items, _ := out["items"].([]any)
	if len(items) != 1 {
		t.Fatalf("got %d items, want 1", len(items))
	}
	only, _ := items[0].(map[string]any)
	if got, _ := only["requestId"].(string); got != "act_1" {
		t.Errorf("item is %q, want act_1", got)
	}
}

func TestRequestsRejectsAnUnknownStatusFilter(t *testing.T) {
	st := newFakeStore()
	seedSellerAccount(t, st)
	seedBuyerAccount(t, st)
	seedPendingRequest(t, st, "act_1", testInstallID, nil)
	engine := newTestEngine(t, st, adminAccountID)

	// A typo'd filter must not read as "nothing to confirm".
	rec, out := doJSON(t, engine, http.MethodGet, "/v1/admin/requests?status=comfirmed", nil)
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("status %d, want 400", rec.Code)
	}
	if code, _ := out["error"].(map[string]any)["code"].(string); code != "bad_request" {
		t.Errorf("code %q, want bad_request", code)
	}
}

func TestRequestsItemIsTheContractShape(t *testing.T) {
	st := newFakeStore()
	seedSellerAccount(t, st)
	seedBuyerAccount(t, st)
	amount := int64(9900)
	seedPendingRequest(t, st, "act_1", testInstallID, &amount)
	st.seedRequest(store.ActivationRequest{RequestID: "act_2", AccountID: buyerAccountID, InstallID: "inst-2", Status: store.ActivationPending})
	engine := newTestEngine(t, st, adminAccountID)

	_, out := doJSON(t, engine, http.MethodGet, "/v1/admin/requests?status=pending", nil)
	items, _ := out["items"].([]any)
	if len(items) != 2 {
		t.Fatalf("got %d items, want 2", len(items))
	}

	// Newest first: the row seeded second (no amount stated) leads, the one
	// with the amount follows.
	newest, _ := items[0].(map[string]any)
	if got, want := keysOf(newest), []string{"amountFen", "anchorDigest", "createdAt", "installId", "nickname", "requestId", "status"}; strings.Join(got, ",") != strings.Join(want, ",") {
		t.Errorf("item keys %v, want %v", got, want)
	}
	if got, _ := newest["requestId"].(string); got != "act_2" {
		t.Errorf("newest item is %q, want act_2", got)
	}
	if v, ok := newest["amountFen"]; !ok || v != nil {
		t.Errorf("amountFen = %v (present: %v), want null — the buyer never stated an amount", v, ok)
	}

	older, _ := items[1].(map[string]any)
	if got, _ := older["requestId"].(string); got != "act_1" {
		t.Errorf("older item is %q, want act_1", got)
	}
	if got, _ := older["createdAt"].(string); got != testNow.Format(time.RFC3339) {
		t.Errorf("createdAt %q, want %q — RFC 3339 UTC, no fractional seconds", got, testNow.Format(time.RFC3339))
	}
	if got, _ := older["nickname"].(string); got != "买家" {
		t.Errorf("nickname %q, want 买家", got)
	}
	if got, _ := older["amountFen"].(float64); got != 9900 {
		t.Errorf("amountFen %v, want 9900", got)
	}
}

func TestRequestsSurfacesAStoreFailure(t *testing.T) {
	st := newFakeStore()
	seedSellerAccount(t, st)
	st.listErr = errors.New("connection reset")
	engine := newTestEngine(t, st, adminAccountID)

	rec, _ := doJSON(t, engine, http.MethodGet, "/v1/admin/requests", nil)
	if rec.Code != http.StatusInternalServerError {
		t.Fatalf("status %d, want 500", rec.Code)
	}
}

// ---------------------------------------------------------------------------
// POST /v1/admin/confirm
// ---------------------------------------------------------------------------

func TestConfirmSignsAndStoresTheEnvelope(t *testing.T) {
	st := newFakeStore()
	seedSellerAccount(t, st)
	seedBuyerAccount(t, st)
	pending := seedPendingRequest(t, st, "act_1", testInstallID, nil)
	engine, pub := newTestEngineWithSigner(t, st, adminAccountID)

	rec, out := doJSON(t, engine, http.MethodPost, "/v1/admin/confirm", map[string]string{"requestId": pending.RequestID})
	if rec.Code != http.StatusOK {
		t.Fatalf("status %d, want 200", rec.Code)
	}
	licenseID, _ := out["licenseId"].(string)
	if !strings.HasPrefix(licenseID, "lic_") || len(licenseID) != 4+24 {
		t.Fatalf("licenseId %q, want lic_ + 24 hex chars", licenseID)
	}

	// The stored bytes are the signed licence, and they verify against the
	// issuing key — the whole Build → Issue → JSON → store chain.
	stored, err := st.GetActivationRequest(context.Background(), pending.RequestID)
	if err != nil {
		t.Fatalf("GetActivationRequest: %v", err)
	}
	if stored.Status != store.ActivationConfirmed {
		t.Fatalf("request status %q, want confirmed", stored.Status)
	}
	envelope, err := license.Parse(stored.EnvelopeJSON)
	if err != nil {
		t.Fatalf("stored envelope does not parse: %v", err)
	}
	if envelope.Subject != testInstallID {
		t.Errorf("envelope subject %q, want the install id %q", envelope.Subject, testInstallID)
	}
	if envelope.Plan != license.PlanBuyout {
		t.Errorf("envelope plan %q, want buyout", envelope.Plan)
	}
	if len(envelope.Entitlements) != 1 || envelope.Entitlements[0] != license.EntitlementPro {
		t.Errorf("envelope entitlements %v, want [PRO]", envelope.Entitlements)
	}
	if envelope.ExpiresAt != nil {
		t.Errorf("envelope expires at %v, want perpetual", *envelope.ExpiresAt)
	}
	if err := license.Verify(envelope, pub, testInstallID, testNow, time.Minute); err != nil {
		t.Fatalf("stored envelope does not verify against the issuing key: %v", err)
	}

	// The binding is installed and live: the buyer's poller can collect.
	binding, err := st.LiveBindingByInstallID(context.Background(), testInstallID)
	if err != nil {
		t.Fatalf("LiveBindingByInstallID: %v", err)
	}
	if binding.LicenseID != licenseID {
		t.Errorf("live binding names %q, want %q", binding.LicenseID, licenseID)
	}
	if binding.AccountID != buyerAccountID {
		t.Errorf("binding account %q, want the buyer's", binding.AccountID)
	}
}

func TestConfirmIsIdempotentPerRequest(t *testing.T) {
	st := newFakeStore()
	seedSellerAccount(t, st)
	seedBuyerAccount(t, st)
	pending := seedPendingRequest(t, st, "act_1", testInstallID, nil)
	engine := newTestEngine(t, st, adminAccountID)

	_, first := doJSON(t, engine, http.MethodPost, "/v1/admin/confirm", map[string]string{"requestId": pending.RequestID})
	firstID, _ := first["licenseId"].(string)
	after, err := st.GetActivationRequest(context.Background(), pending.RequestID)
	if err != nil {
		t.Fatalf("GetActivationRequest: %v", err)
	}

	_, second := doJSON(t, engine, http.MethodPost, "/v1/admin/confirm", map[string]string{"requestId": pending.RequestID})
	secondID, _ := second["licenseId"].(string)
	if secondID != firstID {
		t.Fatalf("second confirm minted %q, want the first licence %q — one request, one licence", secondID, firstID)
	}

	again, err := st.GetActivationRequest(context.Background(), pending.RequestID)
	if err != nil {
		t.Fatalf("GetActivationRequest: %v", err)
	}
	if string(again.EnvelopeJSON) != string(after.EnvelopeJSON) {
		t.Error("the second confirm re-signed the envelope; ECDSA would have handed the buyer a new token")
	}
	bindings, err := st.ListBindings(context.Background())
	if err != nil {
		t.Fatalf("ListBindings: %v", err)
	}
	if len(bindings) != 1 {
		t.Errorf("got %d bindings, want 1", len(bindings))
	}
}

func TestConfirmOfAnUnknownRequestIs404(t *testing.T) {
	st := newFakeStore()
	seedSellerAccount(t, st)
	engine := newTestEngine(t, st, adminAccountID)

	rec, out := doJSON(t, engine, http.MethodPost, "/v1/admin/confirm", map[string]string{"requestId": "act_nope"})
	if rec.Code != http.StatusNotFound {
		t.Fatalf("status %d, want 404", rec.Code)
	}
	if code, _ := out["error"].(map[string]any)["code"].(string); code != "not_found" {
		t.Errorf("code %q, want not_found", code)
	}
}

func TestConfirmOfARejectedRequestIs409(t *testing.T) {
	st := newFakeStore()
	seedSellerAccount(t, st)
	seedBuyerAccount(t, st)
	rejected := st.seedRequest(store.ActivationRequest{
		RequestID: "act_1", AccountID: buyerAccountID, InstallID: testInstallID, Status: store.ActivationRejected,
	})
	engine := newTestEngine(t, st, adminAccountID)

	rec, out := doJSON(t, engine, http.MethodPost, "/v1/admin/confirm", map[string]string{"requestId": rejected.RequestID})
	if rec.Code != http.StatusConflict {
		t.Fatalf("status %d, want 409 — a no is not revivable by the confirm button", rec.Code)
	}
	if code, _ := out["error"].(map[string]any)["code"].(string); code != "conflict" {
		t.Errorf("code %q, want conflict", code)
	}
}

func TestConfirmOfAnAlreadyConfirmedRequestReturnsTheSameLicence(t *testing.T) {
	st := newFakeStore()
	seedSellerAccount(t, st)
	seedBuyerAccount(t, st)
	confirmed := st.seedRequest(store.ActivationRequest{
		RequestID: "act_1", AccountID: buyerAccountID, InstallID: testInstallID,
		Status: store.ActivationConfirmed, LicenseID: "lic_already",
	})
	engine := newTestEngine(t, st, adminAccountID)

	rec, out := doJSON(t, engine, http.MethodPost, "/v1/admin/confirm", map[string]string{"requestId": confirmed.RequestID})
	if rec.Code != http.StatusOK {
		t.Fatalf("status %d, want 200", rec.Code)
	}
	if got, _ := out["licenseId"].(string); got != "lic_already" {
		t.Errorf("licenseId %q, want lic_already", got)
	}
}

func TestConfirmOfALostRaceReturnsTheWinnersLicence(t *testing.T) {
	st := newFakeStore()
	seedSellerAccount(t, st)
	seedBuyerAccount(t, st)
	pending := seedPendingRequest(t, st, "act_1", testInstallID, nil)
	st.raceWinner = true
	engine := newTestEngine(t, st, adminAccountID)

	rec, out := doJSON(t, engine, http.MethodPost, "/v1/admin/confirm", map[string]string{"requestId": pending.RequestID})
	if rec.Code != http.StatusOK {
		t.Fatalf("status %d, want 200 — the loser of the race still gets the winner's licence", rec.Code)
	}
	if got, _ := out["licenseId"].(string); got != "lic_racewinner" {
		t.Errorf("licenseId %q, want lic_racewinner", got)
	}
}

func TestConfirmSurfacesAStoreFailure(t *testing.T) {
	st := newFakeStore()
	seedSellerAccount(t, st)
	seedBuyerAccount(t, st)
	pending := seedPendingRequest(t, st, "act_1", testInstallID, nil)
	st.confirmErr = errors.New("disk on fire")
	engine := newTestEngine(t, st, adminAccountID)

	rec, _ := doJSON(t, engine, http.MethodPost, "/v1/admin/confirm", map[string]string{"requestId": pending.RequestID})
	if rec.Code != http.StatusInternalServerError {
		t.Fatalf("status %d, want 500", rec.Code)
	}
}

func TestConfirmRejectsAMalformedBody(t *testing.T) {
	st := newFakeStore()
	seedSellerAccount(t, st)
	seedBuyerAccount(t, st)
	// A real row exists, so a 400 below cannot be a disguised 404.
	seedPendingRequest(t, st, "act_1", testInstallID, nil)
	engine := newTestEngine(t, st, adminAccountID)

	cases := []struct {
		name string
		body any
	}{
		{"missing requestId", map[string]string{}},
		{"blank requestId", map[string]string{"requestId": "   "}},
		{"no body at all", nil},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			rec, _ := doJSON(t, engine, http.MethodPost, "/v1/admin/confirm", tc.body)
			if rec.Code != http.StatusBadRequest {
				t.Fatalf("status %d, want 400", rec.Code)
			}
		})
	}

	rec := httptest.NewRecorder()
	req := httptest.NewRequest(http.MethodPost, "/v1/admin/confirm", strings.NewReader("{not json"))
	req.Header.Set("Content-Type", "application/json")
	engine.ServeHTTP(rec, req)
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("malformed JSON: status %d, want 400", rec.Code)
	}
}

// ---------------------------------------------------------------------------
// POST /v1/admin/reissue
// ---------------------------------------------------------------------------

func TestReissueRetiresTheOldBindingAndSignsANewOne(t *testing.T) {
	st := newFakeStore()
	seedSellerAccount(t, st)
	seedBuyerAccount(t, st)
	st.seedBinding(store.LicenceBinding{
		LicenseID: "lic_old", InstallID: testInstallID, AccountID: buyerAccountID, RequestID: "act_old",
	})
	st.seedRequest(store.ActivationRequest{
		RequestID: "act_old", AccountID: buyerAccountID, InstallID: testInstallID,
		Status: store.ActivationConfirmed, LicenseID: "lic_old", EnvelopeJSON: []byte(`{"v":1}`),
	})
	engine := newTestEngine(t, st, adminAccountID)

	rec, out := doJSON(t, engine, http.MethodPost, "/v1/admin/reissue", map[string]string{"installId": testInstallID})
	if rec.Code != http.StatusOK {
		t.Fatalf("status %d, want 200", rec.Code)
	}
	newID, _ := out["licenseId"].(string)
	if newID == "" || newID == "lic_old" {
		t.Fatalf("licenseId %q, want a fresh licence", newID)
	}

	// The old binding is retired, the new one is live.
	live, err := st.LiveBindingByInstallID(context.Background(), testInstallID)
	if err != nil {
		t.Fatalf("LiveBindingByInstallID: %v", err)
	}
	if live.LicenseID != newID {
		t.Errorf("live binding names %q, want %q", live.LicenseID, newID)
	}

	// The buyer's poller learns: the machine's latest request now carries the
	// new licence and its envelope.
	latest, err := st.GetRequestByInstallID(context.Background(), testInstallID)
	if err != nil {
		t.Fatalf("GetRequestByInstallID: %v", err)
	}
	if latest.Status != store.ActivationConfirmed || latest.LicenseID != newID {
		t.Errorf("latest request is %q/%q, want confirmed/%q", latest.Status, latest.LicenseID, newID)
	}
	if len(latest.EnvelopeJSON) == 0 {
		t.Error("the reissued request carries no envelope; a licence nobody can poll for is a licence nobody collects")
	}
}

func TestReissueOfAnUnknownMachineIs404(t *testing.T) {
	st := newFakeStore()
	seedSellerAccount(t, st)
	engine := newTestEngine(t, st, adminAccountID)

	rec, _ := doJSON(t, engine, http.MethodPost, "/v1/admin/reissue", map[string]string{"installId": "inst-never-seen"})
	if rec.Code != http.StatusNotFound {
		t.Fatalf("status %d, want 404", rec.Code)
	}
}

func TestReissueSurfacesAStoreFailure(t *testing.T) {
	st := newFakeStore()
	seedSellerAccount(t, st)
	seedBuyerAccount(t, st)
	seedPendingRequest(t, st, "act_1", testInstallID, nil)
	st.reissueErr = errors.New("disk on fire")
	engine := newTestEngine(t, st, adminAccountID)

	rec, _ := doJSON(t, engine, http.MethodPost, "/v1/admin/reissue", map[string]string{"installId": testInstallID})
	if rec.Code != http.StatusInternalServerError {
		t.Fatalf("status %d, want 500", rec.Code)
	}
}

func TestReissueRejectsAMalformedBody(t *testing.T) {
	st := newFakeStore()
	seedSellerAccount(t, st)
	engine := newTestEngine(t, st, adminAccountID)

	for _, body := range []any{map[string]string{}, map[string]string{"installId": "  "}, nil} {
		rec, _ := doJSON(t, engine, http.MethodPost, "/v1/admin/reissue", body)
		if rec.Code != http.StatusBadRequest {
			t.Fatalf("body %v: status %d, want 400", body, rec.Code)
		}
	}
}

// ---------------------------------------------------------------------------
// POST /v1/admin/revoke
// ---------------------------------------------------------------------------

func TestRevokeMarksTheBindingDead(t *testing.T) {
	st := newFakeStore()
	seedSellerAccount(t, st)
	seedBuyerAccount(t, st)
	st.seedBinding(store.LicenceBinding{
		LicenseID: "lic_1", InstallID: testInstallID, AccountID: buyerAccountID, RequestID: "act_1",
	})
	engine := newTestEngine(t, st, adminAccountID)

	rec, out := doJSON(t, engine, http.MethodPost, "/v1/admin/revoke", map[string]string{"licenseId": "lic_1"})
	if rec.Code != http.StatusOK {
		t.Fatalf("status %d, want 200", rec.Code)
	}
	if len(out) != 0 {
		t.Errorf("body %v, want the mock's empty object", out)
	}
	if _, err := st.LiveBindingByInstallID(context.Background(), testInstallID); !errors.Is(err, store.ErrNotFound) {
		t.Errorf("LiveBindingByInstallID error = %v, want ErrNotFound — a revoked licence is not the machine's live one", err)
	}
}

func TestRevokeIsIdempotent(t *testing.T) {
	st := newFakeStore()
	seedSellerAccount(t, st)
	seedBuyerAccount(t, st)
	st.seedBinding(store.LicenceBinding{
		LicenseID: "lic_1", InstallID: testInstallID, AccountID: buyerAccountID, RequestID: "act_1",
	})
	engine := newTestEngine(t, st, adminAccountID)

	for i := 0; i < 2; i++ {
		rec, _ := doJSON(t, engine, http.MethodPost, "/v1/admin/revoke", map[string]string{"licenseId": "lic_1"})
		if rec.Code != http.StatusOK {
			t.Fatalf("revoke #%d: status %d, want 200 — a misclick on a stale console is not an error", i+1, rec.Code)
		}
	}
}

func TestRevokeOfAnUnknownLicenceIs404(t *testing.T) {
	st := newFakeStore()
	seedSellerAccount(t, st)
	engine := newTestEngine(t, st, adminAccountID)

	rec, _ := doJSON(t, engine, http.MethodPost, "/v1/admin/revoke", map[string]string{"licenseId": "lic_nope"})
	if rec.Code != http.StatusNotFound {
		t.Fatalf("status %d, want 404", rec.Code)
	}
}

func TestRevokeSurfacesAStoreFailure(t *testing.T) {
	st := newFakeStore()
	seedSellerAccount(t, st)
	seedBuyerAccount(t, st)
	st.seedBinding(store.LicenceBinding{
		LicenseID: "lic_1", InstallID: testInstallID, AccountID: buyerAccountID, RequestID: "act_1",
	})
	st.revokeErr = errors.New("disk on fire")
	engine := newTestEngine(t, st, adminAccountID)

	rec, _ := doJSON(t, engine, http.MethodPost, "/v1/admin/revoke", map[string]string{"licenseId": "lic_1"})
	if rec.Code != http.StatusInternalServerError {
		t.Fatalf("status %d, want 500", rec.Code)
	}
}

func TestRevokeRejectsAMalformedBody(t *testing.T) {
	st := newFakeStore()
	seedSellerAccount(t, st)
	engine := newTestEngine(t, st, adminAccountID)

	for _, body := range []any{map[string]string{}, map[string]string{"licenseId": "  "}, nil} {
		rec, _ := doJSON(t, engine, http.MethodPost, "/v1/admin/revoke", body)
		if rec.Code != http.StatusBadRequest {
			t.Fatalf("body %v: status %d, want 400", body, rec.Code)
		}
	}
}
