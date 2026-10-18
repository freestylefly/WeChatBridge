// Package config loads the service's settings from the environment.
//
// Everything here is an environment variable, nothing is a flag, and every
// field has a default that is safe for a local run and obviously wrong for
// production — so the failure mode of a half-configured deployment is a loud
// one at start-up rather than a silent one at the first customer.
//
// The payment half of the old configuration is gone with the billing package:
// the buy-out flow is a seller confirming a WeChat payment by hand, so there
// is no merchant id, no APIv3 key and no notify URL to half-configure. What
// is left is the shape of the sign-in, the licence key and the admin OpenID
// whitelist.
package config

import (
	"fmt"
	"os"
	"strconv"
	"strings"
	"time"
)

// Config is the whole service's configuration.
type Config struct {
	Env           string // "dev" or "prod"; prod turns on stricter checks
	HTTPAddr      string
	PublicBaseURL string // https://api.example.com, no trailing slash
	BasePath      string // "", or "/wechat_auth" — see below
	LogLevel      string

	DatabaseURL string

	// WeChat open platform. AppID/AppSecret are the website app's, and the
	// secret never reaches a log line.
	WeChatAppID     string
	WeChatAppSecret string
	WeChatAuthURL   string // https://open.weixin.qq.com/connect/qrconnect
	WeChatAPIBase   string // https://api.weixin.qq.com

	// Licence issuing. The private key is a file path for the same reason.
	LicensePrivateKeyPath string

	// Product policy. One product: the perpetual buy-out.
	BuyoutProductID string

	// AdminOpenIDs is the WeChat open-platform OpenID whitelist for the
	// seller's console (design §11.7): the admin API answers only to a
	// Bearer access token whose account OpenID is on this list. Empty in
	// dev means "no console" — fail closed, because a console that answers
	// "unauthorized" is safer than one that answers at all.
	AdminOpenIDs []string

	// TrustedProxies are the peers whose X-Forwarded-For is believed when
	// logging a client IP. Empty means none: the remote address is used as
	// it arrives. See TrustedProxyList for why that is the default.
	TrustedProxies []string

	// Lifetimes, in days. Zero for the buy-out plan is not "unconfigured" — a
	// buy-out licence is perpetual by definition and has no expiry field.
	BuyoutLicenseDays int

	AuthSessionTTL  time.Duration
	AccessTokenTTL  time.Duration
	RefreshTokenTTL time.Duration

	// Signing key for the tokens the client carries. Access is short and
	// self-contained; refresh is checked against the store so it can be
	// revoked.
	TokenSigningSecret string
}

// IsProd reports whether production checks apply.
func (c Config) IsProd() bool { return c.Env == "prod" }

