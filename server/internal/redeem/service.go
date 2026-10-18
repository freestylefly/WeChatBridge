// Package redeem burns one-time redemption codes for the buy-out edition.
//
// The endpoint is a burn, not a trade. An earlier draft of this contract was
// "POST /v1/license/activate { licenseKey }", which is four products' worth of
// trouble: a leaked key becomes a permanent minting right; "used on another
// machine" and "used on two machines" become indistinguishable; a lost response is
// unrecoverable because there is no server state to consult; and nothing is ever
// burned, so there is no "already used" for support to quote.
//
// One code, one (code, installId) pair, one licence — that is the invariant, and
// the idempotency key exists to make it survivable. See the design document, §3.1
// and §3.2.
package redeem

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"strings"
	"time"

	"github.com/freestylefly/WeChatBridge/server/internal/httpx"
	"github.com/freestylefly/WeChatBridge/server/internal/license"
	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// Status values the client branches on. RedeemResponse.GrantsLicense accepts
// exactly these two and refuses to write a licence file for anything else, so a
// status the server invents here can never be mistaken for success.
const (
	// StatusRedeemed is the first answer: this request burned the code.
	StatusRedeemed = "redeemed"

	// StatusAlreadyRedeemed means the code was already burned by this machine
	// and the original envelope is being returned unchanged.
	StatusAlreadyRedeemed = "alreadyRedeemed"
)

// Reason values the client's RedeemReason pins. httpx.ErrorBody has no field to
// carry them — see Register for the note about that gap.
const (
	ReasonInvalidCode              = "invalidCode"
	ReasonAlreadyRedeemedElsewhere = "alreadyRedeemedElsewhere"
	ReasonExpired                  = "expired"
	ReasonRevoked                  = "revoked"
	ReasonProductMismatch          = "productMismatch"
	ReasonRateLimited              = "rateLimited"
)

// autoKeyDomain separates the derived idempotency key from every other hash over
// the same inputs. Without a domain separator two hash namespaces sharing one
// table is an accident waiting to happen.
const autoKeyDomain = "wechatbridge.redemption.autokey.v1"

// Request is the wire body. Field names are pinned lowercase because this is a
// contract with a client written in another language, and a naming-policy change
// on either side would break every customer silently.
type Request struct {
	RedeemCode   string `json:"redeemCode" binding:"required"`
	InstallID    string `json:"installId" binding:"required"`
	DeviceDigest string `json:"deviceDigest"`
	RequestID    string `json:"requestId" binding:"required"`
	// ProductID is not in §3's body. The client sends it anyway, and it is the
	// one field that lets a subscription code be refused here instead of being
	// burnt and then discovered to be the wrong product.
	ProductID string `json:"productId"`
}

// Result is the success body.
type Result struct {
	Status string `json:"status"`
	// LicenseToken is base64url over the envelope JSON — not the JSON itself.
	// The client base64url-decodes it and only then deserialises, so a raw JSON
	// string here would arrive as a licence that will not parse.
	LicenseToken string `json:"licenseToken"`
	LicenseID    string `json:"licenseId"`
	RedeemedAt   string `json:"redeemedAt"`
	// ExpiresAt is null for the buy-out plan: a buy-out licence has no expiry
	// and that absence is the product rather than a missing field.
	ExpiresAt *string `json:"expiresAt"`

	// HTTPStatus is 201 for the first answer and 200 for a replay.
	HTTPStatus int `json:"-"`
}

