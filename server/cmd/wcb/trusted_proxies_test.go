package main

import (
	"net/http"
	"net/http/httptest"
	"testing"

	"github.com/gin-gonic/gin"
)

// TestForwardedForIsOnlyBelievedFromTrustedPeers is the check that the
// request log cannot be written by whoever calls.
//
// X-Forwarded-For is a header any client may set. gin trusts every source
// unless SetTrustedProxies narrows it, and the default is 0.0.0.0/0 — so
// before this was set, an unauthenticated caller could put any address at
// all into the one record of who called. That is the whole problem: a log
// that any caller can edit is not evidence.
//
// The two cases bracket the behaviour. From a trusted peer the forwarding
// chain is believed and the client's real address surfaces; from anyone else
// the header is ignored and what lands in the log is the peer's own address,
// which is the truth about the connection.
func TestForwardedForIsOnlyBelievedFromTrustedPeers(t *testing.T) {
	gin.SetMode(gin.TestMode)

	// 10.1.0.7 is the reverse proxy in this test; 203.0.113.9 is the client
	// it forwarded for.
	const proxyAddr = "10.1.0.7:41234"

	newEngine := func(t *testing.T, trusted []string) *gin.Engine {
		t.Helper()
		e := gin.New()
		if err := e.SetTrustedProxies(trusted); err != nil {
			t.Fatalf("SetTrustedProxies(%v): %v", trusted, err)
		}
		e.GET("/who", func(c *gin.Context) {
			c.String(http.StatusOK, c.ClientIP())
		})
		return e
	}

	cases := []struct {
		name    string
		trusted []string
		remote  string
		xff     string
		want    string
	}{
		{
			name:    "a trusted proxy's forwarding header is believed",
			trusted: []string{"10.1.0.0/24"},
			remote:  proxyAddr,
			xff:     "203.0.113.9",
			want:    "203.0.113.9",
		},
		{
			name:    "a stranger's forwarding header is ignored",
			trusted: []string{"10.1.0.0/24"},
			remote:  "198.51.100.5:5555",
			xff:     "203.0.113.9",
			// The address recorded is the one the connection came from, not
			// the one the caller asked to be recorded.
			want: "198.51.100.5",
		},
		{
			name:    "trusting nobody ignores the header entirely",
			trusted: nil,
			remote:  proxyAddr,
			xff:     "203.0.113.9",
			want:    "10.1.0.7",
		},
		{
			name:    "a chained header is walked back to the first untrusted hop",
			trusted: []string{"10.1.0.0/24"},
			remote:  proxyAddr,
			// A client that prepends its own entries must not be able to
			// bury the real address behind them.
			xff:  "203.0.113.9, 198.51.100.5, 10.1.0.7",
			want: "198.51.100.5",
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			req := httptest.NewRequest(http.MethodGet, "/who", nil)
			req.RemoteAddr = tc.remote
			if tc.xff != "" {
				req.Header.Set("X-Forwarded-For", tc.xff)
			}

			rec := httptest.NewRecorder()
			newEngine(t, tc.trusted).ServeHTTP(rec, req)

			if got := rec.Body.String(); got != tc.want {
				t.Errorf("ClientIP() = %q, want %q (remote %s, XFF %q, trusted %v)",
					got, tc.want, tc.remote, tc.xff, tc.trusted)
			}
		})
	}
}

// TestSetTrustedProxiesRejectsGarbage covers the start-up contract: a typo in
// WCB_TRUSTED_PROXIES must stop the boot. Silently falling back would leave
// the service running and logging proxy addresses for every request, with
// nothing in the logs to say so.
func TestSetTrustedProxiesRejectsGarbage(t *testing.T) {
	gin.SetMode(gin.TestMode)
	for _, bad := range []string{"not-an-ip", "10.1.0.0/99", "999.1.1.1"} {
		e := gin.New()
		if err := e.SetTrustedProxies([]string{bad}); err == nil {
			t.Errorf("SetTrustedProxies(%q) returned no error; a typo here would "+
				"leave the service running with a proxy list nobody intended", bad)
		}
	}
}
