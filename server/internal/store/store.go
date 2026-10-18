// Package store defines the persistence contract for the WeChatBridge API.
//
// The interfaces here are the seam every other package is written against, so
// they are deliberately small and split by domain rather than one large
// interface with thirty methods. A caller takes only the one it needs, and the
// in-memory implementation used by tests satisfies each piece independently.
//
// Two rules shape this package:
//
//   - Data that leaves the database is written down. Types are concrete; there
//     is no generic "any" row and no map[string]any.
//
//   - Anything that must not happen twice carries an idempotency key and
//     exposes the conflict rather than resolving it silently. Burning a
//     redemption code twice, or paying an order out twice, are money bugs, and
//     the caller is the only party that knows which of the two it is looking
//     at.
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
	// needed — a code already burnt, an order already closed, a token already
	// consumed. It is separate from ErrNotFound because "somebody else got
	// there first" and "that was never there" need different words in the
	// reply.
	ErrConflict = errors.New("store: conflict")

	// ErrReuseDetected is its own error, not a flavour of ErrConflict, because
	// it means the chain is being revoked: a consumed refresh token presented
	// twice is theft or a bug, and both want the whole chain gone.
	ErrReuseDetected = errors.New("store: token reuse detected")
)

// ID is the database's identifier type. Kept as a distinct type so a function
// cannot take an account id where an order id belongs.
type ID = string

// ---------------------------------------------------------------------------
// Accounts and sessions — the subscription product.
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
	// in. Keeping it is what lets a purchase migrate the history that piled up
	// before the person had an account — see the design document, §5.
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

// Session holds the credentials for one signed-in device.
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

// Quota is one metering window. LLM calls cost money, so the limit is a hard
// server-side number, not a client-side flag that a script can turn off.
type Quota struct {
	AccountID ID
	Used      int64
	Limit     int64
	ResetAt   time.Time
}

// AuthStore is the subscription product's persistence.
type AuthStore interface {
	CreateAuthSession(ctx context.Context, s AuthSession) error
	GetAuthSession(ctx context.Context, ticket string) (AuthSession, error)
	// CompleteAuthSession atomically marks the ticket authorized and resolves or
	// creates the account for the OpenID. It must also migrate the anonymous
	// history: a person who paid before signing in must not find their data
	// missing afterwards.
	CompleteAuthSession(ctx context.Context, ticket, openID, unionID, nickname, avatar, installID string) (Account, error)

	// UpsertAccountByOpenID returns the account and whether it was created, so
	// the caller can tell a first sign-in from a return visit.
	UpsertAccountByOpenID(ctx context.Context, openID, unionID, nickname, avatar string) (Account, bool, error)
	GetAccount(ctx context.Context, id ID) (Account, error)

	CreateSession(ctx context.Context, s Session) error
	GetSessionByRefreshHash(ctx context.Context, hash string) (Session, error)
	// RotateRefresh atomically consumes the old refresh token and installs its
	// replacement. Presenting a token that was already consumed returns
	// ErrReuseDetected, and the caller revokes the chain.
	RotateRefresh(ctx context.Context, oldHash, newHash string, newAccessHash string, accessExpiresAt time.Time) (Session, RefreshOutcome, error)
	RevokeChain(ctx context.Context, chainID string, at time.Time) error

	GetQuota(ctx context.Context, accountID ID) (Quota, error)
	ConsumeQuota(ctx context.Context, accountID ID, units int64) (Quota, error)
}

// ---------------------------------------------------------------------------
// Redemption codes — the buy-out product's only online path.
// ---------------------------------------------------------------------------

// RedeemCode is a one-time credential burned for a licence.
//
// It travels through email, an order page and support conversations, all three
// of which get screenshotted and forwarded. That is why it is burned rather
// than exchanged: a code that merely unlocked a licence would leak into the
// wild and mint entitlement forever.
type RedeemCode struct {
	Hash              string
	ProductID         string
	Plan              Plan
	Lifetime          time.Duration // zero means perpetual
	IssuedAt          time.Time
	ExpiresAt         time.Time
	RedeemedAt        *time.Time
	RedeemedInstallID string
	LicenseID         string
	RevokedAt         *time.Time
}

// IsBurnable reports whether this code can still be spent right now. Expiry is
// the caller's to apply with its own clock; this only reports the stored state.
func (c RedeemCode) IsBurnable() bool {
	return c.RedeemedAt == nil && c.RevokedAt == nil
}

// RedemptionRequest records one user gesture against one code. It is what lets
// a retry after an uncertain outcome become a question rather than a second
// attempt — see the design document, §3.2.
//
// The envelope is stored as the exact bytes that were returned the first time.
// Re-signing would produce a different signature (ECDSA picks a random k), and
// while the client would accept either, any future hashing or de-duplication
// would fork on the difference.
type RedemptionRequest struct {
	RequestID    string
	CodeHash     string
	LicenseID    string
	EnvelopeJSON []byte
	CreatedAt    time.Time
}

// LicenceBinding is one machine a licence is pinned to. A buy-out licence is
// device-bound; this table is what lets support free a slot when a machine
// dies, and what stops one person minting a licence per machine forever.
type LicenceBinding struct {
	ID         string
	OrderID    ID
	InstallID  string
	Plan       Plan
	BoundAt    time.Time
	LastSeenAt time.Time
	ReleasedAt *time.Time
}

