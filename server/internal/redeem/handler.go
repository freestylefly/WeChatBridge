package redeem

import (
	"context"
	"errors"
	"io"
	"net/http"
	"time"

	"github.com/gin-gonic/gin"

	"github.com/freestylefly/WeChatBridge/server/internal/httpx"
	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// Register mounts the buy-out edition's one online endpoint.
//
// Note on a gap this cannot close from here: the client's RedeemService reads a
// `reason` string out of the error body (RedeemReason, camelCase) to pick which
// message to show, and httpx.ErrorBody carries only code/message/request_id.
// Until httpx grows a reason field the client falls back to its generic
// non-retryable message, which is correct behaviour but loses the wording. The
// error *codes* below are the contract that does survive.
func (s *Service) Register(r gin.IRouter) {
	r.POST("/v1/license/redeem", s.handleRedeem)
}

func (s *Service) handleRedeem(c *gin.Context) {
	var req Request
	if err := c.ShouldBindJSON(&req); err != nil {
		httpx.Fail(c, httpx.BadRequest("请求体不合法。"))
		return
	}

	result, err := s.Redeem(c.Request.Context(), req)
	if err != nil {
		httpx.Fail(c, asAPIError(err))
		return
	}
	httpx.OK(c, result.HTTPStatus, result)
}

// asAPIError keeps a handler from having to know which errors are already shaped.
// Anything that is not already an APIError becomes a 500 with a cause that is
// logged and never serialised.
func asAPIError(err error) *httpx.APIError {
	var apiErr *httpx.APIError
	if errors.As(err, &apiErr) {
		return apiErr
	}
	return httpx.Internal(err)
}

// Authenticator decides whether a caller may touch the code administration
// endpoints. It returns the error to answer with, or nil to let the request
// through.
//
// It is a parameter rather than a hard-coded check because the session and role
// store lives in another package, and because an endpoint that mints entitlements
// must never be reachable by accident.
type Authenticator func(c *gin.Context) *httpx.APIError

// RegisterAdmin mounts code generation, extension and revocation.
//
// A nil authenticator refuses everything. Failing closed is the only acceptable
// default for an endpoint that hands out licences.
func (s *Service) RegisterAdmin(r gin.IRouter, auth Authenticator) {
	if auth == nil {
		auth = func(*gin.Context) *httpx.APIError {
			return httpx.Forbidden("管理接口未配置鉴权。")
		}
	}
	g := r.Group("/v1/admin", func(c *gin.Context) {
		if err := auth(c); err != nil {
			httpx.Fail(c, err)
			c.Abort()
			return
		}
		c.Next()
	})
	g.POST("/redeem-codes", s.handleGenerateCodes)
	g.GET("/redeem-codes/:hash", s.handleGetCode)
	g.PATCH("/redeem-codes/:hash", s.handlePatchCode)
}

// GenerateRequest asks for n codes.
type GenerateRequest struct {
	// Count is how many codes to mint. Bounded so one request cannot become a
	// bulk dump of the whole ledger.
	Count int `json:"count"`
	// ProductID defaults to the service's own product.
	ProductID string `json:"productId"`
	// TTLHours overrides the configured 90-day window. Support uses it to
	// extend rather than reissue, because a customer holding an unspent code
	// should not have to be told it died.
	TTLHours int `json:"ttlHours"`
	// ExpiresAt is an explicit absolute expiry, RFC 3339. It wins over TTLHours.
	ExpiresAt string `json:"expiresAt"`
}

// GeneratedCode is one minted code.
//
// The plaintext appears here and nowhere else — not in the store, not in a log
// line, not in a later support query. Whoever calls this endpoint owns delivering
// it, and losing it means revoking and minting another.
type GeneratedCode struct {
	Code      string `json:"code"`
	Digest    string `json:"digest"`
	ExpiresAt string `json:"expiresAt"`
}

// GenerateResponse is the one and only time the plaintext exists on the wire.
type GenerateResponse struct {
	Count int             `json:"count"`
	Codes []GeneratedCode `json:"codes"`
}

// maxGeneratedPerCall bounds one generation request.
const maxGeneratedPerCall = 100

func (s *Service) handleGenerateCodes(c *gin.Context) {
	var req GenerateRequest
	if err := c.ShouldBindJSON(&req); err != nil {
		httpx.Fail(c, httpx.BadRequest("请求体不合法。"))
		return
	}
	if req.Count <= 0 {
		req.Count = 1
	}
	if req.Count > maxGeneratedPerCall {
		httpx.Fail(c, httpx.BadRequest("单次最多生成 100 个兑换码。"))
		return
	}
	productID := req.ProductID
	if productID == "" {
		productID = s.opts.ProductID
	}
	if productID != s.opts.ProductID {
		httpx.Fail(c, &httpx.APIError{
			Status: http.StatusUnprocessableEntity, Code: httpx.CodeWrongProduct,
			Message: "该产品不是本服务可生成的兑换码所属产品。",
		})
		return
	}

	now := s.opts.now()
	expires := now.Add(s.opts.CodeTTL)
	if req.TTLHours > 0 {
		expires = now.Add(hoursDuration(req.TTLHours))
	}
	if req.ExpiresAt != "" {
		parsed, err := time.Parse(time.RFC3339, req.ExpiresAt)
		if err != nil {
			httpx.Fail(c, httpx.BadRequest("expiresAt 不是合法的 RFC 3339 时间。"))
			return
		}
		expires = parsed.UTC()
	}
	if !expires.After(now) {
		httpx.Fail(c, httpx.BadRequest("expiresAt 必须晚于当前时间。"))
		return
	}

	resp := GenerateResponse{Count: req.Count, Codes: make([]GeneratedCode, 0, req.Count)}
	for i := 0; i < req.Count; i++ {
		canonical, digest, err := Generate()
		if err != nil {
			httpx.Fail(c, httpx.Internal(err))
			return
		}
		if err := s.store.CreateRedeemCode(c.Request.Context(), store.RedeemCode{
			Hash:      digest,
			ProductID: productID,
			Plan:      store.PlanBuyout,
			Lifetime:  s.opts.Lifetime,
			IssuedAt:  now,
			ExpiresAt: expires,
		}); err != nil {
			httpx.Fail(c, httpx.FromStore(err))
			return
		}
		// The digest is logged, the code is not. Never the other way round.
		s.opts.log().Info("redeem code generated",
			"code_digest", digest, "product_id", productID, "expires_at", expires)
		resp.Codes = append(resp.Codes, GeneratedCode{
			Code:      canonical,
			Digest:    digest,
			ExpiresAt: formatTime(expires),
		})
	}
	httpx.OK(c, http.StatusCreated, resp)
}

// PatchRequest extends or voids a code.
type PatchRequest struct {
	// ExpiresAt is an absolute RFC 3339 expiry. The 90-day TTL is a product
	// decision, not a law: a customer whose code lapsed while it sat in a
	// support queue is a customer who paid and got nothing.
	ExpiresAt string `json:"expiresAt"`
	// Revoke voids the code. It cannot be undone, so it is a separate explicit
	// flag rather than an implicit side effect of the call.
	Revoke bool `json:"revoke"`
}

func (s *Service) handlePatchCode(c *gin.Context) {
	digest := c.Param("hash")
	if digest == "" {
		httpx.Fail(c, httpx.BadRequest("缺少兑换码摘要。"))
		return
	}

	var req PatchRequest
	if err := c.ShouldBindJSON(&req); err != nil && !errors.Is(err, io.EOF) {
		httpx.Fail(c, httpx.BadRequest("请求体不合法。"))
		return
	}
	if req.ExpiresAt == "" && !req.Revoke {
		httpx.Fail(c, httpx.BadRequest("请至少提供 expiresAt 或 revoke。"))
		return
	}

	ctx := c.Request.Context()
	now := s.opts.now()

	if req.ExpiresAt != "" {
		parsed, err := time.Parse(time.RFC3339, req.ExpiresAt)
		if err != nil {
			httpx.Fail(c, httpx.BadRequest("expiresAt 不是合法的 RFC 3339 时间。"))
			return
		}
		if err := s.store.ExtendRedeemCode(ctx, digest, parsed.UTC()); err != nil {
			httpx.Fail(c, httpx.FromStore(err))
			return
		}
		s.opts.log().Info("redeem code extended", "code_digest", digest, "expires_at", parsed.UTC())
	}

	if req.Revoke {
		if err := s.store.RevokeRedeemCode(ctx, digest, now); err != nil {
			httpx.Fail(c, httpx.FromStore(err))
			return
		}
		s.opts.log().Info("redeem code revoked", "code_digest", digest)
	}

	status, err := s.codeStatus(ctx, digest)
	if err != nil {
		httpx.Fail(c, httpx.FromStore(err))
		return
	}
	httpx.OK(c, http.StatusOK, status)
}

// CodeStatus is the support view of a code. It is keyed by digest, so the
// endpoint cannot be used to confirm whether a guessed plaintext is real beyond
// what the digest itself already gives away, and it never returns the code.
type CodeStatus struct {
	Digest            string     `json:"digest"`
	ProductID         string     `json:"productId"`
	Plan              store.Plan `json:"plan"`
	IssuedAt          string     `json:"issuedAt"`
	ExpiresAt         string     `json:"expiresAt"`
	RedeemedAt        *string    `json:"redeemedAt"`
	RedeemedInstallID string     `json:"redeemedInstallId,omitempty"`
	RevokedAt         *string    `json:"revokedAt"`
	LicenseID         string     `json:"licenseId,omitempty"`
}

func (s *Service) handleGetCode(c *gin.Context) {
	digest := c.Param("hash")
	if digest == "" {
		httpx.Fail(c, httpx.BadRequest("缺少兑换码摘要。"))
		return
	}
	status, err := s.codeStatus(c.Request.Context(), digest)
	if err != nil {
		httpx.Fail(c, httpx.FromStore(err))
		return
	}
	httpx.OK(c, http.StatusOK, status)
}

func (s *Service) codeStatus(ctx context.Context, digest string) (CodeStatus, error) {
	code, err := s.store.GetRedeemCode(ctx, digest)
	if err != nil {
		return CodeStatus{}, err
	}
	status := CodeStatus{
		Digest:            code.Hash,
		ProductID:         code.ProductID,
		Plan:              code.Plan,
		IssuedAt:          formatTime(code.IssuedAt),
		RedeemedInstallID: code.RedeemedInstallID,
		LicenseID:         code.LicenseID,
	}
	if !code.ExpiresAt.IsZero() {
		status.ExpiresAt = formatTime(code.ExpiresAt)
	}
	if code.RedeemedAt != nil {
		v := formatTime(*code.RedeemedAt)
		status.RedeemedAt = &v
	}
	if code.RevokedAt != nil {
		v := formatTime(*code.RevokedAt)
		status.RevokedAt = &v
	}
	return status, nil
}

func hoursDuration(h int) time.Duration { return time.Duration(h) * time.Hour }