// Options is everything the service needs that is not a collaborator. A struct
// rather than eleven positional arguments, so the next policy field does not have
// to be threaded through every call site.
type Options struct {
	// ProductID is the code's product. A code minted for the other edition is
	// refused rather than burnt.
	ProductID string

	// MaxDevices is how many machines one buy-out licence may be pinned to.
	MaxDevices int

	// Lifetime is the licence's lifetime. Zero means perpetual, which is the
	// buy-out definition.
	Lifetime time.Duration

	// CodeTTL is how long a freshly minted code stays spendable. The design
	// settles on 90 days: short enough that a code leaked into the wild before
	// the customer uses it expires, long enough to cover an email, an order page
	// bookmarked for three months and a support queue. Support can extend a code;
	// nobody has to reissue one.
	CodeTTL time.Duration

	// ClockSkew is the tolerance the client applies, carried here so both sides
	// agree on the number.
	ClockSkew time.Duration

	// Entitlements are the bits the licence unlocks.
	Entitlements []string

	// Clock is injectable so expiry and idempotency are testable.
	Clock func() time.Time

	// Logger never receives a code. Only digests, ids and counts.
	Logger *slog.Logger
}

func (o *Options) now() time.Time {
	if o.Clock != nil {
		return o.Clock()
	}
	return time.Now().UTC()
}

func (o *Options) log() *slog.Logger {
	if o.Logger != nil {
		return o.Logger
	}
	return slog.Default()
}

// Service burns codes. It holds the licence signer, so it is the only place in
// this package that can mint an entitlement.
type Service struct {
	store  store.LicenceStore
	signer *license.Signer
	opts   Options
}

// NewService wires the service. It fails loudly rather than defaulting anything,
// because a service with no signer would burn every code and answer 500.
func NewService(st store.LicenceStore, signer *license.Signer, opts Options) (*Service, error) {
	if st == nil {
		return nil, errors.New("redeem: store is nil")
	}
	if signer == nil {
		return nil, errors.New("redeem: signer is nil")
	}
	if strings.TrimSpace(opts.ProductID) == "" {
		return nil, errors.New("redeem: product id is empty")
	}
	if opts.MaxDevices < 1 {
		return nil, fmt.Errorf("redeem: max devices is %d, want at least 1", opts.MaxDevices)
	}
	return &Service{store: st, signer: signer, opts: opts}, nil
}

