// Package activation is the buy-out product's customer-facing half: a signed-in
// machine registers itself, polls until the seller confirms the money, and
// collects the signed licence. Three endpoints, no payment gateway.
//
//	POST /v1/activation/request   {installId, anchorDigest, amountFen?} -> {requestId, status}
//	GET  /v1/activation/status                            -> {status, licenseId?}
//	GET  /v1/activation/licence                           -> {token, envelope}
//
// The wire shapes are the frozen contract of the design document §11.3, the
// runnable form of which is server/mock/server.js — the mock this package was
// written against, field for field, because the Windows client was written
// against it in parallel. Every field name below is pinned with a tag rather
// than a naming policy for the same reason the envelope's are: the other side
// of this contract is another language, and a policy change on either side
// would silently break every purchase in flight.
//
// Two rules shape the handlers:
//
//   - The account comes from the bearer token, never from the body. A request
//     body that named its own account would let any signed-in customer file
//     a purchase under somebody else's name.
//   - "Confirmed" is the only state that yields a licence, and the read is
//     keyed by the caller's account, not by a request id in the URL. A URL
//     that carried the id would let any signed-in customer read any other
//     customer's licence by guessing it.
//
// This package was ported onto the 4-table store (activation requests and
// licence bindings are separate rows here, not one), and one behaviour
// changed shape as a result: a licence is handed out only while the
// machine's live binding is the licence the confirmed request names. That is
// where revocation lives in this schema — a date on the binding, not a state
// on the request — so the licence endpoint reads the binding to answer "is
// this ticket still good" rather than asking the request row.
package activation

