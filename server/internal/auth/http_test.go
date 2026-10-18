package auth

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"regexp"
	"strings"
	"testing"
	"time"

	"github.com/gin-gonic/gin"

	"github.com/freestylefly/WeChatBridge/server/internal/token"
	"github.com/freestylefly/WeChatBridge/server/internal/wechat"
)

func init() { gin.SetMode(gin.TestMode) }

// isoPattern is the client's UtcIso8601Converter format, exactly. A bare
// time.Time would marshal as RFC 3339 with an offset — "+08:00" or "+00:00" —
// which on the other end is a JsonException rather than a wrong-looking string
// a human would catch.
var isoPattern = regexp.MustCompile(`^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$`)

func newTestServer(t *testing.T) (*harness, *gin.Engine) {
	t.Helper()
	h := newHarness(t)
	engine := gin.New()
	h.svc.RegisterRoutes(engine)
	return h, engine
}

func doJSON(t *testing.T, engine *gin.Engine, method, path string, body any, headers map[string]string) (*httptest.ResponseRecorder, map[string]any) {
	t.Helper()

	var reader io.Reader
	if body != nil {
		raw, err := json.Marshal(body)
		if err != nil {
			t.Fatalf("marshal request: %v", err)
		}
		reader = bytes.NewReader(raw)
	}
	req := httptest.NewRequest(method, path, reader)
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	for k, v := range headers {
		req.Header.Set(k, v)
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

func createTicket(t *testing.T, engine *gin.Engine, installID string) map[string]any {
	t.Helper()
	rec, body := doJSON(t, engine, http.MethodPost, "/v1/auth/session",
		map[string]any{"installId": installID, "deviceId": "device-1"}, nil)
	if rec.Code != http.StatusCreated {
		t.Fatalf("POST /v1/auth/session = %d: %s", rec.Code, rec.Body.String())
	}
	return body
}

// ---------------------------------------------------------------------------
// Shapes
// ---------------------------------------------------------------------------

// Every response this service writes is camelCase, because the client's
// JsonOptions are. Go's default is the Go field name, so a missing tag is not
// a cosmetic problem — it is a field the client never binds.
func TestEveryResponseIsCamelCase(t *testing.T) {
	t.Parallel()

	h, engine := newTestServer(t)

	created := createTicket(t, engine, "install-1")
	for _, key := range []string{"ticket", "authorizeUrl", "qrImageUrl", "expiresAt"} {
		if _, ok := created[key]; !ok {
			t.Errorf("create session response is missing %q; got keys %v", key, keysOf(created))
		}
	}

	if _, err := h.svc.HandleCallback(t.Context(), "auth-code", created["ticket"].(string)); err != nil {
		t.Fatalf("HandleCallback: %v", err)
	}

	_, poll := doJSON(t, engine, http.MethodGet, "/v1/auth/session/"+created["ticket"].(string), nil, nil)
	for _, key := range []string{"status", "accessToken", "refreshToken", "expiresAt", "account"} {
		if _, ok := poll[key]; !ok {
			t.Errorf("poll response is missing %q; got keys %v", key, keysOf(poll))
		}
	}
	account, _ := poll["account"].(map[string]any)
	for _, key := range []string{"id", "nickname", "avatarUrl", "plan", "createdAt", "lastLoginAt", "wechatBound"} {
		if _, ok := account[key]; !ok {
			t.Errorf("account is missing %q; got keys %v", key, keysOf(account))
		}
	}

	access := poll["accessToken"].(string)
	refresh := poll["refreshToken"].(string)

	_, refreshed := doJSON(t, engine, http.MethodPost, "/v1/auth/refresh",
		map[string]any{"refreshToken": refresh}, nil)
	for _, key := range []string{"status", "accessToken", "refreshToken", "expiresAt", "account"} {
		if _, ok := refreshed[key]; !ok {
			t.Errorf("refresh response is missing %q; got keys %v", key, keysOf(refreshed))
		}
	}

	bearer := map[string]string{"Authorization": "Bearer " + access}
	_, acct := doJSON(t, engine, http.MethodGet, "/v1/account", nil, bearer)
	for _, key := range []string{"id", "nickname", "avatarUrl", "plan", "createdAt", "lastLoginAt", "wechatBound"} {
		if _, ok := acct[key]; !ok {
			t.Errorf("GET /v1/account is missing %q; got keys %v", key, keysOf(acct))
		}
	}

	// And nothing anywhere is PascalCase or snake_case. The error envelope is
	// excluded: internal/httpx owns that shape and request_id is deliberately
	// snake_case there, per a decision made in that package.
	assertAllLowerCamel(t, "", created)
	assertAllLowerCamel(t, "", poll)
	assertAllLowerCamel(t, "", refreshed)
}

func TestEveryTimestampIsTheClientsFormat(t *testing.T) {
	t.Parallel()

	h, engine := newTestServer(t)
	created := createTicket(t, engine, "install-1")
	if got := created["expiresAt"].(string); !isoPattern.MatchString(got) {
		t.Errorf("create expiresAt = %q, want yyyy-MM-ddTHH:mm:ssZ", got)
	}
	// The clock is UTC in the harness; if the formatter ever forgot to
	// convert, this is where it would show as a +00:00 suffix.
	if strings.Contains(created["expiresAt"].(string), "+") {
		t.Errorf("expiresAt carries an offset: %q", created["expiresAt"])
	}

	if _, err := h.svc.HandleCallback(t.Context(), "auth-code", created["ticket"].(string)); err != nil {
		t.Fatalf("HandleCallback: %v", err)
	}
	_, poll := doJSON(t, engine, http.MethodGet, "/v1/auth/session/"+created["ticket"].(string), nil, nil)
	if got := poll["expiresAt"].(string); !isoPattern.MatchString(got) {
		t.Errorf("poll expiresAt = %q", got)
	}
	for _, key := range []string{"createdAt", "lastLoginAt"} {
		account := poll["account"].(map[string]any)
		if got := account[key].(string); !isoPattern.MatchString(got) {
			t.Errorf("account.%s = %q", key, got)
		}
	}
}

// ---------------------------------------------------------------------------
// Ticket endpoints
// ---------------------------------------------------------------------------

func TestPollReturnsExpiredForATicketThatIsGone(t *testing.T) {
	t.Parallel()

	_, engine := newTestServer(t)
	rec, body := doJSON(t, engine, http.MethodGet, "/v1/auth/session/nosuchticket", nil, nil)
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200: a poll must answer with a state, not a 404", rec.Code)
	}
	if body["status"] != StatusExpired {
		t.Errorf("status = %v, want expired", body["status"])
	}
}

func TestTheQREndpointReturnsAPngWithinTheClientLimit(t *testing.T) {
	t.Parallel()

	_, engine := newTestServer(t)
	created := createTicket(t, engine, "install-1")
	ticket := created["ticket"].(string)

	req := httptest.NewRequest(http.MethodGet, "/v1/auth/session/"+ticket+"/qr", nil)
	rec := httptest.NewRecorder()
	engine.ServeHTTP(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, body %s", rec.Code, rec.Body.String())
	}
	if ct := rec.Header().Get("Content-Type"); ct != "image/png" {
		t.Errorf("Content-Type = %q, want image/png", ct)
	}

	body := rec.Body.Bytes()
	// The client checks these bytes before handing them to WPF. A wrong
	// server answering with an HTML error page would otherwise surface as a
	// NotSupportedException inside the image decoder, which reads like a
	// client bug rather than a bad response.
	if !wechat.IsPNG(body) {
		t.Fatalf("the response is not a PNG: % x", body[:min(16, len(body))])
	}
	if len(body) > wechat.MaxQRBytes {
		t.Fatalf("the PNG is %d bytes, over the client's %d byte limit", len(body), wechat.MaxQRBytes)
	}
}

func TestTheQREndpoint404sForATicketThatDoesNotExist(t *testing.T) {
	t.Parallel()

	_, engine := newTestServer(t)
	req := httptest.NewRequest(http.MethodGet, "/v1/auth/session/nosuchticket/qr", nil)
	rec := httptest.NewRecorder()
	engine.ServeHTTP(rec, req)
	if rec.Code != http.StatusNotFound {
		t.Errorf("status = %d, want 404", rec.Code)
	}
}

func TestTooManyOutstandingTicketsAnswers429(t *testing.T) {
	t.Parallel()

	_, engine := newTestServer(t)
	for n := 0; n < 3; n++ {
		createTicket(t, engine, "install-1")
	}
	rec, body := doJSON(t, engine, http.MethodPost, "/v1/auth/session",
		map[string]any{"installId": "install-1"}, nil)
	if rec.Code != http.StatusTooManyRequests {
		t.Fatalf("status = %d, want 429", rec.Code)
	}
	errObj, _ := body["error"].(map[string]any)
	if errObj["code"] != "rate_limited" {
		t.Errorf("error code = %v, want rate_limited", errObj["code"])
	}
	if rec.Header().Get("Retry-After") == "" {
		t.Error("a 429 without Retry-After gives the client nothing to back off with")
	}
}

// ---------------------------------------------------------------------------
// The redirect
// ---------------------------------------------------------------------------

// §15.1 step 7. The callback is a browser-facing GET and answers 200 with
// nothing. A redirect here is the reflexive browser-shaped answer and it breaks
// the flow, because the destination is a page in a browser the desktop process
// has never heard of. The poller is the channel.
func TestTheCallbackAnswers200WithAnEmptyBodyAndNoRedirect(t *testing.T) {
	t.Parallel()

	h, engine := newTestServer(t)
	created := createTicket(t, engine, "install-1")
	ticket := created["ticket"].(string)

	req := httptest.NewRequest(http.MethodGet, CallbackPath+"?code=auth-code&state="+ticket, nil)
	rec := httptest.NewRecorder()
	engine.ServeHTTP(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200", rec.Code)
	}
	if loc := rec.Header().Get("Location"); loc != "" {
		t.Fatalf("the callback issued a redirect to %q; the desktop client cannot follow that and the poller is the channel", loc)
	}
	if n := rec.Header().Get("Content-Length"); n != "" && n != "0" {
		t.Errorf("Content-Length = %q, want an empty body", n)
	}
	if body := rec.Body.Bytes(); len(bytes.TrimSpace(body)) != 0 {
		t.Errorf("the callback wrote a body: %q", body)
	}

	// And the scan really did complete — the browser's 200 is not covering
	// for a failure.
	poll, err := h.svc.Poll(t.Context(), ticket)
	if err != nil {
		t.Fatalf("Poll: %v", err)
	}
	if poll.Status != StatusAuthorized {
		t.Fatalf("the callback reported success but the ticket is %q", poll.Status)
	}
}

func TestTheCallbackRejectsAForgedStateWith400(t *testing.T) {
	t.Parallel()

	_, engine := newTestServer(t)
	req := httptest.NewRequest(http.MethodGet, CallbackPath+"?code=auth-code&state=forged", nil)
	rec := httptest.NewRecorder()
	engine.ServeHTTP(rec, req)

	if rec.Code != http.StatusBadRequest {
		t.Fatalf("status = %d, want 400", rec.Code)
	}
	body := rec.Body.String()
	// The reply must not say which half it was. "That ticket does not exist"
	// versus "that ticket has already been used" is a free probe.
	for _, leak := range []string{"unknown ticket", "already used", "forged", "not found"} {
		if strings.Contains(strings.ToLower(body), leak) {
			t.Errorf("the 400 body leaks why the state failed (%q): %s", leak, body)
		}
	}
}

func TestTheCallbackReportsAWeChatOutageAs503(t *testing.T) {
	t.Parallel()

	h, engine := newTestServer(t)
	h.wc.exchangeHandler = func(rw http.ResponseWriter, r *http.Request) {
		rw.WriteHeader(http.StatusInternalServerError)
		_, _ = rw.Write([]byte(`{"errcode":-1,"errmsg":"system error"}`))
	}
	ticket := createTicket(t, engine, "install-1")["ticket"].(string)

	req := httptest.NewRequest(http.MethodGet, CallbackPath+"?code=auth-code&state="+ticket, nil)
	rec := httptest.NewRecorder()
	engine.ServeHTTP(rec, req)

	if rec.Code != http.StatusServiceUnavailable {
		t.Fatalf("status = %d, want 503 — a WeChat blip is retryable, not a rejected sign-in", rec.Code)
	}
	if session, _ := h.store.authSessionByTicket(ticket); session.State != "pending" {
		t.Fatal("a WeChat outage consumed the ticket")
	}
}

func TestTheCallbackRejectsAWeChatRejectedCodeWith400(t *testing.T) {
	t.Parallel()

	h, engine := newTestServer(t)
	h.wc.exchangeHandler = func(rw http.ResponseWriter, r *http.Request) {
		_, _ = rw.Write([]byte(`{"errcode":40029,"errmsg":"invalid code"}`))
	}
	ticket := createTicket(t, engine, "install-1")["ticket"].(string)

	req := httptest.NewRequest(http.MethodGet, CallbackPath+"?code=bad&state="+ticket, nil)
	rec := httptest.NewRecorder()
	engine.ServeHTTP(rec, req)
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("status = %d, want 400", rec.Code)
	}
}

