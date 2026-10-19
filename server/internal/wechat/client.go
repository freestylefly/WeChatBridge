// Package wechat is the open-platform client for the website app's OAuth2.0
// QR sign-in.
//
// The client never talks to a browser. It produces a URL for a QR code, and it
// exchanges the code that comes back on the redirect for an OpenID. Everything
// that identifies a person to WeChat happens on this side of the wire; the
// desktop client never sees an AppID, an AppSecret, or a WeChat access token.
//
// Two facts about the platform shape everything here:
//
//   - The redirect domain is configured on the open platform and must be a
//     filed public HTTPS domain. Not 127.0.0.1, not a port, not an IP. So the
//     callback always lands on the server and the desktop client can only learn
//     the outcome by polling. There is no flow in which the desktop process
//     receives the code.
//   - The error body is JSON and the HTTP status is often 200. A client that
//     branches on the status code alone will read an error as a success and
//     then fail on a missing field much further downstream. Every call here
//     parses the body before believing it.
package wechat

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// Scope is the scope the website app's QR sign-in uses. It is not
// snsapi_userinfo: that scope requires the person to confirm a second consent
// screen, and a login flow that can ask for consent is a login flow that can be
// refused. Profile fields that the token does not carry are treated as
// optional, never as a failure — see FetchProfile.
const Scope = "snsapi_login"

// redirectFragment is not decoration. Without it WeChat renders a blank page
// instead of the QR image, and the symptom is a scan that "does nothing"
// rather than an error. It is a fragment, so it goes after the query string and
// is never percent-encoded into the value of a parameter.
const redirectFragment = "#wechat_redirect"

// maxBodyBytes caps what a response may be. WeChat's largest legitimate
// response here is a few hundred bytes; anything larger is a misconfiguration
// or an interception, not an answer.
const maxBodyBytes = 1 << 20

// Config is the website app's identity plus the two base URLs. AppSecret is
// held in memory for the life of the process and is never written to a log, an
// error string, or a URL that gets logged.
type Config struct {
	AppID     string
	AppSecret string
	// AuthURL is the browser-facing entry point, normally
	// https://open.weixin.qq.com/connect/qrconnect.
	AuthURL string
	// APIBase is the server-to-server entry point, normally
	// https://api.weixin.qq.com.
	APIBase string
	// Timeout is the per-request ceiling. Zero means defaultTimeout.
	Timeout time.Duration
}

const defaultTimeout = 10 * time.Second

// Client calls the open platform. It is safe for concurrent use and is meant
// to be created once: the underlying connection pool is the whole point, and a
// client constructed per request throws away every keep-alive and spends a
// handshake on every login.
type Client struct {
	appID     string
	appSecret string
	authURL   string
	apiBase   string
	http      *http.Client
}

// NewClient builds a client with a pooled, time-bounded transport. It does not
// use http.DefaultClient: that one has no timeout at all, which turns a stalled
// connection into a login that hangs until the user's patience runs out.
func NewClient(cfg Config) *Client {
	timeout := cfg.Timeout
	if timeout <= 0 {
		timeout = defaultTimeout
	}
	transport := &http.Transport{
		Proxy:                 http.ProxyFromEnvironment,
		DialContext:           (&net.Dialer{Timeout: 5 * time.Second, KeepAlive: 30 * time.Second}).DialContext,
		ForceAttemptHTTP2:     true,
		MaxIdleConns:          64,
		MaxIdleConnsPerHost:   16,
		IdleConnTimeout:       90 * time.Second,
		TLSHandshakeTimeout:   5 * time.Second,
		ExpectContinueTimeout: time.Second,
	}
	return &Client{
		appID:     cfg.AppID,
		appSecret: cfg.AppSecret,
		authURL:   strings.TrimRight(cfg.AuthURL, "/"),
		apiBase:   strings.TrimRight(cfg.APIBase, "/"),
		http:      &http.Client{Transport: transport, Timeout: timeout},
	}
}

// ---------------------------------------------------------------------------
// Errors
// ---------------------------------------------------------------------------

// APIError is a failure WeChat described in the body. ErrCode is WeChat's
// numeric code and is the field to branch on; ErrMsg is prose and is never
// parsed. The request URL is deliberately absent — it carries the AppSecret in
// its query string, and error strings end up in logs.
type APIError struct {
	Op      string
	Status  int
	ErrCode int
	ErrMsg  string
}

