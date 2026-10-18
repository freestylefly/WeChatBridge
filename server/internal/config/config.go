// Package config loads the service's settings from the environment.
//
// Everything here is an environment variable, nothing is a flag, and every
// field has a default that is safe for a local run and obviously wrong for
// production — so the failure mode of a half-configured deployment is a loud
// one at start-up rather than a silent one at the first payment.
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

	// WeChat Pay. The merchant key material is a file path, not an
	// environment value, because the APIv3 key is a 32-character string that
	// belongs in a secret store and not in a shell history.
	WeChatPayMchID           string
	WeChatPayAPIv3Key        string
	WeChatPayPrivateKeyPath  string
	WeChatPaySerialNo        string
	WeChatPayNotifyURL       string
	WeChatPayPlatformCertDir string

	// Licence issuing. The private key is a file path for the same reason.
	LicensePrivateKeyPath string
	// LicenseClockSkew is the tolerance the client will apply. The server sends
	// it along so both sides agree.
	LicenseClockSkew time.Duration

	// Product and quota policy.
	BuyoutProductID        string
	SubscriptionProductID  string
	SubscriptionMonthlyFen int64
	SubscriptionYearlyFen  int64
	MaxDevicesPerOrder     int

	// Lifetimes, in days. Zero for the buy-out plan is not "unconfigured" — a
	// buy-out licence is perpetual by definition and has no expiry field.
	BuyoutLicenseDays       int
	SubscriptionLicenseDays int

	RedeemCodeTTL   time.Duration
	AuthSessionTTL  time.Duration
	OrderTTL        time.Duration
	AccessTokenTTL  time.Duration
	RefreshTokenTTL time.Duration

	// Signing keys for the tokens the client carries. Access is short and
	// self-contained; refresh is checked against the store so it can be revoked.
	TokenSigningSecret string
}

// IsProd reports whether production checks apply.
func (c Config) IsProd() bool { return c.Env == "prod" }

// Load reads the environment and returns the configuration, or an error
// naming every variable that is missing or unparseable — all of them, not the
// first one, because a deployment that fixes errors one restart at a time is a
// deployment that spends the night that way.
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

		WeChatPayMchID:           env("WCB_WECHATPAY_MCH_ID", ""),
		WeChatPayAPIv3Key:        env("WCB_WECHATPAY_API_V3_KEY", ""),
		WeChatPayPrivateKeyPath:  env("WCB_WECHATPAY_PRIVATE_KEY_PATH", ""),
		WeChatPaySerialNo:        env("WCB_WECHATPAY_SERIAL_NO", ""),
		WeChatPayNotifyURL:       env("WCB_WECHATPAY_NOTIFY_URL", ""),
		WeChatPayPlatformCertDir: env("WCB_WECHATPAY_PLATFORM_CERT_DIR", ""),

		LicensePrivateKeyPath: required("WCB_LICENSE_PRIVATE_KEY_PATH"),
		TokenSigningSecret:    required("WCB_TOKEN_SIGNING_SECRET"),
	}

	var err error
	if c.LicenseClockSkew, err = duration("WCB_LICENSE_CLOCK_SKEW", 48*time.Hour); err != nil {
		return c, err
	}
	for _, f := range []struct {
		dst  *time.Duration
		name string
		def  time.Duration
	}{
		{&c.RedeemCodeTTL, "WCB_REDEEM_CODE_TTL", 90 * 24 * time.Hour},
		{&c.AuthSessionTTL, "WCB_AUTH_SESSION_TTL", 5 * time.Minute},
		{&c.OrderTTL, "WCB_ORDER_TTL", 30 * time.Minute},
		{&c.AccessTokenTTL, "WCB_ACCESS_TOKEN_TTL", time.Hour},
		{&c.RefreshTokenTTL, "WCB_REFRESH_TOKEN_TTL", 30 * 24 * time.Hour},
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
		{&c.MaxDevicesPerOrder, "WCB_MAX_DEVICES_PER_ORDER", 3},
		{&c.BuyoutLicenseDays, "WCB_BUYOUT_LICENSE_DAYS", 0},
		{&c.SubscriptionLicenseDays, "WCB_SUBSCRIPTION_LICENSE_DAYS", 31},
	} {
		if *f.dst, err = integer(f.name, f.def); err != nil {
			return c, err
		}
	}

	// Prices are in fen — integer minor units, never a float. A price that
	// cannot be represented exactly in fen is a price that will eventually be
	// charged wrong.
	for _, f := range []struct {
		dst  *int64
		name string
	}{
		{&c.SubscriptionMonthlyFen, "WCB_SUBSCRIPTION_MONTHLY_FEN"},
		{&c.SubscriptionYearlyFen, "WCB_SUBSCRIPTION_YEARLY_FEN"},
	} {
		if *f.dst, err = integer64(f.name, 0); err != nil {
			return c, err
		}
	}

	c.BuyoutProductID = env("WCB_BUYOUT_PRODUCT_ID", "windows-buyout")
	c.SubscriptionProductID = env("WCB_SUBSCRIPTION_PRODUCT_ID", "windows-subscription")

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
		if c.WeChatPayNotifyURL != "" && !strings.HasPrefix(c.WeChatPayNotifyURL, "https://") {
			add("WCB_WECHATPAY_NOTIFY_URL must be https in production")
		}
		if c.WeChatPayMchID != "" {
			// Payment configured but half-configured is worse than not
			// configured: orders would be created and never confirmed.
			for _, v := range []string{c.WeChatPayAPIv3Key, c.WeChatPayPrivateKeyPath, c.WeChatPaySerialNo, c.WeChatPayNotifyURL} {
				if v == "" {
					add("WCB_WECHATPAY_MCH_ID is set but a companion payment variable is not")
					break
				}
			}
		}
	}
	if c.MaxDevicesPerOrder < 1 {
		add("WCB_MAX_DEVICES_PER_ORDER must be at least 1, got %d", c.MaxDevicesPerOrder)
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

// BuyoutLifetime is the buy-out licence's lifetime. It is always zero — a
// buy-out licence has no expiry, and that absence is the product rather than an
// unset field.
func (c Config) BuyoutLifetime() time.Duration {
	if c.BuyoutLicenseDays <= 0 {
		return 0
	}
	return time.Duration(c.BuyoutLicenseDays) * 24 * time.Hour
}

// PaymentConfigured reports whether the billing endpoints should be mounted at
// all. Half-configured payment is worse than absent payment, so this is false
// unless the whole set is present.
func (c Config) PaymentConfigured() bool {
	return c.WeChatPayMchID != "" && c.WeChatPayAPIv3Key != "" &&
		c.WeChatPayPrivateKeyPath != "" && c.WeChatPaySerialNo != "" && c.WeChatPayNotifyURL != ""
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

func integer64(name string, def int64) (int64, error) {
	raw := strings.TrimSpace(os.Getenv(name))
	if raw == "" {
		return def, nil
	}
	v, err := strconv.ParseInt(raw, 10, 64)
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
