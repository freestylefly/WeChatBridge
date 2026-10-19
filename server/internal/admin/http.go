package admin

import (
	"errors"
	"io"
	"net/http"
	"strings"
	"time"

	"github.com/gin-gonic/gin"

	"github.com/freestylefly/WeChatBridge/server/internal/httpx"
	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// ---------------------------------------------------------------------------
// Request and response bodies
//
// Field names are the contract (design §11.3, mock/server.js, the console's
// api.js). Go's default marshalling is the Go field name, so a missing tag
// here is a field the console never binds — the same rule, and the same
// reason, as the activation package's.
// ---------------------------------------------------------------------------

// requestItem is one row of the seller's table. createdAt is RFC 3339 in
// UTC with no fractional seconds, which is what the mock's stamp() rendered
// and what the console parses.
type requestItem struct {
	RequestID    string `json:"requestId"`
	InstallID    string `json:"installId"`
	AnchorDigest string `json:"anchorDigest"`
	Nickname     string `json:"nickname"`
	AmountFen    *int64 `json:"amountFen"`
	CreatedAt    string `json:"createdAt"`
	Status       string `json:"status"`
}

type requestsResponse struct {
	Items []requestItem `json:"items"`
}

type confirmRequest struct {
	RequestID string `json:"requestId"`
}

type reissueRequest struct {
	InstallID string `json:"installId"`
}

type revokeRequest struct {
	LicenseID string `json:"licenseId"`
}

type licenseResponse struct {
	LicenseID string `json:"licenseId"`
}

// ---------------------------------------------------------------------------
// Handlers
// ---------------------------------------------------------------------------

// handleRequests is the console's table. An absent status means every state,
// which is what the ledger page asks for; a status that is not one of the
// three the product has is a 400 rather than a silently empty list — see the
// package comment.
func (s *Service) handleRequests(c *gin.Context) {
	want := strings.TrimSpace(c.Query("status"))
	status := store.ActivationStatus(want)
	if want != "" && !knownStatus(status) {
		httpx.Fail(c, httpx.BadRequest("状态筛选值不正确。"))
		return
	}

	views, err := s.activation.ListActivationRequests(c.Request.Context(), status)
	if err != nil {
		httpx.Fail(c, httpx.FromStore(err))
		return
	}

	items := make([]requestItem, 0, len(views))
	for _, v := range views {
		items = append(items, requestItem{
			RequestID:    v.RequestID,
			InstallID:    v.InstallID,
			AnchorDigest: v.AnchorDigest,
			Nickname:     v.Nickname,
			AmountFen:    v.AmountFen,
			CreatedAt:    v.CreatedAt.UTC().Format(time.RFC3339),
			Status:       string(v.Status),
		})
	}
	httpx.OK(c, http.StatusOK, requestsResponse{Items: items})
}

func (s *Service) handleConfirm(c *gin.Context) {
	var body confirmRequest
	if err := c.ShouldBindJSON(&body); err != nil && !errors.Is(err, io.EOF) {
		httpx.Fail(c, httpx.BadRequest("请求体格式不正确。"))
		return
	}
	requestID := strings.TrimSpace(body.RequestID)
	if requestID == "" {
		httpx.Fail(c, httpx.BadRequest("请求号缺失。"))
		return
	}

	licenseID, err := s.Confirm(c.Request.Context(), requestID)
	if err != nil {
		switch {
		case errors.Is(err, store.ErrNotFound):
			httpx.Fail(c, httpx.NotFound("请求的内容不存在。"))
		case errors.Is(err, ErrRequestRejected):
			httpx.Fail(c, httpx.Conflict("该购买请求已被拒绝，无法确认。"))
		default:
			// Includes the lost race the service could not reconcile: the
			// row moved again between the re-read and here.
			httpx.Fail(c, httpx.FromStore(err))
		}
		return
	}

	httpx.OK(c, http.StatusOK, licenseResponse{LicenseID: licenseID})
}

func (s *Service) handleReissue(c *gin.Context) {
	var body reissueRequest
	if err := c.ShouldBindJSON(&body); err != nil && !errors.Is(err, io.EOF) {
		httpx.Fail(c, httpx.BadRequest("请求体格式不正确。"))
		return
	}
	installID := strings.TrimSpace(body.InstallID)
	if installID == "" {
		httpx.Fail(c, httpx.BadRequest("机器码缺失。"))
		return
	}

	licenseID, err := s.Reissue(c.Request.Context(), installID)
	if err != nil {
		if errors.Is(err, store.ErrNotFound) {
			httpx.Fail(c, httpx.NotFound("该机器没有可重签的购买记录。"))
			return
		}
		httpx.Fail(c, httpx.FromStore(err))
		return
	}

	httpx.OK(c, http.StatusOK, licenseResponse{LicenseID: licenseID})
}

func (s *Service) handleRevoke(c *gin.Context) {
	var body revokeRequest
	if err := c.ShouldBindJSON(&body); err != nil && !errors.Is(err, io.EOF) {
		httpx.Fail(c, httpx.BadRequest("请求体格式不正确。"))
		return
	}
	licenseID := strings.TrimSpace(body.LicenseID)
	if licenseID == "" {
		httpx.Fail(c, httpx.BadRequest("许可证号缺失。"))
		return
	}

	if err := s.Revoke(c.Request.Context(), licenseID); err != nil {
		if errors.Is(err, store.ErrNotFound) {
			httpx.Fail(c, httpx.NotFound("许可证不存在。"))
			return
		}
		httpx.Fail(c, httpx.FromStore(err))
		return
	}

	// The mock's empty object, field for field: the console reads the status
	// code and nothing else.
	httpx.OK(c, http.StatusOK, struct{}{})
}

// knownStatus reports whether a status filter names a state the product
// actually has. Rejected is a seller's no; the row stays, and the ledger
// page filters on it.
func knownStatus(s store.ActivationStatus) bool {
	switch s {
	case store.ActivationPending, store.ActivationConfirmed, store.ActivationRejected:
		return true
	}
	return false
}