// ---------------------------------------------------------------------------
// Refresh
// ---------------------------------------------------------------------------

func signInViaHTTP(t *testing.T, engine *gin.Engine) (access, refresh string) {
	t.Helper()
	created := createTicket(t, engine, "install-1")
	ticket := created["ticket"].(string)

	req := httptest.NewRequest(http.MethodGet, CallbackPath+"?code=auth-code&state="+ticket, nil)
	engine.ServeHTTP(httptest.NewRecorder(), req)

	_, poll := doJSON(t, engine, http.MethodGet, "/v1/auth/session/"+ticket, nil, nil)
	return poll["accessToken"].(string), poll["refreshToken"].(string)
}

func TestRefreshAcceptsTheTokenInABody(t *testing.T) {
	t.Parallel()

	_, engine := newTestServer(t)
	_, refresh := signInViaHTTP(t, engine)

	rec, body := doJSON(t, engine, http.MethodPost, "/v1/auth/refresh",
		map[string]any{"refreshToken": refresh}, nil)
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d: %s", rec.Code, rec.Body.String())
	}
	if body["status"] != StatusAuthorized {
		t.Errorf("status = %v, want authorized", body["status"])
	}
	if body["accessToken"] == refresh {
		t.Error("the refresh response handed back the token it was given")
	}
}

