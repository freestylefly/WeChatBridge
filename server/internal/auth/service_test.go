package auth

import (
	"context"
	"errors"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
	"github.com/freestylefly/WeChatBridge/server/internal/token"
	"github.com/freestylefly/WeChatBridge/server/internal/wechat"
)

const (
	testSecret     = "0123456789abcdef0123456789abcdef0123456789abcdef"
	testPublicBase = "https://api.example.com"
	testAppID      = "wxappid0001"
	testAppSecret  = "app-secret-never-logged"
	testAccessTTL  = time.Hour
	testRefreshTTL = 30 * 24 * time.Hour
	testSessionTTL = 5 * time.Minute
)

// wechatStub is a fake open platform. Every field is settable per test because
// the interesting cases are all "WeChat answered something other than what it
// was supposed to".
type wechatStub struct {
	srv *httptest.Server

	// exchangeHandler overrides the token endpoint when a test needs a
	// specific answer.
	exchangeHandler http.HandlerFunc
	profileHandler  http.HandlerFunc

	exchangeCalls int
	lastSecret    string
	lastCode      string
}

func newWechatStub(t *testing.T) *wechatStub {
	t.Helper()
	w := &wechatStub{}
	mux := http.NewServeMux()
	mux.HandleFunc("/sns/oauth2/access_token", func(rw http.ResponseWriter, r *http.Request) {
		w.exchangeCalls++
		w.lastSecret = r.URL.Query().Get("secret")
		w.lastCode = r.URL.Query().Get("code")
		if w.exchangeHandler != nil {
			w.exchangeHandler(rw, r)
			return
		}
		rw.Header().Set("Content-Type", "application/json")
		_, _ = rw.Write([]byte(`{"access_token":"OAUTH","expires_in":7200,` +
			`"openid":"oTESTOPENID","scope":"snsapi_login","unionid":"uTESTUNION"}`))
	})
	mux.HandleFunc("/sns/userinfo", func(rw http.ResponseWriter, r *http.Request) {
		if w.profileHandler != nil {
			w.profileHandler(rw, r)
			return
		}
		// The realistic answer under snsapi_login: no scope, no profile.
		rw.Header().Set("Content-Type", "application/json")
		_, _ = rw.Write([]byte(`{"errcode":42003,"errmsg":"insufficient scope"}`))
	})
	w.srv = httptest.NewServer(mux)
	t.Cleanup(w.srv.Close)
	return w
}

type harness struct {
	svc   *Service
	store *fakeStore
	clock time.Time
	wc    *wechatStub
}

func newHarness(t *testing.T) *harness {
	t.Helper()

	wc := newWechatStub(t)
	st := newFakeStore()
	h := &harness{store: st, clock: time.Date(2026, 10, 18, 12, 0, 0, 0, time.UTC), wc: wc}

	issuer, err := token.NewIssuer(testSecret, testAccessTTL, testRefreshTTL)
	if err != nil {
		t.Fatalf("token.NewIssuer: %v", err)
	}
	// One clock for the service, the issuer and the handoff, so ageing a
	// ticket in a test does not need a sleep.
	h.clock = time.Date(2026, 10, 18, 12, 0, 0, 0, time.UTC)
	issuer.SetClock(func() time.Time { return h.clock })

	client := wechat.NewClient(wechat.Config{
		AppID:     testAppID,
		AppSecret: testAppSecret,
		AuthURL:   "https://open.weixin.qq.com/connect/qrconnect",
		APIBase:   wc.srv.URL,
		Timeout:   5 * time.Second,
	})

	svc, err := NewService(Config{
		PublicBaseURL:         testPublicBase,
		AuthSessionTTL:        testSessionTTL,
		MaxOutstandingTickets: 3,
	}, Options{
		Store:   st,
		WeChat:  client,
		Issuer:  issuer,
		Handoff: NewMemoryHandoff(func() time.Time { return h.clock }),
		Now:     func() time.Time { return h.clock },
		Logger:  discardLogger(),
	})
	if err != nil {
		t.Fatalf("auth.NewService: %v", err)
	}
	h.svc = svc
	return h
}

// signIn runs the whole happy path and returns the credentials the poller
// would have collected.
func (h *harness) signIn(t *testing.T, installID string) (ticket string, creds PollResult) {
	t.Helper()
	created, err := h.svc.CreateSession(context.Background(), installID, "device-1")
	if err != nil {
		t.Fatalf("CreateSession: %v", err)
	}
	if _, err := h.svc.HandleCallback(context.Background(), "auth-code", created.Ticket); err != nil {
		t.Fatalf("HandleCallback: %v", err)
	}
	poll, err := h.svc.Poll(context.Background(), created.Ticket)
	if err != nil {
		t.Fatalf("Poll: %v", err)
	}
	return created.Ticket, poll
}

func discardLogger() *slog.Logger { return slog.New(slog.DiscardHandler) }

// ---------------------------------------------------------------------------
// Ticket creation
// ---------------------------------------------------------------------------

