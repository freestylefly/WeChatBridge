package wechat

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
)

// newTestClient points a client at a stub server. The whole point of using a
// stub is that the redirect domain cannot be 127.0.0.1 in production, so
// nothing here may depend on a real WeChat being reachable — and equally, the
// tests must not quietly pass against a base URL the client does not use.
func newTestClient(t *testing.T, srv *httptest.Server) *Client {
	t.Helper()
	return NewClient(Config{
		AppID:     "wxappid0001",
		AppSecret: "s3cr3t-value-that-must-never-be-logged",
		AuthURL:   "https://open.weixin.qq.com/connect/qrconnect",
		APIBase:   srv.URL,
		Timeout:   2 * 1e9, // 2s, expressed without importing time twice
	})
}

func TestAuthorizeURLCarriesEveryParameterWechatNeeds(t *testing.T) {
	// The test that exists because dropping the fragment produces a blank
	// page rather than an error: the QR scans, nothing happens, and the bug is
	// reported as "the code does not work" weeks later.
	t.Parallel()

	c := NewClient(Config{
		AppID:   "wxappid0001",
		AuthURL: "https://open.weixin.qq.com/connect/qrconnect",
	})

	const redirect = "https://api.example.com/v1/auth/wechat/callback"
	raw := c.AuthorizeURL("ticket-abc", redirect)

	if !strings.HasSuffix(raw, "#wechat_redirect") {
		t.Fatalf("authorize URL must end with the #wechat_redirect fragment, got %q", raw)
	}
	if strings.Contains(raw, "%23wechat_redirect") {
		t.Fatalf("the fragment must not be percent-encoded into the query, got %q", raw)
	}

	// Split off the fragment before parsing: url.Parse keeps it in Fragment,
	// and the fragment is not part of what the server receives.
	parsed, err := url.Parse(raw)
	if err != nil {
		t.Fatalf("authorize URL does not parse: %v", err)
	}
	if parsed.Fragment != "wechat_redirect" {
		t.Fatalf("fragment = %q, want wechat_redirect", parsed.Fragment)
	}

	q := parsed.Query()
	for _, tc := range []struct{ name, want string }{
		{"appid", "wxappid0001"},
		{"redirect_uri", redirect},
		{"response_type", "code"},
		{"scope", "snsapi_login"},
		{"state", "ticket-abc"},
	} {
		if got := q.Get(tc.name); got != tc.want {
			t.Errorf("query %s = %q, want %q", tc.name, got, tc.want)
		}
	}

	// scope must be snsapi_login and not snsapi_userinfo: the latter makes
	// WeChat show a second consent screen, and a login flow that can be
	// refused is not a login flow.
	if q.Get("scope") != Scope {
		t.Errorf("scope = %q, want %q", q.Get("scope"), Scope)
	}
	// A secret in the browser-facing URL would be a secret in a browser
	// history.
	if q.Get("secret") != "" {
		t.Error("the authorize URL must not carry the app secret")
	}
}

func TestAuthorizeURLDoesNotDoubleSlashTheBase(t *testing.T) {
	t.Parallel()
	c := NewClient(Config{AppID: "wx", AuthURL: "https://open.weixin.qq.com/connect/qrconnect/"})
	if got := c.AuthorizeURL("s", "https://api.example.com/cb"); strings.Contains(got, "//?") {
		t.Fatalf("trailing slash on the auth base produced %q", got)
	}
}

func TestExchangeCodeReadsTheBodyNotTheStatus(t *testing.T) {
	t.Parallel()

	var gotQuery url.Values
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/sns/oauth2/access_token" {
			t.Errorf("unexpected path %q", r.URL.Path)
		}
		gotQuery = r.URL.Query()
		w.Header().Set("Content-Type", "application/json")
		// 200 OK with a success body: the ordinary case.
		_, _ = w.Write([]byte(`{"access_token":"ACCESS","expires_in":7200,` +
			`"refresh_token":"REFRESH","openid":"oABC","scope":"snsapi_login","unionid":"uXYZ"}`))
	}))
	defer srv.Close()

	c := newTestClient(t, srv)
	sess, err := c.ExchangeCode(context.Background(), "the-code")
	if err != nil {
		t.Fatalf("ExchangeCode: %v", err)
	}
	if sess.OpenID != "oABC" {
		t.Errorf("openid = %q, want oABC", sess.OpenID)
	}
	if sess.UnionID != "uXYZ" {
		t.Errorf("unionid = %q, want uXYZ", sess.UnionID)
	}
	if sess.ExpiresIn.Seconds() != 7200 {
		t.Errorf("expires_in = %v, want 7200s", sess.ExpiresIn)
	}
	if gotQuery.Get("grant_type") != "authorization_code" {
		t.Errorf("grant_type = %q", gotQuery.Get("grant_type"))
	}
	if gotQuery.Get("appid") != "wxappid0001" {
		t.Errorf("appid = %q", gotQuery.Get("appid"))
	}
}

