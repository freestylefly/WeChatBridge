package activation

import (
	"encoding/base64"
	"encoding/json"
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

// testAccountKey is where the test middleware leaves the account id. The
// production middleware leaves it under auth's own key and the production
// reader (auth.AccountIDFrom) finds it there; the pair passed to the service
// only has to agree with itself.
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

func newTestEngine(t *testing.T, st *fakeActivationStore, accountID string) *gin.Engine {
	t.Helper()
	svc, err := NewService(Options{Store: st, AccountOf: testAccountReader(accountID)})
	if err != nil {
		t.Fatalf("NewService: %v", err)
	}
	engine := gin.New()
	svc.RegisterRoutes(engine, testAuth(accountID))
	return engine
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

// signedEnvelope signs a buyout licence for a machine, the way the seller's
// confirm endpoint will.
func signedEnvelope(t *testing.T, installID string) []byte {
	t.Helper()
	key, err := license.GenerateKey()
	if err != nil {
		t.Fatalf("GenerateKey: %v", err)
	}
	signer, err := license.NewSigner(key)
	if err != nil {
		t.Fatalf("NewSigner: %v", err)
	}
	envelope, err := signer.Build(license.IssueParams{
		Subject:      installID,
		Plan:         license.PlanBuyout,
		Entitlements: []string{license.EntitlementPro},
	})
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	raw, err := envelope.JSON()
	if err != nil {
		t.Fatalf("JSON: %v", err)
	}
	return raw
}

// ---------------------------------------------------------------------------
// POST /v1/activation/request
// ---------------------------------------------------------------------------

func TestRegisterCreatesAPendingRequest(t *testing.T) {
	t.Parallel()
	engine := newTestEngine(t, newFakeStore(), "acct-1")

	rec, body := doJSON(t, engine, http.MethodPost, "/v1/activation/request", map[string]any{
		"installId":    "aXRlMS0yMjQ0NDQ",
		"anchorDigest": "sha256:9f86d081884c7d65",
		"amountFen":    9900,
	})
	if rec.Code != http.StatusCreated {
		t.Fatalf("register = %d, want 201: %s", rec.Code, rec.Body.String())
	}
	if got := keysOf(body); len(got) != 2 || got[0] != "requestId" || got[1] != "status" {
		t.Fatalf("register response keys = %v, want [requestId status]", got)
	}
	if body["status"] != "pending" {
		t.Fatalf("status = %v, want pending", body["status"])
	}
	requestID, _ := body["requestId"].(string)
	if !strings.HasPrefix(requestID, "act_") {
		t.Fatalf("requestId = %q, want the act_ prefix the mock and the client logs use", requestID)
	}
}

func TestRegisterIsIdempotentForTheSameAccount(t *testing.T) {
	t.Parallel()
	st := newFakeStore()
	engine := newTestEngine(t, st, "acct-1")

	first := map[string]any{"installId": "install-1", "amountFen": 9900}
	_, body1 := doJSON(t, engine, http.MethodPost, "/v1/activation/request", first)
	// A second tap with a different amount refreshes the row; it must not
	// queue a second line on the seller's confirm list, and the 单号 the
	// customer already saw must stay the 单号 the console shows.
	_, body2 := doJSON(t, engine, http.MethodPost, "/v1/activation/request",
		map[string]any{"installId": "install-1", "amountFen": 19900})

	if body1["requestId"] != body2["requestId"] {
		t.Fatalf("re-register changed the request id: %v -> %v", body1["requestId"], body2["requestId"])
	}
	rows, err := st.ListActivationRequests(t.Context(), store.ActivationPending)
	if err != nil {
		t.Fatalf("ListActivationRequests: %v", err)
	}
	if len(rows) != 1 {
		t.Fatalf("pending rows = %d, want 1", len(rows))
	}
	if rows[0].AmountFen == nil || *rows[0].AmountFen != 19900 {
		t.Fatalf("amount = %v, want the refreshed 19900", rows[0].AmountFen)
	}
}

func TestRegisterStartsANewRequestAfterConfirmation(t *testing.T) {
	t.Parallel()
	st := newFakeStore()
	engine := newTestEngine(t, st, "acct-1")

	confirmed := st.seed(store.ActivationRequest{
		RequestID: "act_old", InstallID: "install-1", AccountID: "acct-1",
		Status: store.ActivationConfirmed, LicenseID: "lic_old",
	})
	if confirmed.Status != store.ActivationConfirmed {
		t.Fatal("seed did not take")
	}

	_, body := doJSON(t, engine, http.MethodPost, "/v1/activation/request",
		map[string]any{"installId": "install-1"})
	if body["requestId"] == "act_old" {
		t.Fatal("a new purchase on a licensed machine returned the old request id")
	}
	// The old row is not live, so nothing conflicts; the seller sees the new
	// registration as its own line.
	rows, _ := st.ListActivationRequests(t.Context(), store.ActivationPending)
	if len(rows) != 1 || rows[0].RequestID != body["requestId"] {
		t.Fatalf("pending rows = %+v, want exactly the new request", rows)
	}
}

func TestRegisterRefusesAMachineAnotherAccountIsBuying(t *testing.T) {
	t.Parallel()
	st := newFakeStore()
	st.seed(store.ActivationRequest{
		RequestID: "act_first", InstallID: "install-1", AccountID: "acct-1",
		Status: store.ActivationPending,
	})
	engine := newTestEngine(t, st, "acct-2")

	rec, body := doJSON(t, engine, http.MethodPost, "/v1/activation/request",
		map[string]any{"installId": "install-1"})
	if rec.Code != http.StatusConflict {
		t.Fatalf("register on a taken machine = %d, want 409: %s", rec.Code, rec.Body.String())
	}
	failure, _ := body["error"].(map[string]any)
	if failure["code"] != "conflict" {
		t.Fatalf("error code = %v, want conflict", failure["code"])
	}
	// The first buyer's row must be untouched: same owner, same id, still
	// pending. Adopting it would move a purchase between accounts.
	rows, _ := st.ListActivationRequests(t.Context(), store.ActivationPending)
	if len(rows) != 1 || rows[0].RequestID != "act_first" || rows[0].AccountID != "acct-1" {
		t.Fatalf("pending rows = %+v, want the original row intact", rows)
	}
}

func TestRegisterRequiresASignedInAccount(t *testing.T) {
	t.Parallel()
	// The middleware never ran, so there is no account to scope the purchase
	// to. Fail closed rather than file it under nobody.
	engine := newTestEngine(t, newFakeStore(), "")

	rec, _ := doJSON(t, engine, http.MethodPost, "/v1/activation/request",
		map[string]any{"installId": "install-1"})
	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("register without an account = %d, want 401", rec.Code)
	}
}

func TestRegisterRejectsAnEmptyMachineCode(t *testing.T) {
	t.Parallel()
	engine := newTestEngine(t, newFakeStore(), "acct-1")

	for _, body := range []map[string]any{
		{"installId": ""},
		{"installId": "   "},
		{},
	} {
		rec, _ := doJSON(t, engine, http.MethodPost, "/v1/activation/request", body)
		if rec.Code != http.StatusBadRequest {
			t.Fatalf("register %v = %d, want 400", body, rec.Code)
		}
	}
}

func TestRegisterRejectsANegativeAmount(t *testing.T) {
	t.Parallel()
	engine := newTestEngine(t, newFakeStore(), "acct-1")

	rec, _ := doJSON(t, engine, http.MethodPost, "/v1/activation/request",
		map[string]any{"installId": "install-1", "amountFen": -1})
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("register with a negative amount = %d, want 400", rec.Code)
	}
}