func TestCreateSessionReturnsEverythingTheClientNeeds(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	created, err := h.svc.CreateSession(context.Background(), "install-1", "device-1")
	if err != nil {
		t.Fatalf("CreateSession: %v", err)
	}

	if created.Ticket == "" {
		t.Fatal("no ticket")
	}
	if !strings.HasPrefix(created.QRImageURL, testPublicBase) {
		t.Errorf("qrImageUrl must be absolute, got %q", created.QRImageURL)
	}
	if created.QRImageURL != testPublicBase+QRImagePath(created.Ticket) {
		t.Errorf("qrImageUrl = %q", created.QRImageURL)
	}
	parsed, err := url.Parse(created.AuthorizeURL)
	if err != nil {
		t.Fatalf("authorize URL does not parse: %v", err)
	}
	q := parsed.Query()
	// The state has to be the ticket: it is what comes back on the redirect
	// and it is the only thing that ties a scan to this desktop process.
	if q.Get("state") != created.Ticket {
		t.Errorf("state = %q, want the ticket %q", q.Get("state"), created.Ticket)
	}
	if parsed.Fragment != "wechat_redirect" {
		t.Errorf("authorize URL lost the #wechat_redirect fragment: %q", created.AuthorizeURL)
	}
	if got, want := q.Get("redirect_uri"), testPublicBase+CallbackPath; got != want {
		t.Errorf("redirect_uri = %q, want %q", got, want)
	}
	if got, want := created.ExpiresAt, h.clock.Add(testSessionTTL); !got.Equal(want) {
		t.Errorf("expiresAt = %v, want %v", got, want)
	}

	if _, err := h.svc.Poll(context.Background(), created.Ticket); err != nil {
		t.Fatalf("Poll: %v", err)
	}
	session, ok := h.store.authSessionByTicket(created.Ticket)
	if !ok {
		t.Fatal("the ticket was not written to the store")
	}
	if session.State != store.AuthPending {
		t.Errorf("state = %q, want pending", session.State)
	}
	if session.InstallID != "install-1" {
		t.Errorf("install id was not recorded: %q", session.InstallID)
	}
}

func TestTicketsAreDistinctPerRequest(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	seen := map[string]bool{}
	// Three is the per-install cap, so each round gets its own install id —
	// otherwise this test would be measuring the limiter rather than the
	// issuer.
	for n := 0; n < 20; n++ {
		install := "install-" + strconv.Itoa(n)
		created, err := h.svc.CreateSession(context.Background(), install, "device-1")
		if err != nil {
			t.Fatalf("CreateSession %d: %v", n, err)
		}
		if seen[created.Ticket] {
			t.Fatal("the issuer handed out the same ticket twice")
		}
		seen[created.Ticket] = true
	}
}

func TestTheAppSecretNeverReachesTheClient(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	created, err := h.svc.CreateSession(context.Background(), "install-1", "device-1")
	if err != nil {
		t.Fatalf("CreateSession: %v", err)
	}
	if strings.Contains(created.AuthorizeURL, testAppSecret) {
		t.Fatalf("the authorize URL leaked the app secret: %q", created.AuthorizeURL)
	}
	if strings.Contains(created.QRImageURL, testAppSecret) {
		t.Fatal("the QR URL leaked the app secret")
	}
}

func TestRepeatedQRRequestsAreCapped(t *testing.T) {
	t.Parallel()

	// §15.2: without a cap, hammering "get me a new code" is both a bad
	// experience and a cheap way to flood the table.
	h := newHarness(t)
	var err error
	for n := 0; n < 3; n++ {
		if _, err = h.svc.CreateSession(context.Background(), "install-1", "device-1"); err != nil {
			t.Fatalf("CreateSession %d: %v", n, err)
		}
	}
	_, err = h.svc.CreateSession(context.Background(), "install-1", "device-1")
	if !IsRateLimited(err) {
		t.Fatalf("the fourth ticket for one install should be refused, got %v", err)
	}

	// A different install is a different person and gets its own budget.
	if _, err := h.svc.CreateSession(context.Background(), "install-2", "device-1"); err != nil {
		t.Fatalf("a second install was refused: %v", err)
	}

	// And once the old tickets age out, the budget comes back.
	h.clock = h.clock.Add(2 * testSessionTTL)
	if _, err := h.svc.CreateSession(context.Background(), "install-1", "device-1"); err != nil {
		t.Fatalf("the budget did not come back after the tickets expired: %v", err)
	}
}

// ---------------------------------------------------------------------------
// The state machine
// ---------------------------------------------------------------------------

func TestPollingAPendingTicketReportsPending(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	created, err := h.svc.CreateSession(context.Background(), "install-1", "device-1")
	if err != nil {
		t.Fatalf("CreateSession: %v", err)
	}
	poll, err := h.svc.Poll(context.Background(), created.Ticket)
	if err != nil {
		t.Fatalf("Poll: %v", err)
	}
	if poll.Status != StatusPending {
		t.Errorf("status = %q, want pending", poll.Status)
	}
	if poll.AccessToken != "" || poll.RefreshToken != "" {
		t.Error("a pending ticket must not carry tokens")
	}
	if poll.Account != nil {
		t.Error("a pending ticket must not carry an account")
	}
	if !poll.ExpiresAt.Equal(created.ExpiresAt) {
		t.Errorf("poll expiresAt = %v, want %v", poll.ExpiresAt, created.ExpiresAt)
	}
}