func (e *APIError) Error() string {
	if e.ErrMsg != "" {
		return fmt.Sprintf("wechat: %s: errcode %d: %s", e.Op, e.ErrCode, e.ErrMsg)
	}
	return fmt.Sprintf("wechat: %s: errcode %d", e.Op, e.ErrCode)
}

// Transient reports whether retrying unchanged could plausibly succeed. The
// caller uses this to decide between "try again" and "the sign-in failed",
// and getting it backwards logs paying customers out over a bad minute on
// WeChat's side.
func (e *APIError) Transient() bool {
	if e.Status >= 500 {
		return true
	}
	switch e.ErrCode {
	case -1, // system busy
		45009, // reach the API rate limit
		45011, // API calls per minute over limit
		500:   // server internal error
		return true
	}
	return false
}

// MalformedError is a response that parsed as neither a success nor an error —
// an HTML error page from a proxy, a truncated body, a captive portal. It is
// never transient, because retrying a consistently wrong answer is not
// progress; it needs a human to look at the deployment.
type MalformedError struct {
	Op    string
	Body  []byte
	Cause error
}

func (e *MalformedError) Error() string {
	return fmt.Sprintf("wechat: %s: malformed response (%d bytes)", e.Op, len(e.Body))
}

func (e *MalformedError) Unwrap() error { return e.Cause }

func (e *MalformedError) Transient() bool { return false }

// TransportError is a request that never produced a body: DNS, refused
// connection, TLS, timeout. Always transient.
type TransportError struct {
	Op    string
	Cause error
}

func (e *TransportError) Error() string { return "wechat: " + e.Op + ": " + e.Cause.Error() }

func (e *TransportError) Unwrap() error { return e.Cause }

func (e *TransportError) Transient() bool { return true }

// IsTransient reports whether err is worth retrying. A caller that does not
// recognise the error gets false, because the safe default for an unknown
// failure is "do not silently retry", not "keep going".
func IsTransient(err error) bool {
	if err == nil {
		return false
	}
	var t interface{ Transient() bool }
	if errors.As(err, &t) {
		return t.Transient()
	}
	return false
}

// ---------------------------------------------------------------------------
// Wire shapes
// ---------------------------------------------------------------------------

// tokenResponse is the body of sns/oauth2/access_token. The errcode/errmsg pair
// is present on failure and absent on success, which is why every field is
// optional and why the error check runs first.
type tokenResponse struct {
	AccessToken  string `json:"access_token"`
	ExpiresIn    int64  `json:"expires_in"`
	RefreshToken string `json:"refresh_token"`
	OpenID       string `json:"openid"`
	Scope        string `json:"scope"`
	UnionID      string `json:"unionid"`
	ErrCode      *int   `json:"errcode"`
	ErrMsg       string `json:"errmsg"`
}

type userInfoResponse struct {
	Nickname   string `json:"nickname"`
	HeadImgURL string `json:"headimgurl"`
	OpenID     string `json:"openid"`
	UnionID    string `json:"unionid"`
	ErrCode    *int   `json:"errcode"`
	ErrMsg     string `json:"errmsg"`
}

// ---------------------------------------------------------------------------
// Public surface
// ---------------------------------------------------------------------------

// AuthorizeURL builds the URL to encode into the QR code. state is opaque to
// WeChat and comes back untouched on the redirect, so it is where the ticket
// that ties this scan to this desktop process travels.
func (c *Client) AuthorizeURL(state, redirectURI string) string {
	q := url.Values{}
	q.Set("appid", c.appID)
	q.Set("redirect_uri", redirectURI)
	q.Set("response_type", "code")
	q.Set("scope", Scope)
	q.Set("state", state)
	// url.Values.Encode sorts keys and escapes; the fragment is appended raw
	// afterwards so it stays a fragment.
	return c.authURL + "?" + q.Encode() + redirectFragment
}

// Session is what a code turned into.
type Session struct {
	AccessToken  string
	RefreshToken string
	ExpiresIn    time.Duration
	Scope        string
	OpenID       string
	UnionID      string
}

// Profile is the person, as far as WeChat is willing to say. Nickname and
// AvatarURL are frequently empty: snsapi_login does not carry them and
// sns/userinfo will refuse without the extra scope. That is a normal state,
// not a failure.
type Profile struct {
	Nickname  string
	AvatarURL string
}

