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
		LogLevel:      env("WCB_LOG_LEVEL", "info"),

		DatabaseURL: required("WCB_DATABASE_URL"),

		WeChatAppID:     required("WCB_WECHAT_APP_ID"),
		WeChatAppSecret: required("WCB_WECHAT_APP_SECRET"),
		WeChatAuthURL:   env("WCB_WECHAT_AUTH_URL", "https://open.weixin.qq.com/connect/qrconnect"),
		WeChatAPIBase:   env("WCB_WECHAT_API_BASE", "https://api.weixin.qq.com"),

		LicensePrivateKeyPath: required("WCB_LICENSE_PRIVATE_KEY_PATH"),
		TokenSigningSecret:    required("WCB_TOKEN_SIGNING_SECRET"),

		AdminOpenIDs: openIDList("WCB_ADMIN_OPENIDS"),
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

	c.BuyoutProductID = env("WCB_BUYOUT_PRODUCT_ID", "windows-buyout")

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
// openIDList parses a comma-separated OpenID whitelist, dropping blanks so
// that "a,,b" and "a, b" both mean {a, b} — a stray comma in a deploy
// script must not silently shrink the whitelist.
func openIDList(name string) []string {
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
