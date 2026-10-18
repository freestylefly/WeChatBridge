package auth

import (
	"errors"
	"io"
	"net/http"
	"strings"

	"github.com/gin-gonic/gin"

	"github.com/freestylefly/WeChatBridge/server/internal/httpx"
	"github.com/freestylefly/WeChatBridge/server/internal/store"
	"github.com/freestylefly/WeChatBridge/server/internal/token"
	"github.com/freestylefly/WeChatBridge/server/internal/wechat"
)

// CallbackPath is the redirect URI's path. It is fixed rather than configured
// because it is the domain WeChat has been told about; a typo in a variable is
// an outage that only shows up at the moment somebody tries to sign in.
const CallbackPath = "/v1/auth/wechat/callback"

// The context key a verified access token's claims are stored under. It is an
// unexported type so no other package can collide with it.
type claimsKey struct{}

// AccountIDFrom returns the account behind a bearer token that a RequireAuth
// middleware already verified. A handler reading it without the middleware in
// front of it gets false and must fail — which is the right default, because
// the alternative is an unauthenticated read that looks authenticated.
func AccountIDFrom(c *gin.Context) (string, bool) {
	v, ok := c.Get(claimsKey{})
	if !ok {
		return "", false
	}
	claims, ok := v.(token.Claims)
	if !ok || claims.AccountID == "" {
		return "", false
	}
	return claims.AccountID, true
}

// BearerToken pulls the credential out of an Authorization header.
func BearerToken(c *gin.Context) string {
	raw := c.GetHeader("Authorization")
	const prefix = "bearer "
	if len(raw) <= len(prefix) || !strings.EqualFold(raw[:len(prefix)], prefix) {
		return ""
	}
	return strings.TrimSpace(raw[len(prefix):])
}

// RegisterRoutes mounts everything this package owns.
//
// The route set is passed in as an interface rather than constructed here, so
// the composition root (internal/httpapi, which this package does not own)
// decides what else is mounted and in what order.
func (s *Service) RegisterRoutes(r gin.IRouter) {
	group := r.Group("/v1/auth")
	group.POST("/session", s.handleCreateSession)
	group.GET("/session/:ticket", s.handlePoll)
	group.GET("/session/:ticket/qr", s.handleQRCode)
	group.DELETE("/session", s.handleRevoke)
	group.POST("/refresh", s.handleRefresh)
	group.GET("/wechat/callback", s.handleCallback)

	r.GET("/v1/account", s.RequireAuth(), s.handleAccount)
	r.GET("/v1/quota", s.RequireAuth(), s.handleQuota)
}

// RequireAuth is the bearer middleware. It verifies the signature and the
// expiry and nothing else — no database round trip — which is what a one-hour
// access token buys.
func (s *Service) RequireAuth() gin.HandlerFunc {
	return func(c *gin.Context) {
		raw := BearerToken(c)
		if raw == "" {
			httpx.Fail(c, httpx.Unauthorized("缺少访问令牌。"))
			return
		}
		claims, err := s.issuer.Verify(raw)
		if err != nil {
			// An expired token and a forged one are both 401 with the same
			// body. Distinguishing them in the reply would tell an attacker
			// that a token they forged was detected rather than ignored,
			// which is a free oracle.
			httpx.Fail(c, httpx.Unauthorized("访问令牌无效或已过期。"))
			return
		}
		c.Set(claimsKey{}, claims)
		c.Next()
	}
}

// ---------------------------------------------------------------------------
// Request bodies
// ---------------------------------------------------------------------------

type createSessionRequest struct {
	InstallID string `json:"installId"`
	DeviceID  string `json:"deviceId"`
}

type refreshRequest struct {
	RefreshToken string `json:"refreshToken"`
}

type revokeRequest struct {
	RefreshToken string `json:"refreshToken"`
}

// presentedRefresh finds the refresh token for a refresh or revoke call.
//
// §8.11 left this open between a body field and a header. Both are accepted:
// the body is the canonical form and wins, but a client that would rather not
// have a token in a request body that a proxy might log sends it as a bearer
// instead. Guessing wrong here would be a login that fails for everyone
// depending on which choice was wrong.
func presentedRefresh(c *gin.Context, body string) string {
	if strings.TrimSpace(body) != "" {
		return body
	}
	return BearerToken(c)
}

// ---------------------------------------------------------------------------
// Handlers
// ---------------------------------------------------------------------------