// ExchangeCode trades a one-time authorization code for an OpenID. The code
// lives about five minutes and fails on a second use, so a failure here must
// not be retried by the caller.
func (c *Client) ExchangeCode(ctx context.Context, code string) (Session, error) {
	const op = "exchange code"
	if strings.TrimSpace(code) == "" {
		return Session{}, &APIError{Op: op, Status: http.StatusBadRequest, ErrCode: -1, ErrMsg: "empty code"}
	}
	q := url.Values{}
	q.Set("appid", c.appID)
	q.Set("secret", c.appSecret)
	q.Set("code", code)
	q.Set("grant_type", "authorization_code")

	var out tokenResponse
	if err := c.get(ctx, op, "/sns/oauth2/access_token", q, &out); err != nil {
		return Session{}, err
	}
	if out.OpenID == "" {
		// A 200 with neither errcode nor openid is a response we cannot act
		// on. Treating it as success would sign nobody in and report success.
		return Session{}, &MalformedError{Op: op, Body: []byte(`{"errcode":null,"openid":""}`)}
	}
	return Session{
		AccessToken:  out.AccessToken,
		RefreshToken: out.RefreshToken,
		ExpiresIn:    time.Duration(out.ExpiresIn) * time.Second,
		Scope:        out.Scope,
		OpenID:       out.OpenID,
		UnionID:      out.UnionID,
	}, nil
}

// FetchProfile reads the nickname and avatar for a token obtained above. It
// deliberately does not return an error for the "insufficient scope" case:
// those codes are the expected answer under snsapi_login, and failing the
// whole sign-in because a display name is missing would be a bad trade.
func (c *Client) FetchProfile(ctx context.Context, oauthAccessToken, openID string) (Profile, error) {
	const op = "fetch profile"
	if oauthAccessToken == "" || openID == "" {
		return Profile{}, nil
	}
	q := url.Values{}
	q.Set("access_token", oauthAccessToken)
	q.Set("openid", openID)
	q.Set("lang", "zh_CN")

	var out userInfoResponse
	if err := c.get(ctx, op, "/sns/userinfo", q, &out); err != nil {
		if apiErr := (*APIError)(nil); errors.As(err, &apiErr) && !apiErr.Transient() {
			// 42003 (no scope), 48001 (api unauthorized), 40125 (bad secret)
			// all mean "not available", not "broken".
			return Profile{}, nil
		}
		return Profile{}, err
	}
	return Profile{Nickname: out.Nickname, AvatarURL: out.HeadImgURL}, nil
}

// get performs one call and hands back the parsed body only if the body itself
// says the call worked. The status code is read for context and for the
// transient decision, but it is never the sole arbiter: WeChat answers many
// errors with 200.
func (c *Client) get(ctx context.Context, op, path string, q url.Values, out any) error {
	endpoint := c.apiBase + path
	if len(q) > 0 {
		endpoint += "?" + q.Encode()
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, endpoint, nil)
	if err != nil {
		return &TransportError{Op: op, Cause: err}
	}
	req.Header.Set("Accept", "application/json")

	resp, err := c.http.Do(req)
	if err != nil {
		return &TransportError{Op: op, Cause: err}
	}
	defer resp.Body.Close()

	body, err := io.ReadAll(io.LimitReader(resp.Body, maxBodyBytes))
	if err != nil {
		return &TransportError{Op: op, Cause: err}
	}
	if apiErr := checkWechatError(op, resp.StatusCode, body); apiErr != nil {
		return apiErr
	}
	if err := json.Unmarshal(body, out); err != nil {
		return &MalformedError{Op: op, Body: body, Cause: err}
	}
	return nil
}

// checkWechatError extracts errcode/errmsg from a body that may or may not be
// an error at all. It returns nil for a body with no error marker, leaving the
// success parse to the caller.
func checkWechatError(op string, status int, body []byte) *APIError {
	var probe struct {
		ErrCode *int   `json:"errcode"`
		ErrMsg  string `json:"errmsg"`
	}
	if err := json.Unmarshal(body, &probe); err != nil {
		// Not JSON at all. If the status was also bad, say so with the
		// status; if the status looked fine, the caller will turn this into a
		// MalformedError with the bytes attached.
		if status >= 400 {
			return &APIError{Op: op, Status: status, ErrCode: -1, ErrMsg: "non-JSON error response"}
		}
		return nil
	}
	if probe.ErrCode == nil {
		// A missing errcode on a 4xx/5xx is still a failure; WeChat's
		// gateways are not consistent about this.
		if status >= 400 {
			return &APIError{Op: op, Status: status, ErrCode: -1}
		}
		return nil
	}
	if *probe.ErrCode == 0 {
		return nil
	}
	return &APIError{Op: op, Status: status, ErrCode: *probe.ErrCode, ErrMsg: probe.ErrMsg}
}