func TestAnExpiredTicketReportsExpiredRatherThanDisappearing(t *testing.T) {
	t.Parallel()

	// §15.2: the client needs a definite terminal state, not a 404. The 404
	// path exists and is a fallback; this is the normal way a ticket ends.
	h := newHarness(t)
	created, err := h.svc.CreateSession(context.Background(), "install-1", "device-1")
	if err != nil {
		t.Fatalf("CreateSession: %v", err)
	}

	h.clock = h.clock.Add(testSessionTTL + time.Second)
	poll, err := h.svc.Poll(context.Background(), created.Ticket)
	if err != nil {
		t.Fatalf("Poll: %v", err)
	}
	if poll.Status != StatusExpired {
		t.Fatalf("status = %q, want expired", poll.Status)
	}
}

func TestAnUnknownTicketReportsExpired(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	poll, err := h.svc.Poll(context.Background(), "no-such-ticket")
	if err != nil {
		t.Fatalf("a ticket we never issued must not be an error: %v", err)
	}
	if poll.Status != StatusExpired {
		t.Errorf("status = %q, want expired", poll.Status)
	}
}

func TestAnEmptyTicketIsExpiredNotAnError(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	poll, err := h.svc.Poll(context.Background(), "   ")
	if err != nil {
		t.Fatalf("err = %v", err)
	}
	if poll.Status != StatusExpired {
		t.Errorf("status = %q, want expired", poll.Status)
	}
}

// A row this process did not write, holding a state this process does not
// recognise. The store's contract says it maps those to expired and never
// returns one, so this arm is unreachable against a conforming store — which is
// exactly why the default has to be safe. A panic here would be a request
// handler killed by a string in a database column.
func TestAnUnknownStateStringDoesNotPanic(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	created, err := h.svc.CreateSession(context.Background(), "install-1", "device-1")
	if err != nil {
		t.Fatalf("CreateSession: %v", err)
	}

	for _, state := range []store.AuthSessionState{
		store.AuthSessionState("Authorized"), // wrong case
		store.AuthSessionState("AUTHORIZED"), // shouting
		store.AuthSessionState("done"),       // a future word
		store.AuthSessionState(""),           // empty
		store.AuthSessionState("pe\ngant"),   // control characters
		store.AuthSessionState("🔐"),          // not even text
	} {
		h.store.forceAuthSessionState(created.Ticket, state)
		poll, err := h.svc.Poll(context.Background(), created.Ticket)
		if err != nil {
			t.Errorf("state %q produced an error: %v", state, err)
			continue
		}
		if poll.Status != StatusExpired {
			t.Errorf("state %q reported as %q, want expired", state, poll.Status)
		}
	}
}

func TestAnUnknownStateWhileStillPendingDoesNotHideTheDeadline(t *testing.T) {
	t.Parallel()

	// The client judges expiry from expiresAt. Reporting an unknown state
	// without it would leave the poller with no deadline.
	h := newHarness(t)
	created, _ := h.svc.CreateSession(context.Background(), "install-1", "device-1")
	h.store.forceAuthSessionState(created.Ticket, store.AuthSessionState("something-new"))

	poll, err := h.svc.Poll(context.Background(), created.Ticket)
	if err != nil {
		t.Fatalf("Poll: %v", err)
	}
	if poll.ExpiresAt.IsZero() {
		t.Error("an unknown state must still carry the deadline")
	}
}

func TestRejectedAndExpiredStatesAreReportedAsStored(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	created, _ := h.svc.CreateSession(context.Background(), "install-1", "device-1")

	for state, want := range map[store.AuthSessionState]string{
		store.AuthExpired:  StatusExpired,
		store.AuthRejected: StatusRejected,
	} {
		h.store.forceAuthSessionState(created.Ticket, state)
		poll, err := h.svc.Poll(context.Background(), created.Ticket)
		if err != nil {
			t.Fatalf("Poll: %v", err)
		}
		if poll.Status != want {
			t.Errorf("stored state %q reported as %q, want %q", state, poll.Status, want)
		}
	}
}

// ---------------------------------------------------------------------------
// The redirect
// ---------------------------------------------------------------------------

func TestCallbackRejectsAForgedState(t *testing.T) {
	t.Parallel()

	// The CSRF case: somebody gets a callback delivered with a state they
	// chose, and the naive implementation signs them into whatever account
	// that state resolves to — or, worse, into the victim's.
	h := newHarness(t)
	h.store.seedAccount("victim")

	cases := []struct {
		name  string
		state string
	}{
		{"a state we never issued", "forged-state"},
		{"an empty state", ""},
		{"whitespace", "   "},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if _, err := h.svc.HandleCallback(context.Background(), "auth-code", tc.state); !IsInvalidState(err) {
				t.Fatalf("error = %v, want InvalidStateError", err)
			}
		})
	}
	if h.wc.exchangeCalls != 0 {
		t.Fatalf("a rejected state still resulted in %d WeChat calls; nothing should be exchanged before the state is checked", h.wc.exchangeCalls)
	}
	if h.store.completeCount() != 0 {
		t.Fatal("a rejected state still completed a ticket")
	}
}