func TestRegisterRejectsAMalformedBody(t *testing.T) {
	t.Parallel()
	engine := newTestEngine(t, newFakeStore(), "acct-1")

	req := httptest.NewRequest(http.MethodPost, "/v1/activation/request", strings.NewReader("{not json"))
	req.Header.Set("Content-Type", "application/json")
	rec := httptest.NewRecorder()
	engine.ServeHTTP(rec, req)
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("register with a malformed body = %d, want 400", rec.Code)
	}
}

// ---------------------------------------------------------------------------
// GET /v1/activation/status
// ---------------------------------------------------------------------------

func TestStatus404sWhenNothingIsInFlight(t *testing.T) {
	t.Parallel()
	engine := newTestEngine(t, newFakeStore(), "acct-1")

	rec, body := doJSON(t, engine, http.MethodGet, "/v1/activation/status", nil)
	if rec.Code != http.StatusNotFound {
		t.Fatalf("status = %d, want 404: %s", rec.Code, rec.Body.String())
	}
	failure, _ := body["error"].(map[string]any)
	if failure["code"] != "not_found" {
		t.Fatalf("error code = %v, want not_found", failure["code"])
	}
}

func TestStatusReportsPendingWithoutALicenseId(t *testing.T) {
	t.Parallel()
	st := newFakeStore()
	st.seed(store.ActivationRequest{
		RequestID: "act_1", InstallID: "install-1", AccountID: "acct-1",
		Status: store.ActivationPending,
	})
	engine := newTestEngine(t, st, "acct-1")

	_, body := doJSON(t, engine, http.MethodGet, "/v1/activation/status", nil)
	if got := keysOf(body); len(got) != 1 || got[0] != "status" {
		t.Fatalf("status response keys = %v, want [status]", got)
	}
	if body["status"] != "pending" {
		t.Fatalf("status = %v, want pending", body["status"])
	}
}