// §8.11 asked whether this endpoint takes a body field or a header. It takes
// either, so neither choice can be the one that breaks the client.
func TestRefreshAlsoAcceptsTheTokenAsABearer(t *testing.T) {
	t.Parallel()

	_, engine := newTestServer(t)
	_, refresh := signInViaHTTP(t, engine)

	rec, body := doJSON(t, engine, http.MethodPost, "/v1/auth/refresh", nil,
		map[string]string{"Authorization": "Bearer " + refresh})
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d: %s", rec.Code, rec.Body.String())
	}
	if body["status"] != StatusAuthorized {
		t.Errorf("status = %v, want authorized", body["status"])
	}
}

func TestRefreshReplayAnswers409AndRevokesTheChain(t *testing.T) {
	t.Parallel()

	h, engine := newTestServer(t)
	_, refresh := signInViaHTTP(t, engine)

	rec, _ := doJSON(t, engine, http.MethodPost, "/v1/auth/refresh",
		map[string]any{"refreshToken": refresh}, nil)
	if rec.Code != http.StatusOK {
		t.Fatalf("the first rotation = %d", rec.Code)
	}

	rec, body := doJSON(t, engine, http.MethodPost, "/v1/auth/refresh",
		map[string]any{"refreshToken": refresh}, nil)
	// §15.3: reuse is a 409, and the client treats 409 as terminal, which is
	// what forces the user back to the QR.
	if rec.Code != http.StatusConflict {
		t.Fatalf("the replay = %d, want 409: %s", rec.Code, rec.Body.String())
	}
	errObj, _ := body["error"].(map[string]any)
	if errObj == nil || errObj["code"] == nil {
		t.Fatalf("expected the standard error envelope, got %s", rec.Body.String())
	}
	if h.store.revokeCount() == 0 {
		t.Fatal("the chain was not revoked")
	}
}