func TestCallbackRejectsATicketThatHasAlreadyBeenUsed(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	created, _ := h.svc.CreateSession(context.Background(), "install-1", "device-1")
	if _, err := h.svc.HandleCallback(context.Background(), "auth-code", created.Ticket); err != nil {
		t.Fatalf("first callback: %v", err)
	}

	// The redirect lands twice — a browser retry, a prefetcher, or someone
	// replaying it. The second must not mint a second session.
	if _, err := h.svc.HandleCallback(context.Background(), "auth-code", created.Ticket); !IsInvalidState(err) {
		t.Fatalf("a replayed callback returned %v, want InvalidStateError", err)
	}
}

func TestCallbackRejectsAnExpiredTicket(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	created, _ := h.svc.CreateSession(context.Background(), "install-1", "device-1")
	h.clock = h.clock.Add(testSessionTTL + time.Minute)

	if _, err := h.svc.HandleCallback(context.Background(), "auth-code", created.Ticket); !IsInvalidState(err) {
		t.Fatalf("error = %v, want InvalidStateError", err)
	}
	if h.wc.exchangeCalls != 0 {
		t.Fatal("an expired ticket still exchanged a code at WeChat")
	}
}

func TestCallbackExchangesTheCodeAndIssuesOurOwnTokens(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	created, _ := h.svc.CreateSession(context.Background(), "install-1", "device-1")

	res, err := h.svc.HandleCallback(context.Background(), "auth-code", created.Ticket)
	if err != nil {
		t.Fatalf("HandleCallback: %v", err)
	}
	if res.AccountID == "" || res.ChainID == "" {
		t.Fatalf("callback returned %+v", res)
	}
	if h.wc.lastCode != "auth-code" {
		t.Errorf("the code was not forwarded: %q", h.wc.lastCode)
	}
	if h.wc.lastSecret != testAppSecret {
		t.Error("the app secret was not used in the exchange")
	}

	poll, err := h.svc.Poll(context.Background(), created.Ticket)
	if err != nil {
		t.Fatalf("Poll: %v", err)
	}
	if poll.Status != StatusAuthorized {
		t.Fatalf("status = %q, want authorized", poll.Status)
	}
	if poll.AccessToken == "" || poll.RefreshToken == "" {
		t.Fatal("an authorized poll carried no tokens")
	}
	if poll.Account == nil {
		t.Fatal("an authorized poll carried no account")
	}
	// The credential the client carries is ours, verifiable locally, and
	// carries nothing that came from WeChat.
	claims, err := h.svc.issuer.Verify(poll.AccessToken)
	if err != nil {
		t.Fatalf("the access token does not verify: %v", err)
	}
	if claims.AccountID != poll.Account.ID {
		t.Errorf("token subject %q does not match the account %q", claims.AccountID, poll.Account.ID)
	}
	if strings.Contains(poll.AccessToken, "OAUTH") {
		t.Error("WeChat's own access token reached the client")
	}
}

func TestCallbackCarriesTheInstallIDIntoSignIn(t *testing.T) {
	t.Parallel()

	// The install_records table is gone with the payment rewrite, but the
	// install id still has to reach the sign-in: it is what ties a machine
	// to the person who bought for it. This asserts the caller actually
	// invoked CompleteAuthSession with it rather than doing a partial job
	// of its own.
	h := newHarness(t)
	created, _ := h.svc.CreateSession(context.Background(), "install-abc", "device-1")

	if _, err := h.svc.HandleCallback(context.Background(), "auth-code", created.Ticket); err != nil {
		t.Fatalf("HandleCallback: %v", err)
	}

	carried, ok := h.store.migrationFor(created.Ticket)
	if !ok {
		t.Fatal("CompleteAuthSession was never called, so the install id was not carried")
	}
	if carried != "install-abc" {
		t.Errorf("carried install id = %q, want install-abc", carried)
	}
}

func TestCallbackStoresTheProfileWhenWechatOffersOne(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	h.wc.profileHandler = func(rw http.ResponseWriter, r *http.Request) {
		_, _ = rw.Write([]byte(`{"nickname":"小明","headimgurl":"https://thirdwx.qlogo.cn/a","openid":"oTESTOPENID"}`))
	}
	created, _ := h.svc.CreateSession(context.Background(), "install-1", "device-1")
	if _, err := h.svc.HandleCallback(context.Background(), "auth-code", created.Ticket); err != nil {
		t.Fatalf("HandleCallback: %v", err)
	}
	poll, _ := h.svc.Poll(context.Background(), created.Ticket)
	if poll.Account == nil || poll.Account.Nickname != "小明" {
		t.Fatalf("nickname = %+v, want 小明", poll.Account)
	}
}