func TestStatusReportsConfirmedWithTheLicenseId(t *testing.T) {
	t.Parallel()
	st := newFakeStore()
	st.seed(store.ActivationRequest{
		RequestID: "act_1", InstallID: "install-1", AccountID: "acct-1",
		Status: store.ActivationConfirmed, LicenseID: "lic_1",
	})
	engine := newTestEngine(t, st, "acct-1")

	_, body := doJSON(t, engine, http.MethodGet, "/v1/activation/status", nil)
	if body["status"] != "confirmed" || body["licenseId"] != "lic_1" {
		t.Fatalf("status = %v, licenseId = %v; want confirmed / lic_1", body["status"], body["licenseId"])
	}
}

func TestStatusRequiresASignedInAccount(t *testing.T) {
	t.Parallel()
	engine := newTestEngine(t, newFakeStore(), "")

	rec, _ := doJSON(t, engine, http.MethodGet, "/v1/activation/status", nil)
	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("status without an account = %d, want 401", rec.Code)
	}
}

// ---------------------------------------------------------------------------
// GET /v1/activation/licence
// ---------------------------------------------------------------------------

func TestLicenceRefusesBeforeTheSellerConfirms(t *testing.T) {
	t.Parallel()
	st := newFakeStore()
	st.seed(store.ActivationRequest{
		RequestID: "act_1", InstallID: "install-1", AccountID: "acct-1",
		Status: store.ActivationPending,
	})
	engine := newTestEngine(t, st, "acct-1")

	rec, body := doJSON(t, engine, http.MethodGet, "/v1/activation/licence", nil)
	if rec.Code != http.StatusConflict {
		t.Fatalf("licence before confirm = %d, want 409: %s", rec.Code, rec.Body.String())
	}
	failure, _ := body["error"].(map[string]any)
	if failure["code"] != "conflict" {
		t.Fatalf("error code = %v, want conflict (retryable, per the client's Classify)", failure["code"])
	}
}

func TestLicenceRefusesWhenNothingWasEverRegistered(t *testing.T) {
	t.Parallel()
	engine := newTestEngine(t, newFakeStore(), "acct-1")

	// 409, not 404: the client treats this as "still signing" and polls
	// again, which is the right shape for a customer mid-purchase.
	rec, _ := doJSON(t, engine, http.MethodGet, "/v1/activation/licence", nil)
	if rec.Code != http.StatusConflict {
		t.Fatalf("licence with no request = %d, want 409", rec.Code)
	}
}

