package config

import (
	"strings"
	"testing"
	"time"
)

// defaultForTest is an access-token lifetime that satisfies validate's
// "refresh must outlast access" rule without depending on the package's
// defaults, so a change to those defaults does not quietly change what these
// cases are testing.
const defaultForTest = time.Hour

// TestBasePathNormalisation pins the shape the mount point is allowed to take.
//
// The routes are built by concatenating this onto literals that start with
// "/", so the only two acceptable answers are "" and "/segment" — anything
// else produces either a doubled slash or a path that swallowed the segment
// after it. "wechat_auth/" and "/wechat_auth" are the same deployment and must
// configure identically, because a deploy script that adds a trailing slash
// should not change where the service answers.
func TestBasePathNormalisation(t *testing.T) {
	cases := []struct {
		in   string
		want string
	}{
		{"", ""},
		{"/", ""},
		{"   ", ""},
		{"wechat_auth", "/wechat_auth"},
		{"/wechat_auth", "/wechat_auth"},
		{"/wechat_auth/", "/wechat_auth"},
		{"  /wechat_auth/  ", "/wechat_auth"},
		// Two segments is a legitimate path and must survive intact.
		{"/auth/wechat", "/auth/wechat"},
		// The interior "//" is a typo, not something to tidy: validate names
		// it rather than this quietly producing a different mount point than
		// the operator wrote.
		{"/wechat//auth", "/wechat//auth"},
	}

	for _, tc := range cases {
		if got := basePath(tc.in); got != tc.want {
			t.Errorf("basePath(%q) = %q, want %q", tc.in, got, tc.want)
		}
	}
}

// TestPublicPath is the other half: what the public URL says the mount point
// is. A bare origin has none, which is what lets WCB_BASE_PATH be empty while
// WCB_PUBLIC_BASE_URL still names a prefixed deployment.
func TestPublicPath(t *testing.T) {
	cases := []struct {
		in   string
		want string
	}{
		{"https://host", ""},
		{"https://host/", ""},
		{"http://localhost:8080", ""},
		{"https://host/wechat_auth", "/wechat_auth"},
		{"https://host/wechat_auth/", "/wechat_auth"},
		{"https://host/auth/wechat", "/auth/wechat"},
		// A port is not a path separator.
		{"https://host:8443/wechat_auth", "/wechat_auth"},
		{"https://host:8443", ""},
		// Malformed input is reported by validate in its own words; this
		// returns "" rather than inventing a second error for the same value.
		{"not-a-url", ""},
	}

	for _, tc := range cases {
		if got := publicPath(tc.in); got != tc.want {
			t.Errorf("publicPath(%q) = %q, want %q", tc.in, got, tc.want)
		}
	}
}

// TestBasePathAndPublicURLMustAgree covers the arrangement question: two
// settings, two legal deployments, and one failure mode that is invisible
// until a customer scans a QR code.
//
// The two legal arrangements are (empty, prefixed URL) — a reverse proxy that
// strips the prefix — and (prefixed, prefixed URL) — this service doing the
// mounting. Everything else sends WeChat's callback somewhere the routes are
// not, which the customer discovers as a 404 after scanning.
func TestBasePathAndPublicURLMustAgree(t *testing.T) {
	cases := []struct {
		name    string
		base    string
		public  string
		wantErr string // substring; "" means valid
	}{
		{
			name:   "no prefix at all",
			base:   "",
			public: "https://api.example.com",
		},
		{
			name:   "service mounts it itself",
			base:   "/wechat_auth",
			public: "https://ocbot.aixifs.com/wechat_auth",
		},
		{
			name:   "a proxy strips the prefix",
			base:   "",
			public: "https://ocbot.aixifs.com/wechat_auth",
		},
		{
			name:    "the callback would 404",
			base:    "/wechat_auth",
			public:  "https://ocbot.aixifs.com/other",
			wantErr: "different places",
		},
		{
			name:    "a base path with no public counterpart",
			base:    "/wechat_auth",
			public:  "https://ocbot.aixifs.com",
			wantErr: "no path",
		},
		{
			name:    "one segment off",
			base:    "/wechat_aut",
			public:  "https://ocbot.aixifs.com/wechat_auth",
			wantErr: "different places",
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			c := Config{BasePath: basePath(tc.base), PublicBaseURL: tc.public}
			c.TokenSigningSecret = strings.Repeat("x", 32)
			c.AccessTokenTTL = defaultForTest
			c.RefreshTokenTTL = defaultForTest * 90

			err := c.validate()
			switch {
			case tc.wantErr == "" && err != nil:
				t.Fatalf("validate rejected a legal arrangement: %v", err)
			case tc.wantErr != "" && err == nil:
				t.Fatalf("validate accepted base=%q public=%q, which sends the callback to a 404",
					tc.base, tc.public)
			case tc.wantErr != "" && !strings.Contains(err.Error(), tc.wantErr):
				t.Fatalf("error does not mention %q: %v", tc.wantErr, err)
			}
		})
	}
}

// TestBasePathMustBeAbsolute guards the one shape the concatenation cannot
// absorb: a value that does not start with "/" would be concatenated onto
// nothing sensible.
func TestBasePathMustBeAbsolute(t *testing.T) {
	c := Config{
		BasePath:           "wechat_auth", // deliberately un-normalised
		PublicBaseURL:      "https://ocbot.aixifs.com/wechat_auth",
		TokenSigningSecret: strings.Repeat("x", 32),
	}
	err := c.validate()
	if err == nil || !strings.Contains(err.Error(), "must start with") {
		t.Fatalf("validate accepted a relative base path: %v", err)
	}
}