func TestAMissingProfileDoesNotFailTheSignIn(t *testing.T) {
	t.Parallel()

	// Under snsapi_login there is usually no profile at all, and 42003 is the
	// expected answer rather than a fault.
	h := newHarness(t)
	created, _ := h.svc.CreateSession(context.Background(), "install-1", "device-1")
	if _, err := h.svc.HandleCallback(context.Background(), "auth-code", created.Ticket); err != nil {
		t.Fatalf("a missing profile must not fail the sign-in: %v", err)
	}
	poll, _ := h.svc.Poll(context.Background(), created.Ticket)
	if poll.Status != StatusAuthorized {
		t.Fatalf("status = %q, want authorized", poll.Status)
	}
	if poll.Account == nil || poll.Account.Nickname != "" {
		t.Errorf("expected an account with an empty nickname, got %+v", poll.Account)
	}
}

func TestWechatRejectingTheCodeIsNotTransient(t *testing.T) {
	t.Parallel()

	// A code is single-use. Retrying it can never succeed, so this must not
	// be dressed up as a network problem.
	h := newHarness(t)
	h.wc.exchangeHandler = func(rw http.ResponseWriter, r *http.Request) {
		rw.WriteHeader(http.StatusOK)
		_, _ = rw.Write([]byte(`{"errcode":40029,"errmsg":"invalid code"}`))
	}
	created, _ := h.svc.CreateSession(context.Background(), "install-1", "device-1")

	_, err := h.svc.HandleCallback(context.Background(), "used-code", created.Ticket)
	if !IsInvalidCode(err) {
		t.Fatalf("error = %v, want InvalidCodeError", err)
	}
	if IsUpstreamUnavailable(err) {
		t.Error("an invalid code was classified as transient, which would invite a pointless retry")
	}
}

func TestAWechatOutageLeavesTheTicketUsable(t *testing.T) {
	t.Parallel()

	// The ticket is not burned. A user whose scan raced a WeChat outage should
	// still be able to scan again rather than be told the code is no good.
	h := newHarness(t)
	h.wc.exchangeHandler = func(rw http.ResponseWriter, r *http.Request) {
		rw.WriteHeader(http.StatusInternalServerError)
		_, _ = rw.Write([]byte(`{"errcode":-1,"errmsg":"system error"}`))
	}
	created, _ := h.svc.CreateSession(context.Background(), "install-1", "device-1")

	if _, err := h.svc.HandleCallback(context.Background(), "auth-code", created.Ticket); !IsUpstreamUnavailable(err) {
		t.Fatalf("error = %v, want UpstreamUnavailableError", err)
	}
	session, _ := h.store.authSessionByTicket(created.Ticket)
	if session.State != store.AuthPending {
		t.Fatalf("state = %q, want the ticket left pending", session.State)
	}

	// WeChat recovers; the same ticket now works.
	h.wc.exchangeHandler = nil
	if _, err := h.svc.HandleCallback(context.Background(), "auth-code", created.Ticket); err != nil {
		t.Fatalf("the ticket was unusable after WeChat recovered: %v", err)
	}
}

func TestTokensAreHandedOverExactlyOnce(t *testing.T) {
	t.Parallel()

	// The store keeps hashes, so the handoff is the only place the plaintext
	// exists. It is readable once.
	h := newHarness(t)
	ticket, first := h.signIn(t, "install-1")
	if first.AccessToken == "" {
		t.Fatal("the first poll returned no token")
	}

	second, err := h.svc.Poll(context.Background(), ticket)
	if err != nil {
		t.Fatalf("second Poll: %v", err)
	}
	if second.Status != StatusAuthorized {
		t.Errorf("status = %q, want authorized", second.Status)
	}
	if second.AccessToken != "" || second.RefreshToken != "" {
		t.Error("the second poll handed out tokens again")
	}
	// The account is not a secret and lets the UI say who is signed in.
	if second.Account == nil || second.Account.ID != first.Account.ID {
		t.Error("the repeat poll lost the account")
	}
}

func TestTheHandoffDoesNotOutliveTheTicket(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	ticket, _ := h.signIn(t, "install-1")
	h.clock = h.clock.Add(2 * testSessionTTL)

	// A new scan must not find yesterday's credentials.
	h2 := h
	created, err := h2.svc.CreateSession(context.Background(), "install-2", "device-1")
	if err != nil {
		t.Fatalf("CreateSession: %v", err)
	}
	if _, err := h2.svc.HandleCallback(context.Background(), "auth-code", created.Ticket); err != nil {
		t.Fatalf("HandleCallback: %v", err)
	}
	poll, _ := h2.svc.Poll(context.Background(), created.Ticket)
	if poll.AccessToken == "" {
		t.Fatal("a fresh scan collected nothing")
	}
	// And the old ticket, polled again, has nothing to give.
	if mem, ok := h2.svc.handoff.(*MemoryHandoff); ok {
		if mem.Len() != 0 {
			t.Errorf("%d expired credentials are still held", mem.Len())
		}
	}
	_ = ticket
}

// ---------------------------------------------------------------------------
// Refresh
// ---------------------------------------------------------------------------