// Load reads the environment and returns the configuration, or an error
// naming every variable that is missing or unparseable — all of them, not the
// first one, because a deployment that fixes errors one restart at a time is
// a deployment that spends the night that way.
func Load() (Config, error) {
	var missing []string
	required := func(name string) string {
		v := strings.TrimSpace(os.Getenv(name))
		if v == "" {
			missing = append(missing, name)
		}
		return v
	}

	c := Config{
		Env:           env("WCB_ENV", "dev"),
		HTTPAddr:      env("WCB_HTTP_ADDR", ":8080"),
		PublicBaseURL: strings.TrimRight(env("WCB_PUBLIC_BASE_URL", "http://localhost:8080"), "/"),
		BasePath:      basePath(env("WCB_BASE_PATH", "")),
		LogLevel:      env("WCB_LOG_LEVEL", "info"),

		DatabaseURL: required("WCB_DATABASE_URL"),

		WeChatAppID:     required("WCB_WECHAT_APP_ID"),
		WeChatAppSecret: required("WCB_WECHAT_APP_SECRET"),
		WeChatAuthURL:   env("WCB_WECHAT_AUTH_URL", "https://open.weixin.qq.com/connect/qrconnect"),
		WeChatAPIBase:   env("WCB_WECHAT_API_BASE", "https://api.weixin.qq.com"),

		LicensePrivateKeyPath: required("WCB_LICENSE_PRIVATE_KEY_PATH"),
		TokenSigningSecret:    required("WCB_TOKEN_SIGNING_SECRET"),

		AdminOpenIDs:   TrustedProxyList("WCB_ADMIN_OPENIDS"),
		TrustedProxies: TrustedProxyList("WCB_TRUSTED_PROXIES"),
	}

	var err error
	for _, f := range []struct {
		dst  *time.Duration
		name string
		def  time.Duration
	}{
		{&c.AuthSessionTTL, "WCB_AUTH_SESSION_TTL", 5 * time.Minute},
		{&c.AccessTokenTTL, "WCB_ACCESS_TOKEN_TTL", time.Hour},
		{&c.RefreshTokenTTL, "WCB_REFRESH_TOKEN_TTL", 90 * 24 * time.Hour},
	} {
		if *f.dst, err = duration(f.name, f.def); err != nil {
			return c, err
		}
	}

	for _, f := range []struct {
		dst  *int
		name string
		def  int
	}{
		{&c.BuyoutLicenseDays, "WCB_BUYOUT_LICENSE_DAYS", 0},
	} {
		if *f.dst, err = integer(f.name, f.def); err != nil {
			return c, err
		}
	}

	c.BuyoutProductID = env("WCB_BUYOUT_PRODUCT_ID", "wechatbridge-windows-buyout")

	if len(missing) > 0 {
		return c, fmt.Errorf("config: missing %s: %s", plural(len(missing), "variable"), strings.Join(missing, ", "))
	}

	if err := c.validate(); err != nil {
		return c, err
	}
	return c, nil
}

func (c Config) validate() error {
	var problems []string
	add := func(format string, args ...any) {
		problems = append(problems, fmt.Sprintf(format, args...))
	}

	if c.PublicBaseURL == "" || !strings.HasPrefix(c.PublicBaseURL, "http") {
		add("WCB_PUBLIC_BASE_URL must be an absolute http(s) URL, got %q", c.PublicBaseURL)
	}
	if c.BasePath != "" && !strings.HasPrefix(c.BasePath, "/") {
		add("WCB_BASE_PATH must start with %q or be empty, got %q", "/", c.BasePath)
	}
	// The base path and the public URL's path have to name the same place,
	// and the way they fail when they disagree is the worst kind: the service
	// boots, the health check passes, the console loads, and the first scan
	// redirects WeChat's callback to a 404 that only the customer sees. Catch
	// the disagreement here instead.
	//
	// A mismatch is not automatically an error, though: a reverse proxy that
	// strips the prefix before forwarding is the whole reason BasePath exists
	// as a separate setting rather than being derived from PublicBaseURL, and
	// in that deployment WCB_BASE_PATH is empty while WCB_PUBLIC_BASE_URL
	// carries the path. So the check is that they *describe* one of the two
	// arrangements, not that they are equal.
	if want := publicPath(c.PublicBaseURL); c.BasePath != "" && c.BasePath != want {
		if want == "" {
			add("WCB_BASE_PATH is %q but WCB_PUBLIC_BASE_URL has no path (%q); "+
				"either drop the base path or give the public URL one — a stripped "+
				"reverse proxy wants WCB_BASE_PATH empty", c.BasePath, c.PublicBaseURL)
		} else {
			add("WCB_BASE_PATH is %q but WCB_PUBLIC_BASE_URL's path is %q; "+
				"they name different places, so the callback would be sent to a 404",
				c.BasePath, want)
		}
	}
	if c.IsProd() {
		if !strings.HasPrefix(c.PublicBaseURL, "https://") {
			add("WCB_PUBLIC_BASE_URL must be https in production, got %q", c.PublicBaseURL)
		}
		// The console is the mint: it signs licences with one click. A
		// production deployment without an admin whitelist is a deployment
		// where nobody can administer the mint — loud, not silent.
		if len(c.AdminOpenIDs) == 0 {
			add("WCB_ADMIN_OPENIDS is required in production")
		}
	}
	if c.AccessTokenTTL <= 0 || c.RefreshTokenTTL <= c.AccessTokenTTL {
		add("token lifetimes are inconsistent: refresh must outlast access")
	}
	if len(c.TokenSigningSecret) < 32 {
		add("WCB_TOKEN_SIGNING_SECRET must be at least 32 bytes of entropy")
	}

	if len(problems) > 0 {
		return fmt.Errorf("config: %s", strings.Join(problems, "; "))
	}
	return nil
}

