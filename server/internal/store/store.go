// Package store defines the persistence contract for the WeChatBridge API.
//
// The interfaces here are the seam every other package is written against, so
// they are deliberately small and split by domain rather than one large
// interface with thirty methods. A caller takes only the one it needs, and
// the in-memory implementation used by tests satisfies each piece
// independently.
//
// Three rules shape this package:
//
//   - Data that leaves the database is written down. Types are concrete; there
//     is no generic "any" row and no map[string]any.
//
//   - Anything that must not happen twice carries a condition and exposes the
//     conflict rather than resolving it silently. Confirming one activation
//     request twice must mint one licence, not two, and the caller is the only
//     party that knows which of the two it is looking at.
//
//   - The buy-out convergence deleted the payment half of this package. There
//     are no orders, no redeem codes and no quotas any more: a licence is
//     minted by a seller pressing one button after checking WeChat's own
//     payment record. What remains is sign-in, one activation request per
//     purchase, and the binding that says which machine a licence belongs to.
package store

import (
	"context"
	"errors"
	"time"
)

// Sentinel errors. Callers match with errors.Is; the HTTP layer maps them onto
// status codes in one place.
var (
	// ErrNotFound is a miss, not a failure. It is a normal answer.
	ErrNotFound = errors.New("store: not found")

	// ErrConflict means the row exists but is not in the state the caller
	// needed — a request already confirmed, a machine already licensed, a
	// chain already revoked. It is separate from ErrNotFound because
	// "somebody else got there first" and "that was never there" need
	// different words in the reply.
	ErrConflict = errors.New("store: conflict")

	// ErrReuseDetected is its own error, not a flavour of ErrConflict, because
	// it means the chain is being revoked: a consumed refresh token presented
	// twice is theft or a bug, and both want the whole chain gone.
	ErrReuseDetected = errors.New("store: token reuse detected")
)

// ID is the database's identifier type. Kept as a distinct type so a function
// cannot take an account id where a request id belongs.
type ID = string

// ---------------------------------------------------------------------------
// Accounts and sessions — the sign-in every buyer passes through.
// ---------------------------------------------------------------------------

// Account is a person who signed in with WeChat. OpenID is not the primary
// key and not portable between WeChat surfaces: the same person has a
// different OpenID under the website app, under a mini programme and under an
// official account. UnionID ties them together, and only exists because both
// are registered against the same open platform account.
type Account struct {
	ID          ID
	OpenID      string
	UnionID     string
	Nickname    string
	AvatarURL   string
	Plan        Plan
	CreatedAt   time.Time
	LastLoginAt time.Time
	WechatBound bool
	// InstallID is the anonymous identity the client had before it ever signed
	// in. Keeping it is what lets a purchase made before signing in find the
	// account it belongs to.
	InstallID string
}

// Plan is what an account is entitled to.
type Plan string

const (
	PlanFree         Plan = "free"
	PlanBuyout       Plan = "buyout"
	PlanSubscription Plan = "subscription"
)

// AuthSession is one QR sign-in in flight. Ticket is the client's handle; it
// is short-lived and single-use, and it is not a credential.
type AuthSession struct {
	Ticket       string
	State        AuthSessionState
	InstallID    string
	AuthorizeURL string
	ExpiresAt    time.Time
	AccountID    ID
	CreatedAt    time.Time
}

// AuthSessionState mirrors the client's enum exactly. Unknown is not a
// fifth state here — it is a parse failure, and the store treats any string it
// does not recognise as expired rather than guessing.
type AuthSessionState string

const (
	AuthPending    AuthSessionState = "pending"
	AuthAuthorized AuthSessionState = "authorized"
	AuthExpired    AuthSessionState = "expired"
	AuthRejected   AuthSessionState = "rejected"
)

// Session holds the credentials for one signed-in account.
//
// The convergence moved the token chain off its own tables and onto the
// accounts row: one live session per account. That is a deliberate trade, not
// an oversight — the product has one buyer per licence and a second machine
// signing in is the machine-change case the seller handles with a reissue,
// not a second concurrent session. What survives unchanged is everything the
// client can observe: rotation keeps the chain, a replayed refresh token
// still kills the family, and a revoked chain stays dead.
type Session struct {
	ID               ID
	AccountID        ID
	DeviceID         string
	AccessTokenHash  string
	AccessExpiresAt  time.Time
	RefreshTokenHash string
	RefreshExpiresAt time.Time
	// ChainID groups every token descended from one sign-in. Revoking a chain
	// revokes the whole family, which is what makes reuse detection able to
	// punish an attacker without logging the legitimate user out forever.
	ChainID   string
	RevokedAt *time.Time
	CreatedAt time.Time
}

// RefreshOutcome is what a refresh attempt did. The client needs all three:
// Rotated is the happy path, Expired means re-authenticate, Transient means
// try again later and — importantly — do not clear the local session. Treating
// a network blip as a dead session kicks paying customers out for nothing.
type RefreshOutcome string

const (
	RefreshRotated   RefreshOutcome = "rotated"
	RefreshExpired   RefreshOutcome = "expired"
	RefreshTransient RefreshOutcome = "transient"
)