func TestLicenceReturnsTheSignedEnvelope(t *testing.T) {
	t.Parallel()
	st := newFakeStore()
	const installID = "aXRlMS0yMjQ0NA"
	st.seed(store.ActivationRequest{
		RequestID: "act_1", InstallID: installID, AccountID: "acct-1",
		Status: store.ActivationConfirmed, LicenseID: "lic_1",
		EnvelopeJSON: signedEnvelope(t, installID),
	})
	st.seedBinding(store.LicenceBinding{
		LicenseID: "lic_1", InstallID: installID, AccountID: "acct-1", RequestID: "act_1",
	})
	engine := newTestEngine(t, st, "acct-1")

	rec, body := doJSON(t, engine, http.MethodGet, "/v1/activation/licence", nil)
	if rec.Code != http.StatusOK {
		t.Fatalf("licence = %d, want 200: %s", rec.Code, rec.Body.String())
	}
	if got := keysOf(body); len(got) != 2 || got[0] != "envelope" || got[1] != "token" {
		t.Fatalf("licence response keys = %v, want [envelope token]", got)
	}

	// The token is the stored bytes, base64url — the same envelope the
	// seller signed, not a re-serialisation that could drift.
	token, _ := body["token"].(string)
	decoded, err := base64.RawURLEncoding.DecodeString(token)
	if err != nil {
		t.Fatalf("token is not unpadded base64url: %v", err)
	}
	var fromToken license.Envelope
	if err := json.Unmarshal(decoded, &fromToken); err != nil {
		t.Fatalf("token does not decode to an envelope: %v", err)
	}
	if fromToken.Subject != installID {
		t.Fatalf("envelope subject = %q, want the install id %q", fromToken.Subject, installID)
	}

	envelope, _ := body["envelope"].(map[string]any)
	for _, key := range []string{"v", "sub", "plan", "iat", "nbf", "exp", "ent", "sig"} {
		if _, ok := envelope[key]; !ok {
			t.Fatalf("envelope is missing %q; got keys %v", key, keysOf(envelope))
		}
	}
	if envelope["sub"] != installID {
		t.Fatalf("envelope.sub = %v, want %q", envelope["sub"], installID)
	}
	if envelope["plan"] != "buyout" {
		t.Fatalf("envelope.plan = %v, want buyout", envelope["plan"])
	}
	// A buyout licence has no expiry, and the client's verifier reads a null
	// exp as perpetual.
	if envelope["exp"] != nil {
		t.Fatalf("envelope.exp = %v, want null for a buyout", envelope["exp"])
	}
}

func TestLicenceRefusesARevokedRequest(t *testing.T) {
	t.Parallel()
	st := newFakeStore()
	st.seed(store.ActivationRequest{
		RequestID: "act_1", InstallID: "install-1", AccountID: "acct-1",
		Status: store.ActivationConfirmed, LicenseID: "lic_1",
		EnvelopeJSON: signedEnvelope(t, "install-1"),
	})
	st.seedBinding(store.LicenceBinding{
		LicenseID: "lic_1", InstallID: "install-1", AccountID: "acct-1", RequestID: "act_1",
	})
	if err := st.RevokeBinding(t.Context(), "lic_1", time.Now().UTC()); err != nil {
		t.Fatalf("RevokeBinding: %v", err)
	}
	engine := newTestEngine(t, st, "acct-1")

	// The seller killed it. The old envelope stays valid wherever it already
	// landed — offline verification has no revocation channel — but this
	// endpoint is not where a revoked licence gets re-handed-out. In this
	// schema revocation is a date on the binding, so the binding is what
	// the handler asks.
	rec, _ := doJSON(t, engine, http.MethodGet, "/v1/activation/licence", nil)
	if rec.Code != http.StatusConflict {
		t.Fatalf("licence for a revoked request = %d, want 409", rec.Code)
	}
}