// BuyoutLifetime is the buy-out licence's lifetime. It is zero unless an
// operator deliberately configures one — a buy-out licence has no expiry, and
// that absence is the definition of the product rather than an unset field.
func (c Config) BuyoutLifetime() time.Duration {
	if c.BuyoutLicenseDays <= 0 {
		return 0
	}
	return time.Duration(c.BuyoutLicenseDays) * 24 * time.Hour
}

// AdminEnabled reports whether the admin API answers at all. An empty
// whitelist is "no console", not "console that trusts everybody".
func (c Config) AdminEnabled() bool { return len(c.AdminOpenIDs) > 0 }

// publicPath is the path component of the public URL: "/wechat_auth" for
// https://host/wechat_auth, and "" for a bare origin.
//
// It reads the string rather than net/url because PublicBaseURL has already
// been checked for an http prefix and a parse failure here would mean the URL
// is malformed — which validate reports in its own words. Returning "" for
// that case keeps one bad value from producing two error messages.
func publicPath(raw string) string {
	i := strings.Index(raw, "://")
	if i < 0 {
		return ""
	}
	rest := raw[i+3:]
	slash := strings.IndexByte(rest, '/')
	if slash < 0 {
		return ""
	}
	return strings.TrimRight(rest[slash:], "/")
}

// basePath normalises the mount prefix to either "" or "/segment" with no
// trailing slash, so the value can be concatenated onto a route literal
// without producing a double slash or swallowing the one after it.
//
// "wechat_auth", "/wechat_auth" and "/wechat_auth/" all become
// "/wechat_auth". A path with an interior "//" is left as it arrived rather
// than being tidied: it is a typo, and validate names it.
func basePath(raw string) string {
	v := strings.TrimRight(strings.TrimSpace(raw), "/")
	if v == "" {
		return ""
	}
	if !strings.HasPrefix(v, "/") {
		v = "/" + v
	}
	return v
}

// TrustedProxyList is the comma-separated set of peers whose forwarding
// headers are believed when the request log records a client IP.
//
// The default is empty, which means believing none of them: the log then
// shows the address the connection actually came from — the reverse proxy,
// when there is one. That is the right answer to have by default, because
// X-Forwarded-For is a client-writable header and gin trusts every source
// unless told otherwise. A deployment that names its proxy here gets the real
// client address in the logs instead; a deployment that forgets loses only
// the precision, not the trust.
//
// Named for what it parses rather than for the one setting that first used
// it: the same comma-separated-blanks-tolerated shape serves the OpenID
// whitelist, and a function called openIDList returning proxy addresses would
// be a small lie in every future caller.
func TrustedProxyList(name string) []string {
	var out []string
	for _, part := range strings.Split(env(name, ""), ",") {
		if v := strings.TrimSpace(part); v != "" {
			out = append(out, v)
		}
	}
	return out
}

func env(name, def string) string {
	if v := strings.TrimSpace(os.Getenv(name)); v != "" {
		return v
	}
	return def
}

func duration(name string, def time.Duration) (time.Duration, error) {
	raw := strings.TrimSpace(os.Getenv(name))
	if raw == "" {
		return def, nil
	}
	v, err := time.ParseDuration(raw)
	if err != nil {
		return 0, fmt.Errorf("config: %s: %w", name, err)
	}
	return v, nil
}

func integer(name string, def int) (int, error) {
	raw := strings.TrimSpace(os.Getenv(name))
	if raw == "" {
		return def, nil
	}
	v, err := strconv.Atoi(raw)
	if err != nil {
		return 0, fmt.Errorf("config: %s: %w", name, err)
	}
	return v, nil
}

func plural(n int, word string) string {
	if n == 1 {
		return fmt.Sprintf("%d %s", n, word)
	}
	return fmt.Sprintf("%d %ss", n, word)
}