import (
	"context"
	"crypto/rand"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"fmt"
	"log/slog"
	"time"

	"github.com/gin-gonic/gin"

	"github.com/freestylefly/WeChatBridge/server/internal/license"
	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// Sentinel errors the handlers map onto specific replies. Everything else that
// leaves the service is a store error and goes through httpx.FromStore.
var (
	// ErrMachineTaken is a live request for this machine under a different
	// account. The store hands the other account's row back rather than
	// adopting or duplicating it, and this is the handler's cue to say so
	// plainly instead of returning a request id the caller cannot poll.
	ErrMachineTaken = errors.New("activation: machine has a live request under another account")

	// ErrNotConfirmed is a read of a licence the seller has not signed yet.
	// It is an answer, not a failure: the customer is early, not wrong.
	ErrNotConfirmed = errors.New("activation: request is not confirmed")

	// ErrLicenceDead is a confirmed request whose licence is no longer the
	// machine's live one — revoked by the seller, or replaced by a reissue.
	// The old envelope stays valid wherever it already landed (offline
	// verification has no revocation channel), but this endpoint is not
	// where a dead licence gets re-handed-out.
	ErrLicenceDead = errors.New("activation: licence is no longer live")

	// ErrEnvelopeMissing is a confirmed row without its bytes. The database
	// forbids that combination (activation_requests_confirmed_is_complete),
	// so reaching it means corruption, and corruption is a 500 with a log
	// line, never a 409 the customer would read as "still signing".
	ErrEnvelopeMissing = errors.New("activation: confirmed request has no envelope")
)

// Bounds on the machine-code fields. The client's install id is 16 random
// bytes, base64url — 22 characters — and the anchor digest is a SHA-256, so
// these caps are an order of magnitude above the real thing and exist only to
// keep a hostile body from becoming a database row nobody asked for.
const (
	maxInstallIDLen    = 128
	maxAnchorDigestLen = 128
)

// Service is the three endpoints' logic. It is safe for concurrent use.
type Service struct {
	store store.ActivationStore
	log   *slog.Logger
	now   func() time.Time

	// accountOf reads the account a verified bearer token belongs to. It is
	// auth's to define and this package's to call: production passes
	// auth.AccountIDFrom, which fails closed when the middleware that fills
	// the claims never ran.
	accountOf func(*gin.Context) (string, bool)
}

// Options are the wiring knobs. The store and the account reader are required:
// this package signs nothing (the seller's confirm endpoint does that, on the
// admin side) and issues no tokens, but every one of its three endpoints is
// scoped to a signed-in account.
type Options struct {
	Store  store.ActivationStore
	Logger *slog.Logger
	// Now defaults to the wall clock. Tests set it so a request can be aged
	// without sleeping.
	Now func() time.Time
	// AccountOf reads the authenticated account off a request. Production
	// passes auth.AccountIDFrom; a test passes whatever it needs the claims
	// to say.
	AccountOf func(*gin.Context) (string, bool)
}

// NewService wires the flow together.
func NewService(opts Options) (*Service, error) {
	if opts.Store == nil {
		return nil, errors.New("activation: store is required")
	}
	if opts.AccountOf == nil {
		return nil, errors.New("activation: account reader is required")
	}
	if opts.Logger == nil {
		opts.Logger = slog.Default()
	}
	if opts.Now == nil {
		opts.Now = func() time.Time { return time.Now().UTC() }
	}
	return &Service{
		store:     opts.Store,
		log:       opts.Logger,
		now:       opts.Now,
		accountOf: opts.AccountOf,
	}, nil
}

// RegisterRoutes mounts the three endpoints.
//
// The bearer middleware is passed in rather than constructed here, for the
// same reason auth.RegisterRoutes takes its router as a parameter: the
// composition root owns what is mounted and in what order. Production wires
// authService.RequireAuth(); the account reader behind it arrived with the
// Options.
func (s *Service) RegisterRoutes(r gin.IRouter, requireAuth gin.HandlerFunc) {
	group := r.Group("/v1/activation", requireAuth)
	group.POST("/request", s.handleRegister)
	group.GET("/status", s.handleStatus)
	group.GET("/licence", s.handleLicence)
}

// Register records a machine's intent to buy. It is idempotent per install id
// for the same account — tapping "buy" twice refreshes the row and returns the
// same request id — and refuses, with ErrMachineTaken, when the machine's live
// request belongs to somebody else.
func (s *Service) Register(ctx context.Context, accountID, installID, anchorDigest string, amountFen *int64) (store.ActivationRequest, error) {
	requestID, err := newRequestID()
	if err != nil {
		return store.ActivationRequest{}, fmt.Errorf("activation: mint request id: %w", err)
	}

	created, err := s.store.CreateActivationRequest(ctx, store.ActivationRequest{
		RequestID:    requestID,
		InstallID:    installID,
		AccountID:    accountID,
		AnchorDigest: anchorDigest,
		AmountFen:    amountFen,
		CreatedAt:    s.now(),
	})
	if err != nil {
		return store.ActivationRequest{}, err
	}
	if created.AccountID != accountID {
		// The upsert's account predicate kept the row with its original
		// owner. Returning it as if it were ours would be a lie the seller's
		// console would eventually contradict.
		return store.ActivationRequest{}, ErrMachineTaken
	}
	return created, nil
}

// LicenceResult is what the client collects: the envelope as an object, and
// the same bytes as one opaque token for anything that would rather ship a
// string than a structure.
type LicenceResult struct {
	Token    string
	Envelope license.Envelope
}

// Licence reads the envelope the seller's confirm endpoint signed and stored.
//
// The bytes come back exactly as they were written: the signature is taken
// over a canonical form of the fields, not over the JSON text, so a
// re-serialisation would not break verification — but the store's promise is
// "the bytes we returned the first time", and honouring it here costs one
// base64 encode.
func (s *Service) Licence(ctx context.Context, accountID string) (LicenceResult, error) {
	r, err := s.store.LatestActivationRequest(ctx, accountID)
	if err != nil {
		return LicenceResult{}, err
	}
	if r.Status != store.ActivationConfirmed || r.LicenseID == "" {
		return LicenceResult{}, ErrNotConfirmed
	}
	if len(r.EnvelopeJSON) == 0 {
		return LicenceResult{}, ErrEnvelopeMissing
	}
	// Confirmed with bytes: the last question is whether the licence is
	// still the machine's live one. Revocation in this schema is a date on
	// the binding, so the binding answers it — a revoked licence, or one a
	// reissue has replaced, is not re-handed-out here.
	live, err := s.store.LiveBindingByInstallID(ctx, r.InstallID)
	switch {
	case errors.Is(err, store.ErrNotFound):
		return LicenceResult{}, ErrLicenceDead
	case err != nil:
		return LicenceResult{}, err
	case live.LicenseID != r.LicenseID:
		return LicenceResult{}, ErrLicenceDead
	}
	envelope, err := license.Parse(r.EnvelopeJSON)
	if err != nil {
		return LicenceResult{}, fmt.Errorf("activation: stored envelope does not parse: %w", err)
	}
	return LicenceResult{
		Token:    base64.RawURLEncoding.EncodeToString(r.EnvelopeJSON),
		Envelope: envelope,
	}, nil
}

// newRequestID mints the 单号 the customer quotes and the seller's console
// shows. The prefix is what the mock used and what the client's logs say, so
// a support conversation reads the same on either side of the cutover.
func newRequestID() (string, error) {
	var buf [12]byte
	if _, err := rand.Read(buf[:]); err != nil {
		return "", err
	}
	return "act_" + hex.EncodeToString(buf[:]), nil
}