func TestATransientRefreshAnswers503AndNotExpired(t *testing.T) {
	t.Parallel()

	// This is the assertion that keeps paying customers signed in. A 503 is
	// one of the four errors the client's poller treats as "keep waiting"; a
	// status:"expired" body is how it decides to clear its stored refresh
	// token. Getting this backwards signs people out over a database blip.
	h, engine := newTestServer(t)
	_, refresh := signInViaHTTP(t, engine)
	h.store.setRotateErr(errors.New("connection refused"))

	rec, body := doJSON(t, engine, http.MethodPost, "/v1/auth/refresh",
		map[string]any{"refreshToken": refresh}, nil)
	if rec.Code != http.StatusServiceUnavailable {
		t.Fatalf("status = %d, want 503: %s", rec.Code, rec.Body.String())
	}
	if body["status"] == StatusExpired {
		t.Fatal("the response told the client the session is over")
	}
	if _, ok := body["accessToken"]; ok {
		t.Error("a transient response handed out a token")
	}
	if _, ok := body["refreshToken"]; ok {
		t.Error("a transient response echoed the refresh token")
	}

	// And the retry works, which is only true because the stored token
	// survived the failure.
	h.store.setRotateErr(nil)
	rec, body = doJSON(t, engine, http.MethodPost, "/v1/auth/refresh",
		map[string]any{"refreshToken": refresh}, nil)
	if rec.Code != http.StatusOK || body["status"] != StatusAuthorized {
		t.Fatalf("the retry after the blip = %d %v", rec.Code, body["status"])
	}
}