func TestLicenceFollowsAReissueToTheNewLicence(t *testing.T) {
	t.Parallel()
	// The reissue ported from the single-table line: the seller retires the
	// machine's binding and files a new one, and the buyer's poller must
	// learn about it — status names the new licence, and the licence
	// endpoint hands out the new envelope. A reissue the poller cannot see
	// is a reissue the buyer never collects.
	st := newFakeStore()
	const installID = "install-1"
	st.seed(store.ActivationRequest{
		RequestID: "act_1", InstallID: installID, AccountID: "acct-1",
		Status: store.ActivationConfirmed, LicenseID: "lic_old",
		EnvelopeJSON: signedEnvelope(t, installID),
	})
	st.seedBinding(store.LicenceBinding{
		LicenseID: "lic_old", InstallID: installID, AccountID: "acct-1", RequestID: "act_1",
	})

	fresh := signedEnvelope(t, installID)
	reissued, err := st.ReissueLicence(t.Context(), installID, "lic_new", fresh, time.Now().UTC())
	if err != nil {
		t.Fatalf("ReissueLicence: %v", err)
	}
	engine := newTestEngine(t, st, "acct-1")

	_, status := doJSON(t, engine, http.MethodGet, "/v1/activation/status", nil)
	if status["licenseId"] != "lic_new" {
		t.Fatalf("status licenseId = %v, want lic_new (the reissued request is %q)", status["licenseId"], reissued.RequestID)
	}

	rec, body := doJSON(t, engine, http.MethodGet, "/v1/activation/licence", nil)
	if rec.Code != http.StatusOK {
		t.Fatalf("licence after reissue = %d, want 200: %s", rec.Code, rec.Body.String())
	}
	token, _ := body["token"].(string)
	decoded, err := base64.RawURLEncoding.DecodeString(token)
	if err != nil {
		t.Fatalf("token is not unpadded base64url: %v", err)
	}
	if string(decoded) != string(fresh) {
		t.Fatal("the licence endpoint handed out the retired envelope, not the reissued one")
	}
}

func TestLicence500sOnAConfirmedRowWithoutBytes(t *testing.T) {
	t.Parallel()
	st := newFakeStore()
	st.seed(store.ActivationRequest{
		RequestID: "act_1", InstallID: "install-1", AccountID: "acct-1",
		Status: store.ActivationConfirmed, LicenseID: "lic_1",
		// No envelope: the database forbids this combination, so reaching it
		// means corruption, and corruption is a 500 rather than a 409 the
		// customer would read as "still signing".
	})
	engine := newTestEngine(t, st, "acct-1")

	rec, body := doJSON(t, engine, http.MethodGet, "/v1/activation/licence", nil)
	if rec.Code != http.StatusInternalServerError {
		t.Fatalf("licence on a corrupt row = %d, want 500: %s", rec.Code, rec.Body.String())
	}
	failure, _ := body["error"].(map[string]any)
	if failure["code"] != "internal" {
		t.Fatalf("error code = %v, want internal", failure["code"])
	}
}

func TestLicenceRequiresASignedInAccount(t *testing.T) {
	t.Parallel()
	st := newFakeStore()
	st.seed(store.ActivationRequest{
		RequestID: "act_1", InstallID: "install-1", AccountID: "acct-1",
		Status: store.ActivationConfirmed, LicenseID: "lic_1",
		EnvelopeJSON: signedEnvelope(t, "install-1"),
	})
	st.seedBinding(store.LicenceBinding{
		LicenseID: "lic_1", InstallID: "install-1", AccountID: "acct-1", RequestID: "act_1",
	})
	// A different signed-in account must not read acct-1's licence: the read
	// is keyed by the token's account, not by anything in the URL.
	engine := newTestEngine(t, st, "acct-2")

	rec, _ := doJSON(t, engine, http.MethodGet, "/v1/activation/licence", nil)
	if rec.Code != http.StatusConflict {
		t.Fatalf("licence for another account = %d, want 409 (not found for this account)", rec.Code)
	}
}