// The one that gets everybody: WeChat answers errors with HTTP 200 and a JSON
// body that says otherwise. A client that branches on the status code reads
// this as a success and fails later, on a missing field, somewhere unrelated.
func TestExchangeCodeTreatsA200WithAnErrcodeAsAFailure(t *testing.T) {
	t.Parallel()

	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusOK) // lying, and WeChat really does this
		_, _ = w.Write([]byte(`{"errcode":40029,"errmsg":"invalid code, hints: [ req_id: xxx ]"}`))
	}))
	defer srv.Close()

	c := newTestClient(t, srv)
	_, err := c.ExchangeCode(context.Background(), "stale-code")
	if err == nil {
		t.Fatal("expected an error for a 200 whose body carries errcode 40029")
	}
	var apiErr *APIError
	if !errors.As(err, &apiErr) {
		t.Fatalf("error is %T, want *APIError: %v", err, err)
	}
	if apiErr.ErrCode != 40029 {
		t.Errorf("errcode = %d, want 40029", apiErr.ErrCode)
	}
	if IsTransient(err) {
		t.Error("a bad code is not transient — retrying it can never succeed")
	}
}

func TestExchangeCodeClassifiesWechatSystemBusyAsTransient(t *testing.T) {
	t.Parallel()

	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte(`{"errcode":-1,"errmsg":"system error"}`))
	}))
	defer srv.Close()

	c := newTestClient(t, srv)
	_, err := c.ExchangeCode(context.Background(), "code")
	if err == nil {
		t.Fatal("expected an error")
	}
	if !IsTransient(err) {
		t.Errorf("errcode -1 must be transient so the ticket is not burned: %v", err)
	}
}

func TestExchangeCodeTreatsA500AsTransient(t *testing.T) {
	t.Parallel()

	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusInternalServerError)
		_, _ = w.Write([]byte(`{"errcode":500,"errmsg":"internal error"}`))
	}))
	defer srv.Close()

	c := newTestClient(t, srv)
	_, err := c.ExchangeCode(context.Background(), "code")
	if err == nil {
		t.Fatal("expected an error")
	}
	if !IsTransient(err) {
		t.Errorf("a 500 must be transient: %v", err)
	}
}

func TestExchangeCodeOnA200WithNoOpenidIsMalformedNotSuccess(t *testing.T) {
	t.Parallel()

	// Neither an error nor a usable answer. Reading it as success signs
	// nobody in and reports success, which is the worst of both.
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte(`{"errcode":0,"errmsg":"ok"}`))
	}))
	defer srv.Close()

	c := newTestClient(t, srv)
	_, err := c.ExchangeCode(context.Background(), "code")
	if err == nil {
		t.Fatal("a success-shaped body with no openid must not be reported as success")
	}
	var mal *MalformedError
	if !errors.As(err, &mal) {
		t.Fatalf("error is %T, want *MalformedError: %v", err, err)
	}
	if IsTransient(err) {
		t.Error("a consistently wrong answer is not transient")
	}
}

func TestExchangeCodeOnAnHTMLBodyIsMalformed(t *testing.T) {
	t.Parallel()

	// A captive portal or a misconfigured proxy. The status is 200, so only
	// the body tells the truth.
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/html")
		_, _ = w.Write([]byte(`<html><body>Sign in to the network</body></html>`))
	}))
	defer srv.Close()

	c := newTestClient(t, srv)
	_, err := c.ExchangeCode(context.Background(), "code")
	var mal *MalformedError
	if !errors.As(err, &mal) {
		t.Fatalf("error is %T, want *MalformedError: %v", err, err)
	}
}