func TestRefreshRotatesAndReturnsBothTokens(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	_, signedIn := h.signIn(t, "install-1")

	result, err := h.svc.Refresh(context.Background(), signedIn.RefreshToken)
	if err != nil {
		t.Fatalf("Refresh: %v", err)
	}
	if result.Outcome != store.RefreshRotated {
		t.Fatalf("outcome = %q, want rotated", result.Outcome)
	}
	if result.AccessToken == "" || result.RefreshToken == "" {
		t.Fatal("a rotation returned no tokens")
	}
	if result.RefreshToken == signedIn.RefreshToken {
		t.Fatal("the refresh token was not rotated")
	}
	if result.Account == nil || result.Account.ID != signedIn.Account.ID {
		t.Error("the account did not survive the rotation")
	}

	// The new token verifies, and it belongs to the same chain — a rotation
	// must not start a new family, or a leaked token would escape the
	// revocation meant for it.
	claims, err := h.svc.issuer.Verify(result.AccessToken)
	if err != nil {
		t.Fatalf("the rotated access token does not verify: %v", err)
	}
	oldClaims, _ := h.svc.issuer.Verify(signedIn.AccessToken)
	if claims.ChainID != oldClaims.ChainID {
		t.Fatalf("the chain changed on rotation: %q -> %q", oldClaims.ChainID, claims.ChainID)
	}
}

func TestRefreshingWithTheSameTokenTwiceRevokesTheChain(t *testing.T) {
	t.Parallel()

	// The replay case, and the reason rotation exists at all. A leaked
	// refresh token is spent the moment the attacker uses it; the legitimate
	// user's next refresh is then the one that trips the alarm, and the only
	// safe response is to tear the family down.
	h := newHarness(t)
	_, signedIn := h.signIn(t, "install-1")

	// One legitimate rotation, establishing that the chain is healthy.
	first, err := h.svc.Refresh(context.Background(), signedIn.RefreshToken)
	if err != nil {
		t.Fatalf("first Refresh: %v", err)
	}
	if h.store.revokeCount() != 0 {
		t.Fatal("a legitimate rotation revoked something")
	}

	// Now the stolen old token comes back.
	_, err = h.svc.Refresh(context.Background(), signedIn.RefreshToken)
	if !IsReuseDetected(err) {
		t.Fatalf("the replay returned %v, want ReuseDetectedError", err)
	}
	// The verdict must not name the chain in the clear, so that an error
	// logged somewhere does not hand out a revocation target.
	firstClaims, _ := h.svc.issuer.Verify(first.AccessToken)
	if strings.Contains(err.Error(), firstClaims.ChainID) {
		t.Fatalf("the reuse error names the chain in plain text: %v", err)
	}
	if h.store.revokeCount() != 1 {
		t.Fatalf("the chain was not revoked (revoke calls: %d)", h.store.revokeCount())
	}

	// And the whole family is gone, including the token the attacker got.
	if !h.store.isRevoked(firstClaims.ChainID) {
		t.Fatal("the chain the attacker holds is still live")
	}
	after, err := h.svc.Refresh(context.Background(), first.RefreshToken)
	if err != nil {
		t.Fatalf("Refresh after revocation: %v", err)
	}
	if after.Outcome != store.RefreshExpired {
		t.Errorf("the attacker's token still works: outcome = %q", after.Outcome)
	}
}

func TestTheAccessTokenFromBeforeTheReplayIsDead(t *testing.T) {
	t.Parallel()

	// Revoking the chain has to bite the access tokens too. They are short
	// lived, but "short lived" is not "already dead", and a window is a
	// window.
	h := newHarness(t)
	_, signedIn := h.signIn(t, "install-1")
	accessClaims, err := h.svc.issuer.Verify(signedIn.AccessToken)
	if err != nil {
		t.Fatalf("Verify: %v", err)
	}

	if _, err := h.svc.Refresh(context.Background(), signedIn.RefreshToken); err != nil {
		t.Fatalf("rotation: %v", err)
	}
	if _, err := h.svc.Refresh(context.Background(), signedIn.RefreshToken); !IsReuseDetected(err) {
		t.Fatalf("the replay was not detected: %v", err)
	}

	// The access token is still cryptographically valid — it is self-contained
	// and a signature cannot be withdrawn. What changed is that the session it
	// names is revoked, which the gateway checks. This documents the limit
	// rather than pretending it away: killing a self-contained token the
	// instant a chain is revoked means the gateway consults the store, and
	// that is §15.4's job rather than this endpoint's.
	session, ok := h.store.sessionByID(accessClaims.SessionID)
	if !ok {
		t.Fatal("the session row is missing")
	}
	if session.RevokedAt == nil {
		t.Fatal("the session row was not marked revoked")
	}
}

