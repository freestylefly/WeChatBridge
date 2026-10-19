package activation

import (
	"errors"
	"io"
	"net/http"
	"strings"

	"github.com/gin-gonic/gin"

	"github.com/freestylefly/WeChatBridge/server/internal/httpx"
	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// ---------------------------------------------------------------------------
// Request and response bodies
//
// Field names are the contract (design §11.3, mock/server.js, the client's
// ActivationContract.cs). Go's default marshalling is the Go field name, so a
// missing tag here is a field the client never binds.
// ---------------------------------------------------------------------------

type registerRequest struct {
	InstallID    string `json:"installId"`
	AnchorDigest string `json:"anchorDigest"`
	// AmountFen is what the customer says they paid, in fen. Optional
	// because the amount is the seller's to match against the transfer, not
	// something the server can know; null and absent both mean "not stated",
	// and both stay null on the way to the database.
	AmountFen *int64 `json:"amountFen"`
}

type registerResponse struct {
	RequestID string `json:"requestId"`
	Status    string `json:"status"`
}

type statusResponse struct {
	Status    string `json:"status"`
	LicenseID string `json:"licenseId,omitempty"`
}

type licenceResponse struct {
	Token    string `json:"token"`
	Envelope any    `json:"envelope"`
}

// ---------------------------------------------------------------------------
// Handlers
// ---------------------------------------------------------------------------

func (s *Service) handleRegister(c *gin.Context) {
	accountID, ok := s.accountOf(c)
	if !ok {
		httpx.Fail(c, httpx.Unauthorized("缺少访问令牌。"))
		return
	}

	var body registerRequest
	if err := c.ShouldBindJSON(&body); err != nil && !errors.Is(err, io.EOF) {
		httpx.Fail(c, httpx.BadRequest("请求体格式不正确。"))
		return
	}
	installID := strings.TrimSpace(body.InstallID)
	if installID == "" {
		httpx.Fail(c, httpx.BadRequest("机器码缺失或格式不正确。"))
		return
	}
	if len(installID) > maxInstallIDLen || len(body.AnchorDigest) > maxAnchorDigestLen {
		httpx.Fail(c, httpx.BadRequest("机器码或硬件摘要长度不正确。"))
		return
	}
	if body.AmountFen != nil && *body.AmountFen < 0 {
		// The database would reject this too (amount_fen >= 0), but a
		// CHECK violation is a 500 with a constraint name in the log and
		// a customer who cannot tell what they did wrong.
		httpx.Fail(c, httpx.BadRequest("金额不能为负数。"))
		return
	}

	created, err := s.Register(c.Request.Context(), accountID, installID, strings.TrimSpace(body.AnchorDigest), body.AmountFen)
	if err != nil {
		if errors.Is(err, ErrMachineTaken) {
			httpx.Fail(c, httpx.Conflict("该机器码已有进行中的购买登记，请联系卖家处理。"))
			return
		}
		httpx.Fail(c, httpx.FromStore(err))
		return
	}

	httpx.OK(c, http.StatusCreated, registerResponse{
		RequestID: created.RequestID,
		Status:    string(created.Status),
	})
}

func (s *Service) handleStatus(c *gin.Context) {
	accountID, ok := s.accountOf(c)
	if !ok {
		httpx.Fail(c, httpx.Unauthorized("缺少访问令牌。"))
		return
	}

	// No policy beyond the read: "where is this customer's purchase?" is one
	// indexed query, and the mapping to the wire is the only thing left to
	// decide. A miss is a 404 the client already reads as "nothing in
	// flight" — the purchase page offers to register.
	r, err := s.store.LatestActivationRequest(c.Request.Context(), accountID)
	if err != nil {
		httpx.Fail(c, httpx.FromStore(err))
		return
	}

	out := statusResponse{Status: string(r.Status)}
	if r.LicenseID != "" {
		out.LicenseID = r.LicenseID
	}
	httpx.OK(c, http.StatusOK, out)
}

func (s *Service) handleLicence(c *gin.Context) {
	accountID, ok := s.accountOf(c)
	if !ok {
		httpx.Fail(c, httpx.Unauthorized("缺少访问令牌。"))
		return
	}

	result, err := s.Licence(c.Request.Context(), accountID)
	if err != nil {
		switch {
		case errors.Is(err, store.ErrNotFound):
			// No request at all. 409 rather than 404, matching the mock the
			// client was written against: the customer's poll resolves this
			// on the next round, and a 404 here would read as "you never
			// bought anything" to a user mid-purchase.
			httpx.Fail(c, httpx.Conflict("授权尚未确认，暂时无法获取许可证。"))
		case errors.Is(err, ErrNotConfirmed), errors.Is(err, ErrLicenceDead):
			// The seller has not confirmed yet — or confirmed a different
			// machine, or revoked this one. Retryable by design; the client
			// shows "正在签发".
			httpx.Fail(c, httpx.Conflict("授权尚未确认，暂时无法获取许可证。"))
		default:
			// ErrEnvelopeMissing and a stored envelope that will not parse
			// are server-side corruption: logged, 500, never dressed up as a
			// customer-visible conflict.
			httpx.Fail(c, httpx.Internal(err))
		}
		return
	}

	httpx.OK(c, http.StatusOK, licenceResponse{
		Token:    result.Token,
		Envelope: result.Envelope,
	})
}