func (s *Service) handleCreateSession(c *gin.Context) {
	var body createSessionRequest
	// A body is optional here. An empty one is a first run; a malformed one
	// is still worth answering 400 rather than pretending nobody sent one.
	if err := c.ShouldBindJSON(&body); err != nil && !errors.Is(err, io.EOF) {
		httpx.Fail(c, httpx.BadRequest("请求体格式不正确。"))
		return
	}

	result, err := s.CreateSession(c.Request.Context(), body.InstallID, body.DeviceID)
	if err != nil {
		if IsRateLimited(err) {
			c.Header("Retry-After", "5")
			httpx.Fail(c, &httpx.APIError{
				Status:  http.StatusTooManyRequests,
				Code:    httpx.CodeRateLimited,
				Message: "待处理的二维码过多，请先完成当前扫码或稍后重试。",
				Cause:   err,
			})
			return
		}
		httpx.Fail(c, httpx.Internal(err))
		return
	}

	httpx.OK(c, http.StatusCreated, createSessionResponse{
		Ticket:       result.Ticket,
		AuthorizeURL: result.AuthorizeURL,
		QRImageURL:   result.QRImageURL,
		ExpiresAt:    newStamp(result.ExpiresAt),
	})
}

func (s *Service) handlePoll(c *gin.Context) {
	result, err := s.Poll(c.Request.Context(), c.Param("ticket"))
	if err != nil {
		httpx.Fail(c, httpx.Internal(err))
		return
	}
	httpx.OK(c, http.StatusOK, toPollResponse(result))
}

func toPollResponse(r PollResult) pollResponse {
	out := pollResponse{Status: r.Status}
	// The tokens go out exactly once. After that the row has hashes and
	// nothing else, and a reply claiming otherwise would be a lie the client
	// believes.
	if r.AccessToken != "" {
		out.AccessToken = r.AccessToken
		out.RefreshToken = r.RefreshToken
	}
	if !r.ExpiresAt.IsZero() {
		out.ExpiresAt = newStamp(r.ExpiresAt)
	}
	if r.Account != nil {
		out.Account = toAccountDTO(*r.Account)
	}
	return out
}

// handleQRCode serves the PNG. The client verifies the magic bytes before
// handing it to a decoder, so an HTML error page here would surface deep
// inside WPF as an UnsupportedException; the content type and the byte count
// are both checked before anything is written.
func (s *Service) handleQRCode(c *gin.Context) {
	ticket := c.Param("ticket")
	session, err := s.store.GetAuthSession(c.Request.Context(), ticket)
	if err != nil {
		if errors.Is(err, store.ErrNotFound) {
			// A PNG-shaped 404 is still a failure the client will reject on
			// its magic-byte check. There is no picture of a ticket that does
			// not exist, so say so.
			c.Status(http.StatusNotFound)
			return
		}
		httpx.Fail(c, httpx.Internal(err))
		return
	}

	png, err := renderQR(session.AuthorizeURL)
	if err != nil {
		httpx.Fail(c, httpx.Internal(err))
		return
	}
	c.Data(http.StatusOK, "image/png", png)
}

func (s *Service) handleCallback(c *gin.Context) {
	code := c.Query("code")
	state := c.Query("state")

	if _, err := s.HandleCallback(c.Request.Context(), code, state); err != nil {
		switch {
		case IsInvalidState(err):
			// 400, and deliberately uninformative. Whether the state was
			// forged, already spent or simply expired is not something the
			// caller needs to know to act, and telling them which it was
			// hands an attacker a probe.
			httpx.Fail(c, httpx.BadRequest("授权状态无效，请重新扫码。"))
		case IsInvalidCode(err):
			httpx.Fail(c, httpx.BadRequest("授权码无效或已使用，请重新扫码。"))
		case IsUpstreamUnavailable(err):
			// The ticket is untouched and the browser gets a retryable answer.
			// It will not retry on its own, but it does not destroy anything
			// by getting this, which a false "expired" would.
			httpx.Fail(c, httpx.Unavailable("微信服务暂时不可用，请稍后重试。"))
		default:
			httpx.Fail(c, httpx.Internal(err))
		}
		return
	}

	// §15.1 step 7, in full: 200, empty body, no redirect. A redirect here is
	// the reflexive browser-shaped answer and it breaks the flow, because the
	// destination is a page in a browser the desktop process has never heard
	// of. The client's channel is the poll, and this handler's only remaining
	// job is to end without damaging anything.
	c.Status(http.StatusOK)
}

