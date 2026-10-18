package auth

import (
	"encoding/json"
	"time"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// The wire shapes below are a contract with a C# client written against a
// camelCase serializer, and two of them are load-bearing in ways that are not
// obvious from the struct alone:
//
//   - Field names are tagged explicitly rather than left to a global naming
//     strategy. Gin's own naming strategy is a router-wide setting that some
//     other handler could change, and a contract that another package can
//     break by accident is not a contract.
//
//   - Timestamps are a dedicated type, not time.Time. The client's
//     UtcIso8601Converter parses yyyy-MM-ddTHH:mm:ssZ and nothing else; Go's
//     default marshals RFC 3339 with an offset, and "2026-10-18T20:00:00+08:00"
//     is a parse failure on the other end rather than a wrong-looking string
//     that a human would catch.

// stampLayout is the client's format, written out as a Go reference time.
const stampLayout = "2006-01-02T15:04:05Z"

// stamp is a UTC instant serialised in the client's format. The zero value is
// not special-cased: a field that must be omitted is a pointer and stays nil.
type stamp struct {
	time.Time
}

func newStamp(t time.Time) *stamp { return &stamp{Time: t.UTC()} }

func (s stamp) MarshalJSON() ([]byte, error) {
	return json.Marshal(s.Time.UTC().Format(stampLayout))
}

// createSessionResponse is POST /v1/auth/session.
//
// QrImageURL is absolute because the client turns it into a Uri and hands it
// to a downloader with no base address to resolve against; a relative path
// would depend on the caller knowing its own origin.
type createSessionResponse struct {
	Ticket       string `json:"ticket"`
	AuthorizeURL string `json:"authorizeUrl"`
	QRImageURL   string `json:"qrImageUrl"`
	ExpiresAt    *stamp `json:"expiresAt"`
}

// The four states the client's AuthSessionStatus converter knows. Anything else
// it has never heard of maps to Unknown and the poller keeps waiting, so the
// server is free to add one without breaking an in-flight scan — but the
// server still never sends one it cannot interpret itself.
const (
	StatusPending    = "pending"
	StatusAuthorized = "authorized"
	StatusExpired    = "expired"
	StatusRejected   = "rejected"
)

// pollResponse is GET /v1/auth/session/{ticket} and POST /v1/auth/refresh.
// The token fields are pointers because "there are none" and "the field is
// absent" are the same thing on this wire, and a client must be able to tell
// them apart from a claim that a token was withheld.
type pollResponse struct {
	Status       string      `json:"status"`
	AccessToken  string      `json:"accessToken,omitempty"`
	RefreshToken string      `json:"refreshToken,omitempty"`
	ExpiresAt    *stamp      `json:"expiresAt,omitempty"`
	Account      *accountDTO `json:"account,omitempty"`
}

// accountDTO is the account as the client sees it. These are our names, not
// WeChat's — headimgurl and friends do not travel. The client's openID is
// deliberately absent: it is an identity anchor, not something a client needs
// to hold, and a client that can read it is a client that can log it.
type accountDTO struct {
	ID          string `json:"id"`
	Nickname    string `json:"nickname"`
	AvatarURL   string `json:"avatarUrl"`
	Plan        string `json:"plan"`
	CreatedAt   *stamp `json:"createdAt"`
	LastLoginAt *stamp `json:"lastLoginAt"`
	WechatBound bool   `json:"wechatBound"`
}

func toAccountDTO(a store.Account) *accountDTO {
	plan := string(a.Plan)
	if plan == "" {
		// A plan the client does not know about is a plan it must not treat
		// as free, and it must not treat as paid either. An empty string
		// fails both branches closed.
		plan = ""
	}
	return &accountDTO{
		ID:          a.ID,
		Nickname:    a.Nickname,
		AvatarURL:   a.AvatarURL,
		Plan:        plan,
		CreatedAt:   newStamp(a.CreatedAt),
		LastLoginAt: newStamp(a.LastLoginAt),
		WechatBound: a.WechatBound || a.OpenID != "",
	}
}

// quotaDTO is GET /v1/quota.
type quotaDTO struct {
	Used    int64  `json:"used"`
	Limit   int64  `json:"limit"`
	ResetAt *stamp `json:"resetAt"`
}