// Redeem burns one code and returns the licence it bought.
//
// The order of the checks is the design:
//
//  1. normalise, because nothing downstream can look a code up otherwise;
//  2. ask the idempotency table first and return the stored bytes verbatim —
//     re-signing would produce a different signature (ECDSA picks a random k),
//     and while the client accepts either, any future hashing or de-duplication
//     forks on the difference;
//  3. only then look at the code, so a retry after a lost response is a question
//     rather than a second burn;
//  4. sign before burning, so the window between the two irreversible steps is as
//     short as it can be;
//  5. burn — the conditional update that makes one code mean at most one licence
//     however many requests arrive at once;
//  6. record, then bind.
//
// Steps 5 and 6 are two store calls and therefore not one transaction: the store
// interface exposes no transaction handle. The conditional burn in step 5 carries
// the invariant on its own. A crash between 5 and 6 leaves a burnt code with no
// envelope, which the same-installId branch below recovers by re-issuing under the
// licence id the burn recorded — the one path in this package that can produce a
// different signature for the same licence.
func (s *Service) Redeem(ctx context.Context, req Request) (Result, error) {
	digest, _, err := CodeDigest(req.RedeemCode)
	if err != nil {
		return Result{}, &httpx.APIError{
			Status: http.StatusBadRequest, Code: httpx.CodeInvalidCode,
			Message: "兑换码格式不正确，请检查后重新输入。",
		}
	}

	installID := strings.TrimSpace(req.InstallID)
	requestID := strings.TrimSpace(req.RequestID)
	if installID == "" || requestID == "" {
		return Result{}, httpx.BadRequest("installId 与 requestId 均不能为空。")
	}
	if req.ProductID != "" && req.ProductID != s.opts.ProductID {
		// Refused before the code is even looked up: the request named a
		// different product, and there is nothing to burn.
		return Result{}, &httpx.APIError{
			Status: http.StatusUnprocessableEntity, Code: httpx.CodeWrongProduct,
			Message: "该兑换码不适用于本产品。",
		}
	}

	// ① The client persists its request id before the first request and reuses it
	// for every retry, so a hit here is the lost-response case from §3.2.
	rec, err := s.store.GetRedemptionRequest(ctx, requestID)
	if err == nil {
		return s.replay(rec)
	}
	if !errors.Is(err, store.ErrNotFound) {
		return Result{}, httpx.Internal(err)
	}

	// ② The same machine arriving with a different request id — its local pending
	// record was lost, but the burn recorded which machine won, and the derived
	// key finds the same envelope. Even this path returns identical bytes.
	rec, err = s.store.GetRedemptionRequest(ctx, autoKey(digest, installID))
	if err == nil {
		return s.replay(rec)
	}
	if !errors.Is(err, store.ErrNotFound) {
		return Result{}, httpx.Internal(err)
	}

	code, err := s.store.GetRedeemCode(ctx, digest)
	if err != nil {
		if errors.Is(err, store.ErrNotFound) {
			return Result{}, &httpx.APIError{
				Status: http.StatusBadRequest, Code: httpx.CodeInvalidCode,
				Message: "兑换码不存在或已被使用。",
			}
		}
		return Result{}, httpx.Internal(err)
	}

	if code.ProductID != s.opts.ProductID {
		return Result{}, &httpx.APIError{
			Status: http.StatusUnprocessableEntity, Code: httpx.CodeWrongProduct,
			Message: "该兑换码不适用于本产品。",
		}
	}

	now := s.opts.now()

	if code.RevokedAt != nil {
		return Result{}, &httpx.APIError{
			Status: http.StatusGone, Code: httpx.CodeCodeRevoked,
			Message: "该兑换码已被作废，请联系客服。",
		}
	}
	if !code.ExpiresAt.IsZero() && !now.Before(code.ExpiresAt) {
		return Result{}, &httpx.APIError{
			Status: http.StatusGone, Code: httpx.CodeCodeExpired,
			Message: "该兑换码已过期，请联系客服延长。",
		}
	}

	licenseID, err := newLicenseID()
	if err != nil {
		return Result{}, httpx.Internal(err)
	}

	if code.RedeemedAt != nil {
		if code.RedeemedInstallID == installID {
			return s.reissue(ctx, digest, installID, code, now)
		}
		return Result{}, &httpx.APIError{
			Status: http.StatusConflict, Code: httpx.CodeCodeRedeemed,
			Message: "该兑换码已在另一台设备上使用，请联系客服。",
		}
	}

	// ③ The device-slot check runs before the burn. Burning a code and only then
	// discovering the machine count was full would take a paid customer down the
	// self-service path to nowhere.
	active, err := s.activeDevices(ctx, licenseID)
	if err != nil {
		return Result{}, httpx.Internal(err)
	}
	if active >= s.opts.MaxDevices {
		return Result{}, s.installMismatch(active)
	}

	token, envelope, err := s.sign(installID, now)
	if err != nil {
		return Result{}, httpx.Internal(err)
	}

	// ④ The conditional update. Everything above was reversible; this is not.
	if err := s.store.BurnRedeemCode(ctx, digest, installID, licenseID, now); err != nil {
		if !errors.Is(err, store.ErrConflict) {
			return Result{}, httpx.Internal(err)
		}
		// Somebody else won the race while this request was signing. Re-read: if
		// it was this machine the answer is the licence, otherwise it is a 409.
		fresh, ferr := s.store.GetRedeemCode(ctx, digest)
		if ferr != nil {
			if errors.Is(ferr, store.ErrNotFound) {
				return Result{}, &httpx.APIError{
					Status: http.StatusBadRequest, Code: httpx.CodeInvalidCode,
					Message: "兑换码不存在或已被使用。",
				}
			}
			return Result{}, httpx.Internal(ferr)
		}
		if fresh.RedeemedInstallID == installID {
			return s.reissue(ctx, digest, installID, fresh, now)
		}
		return Result{}, &httpx.APIError{
			Status: http.StatusConflict, Code: httpx.CodeCodeRedeemed,
			Message: "该兑换码已在另一台设备上使用，请联系客服。",
		}
	}

	if err := s.record(ctx, digest, installID, licenseID, requestID, envelope, now); err != nil {
		return Result{}, httpx.Internal(err)
	}

	s.opts.log().Info("redeemed",
		"code_digest", digest,
		"license_id", licenseID,
		"install_id", installID,
		"status", StatusRedeemed)

	if err := s.bind(ctx, licenseID, installID, now); err != nil {
		return Result{}, err
	}

	return Result{
		Status:       StatusRedeemed,
		LicenseToken: token,
		LicenseID:    licenseID,
		RedeemedAt:   formatTime(now),
		ExpiresAt:    expiryOf(s.opts.Lifetime, now),
		HTTPStatus:   http.StatusCreated,
	}, nil
}