func TestRefreshingAnExpiredTokenAnswersTheExpiredStateWord(t *testing.T) {
	t.Parallel()

	_, engine := newTestServer(t)
	rec, body := doJSON(t, engine, http.MethodPost, "/v1/auth/refresh",
		map[string]any{"refreshToken": "wcb1_never-existed"}, nil)
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200", rec.Code)
	}
	if body["status"] != StatusExpired {
		t.Errorf("status = %v, want expired", body["status"])
	}
}

// ---------------------------------------------------------------------------
// Bearer auth
// ---------------------------------------------------------------------------

func TestAccountRequiresABearerToken(t *testing.T) {
	t.Parallel()

	_, engine := newTestServer(t)
	for _, path := range []string{"/v1/account"} {
		rec, _ := doJSON(t, engine, http.MethodGet, path, nil, nil)
		if rec.Code != http.StatusUnauthorized {
			t.Errorf("GET %s with no token = %d, want 401", path, rec.Code)
		}
		for _, bad := range []string{"", "Bearer ", "Bearer not-a-token", "Basic " + testSecret} {
			rec, _ = doJSON(t, engine, http.MethodGet, path, nil, map[string]string{"Authorization": bad})
			if rec.Code != http.StatusUnauthorized {
				t.Errorf("GET %s with %q = %d, want 401", path, bad, rec.Code)
			}
		}
	}
}

func TestAForgedBearerTokenIsRefused(t *testing.T) {
	t.Parallel()

	h, engine := newTestServer(t)
	h.store.seedAccount("acct-1")

	other, _ := token.NewIssuer("ffffffffffffffffffffffffffffffffffffffff", time.Hour, 24*time.Hour)
	forged, _ := other.Issue("acct-1", "")

	for _, raw := range []string{
		forged.Access.Token,
		"eyJhbGciOiJub25lIn0.eyJzdWIiOiJhY2N0LTEifQ.",
		strings.Repeat("a", 200),
	} {
		rec, _ := doJSON(t, engine, http.MethodGet, "/v1/account", nil,
			map[string]string{"Authorization": "Bearer " + raw})
		if rec.Code != http.StatusUnauthorized {
			t.Errorf("a forged token gave %d, want 401", rec.Code)
		}
	}
}

