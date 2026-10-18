package billing

import (
	"errors"
	"io"
	"net/http"

	"github.com/gin-gonic/gin"

	"github.com/freestylefly/WeChatBridge/server/internal/httpx"
)

// Principal resolves the account behind a request.
//
// It is injected because sessions live in another package, and because an
// endpoint that reads an account id out of a request body would let anyone buy a
// licence for anyone. The default implementation below refuses everything, which
// is the right failure mode: an unmounted session means no purchases, not open
// purchases.
type Principal interface {
	AccountID(c *gin.Context) (string, bool)
}

// principalFunc adapts a function to Principal.
type principalFunc func(c *gin.Context) (string, bool)

func (f principalFunc) AccountID(c *gin.Context) (string, bool) { return f(c) }

// DenyAll is the Principal used when no session has been wired in.
var DenyAll Principal = principalFunc(func(*gin.Context) (string, bool) { return "", false })

// Register mounts the subscription endpoints.
func (s *Service) Register(r gin.IRouter, principal Principal) {
	if principal == nil {
		principal = DenyAll
	}
	account := func(c *gin.Context) (string, bool) { return principal.AccountID(c) }

	g := r.Group("/v1/billing")
	g.POST("/orders", accountOnly(account, s.handleCreateOrder))
	g.GET("/orders/:orderId", accountOnly(account, s.handleGetOrder))
	g.POST("/orders/:orderId/redeem", accountOnly(account, s.handleRedeem))

	// The callback is not authenticated by us: WeChat proves itself with a
	// signature over the body, which is checked before anything is believed.
	g.POST("/notify", s.handleNotify)

	// The QR is fetched without a bearer token on purpose — the client's own
	// tests assert it never sends one to a URL that might be a CDN — so this one
	// is a capability URL over a 128-bit random order id.
	g.GET("/orders/:orderId/qr.png", s.handleQR)
}

// accountOnly resolves the Principal once, puts the account in the request
// context and refuses the call when there is none. The domain methods take the
// account id as an argument, so no decision about who is asking lives in a gin
// context.
func accountOnly(account func(*gin.Context) (string, bool), next gin.HandlerFunc) gin.HandlerFunc {
	return func(c *gin.Context) {
		id, ok := account(c)
		if !ok || id == "" {
			httpx.Fail(c, httpx.Unauthorized("请先登录。"))
			c.Abort()
			return
		}
		c.Set(accountContextKey, id)
		// next is the route's own handler, captured by this closure. Calling
		// c.Next() here would advance gin's engine chain — which ends at this
		// very closure — and silently answer 200 with an empty body.
		next(c)
	}
}

func (s *Service) handleCreateOrder(c *gin.Context) {
	var req CreateOrderRequest
	if err := c.ShouldBindJSON(&req); err != nil {
		httpx.Fail(c, httpx.BadRequest("请求体不合法。"))
		return
	}
	view, err := s.CreateOrder(c.Request.Context(), currentAccount(c), req)
	if err != nil {
		httpx.Fail(c, asAPIError(err))
		return
	}
	httpx.OK(c, view.HTTPStatus, view)
}

func (s *Service) handleGetOrder(c *gin.Context) {
	view, err := s.OrderSnapshot(c.Request.Context(), currentAccount(c), c.Param("orderId"))
	if err != nil {
		httpx.Fail(c, asAPIError(err))
		return
	}
	httpx.OK(c, http.StatusOK, view)
}

func (s *Service) handleRedeem(c *gin.Context) {
	var req struct {
		// InstallID is optional. Present, it is the machine the licence binds to,
		// which is what the client needs for an offline licence to survive a
		// restart; absent, the licence's subject is the account.
		InstallID    string `json:"installId"`
		DeviceDigest string `json:"deviceDigest"`
	}
	if err := c.ShouldBindJSON(&req); err != nil && !errors.Is(err, io.EOF) {
		httpx.Fail(c, httpx.BadRequest("请求体不合法。"))
		return
	}
	result, err := s.Redeem(c.Request.Context(), currentAccount(c), c.Param("orderId"), req.InstallID)
	if err != nil {
		httpx.Fail(c, asAPIError(err))
		return
	}
	httpx.OK(c, result.HTTPStatus, result)
}

// handleNotify is the one handler that does not answer in the API's error shape,
// because its caller is not the API's client.
func (s *Service) handleNotify(c *gin.Context) {
	body, err := io.ReadAll(io.LimitReader(c.Request.Body, 1<<20))
	if err != nil {
		c.JSON(http.StatusBadRequest, NotifyResult{Code: "FAIL", Message: "读取请求体失败"})
		return
	}

	outcome, _ := s.HandleNotify(c.Request.Context(), c.Request.Header, body)
	c.JSON(outcome.Status, outcome.Body)
}

func (s *Service) handleQR(c *gin.Context) {
	png, err := s.QRPNG(c.Request.Context(), c.Param("orderId"))
	if err != nil {
		httpx.Fail(c, asAPIError(err))
		return
	}
	c.Header("Content-Type", "image/png")
	c.Header("Cache-Control", "private, max-age=300")
	// A code_url is a payment credential for as long as the order is open, so it
	// must not sit in a shared cache.
	c.Header("X-Content-Type-Options", "nosniff")
	c.Data(http.StatusOK, "image/png", png)
}

// accountContextKey is where accountOnly leaves the resolved account.
const accountContextKey = "billing_account"

// currentAccount returns the account the accountOnly middleware resolved.
func currentAccount(c *gin.Context) string {
	if v, ok := c.Get(accountContextKey); ok {
		if s, ok := v.(string); ok {
			return s
		}
	}
	return ""
}

func asAPIError(err error) *httpx.APIError {
	var apiErr *httpx.APIError
	if errors.As(err, &apiErr) {
		return apiErr
	}
	return httpx.Internal(err)
}