// The one that must not get widened. A store that could not be reached during
// a rotation is not evidence that the user's credentials are bad, and telling
// them otherwise signs paying customers out over a bad minute.
func TestATransientStoreFailureIsNotALoggedOut(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	_, signedIn := h.signIn(t, "install-1")

	h.store.setRotateErr(errors.New("connection refused"))

	result, err := h.svc.Refresh(context.Background(), signedIn.RefreshToken)
	if err != nil {
		t.Fatalf("Refresh returned a hard error instead of an outcome: %v", err)
	}
	if result.Outcome != store.RefreshTransient {
		t.Fatalf("outcome = %q, want transient", result.Outcome)
	}
	if result.AccessToken != "" || result.RefreshToken != "" {
		t.Error("a transient outcome handed out tokens")
	}
	if !result.TransientHint {
		t.Error("the transient hint was not set")
	}
	if h.store.revokeCount() != 0 {
		t.Fatal("a transient failure revoked the user's sessions")
	}

	// The client keeps its token, so the retry works. This is the assertion
	// that matters: the failure is only safe if the token survives it.
	h.store.setRotateErr(nil)
	retry, err := h.svc.Refresh(context.Background(), signedIn.RefreshToken)
	if err != nil {
		t.Fatalf("retry: %v", err)
	}
	if retry.Outcome != store.RefreshRotated {
		t.Fatalf("the retry after a transient failure = %q, want rotated — the client's token was lost", retry.Outcome)
	}
	if retry.RefreshToken == signedIn.RefreshToken {
		t.Error("the retry did not rotate")
	}
}

func TestASessionLookupFailureIsTransientNotExpired(t *testing.T) {
	t.Parallel()

	// Same rule one step earlier. A database that cannot answer "have I seen
	// this token" has not said the token is bad.
	h := newHarness(t)
	_, signedIn := h.signIn(t, "install-1")

	h.store.setGetSessionErr(errors.New("context deadline exceeded"))
	result, err := h.svc.Refresh(context.Background(), signedIn.RefreshToken)
	if err != nil {
		t.Fatalf("err = %v", err)
	}
	if result.Outcome != store.RefreshTransient {
		t.Fatalf("outcome = %q, want transient", result.Outcome)
	}

	h.store.setGetSessionErr(nil)
	if result, _ := h.svc.Refresh(context.Background(), signedIn.RefreshToken); result.Outcome != store.RefreshRotated {
		t.Fatalf("outcome after recovery = %q", result.Outcome)
	}
}

// A WeChat 500 during any part of a refresh must classify as transient. Refresh
// itself is a local operation — WeChat's access token never entered the
// client's chain — but the classification is shared, and this is the test that
// pins it against the upstream that produced the misclassification in the
// first place.
func TestAWechatServerErrorClassifiesAsTransientNotExpired(t *testing.T) {
	t.Parallel()

	serverError := &wechat.APIError{
		Op:      "exchange code",
		Status:  500,
		ErrCode: 500,
		ErrMsg:  "internal error",
	}
	if !wechat.IsTransient(serverError) {
		t.Fatal("a WeChat 500 must be classified transient")
	}
	if classifyWechatFailure(serverError) == "expired" {
		t.Fatal("a WeChat 500 was classified as an expired sign-in")
	}

	// And the same upstream error in the sign-in path leaves the ticket alone.
	h := newHarness(t)
	h.wc.exchangeHandler = func(rw http.ResponseWriter, r *http.Request) {
		rw.WriteHeader(http.StatusInternalServerError)
		_, _ = rw.Write([]byte(`{"errcode":500,"errmsg":"internal error"}`))
	}
	created, _ := h.svc.CreateSession(context.Background(), "install-1", "device-1")
	if _, err := h.svc.HandleCallback(context.Background(), "auth-code", created.Ticket); !IsUpstreamUnavailable(err) {
		t.Fatalf("error = %v, want UpstreamUnavailableError", err)
	}
	if session, _ := h.store.authSessionByTicket(created.Ticket); session.State != store.AuthPending {
		t.Fatal("a WeChat outage consumed the ticket")
	}
}

// classifyWechatFailure is the shared rule, extracted so both the sign-in and
// refresh paths answer the same question the same way.
func classifyWechatFailure(err error) string {
	if wechat.IsTransient(err) {
		return "transient"
	}
	return "expired"
}

func TestRefreshingAnUnknownTokenIsExpired(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	result, err := h.svc.Refresh(context.Background(), "wcb1_not-a-real-token")
	if err != nil {
		t.Fatalf("err = %v", err)
	}
	if result.Outcome != store.RefreshExpired {
		t.Errorf("outcome = %q, want expired", result.Outcome)
	}
	if h.store.revokeCount() != 0 {
		t.Error("an unknown token triggered a revocation")
	}
}

func TestRefreshingAnEmptyTokenIsExpiredWithoutTouchingTheStore(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	result, err := h.svc.Refresh(context.Background(), "  ")
	if err != nil {
		t.Fatalf("err = %v", err)
	}
	if result.Outcome != store.RefreshExpired {
		t.Errorf("outcome = %q, want expired", result.Outcome)
	}
}

func TestARefreshTokenPastItsExpiryIsExpired(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	_, signedIn := h.signIn(t, "install-1")

	h.clock = h.clock.Add(testRefreshTTL + time.Hour)
	result, err := h.svc.Refresh(context.Background(), signedIn.RefreshToken)
	if err != nil {
		t.Fatalf("err = %v", err)
	}
	if result.Outcome != store.RefreshExpired {
		t.Fatalf("outcome = %q, want expired", result.Outcome)
	}
}