// sign produces the envelope, its base64url token, and the bytes to persist. It is
// the only call to the signer in this package.
//
// The subject is the install id, which is a deliberate deviation from §3.1's
// second layer ("Subject derived from the licence id, never from the request
// body"). The client decides otherwise: RedeemService.Accept refuses any licence
// whose subject is not its own install id, and LicenseResolver binds on the same
// equality, so a derived subject would leave every buy-out customer holding a
// licence the app throws away. The invariant that rule protects is kept anyway,
// and by a stronger mechanism: one code burns once, and the burn records which
// machine won, so a second machine presenting the same code gets 409 rather than a
// second licence.
func (s *Service) sign(installID string, now time.Time) (token string, envelope []byte, err error) {
	env, err := s.signer.Build(license.IssueParams{
		Subject:      installID,
		Plan:         license.PlanBuyout,
		Entitlements: s.opts.Entitlements,
		Lifetime:     s.opts.Lifetime,
		IssuedAt:     now,
		ClockSkew:    s.opts.ClockSkew,
	})
	if err != nil {
		return "", nil, err
	}
	raw, err := env.JSON()
	if err != nil {
		return "", nil, err
	}
	return base64.RawURLEncoding.EncodeToString(raw), raw, nil
}

// reissue recovers the one gap in the design: the burn committed and the record
// did not. The licence id comes from the burn, so the machine gets back the same
// entitlement; the signature is fresh, and this is the only place in the package
// where that can happen.
func (s *Service) reissue(ctx context.Context, digest, installID string, code store.RedeemCode, now time.Time) (Result, error) {
	licenseID := code.LicenseID
	if licenseID == "" {
		return Result{}, httpx.Internal(errors.New("redeem: burnt code carries no licence id"))
	}
	token, envelope, err := s.sign(installID, now)
	if err != nil {
		return Result{}, httpx.Internal(err)
	}
	if err := s.record(ctx, digest, installID, licenseID, "", envelope, now); err != nil {
		return Result{}, httpx.Internal(err)
	}
	if err := s.bind(ctx, licenseID, installID, now); err != nil {
		return Result{}, err
	}
	redeemedAt := now
	if code.RedeemedAt != nil {
		redeemedAt = *code.RedeemedAt
	}
	return Result{
		Status:       StatusAlreadyRedeemed,
		LicenseToken: token,
		LicenseID:    licenseID,
		RedeemedAt:   formatTime(redeemedAt),
		ExpiresAt:    expiryOf(s.opts.Lifetime, now),
		HTTPStatus:   http.StatusOK,
	}, nil
}

// replay turns a stored record back into the answer its first attempt gave,
// byte for byte. Nothing is re-signed here.
func (s *Service) replay(rec store.RedemptionRequest) (Result, error) {
	if len(rec.EnvelopeJSON) == 0 {
		return Result{}, httpx.Internal(errors.New("redeem: idempotency record carries no envelope"))
	}
	env, err := license.Parse(rec.EnvelopeJSON)
	if err != nil {
		return Result{}, httpx.Internal(err)
	}
	var expires *string
	if env.ExpiresAt != nil {
		v := formatTime(time.Unix(*env.ExpiresAt, 0))
		expires = &v
	}
	redeemedAt := rec.CreatedAt
	if redeemedAt.IsZero() {
		redeemedAt = s.opts.now()
	}
	return Result{
		Status:       StatusAlreadyRedeemed,
		LicenseToken: base64.RawURLEncoding.EncodeToString(rec.EnvelopeJSON),
		LicenseID:    rec.LicenseID,
		RedeemedAt:   formatTime(redeemedAt),
		ExpiresAt:    expires,
		HTTPStatus:   http.StatusOK,
	}, nil
}