func TestAnExpiredAccessTokenIsRefused(t *testing.T) {
	t.Parallel()

	h, engine := newTestServer(t)
	access, _ := signInViaHTTP(t, engine)
	h.clock = h.clock.Add(2 * testAccessTTL)

	rec, _ := doJSON(t, engine, http.MethodGet, "/v1/account", nil,
		map[string]string{"Authorization": "Bearer " + access})
	if rec.Code != http.StatusUnauthorized {
		t.Errorf("an expired token gave %d, want 401", rec.Code)
	}
}

func TestTheBearerSchemeIsCaseInsensitive(t *testing.T) {
	t.Parallel()

	_, engine := newTestServer(t)
	access, _ := signInViaHTTP(t, engine)
	for _, scheme := range []string{"Bearer ", "bearer ", "BEARER "} {
		rec, _ := doJSON(t, engine, http.MethodGet, "/v1/account", nil,
			map[string]string{"Authorization": scheme + access})
		if rec.Code == http.StatusUnauthorized {
			t.Errorf("the %q scheme was rejected; RFC 7235 says it is case-insensitive", strings.TrimSpace(scheme))
		}
	}
}

// ---------------------------------------------------------------------------
// Revocation
// ---------------------------------------------------------------------------

func TestRevokeWithARefreshTokenEndsTheSession(t *testing.T) {
	t.Parallel()

	h, engine := newTestServer(t)
	access, refresh := signInViaHTTP(t, engine)

	rec, _ := doJSON(t, engine, http.MethodDelete, "/v1/auth/session",
		map[string]any{"refreshToken": refresh}, nil)
	if rec.Code != http.StatusNoContent {
		t.Fatalf("status = %d, want 204: %s", rec.Code, rec.Body.String())
	}

	claims, _ := h.svc.issuer.Verify(access)
	if !h.store.isRevoked(claims.ChainID) {
		t.Fatal("the chain is still live")
	}
	rec, _ = doJSON(t, engine, http.MethodGet, "/v1/account", nil,
		map[string]string{"Authorization": "Bearer " + access})
	if rec.Code != http.StatusUnauthorized && rec.Code != http.StatusOK {
		t.Fatalf("unexpected status after revocation: %d", rec.Code)
	}
	rec, body := doJSON(t, engine, http.MethodPost, "/v1/auth/refresh",
		map[string]any{"refreshToken": refresh}, nil)
	if body["status"] != StatusExpired {
		t.Errorf("a revoked session still refreshed: %v", body["status"])
	}
}

// §8.11 asked whether DELETE revokes the refresh or the access token. It
// accepts either, and an expired access token still works for the purpose —
// signing out is exactly when the token is most likely to be dead, and a
// server that then refuses is a server that leaves people stuck.
func TestRevokeWithAnExpiredAccessTokenStillSignsOut(t *testing.T) {
	t.Parallel()

	h, engine := newTestServer(t)
	access, _ := signInViaHTTP(t, engine)
	claims, _ := h.svc.issuer.Verify(access)

	h.clock = h.clock.Add(2 * testAccessTTL)

	rec, _ := doJSON(t, engine, http.MethodDelete, "/v1/auth/session", nil,
		map[string]string{"Authorization": "Bearer " + access})
	if rec.Code != http.StatusNoContent {
		t.Fatalf("status = %d, want 204", rec.Code)
	}
	if !h.store.isRevoked(claims.ChainID) {
		t.Fatal("an expired access token could not revoke its chain")
	}
}

func TestRevokeWithNothingIsStill204(t *testing.T) {
	t.Parallel()

	// Signing out twice, or on a machine that never signed in, is not an
	// error. Making it one produces a scary dialog for the user and buys
	// nothing.
	_, engine := newTestServer(t)
	rec, _ := doJSON(t, engine, http.MethodDelete, "/v1/auth/session", nil, nil)
	if rec.Code != http.StatusNoContent {
		t.Errorf("status = %d, want 204", rec.Code)
	}
}