// AuthStore is sign-in and the token chain.
type AuthStore interface {
	CreateAuthSession(ctx context.Context, s AuthSession) error
	GetAuthSession(ctx context.Context, ticket string) (AuthSession, error)
	// CompleteAuthSession atomically marks the ticket authorized and resolves
	// or creates the account for the OpenID.
	CompleteAuthSession(ctx context.Context, ticket, openID, unionID, nickname, avatar, installID string) (Account, error)

	// UpsertAccountByOpenID returns the account and whether it was created, so
	// the caller can tell a first sign-in from a return visit.
	UpsertAccountByOpenID(ctx context.Context, openID, unionID, nickname, avatar string) (Account, bool, error)
	GetAccount(ctx context.Context, id ID) (Account, error)

	// CreateSession installs the account's one live session. A second call
	// for the same account replaces the first: that is the machine-change path.
	CreateSession(ctx context.Context, s Session) error
	// GetSessionByRefreshHash resolves the presented hash against the live
	// session and against the one generation before it, so a replay reaches
	// the caller's revocation path instead of looking like an unknown token.
	GetSessionByRefreshHash(ctx context.Context, hash string) (Session, error)
	// RotateRefresh atomically consumes the presented token and installs its
	// replacement. Presenting a token that was already consumed returns
	// ErrReuseDetected, and the caller revokes the chain.
	RotateRefresh(ctx context.Context, oldHash, newHash string, newAccessHash string, accessExpiresAt time.Time) (Session, RefreshOutcome, error)
	RevokeChain(ctx context.Context, chainID string, at time.Time) error
}

// ---------------------------------------------------------------------------
// Activation — the buy-out product's only online path.
// ---------------------------------------------------------------------------

// ActivationRequest is one purchase registration: the buyer's machine code,
// the amount they were asked for, and the seller's answer.
//
// It replaces the old orders + redemption_requests + redeem_codes triple.
// There is no idempotency key and no replay window because there is no
// callback to replay: the only writer after the buyer is the seller, and the
// seller is a person looking at a list.
type ActivationRequest struct {
	RequestID    string
	AccountID    ID
	InstallID    string
	AnchorDigest string
	// AmountFen is what the buyer says they were asked to pay. It is
	// advisory — the seller checks WeChat's own ledger — so it is optional on
	// the wire and nullable here.
	AmountFen *int64
	Status    ActivationStatus
	// EnvelopeJSON is the exact signed licence bytes, written once when the
	// seller confirms. ECDSA picks a random k per signature, so re-signing
	// would hand the same licence a different token on every poll.
	EnvelopeJSON []byte
	LicenseID    string
	CreatedAt    time.Time
	ConfirmedAt  *time.Time
}

// ActivationStatus is the state machine the buyer's poller watches. Rejected
// is the seller saying no; the row stays for the ledger either way.
type ActivationStatus string

const (
	ActivationPending   ActivationStatus = "pending"
	ActivationConfirmed ActivationStatus = "confirmed"
	ActivationRejected  ActivationStatus = "rejected"
)

// ActivationRequestView is the seller-facing projection: the request plus the
// account fields the console shows beside it. It exists so the list is one
// query rather than one query per row.
type ActivationRequestView struct {
	ActivationRequest
	Nickname  string
	AvatarURL string
}

// LicenceBinding is one machine a licence is pinned to. license_id is the
// primary key because that is what the seller quotes in a support
// conversation and what a revoke names.
type LicenceBinding struct {
	LicenseID string
	InstallID string
	AccountID ID
	// RequestID links back to the activation request that produced this
	// licence, which is how a re-delivery finds the original envelope bytes.
	RequestID  string
	IssuedAt   time.Time
	RevokedAt  *time.Time
}

// LicenceBindingView is the ledger projection: the binding plus who it belongs
// to, for the seller's after-the-fact reconciliation.
type LicenceBindingView struct {
	LicenceBinding
	Nickname string
	OpenID   string
}

// ActivationStore is the buy-out product's persistence: registration, the
// seller's three answers, and the bindings that record them.
type ActivationStore interface {
	// Buyer side.
	CreateActivationRequest(ctx context.Context, r ActivationRequest) error
	// LatestActivationRequest is the buyer's own view: the most recent request
	// they made, which is the one their poller is waiting on.
	LatestActivationRequest(ctx context.Context, accountID ID) (ActivationRequest, error)
	GetActivationRequest(ctx context.Context, requestID string) (ActivationRequest, error)

	// Seller side.
	// ListActivationRequests returns the console's table. An empty status
	// means every state, which is what the ledger page asks for.
	ListActivationRequests(ctx context.Context, status ActivationStatus) ([]ActivationRequestView, error)
	// ConfirmActivationRequest flips a pending request to confirmed and
	// installs its binding in one transaction. The flip is conditional on the
	// request still being pending, so two sellers pressing the same button
	// produce one licence; the loser reads ErrConflict and re-reads the row.
	ConfirmActivationRequest(ctx context.Context, requestID, licenseID string, envelopeJSON []byte, at time.Time) (ActivationRequest, error)

	// Licence bindings.
	// ReissueLicence revokes the account's live binding — if any — and
	// installs a new one for installID in the same transaction. If a pending
	// request exists for that install id it is confirmed with the new
	// licence, which is how the buyer's poller learns about a reissue at all.
	// ErrNotFound means there is nothing to reissue: no live binding and no
	// pending request for that machine.
	ReissueLicence(ctx context.Context, installID, licenseID string, envelopeJSON []byte, at time.Time) (ActivationRequest, error)
	// RevokeBinding marks a licence revoked. The row stays: the honest answer
	// to "is this licence still valid" is a date, not a deletion.
	RevokeBinding(ctx context.Context, licenseID string, at time.Time) error
	// LiveBindingByInstallID finds the licence a machine currently holds.
	LiveBindingByInstallID(ctx context.Context, installID string) (LicenceBinding, error)
	ListBindings(ctx context.Context) ([]LicenceBindingView, error)
}

// Store is the union, used by the HTTP layer and by main. Depending on the
// whole set from one place is fine; depending on it from a domain package is
// not, which is why the domain interfaces above are separate.
type Store interface {
	AuthStore
	ActivationStore

	// Close releases pooled connections. Safe to call twice.
	Close() error
}