func (s *Service) handleRefresh(c *gin.Context) {
	var body refreshRequest
	if err := c.ShouldBindJSON(&body); err != nil && !errors.Is(err, io.EOF) {
		httpx.Fail(c, httpx.BadRequest("请求体格式不正确。"))
		return
	}
	raw := presentedRefresh(c, body.RefreshToken)

	result, err := s.Refresh(c.Request.Context(), raw)
	if err != nil {
		if IsReuseDetected(err) {
			// §15.3: reuse is a 409 and means the chain is gone. The client
			// treats 409 as terminal and sends the user back to the QR.
			httpx.Fail(c, httpx.Conflict("登录凭证已被重复使用，所有会话已失效，请重新扫码登录。"))
			return
		}
		httpx.Fail(c, httpx.Internal(err))
		return
	}

	switch result.Outcome {
	case store.RefreshRotated:
		httpx.OK(c, http.StatusOK, pollResponse{
			Status:       StatusAuthorized,
			AccessToken:  result.AccessToken,
			RefreshToken: result.RefreshToken,
			ExpiresAt:    newStamp(result.ExpiresAt),
			Account:      accountOrNil(result.Account),
		})

	case store.RefreshTransient:
		// 503 and not a token-shaped body with status:"expired". The client
		// has exactly one field telling it whether to clear its stored
		// refresh token, and this is the branch that must not set it. A
		// network blip that logs people out of a paid product is the single
		// worst failure this endpoint can have.
		httpx.Fail(c, httpx.Unavailable("服务暂时不可用，请稍后重试。"))

	default:
		// Expired: the credential is finished and the user signs in again.
		// 200 rather than 401, because the contract for this endpoint has a
		// status word in it and inventing a new status word for the client's
		// state machine to learn is worse than using the one it has.
		httpx.OK(c, http.StatusOK, pollResponse{Status: StatusExpired})
	}
}

func accountOrNil(a *store.Account) *accountDTO {
	if a == nil {
		return nil
	}
	return toAccountDTO(*a)
}

func (s *Service) handleRevoke(c *gin.Context) {
	chainID, err := s.chainFromRequest(c)
	if err != nil {
		httpx.Fail(c, httpx.Internal(err))
		return
	}
	if err := s.Revoke(c.Request.Context(), chainID); err != nil {
		httpx.Fail(c, httpx.Internal(err))
		return
	}
	c.Status(http.StatusNoContent)
}

// chainFromRequest finds the family to revoke from whichever credential the
// client presented.
//
// §8.11 asked whether this takes the refresh or the access token. It takes
// either, because the access token's claims carry the chain id — which is why
// that claim is in there. Sign-out has to work with an expired access token and
// no refresh token to hand, and it has to work with a valid access token and no
// body at all.
func (s *Service) chainFromRequest(c *gin.Context) (string, error) {
	var body revokeRequest
	if err := c.ShouldBindJSON(&body); err != nil && !errors.Is(err, io.EOF) {
		return "", nil
	}
	if raw := presentedRefresh(c, body.RefreshToken); raw != "" {
		chain, err := s.ChainForRefresh(c.Request.Context(), raw)
		if err != nil {
			return "", err
		}
		if chain != "" {
			return chain, nil
		}
	}
	// An expired access token still identifies its chain, and refusing to
	// revoke on that basis would leave a user holding a dead token unable to
	// sign out. Peek verifies the signature and the structure and ignores
	// only the clock, so this cannot be used to revoke a chain by guessing
	// its name — the signature is the same check Verify makes.
	if raw := BearerToken(c); raw != "" {
		if claims, err := s.issuer.Peek(raw); err == nil {
			return claims.ChainID, nil
		}
	}
	return "", nil
}

func (s *Service) handleAccount(c *gin.Context) {
	accountID, ok := AccountIDFrom(c)
	if !ok {
		httpx.Fail(c, httpx.Unauthorized("缺少访问令牌。"))
		return
	}
	account, err := s.Account(c.Request.Context(), accountID)
	if err != nil {
		httpx.Fail(c, httpx.FromStore(err))
		return
	}
	httpx.OK(c, http.StatusOK, toAccountDTO(account))
}

func (s *Service) handleQuota(c *gin.Context) {
	accountID, ok := AccountIDFrom(c)
	if !ok {
		httpx.Fail(c, httpx.Unauthorized("缺少访问令牌。"))
		return
	}
	quota, err := s.Quota(c.Request.Context(), accountID)
	if err != nil {
		httpx.Fail(c, httpx.FromStore(err))
		return
	}
	var resetAt *stamp
	if !quota.ResetAt.IsZero() {
		resetAt = newStamp(quota.ResetAt)
	}
	httpx.OK(c, http.StatusOK, quotaDTO{
		Used:    quota.Used,
		Limit:   quota.Limit,
		ResetAt: resetAt,
	})
}

// renderQR is the one call into the encoder, so the size this service asks for
// is one number in one place.
func renderQR(body string) ([]byte, error) {
	return wechat.RenderQRPNG(body, wechat.DefaultQRSize)
}