// ---------------------------------------------------------------------------
// Logging
// ---------------------------------------------------------------------------

// The secret never reaches a log line. This runs the whole flow against a
// logger that keeps everything in memory and then greps it.
func TestNothingSensitiveReachesTheLog(t *testing.T) {
	t.Parallel()

	var sink bytes.Buffer
	wc := newWechatStub(t)
	st := newFakeStore()
	issuer, _ := token.NewIssuer(testSecret, testAccessTTL, testRefreshTTL)
	client := wechat.NewClient(wechat.Config{
		AppID: testAppID, AppSecret: testAppSecret,
		AuthURL: "https://open.weixin.qq.com/connect/qrconnect",
		APIBase: wc.srv.URL, Timeout: 5 * time.Second,
	})

	clock := time.Now().UTC()
	issuer.SetClock(func() time.Time { return clock })

	svc, err := NewService(Config{
		PublicBaseURL: testPublicBase, AuthSessionTTL: testSessionTTL,
	}, Options{
		Store: st, WeChat: client, Issuer: issuer,
		Handoff: NewMemoryHandoff(func() time.Time { return clock }),
		Now:     func() time.Time { return clock },
		Logger:  slog.New(slog.NewJSONHandler(&sink, nil)),
	})
	if err != nil {
		t.Fatalf("NewService: %v", err)
	}

	created, err := svc.CreateSession(t.Context(), "install-1", "device-1")
	if err != nil {
		t.Fatalf("CreateSession: %v", err)
	}
	if _, err := svc.HandleCallback(t.Context(), "auth-code", created.Ticket); err != nil {
		t.Fatalf("HandleCallback: %v", err)
	}
	poll, err := svc.Poll(t.Context(), created.Ticket)
	if err != nil {
		t.Fatalf("Poll: %v", err)
	}
	if _, err := svc.Refresh(t.Context(), poll.RefreshToken); err != nil {
		t.Fatalf("Refresh: %v", err)
	}

	logged := sink.String()
	if logged == "" {
		t.Fatal("the flow produced no log lines at all, so this test proves nothing")
	}
	forbidden := map[string]string{
		testAppSecret:     "the WeChat app secret",
		poll.AccessToken:  "an access token",
		poll.RefreshToken: "a refresh token",
		"oTESTOPENID":     "an openid",
	}
	for secret, what := range forbidden {
		if secret == "" {
			continue
		}
		if strings.Contains(logged, secret) {
			t.Errorf("%s was written to the log:\n%s", what, logged)
		}
	}
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

func keysOf(m map[string]any) []string {
	out := make([]string, 0, len(m))
	for k := range m {
		out = append(out, k)
	}
	return out
}

// assertAllLowerCamel walks a decoded response and fails on any key that is
// not lowerCamelCase. It catches a PascalCase key and a snake_case key alike,
// which is what a missing struct tag produces and a single-field assertion
// would miss.
func assertAllLowerCamel(t *testing.T, path string, v any) {
	t.Helper()
	switch typed := v.(type) {
	case map[string]any:
		// The error envelope is httpx's, not ours. Its request_id is
		// snake_case on purpose, decided in that package, and re-deciding it
		// here would break the client for the sake of a tidy test.
		if _, isErrorEnvelope := typed["error"]; isErrorEnvelope && len(typed) == 1 {
			return
		}
		for key, child := range typed {
			if !isLowerCamel(key) {
				t.Errorf("%s.%s is not lowerCamelCase", path, key)
			}
			assertAllLowerCamel(t, path+"."+key, child)
		}
	case []any:
		for _, child := range typed {
			assertAllLowerCamel(t, path+"[]", child)
		}
	}
}

func isLowerCamel(s string) bool {
	if s == "" {
		return false
	}
	if s[0] < 'a' || s[0] > 'z' {
		return false
	}
	for _, r := range s {
		switch {
		case r >= 'a' && r <= 'z', r >= 'A' && r <= 'Z', r >= '0' && r <= '9':
		default:
			return false
		}
	}
	return true
}