func TestExchangeCodeRejectsAnEmptyCodeWithoutCallingOut(t *testing.T) {
	t.Parallel()

	called := false
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		called = true
	}))
	defer srv.Close()

	c := newTestClient(t, srv)
	if _, err := c.ExchangeCode(context.Background(), "  "); err == nil {
		t.Fatal("an empty code must not reach the network")
	}
	if called {
		t.Error("the stub was called with an empty code")
	}
}

func TestTransportFailureIsTransient(t *testing.T) {
	t.Parallel()

	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {}))
	addr := srv.URL
	srv.Close() // nothing is listening now

	c := NewClient(Config{AppID: "wx", AppSecret: "s", APIBase: addr, Timeout: 1 * 1e9})
	_, err := c.ExchangeCode(context.Background(), "code")
	if err == nil {
		t.Fatal("expected a transport error")
	}
	if !IsTransient(err) {
		t.Errorf("a refused connection must be transient: %v", err)
	}
}

// The secret travels in the query string, so an error that quoted the URL
// would write it to every log that caught the failure.
func TestErrorsNeverCarryTheSecret(t *testing.T) {
	t.Parallel()

	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte(`{"errcode":40013,"errmsg":"invalid appid"}`))
	}))
	defer srv.Close()

	c := newTestClient(t, srv)
	_, err := c.ExchangeCode(context.Background(), "code")
	if err == nil {
		t.Fatal("expected an error")
	}
	if strings.Contains(err.Error(), "s3cr3t-value") {
		t.Fatalf("error message leaked the app secret: %v", err)
	}
}

func TestFetchProfileToleratesAMissingScope(t *testing.T) {
	t.Parallel()

	// 42003 is the expected answer under snsapi_login, not a fault. Failing a
	// sign-in over a missing display name would be a bad trade.
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte(`{"errcode":42003,"errmsg":"insufficient scope"}`))
	}))
	defer srv.Close()

	c := newTestClient(t, srv)
	profile, err := c.FetchProfile(context.Background(), "ACCESS", "oABC")
	if err != nil {
		t.Fatalf("a missing profile scope must not fail the flow: %v", err)
	}
	if profile.Nickname != "" || profile.AvatarURL != "" {
		t.Errorf("expected an empty profile, got %+v", profile)
	}
}

func TestFetchProfileReadsTheWechatFieldNames(t *testing.T) {
	t.Parallel()

	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte(`{"nickname":"小明","headimgurl":"https://thirdwx.qlogo.cn/a","openid":"oABC"}`))
	}))
	defer srv.Close()

	c := newTestClient(t, srv)
	profile, err := c.FetchProfile(context.Background(), "ACCESS", "oABC")
	if err != nil {
		t.Fatalf("FetchProfile: %v", err)
	}
	if profile.Nickname != "小明" {
		t.Errorf("nickname = %q", profile.Nickname)
	}
	if profile.AvatarURL == "" {
		t.Error("avatar URL was not mapped out of headimgurl")
	}
}

func TestFetchProfileIsSkippedWithoutAToken(t *testing.T) {
	t.Parallel()

	called := false
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { called = true }))
	defer srv.Close()

	c := newTestClient(t, srv)
	if _, err := c.FetchProfile(context.Background(), "", ""); err != nil {
		t.Fatalf("err = %v", err)
	}
	if called {
		t.Error("no request should be made without a token")
	}
}

func TestTheClientDoesNotUseTheDefaultHTTPClient(t *testing.T) {
	t.Parallel()

	c := NewClient(Config{AppID: "wx", AppSecret: "s", AuthURL: "https://x/y", APIBase: "https://api.weixin.qq.com"})
	if c.http == http.DefaultClient {
		t.Fatal("the client shares http.DefaultClient, which has no timeout")
	}
	if c.http.Timeout <= 0 {
		t.Fatal("the client has no per-request timeout; a stalled login hangs forever")
	}
	if c.http.Transport == nil {
		t.Fatal("the client uses the shared default transport, so no connection pool of its own")
	}
}

func TestContextCancellationIsTransient(t *testing.T) {
	t.Parallel()

	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		<-r.Context().Done()
	}))
	defer srv.Close()

	c := newTestClient(t, srv)
	ctx, cancel := context.WithCancel(context.Background())
	cancel()

	_, err := c.ExchangeCode(ctx, "code")
	if err == nil {
		t.Fatal("expected an error for a cancelled context")
	}
	if !IsTransient(err) {
		t.Errorf("a cancelled or timed-out request is transient: %v", err)
	}
}