// LicenceStore is redemption, burning and device slots.
type LicenceStore interface {
	CreateRedeemCode(ctx context.Context, c RedeemCode) error
	GetRedeemCode(ctx context.Context, hash string) (RedeemCode, error)

	// BurnRedeemCode marks the code spent. It is a conditional update on
	// redeemed_at IS NULL, so two concurrent attempts cannot both win, and it
	// must record the install id and licence id in the same transaction as the
	// flip. Returns ErrConflict when the code was already burnt — which is how
	// "somebody else got there first" stays distinguishable from "no such code".
	BurnRedeemCode(ctx context.Context, hash, installID, licenceID string, at time.Time) error

	ExtendRedeemCode(ctx context.Context, hash string, expiresAt time.Time) error
	RevokeRedeemCode(ctx context.Context, hash string, at time.Time) error

	SaveRedemptionRequest(ctx context.Context, r RedemptionRequest) error
	GetRedemptionRequest(ctx context.Context, requestID string) (RedemptionRequest, error)

	// BindDevice records a machine against an order and enforces the slot
	// limit. It returns ErrConflict with the live count when the limit is hit,
	// so the caller can tell the user how many machines they already have.
	BindDevice(ctx context.Context, orderID ID, installID string, plan Plan, maxSlots int, at time.Time) error
	ListDevices(ctx context.Context, orderID ID) ([]LicenceBinding, error)
	ReleaseDevice(ctx context.Context, orderID ID, installID string, at time.Time) error
}

// ---------------------------------------------------------------------------
// Orders and payment — the subscription product's billing.
// ---------------------------------------------------------------------------

// Order is one purchase attempt. OutTradeNo is ours; TransactionID is
// WeChat's. Both matter: the first is what we sent, the second is what they
// said they paid, and a callback carrying a transaction id we never asked for
// is a forgery attempt rather than a bookkeeping problem.
type Order struct {
	ID            ID
	OutTradeNo    string
	ProductID     string
	Plan          Plan
	AmountFen     int64
	Currency      string
	Status        OrderStatus
	AccountID     ID
	CodeURL       string
	CreatedAt     time.Time
	ExpiresAt     time.Time
	PaidAt        *time.Time
	TransactionID string
	LicenseID     string
	LicenseKey    string
}

// OrderStatus mirrors the client's enum exactly.
type OrderStatus string

const (
	OrderPending  OrderStatus = "pending"
	OrderPaid     OrderStatus = "paid"
	OrderClosed   OrderStatus = "closed"
	OrderRefunded OrderStatus = "refunded"
)

// IsTerminal reports whether no further transition is possible.
func (o Order) IsTerminal() bool {
	return o.Status == OrderPaid || o.Status == OrderClosed || o.Status == OrderRefunded
}

// BillingStore is orders, payment callbacks and licence issuance against an
// order.
type BillingStore interface {
	CreateOrder(ctx context.Context, o Order) error
	GetOrder(ctx context.Context, id ID) (Order, error)
	GetOrderByOutTradeNo(ctx context.Context, outTradeNo string) (Order, error)
	// FindLiveOrderByAccount lets the second device be told about the first
	// device's pending order instead of being handed a confusing conflict.
	FindLiveOrderByAccount(ctx context.Context, accountID ID) (Order, error)

	// MarkPaid is conditional on the order still being pending. WeChat retries
	// a callback until it gets a success, so this runs more than once; making it
	// idempotent here is the difference between one licence and several.
	MarkPaid(ctx context.Context, id ID, transactionID string, paidAt time.Time) (Order, error)

	CloseOrder(ctx context.Context, id ID, at time.Time) error
	RefundOrder(ctx context.Context, id ID, transactionID string, at time.Time) error

	// IssueOrderLicense stores the envelope and returns the identical bytes on
	// any later call for the same order. Never re-signs.
	IssueOrderLicense(ctx context.Context, orderID ID, licenseID, licenseKey string, envelopeJSON []byte) ([]byte, error)
}

// ---------------------------------------------------------------------------
// QR payloads — short-lived, cacheable, never authoritative.
// ---------------------------------------------------------------------------

// QrPayload is a rendered-or-renderable QR body with its own lifetime. It is
// not a credential: losing it costs a re-scan, nothing more.
type QrPayload struct {
	Kind string // "login" or "pay"
	Ref  string
	Body string
	At   time.Time
}

// QrCache stores QR payloads for the window between creating one and the user
// acting on it. It is a cache, so an eviction is a normal event and callers
// must handle ErrNotFound by issuing a fresh code rather than as a failure.
type QrCache interface {
	Put(ctx context.Context, p QrPayload) error
	Get(ctx context.Context, kind, ref string) (QrPayload, error)
	Delete(ctx context.Context, kind, ref string) error
}

// Store is the union, used by the HTTP layer and by main. Depending on the
// whole set from one place is fine; depending on it from a domain package is
// not, which is why the domain interfaces above are separate.
type Store interface {
	AuthStore
	LicenceStore
	BillingStore
	QrCache

	// Close releases pooled connections. Safe to call twice.
	Close() error
}