// Two refreshes racing on one token: one wins, one loses. The loser is holding
// a spent token and must be told so rather than being handed a second one.
func TestConcurrentRefreshesHaveExactlyOneWinner(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	_, signedIn := h.signIn(t, "install-1")

	const racers = 8
	type outcome struct {
		res RefreshResult
		err error
	}
	results := make(chan outcome, racers)
	start := make(chan struct{})
	for n := 0; n < racers; n++ {
		go func() {
			<-start
			res, err := h.svc.Refresh(context.Background(), signedIn.RefreshToken)
			results <- outcome{res, err}
		}()
	}
	close(start)

	rotated := 0
	for n := 0; n < racers; n++ {
		got := <-results
		switch {
		case got.err != nil:
			// A replay verdict is a legitimate ending for the losers: the
			// chain is torn down rather than one racer quietly winning.
			if !IsReuseDetected(got.err) {
				t.Fatalf("unexpected error: %v", got.err)
			}
		case got.res.Outcome == store.RefreshRotated:
			rotated++
		case got.res.Outcome == store.RefreshExpired:
			// A racer that arrives after the revocation has already landed
			// sees a dead chain and is told so. Same ending, fewer moving
			// parts: no second revocation, no spurious replay report.
		default:
			t.Fatalf("unexpected outcome %q", got.res.Outcome)
		}
	}
	if rotated != 1 {
		t.Fatalf("%d racers were handed a rotated pair; exactly one should win", rotated)
	}
}

// ---------------------------------------------------------------------------
// Revocation and reads
// ---------------------------------------------------------------------------

func TestRevokeTearsDownTheWholeChain(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	_, signedIn := h.signIn(t, "install-1")
	claims, _ := h.svc.issuer.Verify(signedIn.AccessToken)

	if err := h.svc.Revoke(context.Background(), claims.ChainID); err != nil {
		t.Fatalf("Revoke: %v", err)
	}
	if !h.store.isRevoked(claims.ChainID) {
		t.Fatal("the chain is still live")
	}
	result, _ := h.svc.Refresh(context.Background(), signedIn.RefreshToken)
	if result.Outcome != store.RefreshExpired {
		t.Errorf("a revoked chain still refreshes: %q", result.Outcome)
	}
}

func TestChainForRefreshFindsTheFamily(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	_, signedIn := h.signIn(t, "install-1")
	claims, _ := h.svc.issuer.Verify(signedIn.AccessToken)

	chain, err := h.svc.ChainForRefresh(context.Background(), signedIn.RefreshToken)
	if err != nil {
		t.Fatalf("ChainForRefresh: %v", err)
	}
	if chain != claims.ChainID {
		t.Errorf("chain = %q, want %q", chain, claims.ChainID)
	}

	// An unknown token names no chain, and that is not an error — sign-out
	// with a token the server has forgotten is exactly the state a user trying
	// to sign out of a stale install is in.
	chain, err = h.svc.ChainForRefresh(context.Background(), "wcb1_nope")
	if err != nil || chain != "" {
		t.Fatalf("ChainForRefresh(unknown) = %q, %v", chain, err)
	}
}

func TestAccountIsReadThrough(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	account := h.store.seedAccount("acct-read")

	got, err := h.svc.Account(context.Background(), account.ID)
	if err != nil {
		t.Fatalf("Account: %v", err)
	}
	if got.ID != account.ID {
		t.Errorf("id = %q", got.ID)
	}

	if _, err := h.svc.Account(context.Background(), "no-such-account"); !errors.Is(err, store.ErrNotFound) {
		t.Errorf("Account(unknown) = %v, want ErrNotFound", err)
	}
}

// ---------------------------------------------------------------------------
// Construction
// ---------------------------------------------------------------------------

func TestNewServiceRefusesAnIncompleteWiring(t *testing.T) {
	t.Parallel()

	issuer, _ := token.NewIssuer(testSecret, testAccessTTL, testRefreshTTL)
	client := wechat.NewClient(wechat.Config{AppID: testAppID, APIBase: "https://x"})

	for name, opts := range map[string]Options{
		"no store":  {WeChat: client, Issuer: issuer},
		"no issuer": {Store: newFakeStore(), WeChat: client},
		"no wechat": {Store: newFakeStore(), Issuer: issuer},
	} {
		if _, err := NewService(Config{PublicBaseURL: testPublicBase}, opts); err == nil {
			t.Errorf("%s: expected an error", name)
		}
	}
	if _, err := NewService(Config{}, Options{
		Store: newFakeStore(), WeChat: client, Issuer: issuer,
	}); err == nil {
		t.Error("a service with no public base URL must be refused; the redirect URI is derived from it")
	}
}

func TestCallbackURLIsDerivedFromThePublicBase(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	if got := h.svc.CallbackURL(); got != testPublicBase+CallbackPath {
		t.Errorf("CallbackURL = %q", got)
	}
}