// record writes the envelope under both keys: the client's request id, and the
// derived one that lets a later attempt from the same machine find it even when
// the client's own pending record was lost.
func (s *Service) record(ctx context.Context, digest, installID, licenseID, requestID string, envelope []byte, now time.Time) error {
	if requestID != "" {
		if err := s.store.SaveRedemptionRequest(ctx, store.RedemptionRequest{
			RequestID:    requestID,
			CodeHash:     digest,
			LicenseID:    licenseID,
			EnvelopeJSON: envelope,
			CreatedAt:    now,
		}); err != nil {
			return err
		}
	}
	return s.store.SaveRedemptionRequest(ctx, store.RedemptionRequest{
		RequestID:    autoKey(digest, installID),
		CodeHash:     digest,
		LicenseID:    licenseID,
		EnvelopeJSON: envelope,
		CreatedAt:    now,
	})
}

// bind pins the machine. The buy-out edition has no order, so the licence id is
// used as the binding's order id: it is unique per code, and it is what support
// needs to answer "which machines hold this licence" (§10.11).
func (s *Service) bind(ctx context.Context, licenseID, installID string, now time.Time) error {
	err := s.store.BindDevice(ctx, licenseID, installID, store.PlanBuyout, s.opts.MaxDevices, now)
	if err == nil {
		return nil
	}
	if !errors.Is(err, store.ErrConflict) {
		return httpx.Internal(err)
	}
	active, cerr := s.activeDevices(ctx, licenseID)
	if cerr != nil {
		return httpx.Internal(cerr)
	}
	return s.installMismatch(active)
}

func (s *Service) activeDevices(ctx context.Context, orderID string) (int, error) {
	rows, err := s.store.ListDevices(ctx, orderID)
	if err != nil {
		if errors.Is(err, store.ErrNotFound) {
			return 0, nil
		}
		return 0, err
	}
	live := 0
	for _, r := range rows {
		if r.ReleasedAt == nil {
			live++
		}
	}
	return live, nil
}

// installMismatch puts the counts in the message so the client can tell the user
// how many machines they already have, not only that they have too many.
func (s *Service) installMismatch(active int) error {
	return &httpx.APIError{
		Status: http.StatusConflict, Code: httpx.CodeInstallMismatch,
		Message: fmt.Sprintf("该兑换码已绑定 %d 台设备，最多可绑定 %d 台。", active, s.opts.MaxDevices),
	}
}

// autoKey is the deterministic idempotency key for "this machine spent this code".
// It is deliberately longer and differently shaped from a client request id (22
// characters of base64url), so the two can never collide in one table.
func autoKey(digest, installID string) string {
	sum := sha256.Sum256([]byte(autoKeyDomain + "\n" + digest + "\n" + installID))
	return "auto-" + base64.RawURLEncoding.EncodeToString(sum[:])
}

func newLicenseID() (string, error) {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		return "", fmt.Errorf("redeem: random source failed: %w", err)
	}
	return "lic_" + base64.RawURLEncoding.EncodeToString(b[:]), nil
}

// expiryOf renders the licence's expiry, or nil for the perpetual plan.
func expiryOf(lifetime time.Duration, now time.Time) *string {
	if lifetime <= 0 {
		return nil
	}
	v := formatTime(now.Add(lifetime))
	return &v
}

// formatTime is the wire format the client's DateTimeOffset parser expects:
// yyyy-MM-ddTHH:mm:ssZ, in UTC, at second resolution.
func formatTime(t time.Time) string { return t.UTC().Format("2006-01-02T15:04:05Z") }
