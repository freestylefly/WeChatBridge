// Package auth implements the subscription product's QR sign-in: the WeChat
// redirect, the polling the desktop client does in parallel with it, the token
// rotation that follows, and the two authenticated reads behind a bearer token.
//
// The shape of the flow is forced by one fact. WeChat's redirect domain is a
// filed public HTTPS domain — not 127.0.0.1, not a port, not an IP — so the
// redirect lands here and the desktop process never sees a code. There is no
// arrangement of the pieces in which the client can receive it. Everything here
// exists to make polling work:
//
//	POST   /v1/auth/session           -> ticket + authorizeUrl + qrImageUrl + expiresAt
//	GET    /v1/auth/session/{ticket}  -> {status, tokens?, expiresAt?, account?}
//	GET    /v1/auth/session/{t}/qr    -> image/png
//	GET    /v1/auth/wechat/callback   -> 200, empty body, no redirect
//	POST   /v1/auth/refresh           -> rotated pair, or a reason not to
//	DELETE /v1/auth/session           -> revoke the whole chain
//
// Three mistakes this package is written to make impossible:
//
//   - Redirecting from the callback. It is the reflexive thing to do and it
//     breaks everything: the redirect target is a browser page the desktop
//     process has never heard of. The poller is the channel.
//
//   - Letting a network blip read as a dead session. A refresh that fails
//     because WeChat had a bad minute must leave the client's stored refresh
//     token alone. Treating that as an expiry logs paying customers out.
//
//   - Issuing a refresh token the server cannot revoke. They are opaque random
//     strings checked against the store for exactly this reason; see
//     internal/token.
package auth

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"strings"
	"sync"
	"time"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
	"github.com/freestylefly/WeChatBridge/server/internal/token"
	"github.com/freestylefly/WeChatBridge/server/internal/wechat"
)

// Config is the slice of the service configuration this package needs.
type Config struct {
	// PublicBaseURL is the service's own public origin, no trailing slash.
	// The redirect URI is derived from it because the authorized domain is
	// configured on the open platform and cannot carry a port.
	PublicBaseURL string
	// AuthSessionTTL bounds a ticket. It must not outlast the QR image's own
	// validity: the client judges expiry from expiresAt, and two definitions
	// of "how long the code is good for" will drift, and the wrong one tells a
	// user with a working code that it has expired.
	AuthSessionTTL time.Duration
	// MaxOutstandingTickets caps unconsumed tickets per install id, so that
	// hammering "refresh the QR" does not pile up rows.
	MaxOutstandingTickets int
}

// Service is the flow. It is safe for concurrent use; the client holds one for
// the life of the process.
type Service struct {
	cfg     Config
	store   store.AuthStore
	wechat  *wechat.Client
	issuer  *token.Issuer
	handoff Handoff
	log     *slog.Logger
	now     func() time.Time

	// outstanding tracks live tickets per install id. It is an in-process
	// approximation, not an authoritative count — the store has no listing
	// method, and the cost of getting it wrong is a few extra rows that the
	// TTL collects. Across several instances the real limit is higher than
	// this one.
	outstandingMu sync.Mutex
	outstanding   map[string][]time.Time
}

// Options are the wiring knobs that are not configuration.
type Options struct {
	Store   store.AuthStore
	WeChat  *wechat.Client
	Issuer  *token.Issuer
	Handoff Handoff
	Logger  *slog.Logger
	// Now defaults to the wall clock. Tests set it so a ticket can be aged
	// out without sleeping.
	Now func() time.Time
}

// NewService wires the flow together.
func NewService(cfg Config, opts Options) (*Service, error) {
	if opts.Store == nil {
		return nil, errors.New("auth: store is required")
	}
	if opts.Issuer == nil {
		return nil, errors.New("auth: token issuer is required")
	}
	if opts.WeChat == nil {
		return nil, errors.New("auth: wechat client is required")
	}
	if opts.Handoff == nil {
		opts.Handoff = NewMemoryHandoff(opts.Now)
	}
	if opts.Logger == nil {
		opts.Logger = slog.Default()
	}
	if opts.Now == nil {
		opts.Now = func() time.Time { return time.Now().UTC() }
	}
	if cfg.MaxOutstandingTickets <= 0 {
		cfg.MaxOutstandingTickets = 3
	}
	if cfg.AuthSessionTTL <= 0 {
		cfg.AuthSessionTTL = 5 * time.Minute
	}
	cfg.PublicBaseURL = strings.TrimRight(cfg.PublicBaseURL, "/")
	if cfg.PublicBaseURL == "" {
		return nil, errors.New("auth: public base URL is required")
	}

	return &Service{
		cfg:         cfg,
		store:       opts.Store,
		wechat:      opts.WeChat,
		issuer:      opts.Issuer,
		handoff:     opts.Handoff,
		log:         opts.Logger,
		now:         opts.Now,
		outstanding: make(map[string][]time.Time),
	}, nil
}

// ---------------------------------------------------------------------------
// Paths
// ---------------------------------------------------------------------------

// CallbackURL is the redirect URI sent to WeChat.
func (s *Service) CallbackURL() string { return s.cfg.PublicBaseURL + CallbackPath }

// QRImagePath is where the PNG for a ticket lives, relative to the base URL.
func QRImagePath(ticket string) string { return "/v1/auth/session/" + ticket + "/qr" }

// QRImageURL is the absolute URL handed to the client. It has to be absolute:
// the client turns it into a Uri and downloads it with no base to resolve
// against.
func (s *Service) QRImageURL(ticket string) string {
	return s.cfg.PublicBaseURL + QRImagePath(ticket)
}

// ---------------------------------------------------------------------------
// Ticket creation
// ---------------------------------------------------------------------------

// CreateSessionResult is what the client gets to start a scan.
type CreateSessionResult struct {
	Ticket       string
	AuthorizeURL string
	QRImageURL   string
	ExpiresAt    time.Time
}

// CreateSession opens a sign-in. The ticket is this process's handle on the
// scan; state is the same value, because it has to survive a round trip
// through the browser and come back proving which scan it belongs to.
//
// Nothing is signed and nothing here is a credential: knowing a ticket gets you
// a QR image and a status word, and the status word is worthless until the
// person in front of the phone is somebody.
func (s *Service) CreateSession(ctx context.Context, installID, deviceID string) (CreateSessionResult, error) {
	if err := s.reserveTicket(ctx, installID); err != nil {
		return CreateSessionResult{}, err
	}

	ticket, err := s.issuer.NewID()
	if err != nil {
		return CreateSessionResult{}, fmt.Errorf("auth: mint ticket: %w", err)
	}
	now := s.now()
	expiresAt := now.Add(s.cfg.AuthSessionTTL)

	authorizeURL := s.wechat.AuthorizeURL(ticket, s.CallbackURL())

	if err := s.store.CreateAuthSession(ctx, store.AuthSession{
		Ticket:       ticket,
		State:        store.AuthPending,
		InstallID:    installID,
		AuthorizeURL: authorizeURL,
		ExpiresAt:    expiresAt,
		CreatedAt:    now,
	}); err != nil {
		s.releaseTicket(installID, ticket)
		return CreateSessionResult{}, fmt.Errorf("auth: create session: %w", err)
	}

	s.log.Info("auth session opened",
		"ticket", ticket[:8],
		"install", token.Fingerprint(installID),
		"device", token.Fingerprint(deviceID),
		"expires_at", expiresAt.Format(time.RFC3339))

	return CreateSessionResult{
		Ticket:       ticket,
		AuthorizeURL: authorizeURL,
		QRImageURL:   s.QRImageURL(ticket),
		ExpiresAt:    expiresAt,
	}, nil
}

// reserveTicket caps concurrent unconsumed tickets per install. Repeatedly
// asking for a new QR is a thing people do when the first one is too blurry,
// and unlimited, it is also how a row-flooding script looks.
func (s *Service) reserveTicket(_ context.Context, installID string) error {
	if installID == "" {
		// An anonymous caller with no install id gets no per-install budget,
		// and no rate limit either; the global limits upstream are what
		// cover that case.
		return nil
	}
	now := s.now()
	cutoff := now.Add(-s.cfg.AuthSessionTTL)

	s.outstandingMu.Lock()
	defer s.outstandingMu.Unlock()
	s.pruneLocked(cutoff)

	if len(s.outstanding[installID]) >= s.cfg.MaxOutstandingTickets {
		return &RateLimitedError{Limit: s.cfg.MaxOutstandingTickets}
	}
	s.outstanding[installID] = append(s.outstanding[installID], now)
	return nil
}

// pruneLocked drops reservations older than one ticket lifetime. The map is
// keyed by install id and a service sees a great many of them over a day, so it
// needs sweeping or it becomes a slow leak of one empty slice per person who
// ever scanned a QR code.
func (s *Service) pruneLocked(cutoff time.Time) {
	for id := range s.outstanding {
		live := s.outstanding[id][:0]
		for _, at := range s.outstanding[id] {
			if at.After(cutoff) {
				live = append(live, at)
			}
		}
		if len(live) == 0 {
			delete(s.outstanding, id)
			continue
		}
		s.outstanding[id] = live
	}
}

// releaseTicket drops a reservation whose ticket was never stored. The list
// holds timestamps rather than tickets, so the honest thing is to leave the
// entry to age out rather than to pretend a lookup can find it.
func (s *Service) releaseTicket(installID, ticket string) {
	if installID == "" {
		return
	}
	_ = ticket
	s.outstandingMu.Lock()
	defer s.outstandingMu.Unlock()
	if list := s.outstanding[installID]; len(list) <= 1 {
		delete(s.outstanding, installID)
	}
}

// RateLimitedError is raised when an install id has too many live tickets. It
// is a distinct type because the HTTP layer has to answer 429 rather than 500,
// and mapping it from a string would be exactly the kind of guess that turns
// a busy moment into an outage.
type RateLimitedError struct {
	Limit int
}

func (e *RateLimitedError) Error() string {
	return fmt.Sprintf("auth: too many outstanding tickets (limit %d)", e.Limit)
}

// ---------------------------------------------------------------------------
// Polling
// ---------------------------------------------------------------------------

// PollResult is the client's view of one scan.
type PollResult struct {
	Status       string
	AccessToken  string
	RefreshToken string
	ExpiresAt    time.Time
	Account      *store.Account
}

// Poll answers one poll of a ticket.
//
// An unknown ticket is reported as expired rather than as a 404. The two mean
// the same thing to a user and call for the same button, and the client does
// have a 404 path — but that path is the degraded one, and the normal reason a
// ticket is gone is that it timed out.
func (s *Service) Poll(ctx context.Context, ticket string) (PollResult, error) {
	if strings.TrimSpace(ticket) == "" {
		return PollResult{Status: StatusExpired}, nil
	}
	session, err := s.store.GetAuthSession(ctx, ticket)
	if err != nil {
		if errors.Is(err, store.ErrNotFound) {
			return PollResult{Status: StatusExpired}, nil
		}
		// A store that cannot be reached is not the same answer as a ticket
		// that does not exist. Saying "expired" here would show a user a
		// re-scan button for a database blip.
		return PollResult{}, fmt.Errorf("auth: get session: %w", err)
	}

	switch session.State {
	case store.AuthPending:
		if !s.now().Before(session.ExpiresAt) {
			return PollResult{Status: StatusExpired, ExpiresAt: session.ExpiresAt}, nil
		}
		return PollResult{Status: StatusPending, ExpiresAt: session.ExpiresAt}, nil

	case store.AuthAuthorized:
		return s.authorized(ctx, session)

	case store.AuthExpired:
		return PollResult{Status: StatusExpired, ExpiresAt: session.ExpiresAt}, nil

	case store.AuthRejected:
		return PollResult{Status: StatusRejected, ExpiresAt: session.ExpiresAt}, nil

	default:
		// The store's contract says it maps unrecognised states to expired
		// and never hands one back, so this arm is unreachable against a
		// conforming store. It is here anyway, because the alternative is a
		// panic in a request handler triggered by a row this process did not
		// write.
		s.log.Warn("unknown auth session state",
			"ticket", token.Fingerprint(ticket),
			"state", string(session.State))
		return PollResult{Status: StatusExpired, ExpiresAt: session.ExpiresAt}, nil
	}
}

// authorized assembles the reply for a completed scan.
//
// The tokens are read once. After that the row is all the store has left — it
// keeps hashes — and a second poll reports the account without them. That is
// not a happy path; it is what happens if a client polls twice past the end,
// and the honest answer is "you already have these".
func (s *Service) authorized(ctx context.Context, session store.AuthSession) (PollResult, error) {
	creds, ok := s.handoff.Take(ctx, session.Ticket)
	if !ok {
		// Either a repeat poll, or the redirect landed on a different process
		// from this one. The account is still worth reporting: it is not a
		// secret and it lets the UI show who is signed in.
		result := PollResult{Status: StatusAuthorized, ExpiresAt: session.ExpiresAt}
		if session.AccountID != "" {
			account, err := s.store.GetAccount(ctx, session.AccountID)
			if err != nil {
				return PollResult{}, fmt.Errorf("auth: get account: %w", err)
			}
			result.Account = &account
		}
		return result, nil
	}

	result := PollResult{
		Status:       StatusAuthorized,
		AccessToken:  creds.AccessToken,
		RefreshToken: creds.RefreshToken,
		ExpiresAt:    creds.AccessExpiresAt,
		Account:      &creds.Account,
	}
	s.log.Info("auth session delivered",
		"ticket", token.Fingerprint(session.Ticket),
		"account", token.Fingerprint(creds.Account.ID),
		"chain", token.Fingerprint(creds.Account.ID))
	return result, nil
}

// ---------------------------------------------------------------------------
// The redirect
// ---------------------------------------------------------------------------

// CallbackResult is what the redirect handler learned.
type CallbackResult struct {
	AccountID string
	ChainID   string
}

// HandleCallback completes a scan. The order is not negotiable and each step
// exists because the one after it is wrong without it.
//
//  1. The state must name an unconsumed ticket. This is the CSRF and
//     ticket-substitution defence, and it has to come first: everything after
//     it acts on the ticket that step 1 approved, and there is no cheap way to
//     undo a sign-in.
//
//  2. The code is exchanged. It is single-use and lives five minutes, so a
//     failure is a failure and must not be retried.
//
//  3. The account is resolved *before* the ticket is completed. That ordering
//     is the whole reason this function reads the way it does: the poller
//     learns the scan is finished from the ticket's state, so the credentials
//     it will collect have to be in the handoff by then. Resolving the account
//     first and only then flipping the state makes that true by construction
//     rather than by a sleep.
//
//  4. Tokens are issued, the session row is written, and the chain exists so
//     there is something to revoke.
//
//  5. The ticket is completed. Only now does a poller see authorized.
//
//  6. The caller replies 200 with an empty body. Not a redirect.
func (s *Service) HandleCallback(ctx context.Context, code, state string) (CallbackResult, error) {
	session, err := s.validateState(ctx, state)
	if err != nil {
		return CallbackResult{}, err
	}

	sess, err := s.wechat.ExchangeCode(ctx, code)
	if err != nil {
		// Logged without the code, the secret, or the OpenID. A code is
		// single-use but short-lived, and an error string carrying one would
		// put a working credential in the log.
		s.log.Warn("wechat code exchange failed",
			"ticket", token.Fingerprint(session.Ticket),
			"transient", wechat.IsTransient(err),
			"errcode", wechatErrCode(err),
			"error", err)
		if wechat.IsTransient(err) {
			// Leave the ticket pending. A transient failure must not consume
			// a ticket the user is still holding, and must not tell the
			// desktop client that the scan failed.
			return CallbackResult{}, &UpstreamUnavailableError{Cause: err}
		}
		// The ticket stays pending and ages out. Marking it rejected would
		// need a store method that does not exist, and a user who scanned a
		// stale code has the same experience either way.
		return CallbackResult{}, &InvalidCodeError{Cause: err}
	}

	account, _, err := s.store.UpsertAccountByOpenID(ctx, sess.OpenID, sess.UnionID, "", "")
	if err != nil {
		return CallbackResult{}, fmt.Errorf("auth: upsert account: %w", err)
	}

	// The profile is a nicety. Under snsapi_login WeChat will not hand one
	// over, and a sign-in that fails because a display name is unavailable
	// would be a strictly worse product.
	profile, err := s.wechat.FetchProfile(ctx, sess.AccessToken, sess.OpenID)
	if err != nil {
		if wechat.IsTransient(err) {
			s.log.Info("wechat profile unavailable, continuing with the stored one",
				"ticket", token.Fingerprint(session.Ticket),
				"error", err)
		} else {
			s.log.Warn("wechat profile unavailable", "error", err)
		}
		profile = wechat.Profile{}
	}
	if profile.Nickname != "" || profile.AvatarURL != "" {
		// Best effort: refresh the display fields without disturbing the
		// identity or the plan. A failure here costs a stale nickname, which
		// is not worth failing a sign-in over.
		if refreshed, _, err := s.store.UpsertAccountByOpenID(ctx, sess.OpenID, sess.UnionID, profile.Nickname, profile.AvatarURL); err == nil {
			account = refreshed
		}
	}

	creds, err := s.issuer.Issue(account.ID, "")
	if err != nil {
		return CallbackResult{}, fmt.Errorf("auth: issue tokens: %w", err)
	}
	if err := s.store.CreateSession(ctx, store.Session{
		ID:               creds.SessionID,
		AccountID:        account.ID,
		DeviceID:         "",
		AccessTokenHash:  creds.AccessTokenHash,
		AccessExpiresAt:  creds.Access.ExpiresAt,
		RefreshTokenHash: creds.RefreshTokenHash,
		RefreshExpiresAt: creds.RefreshExpiresAt,
		ChainID:          creds.ChainID,
		CreatedAt:        s.now(),
	}); err != nil {
		return CallbackResult{}, fmt.Errorf("auth: create session: %w", err)
	}

	// In the handoff before the ticket flips. See the ordering note above.
	s.handoff.Put(ctx, session.Ticket, Credentials{
		AccessToken:     creds.Access.Token,
		RefreshToken:    creds.Refresh.Token,
		AccessExpiresAt: creds.Access.ExpiresAt,
		Account:         account,
	}, s.cfg.AuthSessionTTL)

	// Completing also migrates the anonymous history this install id
	// accumulated before anyone signed in. That belongs to the account domain
	// and the store does it inside the same transaction as the state flip, so
	// doing it again here would be a second migration of the same rows.
	if _, err := s.store.CompleteAuthSession(ctx, session.Ticket, sess.OpenID, sess.UnionID,
		profile.Nickname, profile.AvatarURL, session.InstallID); err != nil {
		// The chain exists but nobody was told about it. Revoking leaves the
		// user exactly where they were before the scan: signed out.
		if revErr := s.store.RevokeChain(ctx, creds.ChainID, s.now()); revErr != nil {
			s.log.Error("orphan chain could not be revoked", "chain", token.Fingerprint(creds.ChainID), "error", revErr)
		}
		return CallbackResult{}, fmt.Errorf("auth: complete session: %w", err)
	}

	s.log.Info("auth session authorized",
		"ticket", token.Fingerprint(session.Ticket),
		"account", token.Fingerprint(account.ID),
		"chain", token.Fingerprint(creds.ChainID))

	return CallbackResult{AccountID: account.ID, ChainID: creds.ChainID}, nil
}

// validateState is step 1. It rejects everything that is not a live,
// unconsumed, unexpired ticket.
func (s *Service) validateState(ctx context.Context, state string) (store.AuthSession, error) {
	if strings.TrimSpace(state) == "" {
		return store.AuthSession{}, &InvalidStateError{Reason: "missing state"}
	}
	session, err := s.store.GetAuthSession(ctx, state)
	if err != nil {
		if errors.Is(err, store.ErrNotFound) {
			// Either forged, or a ticket that expired before the redirect
			// landed. Indistinguishable from here, and deliberately so: the
			// reply must not tell an attacker which half it was.
			return store.AuthSession{}, &InvalidStateError{Reason: "unknown ticket"}
		}
		return store.AuthSession{}, fmt.Errorf("auth: get session: %w", err)
	}
	if session.State != store.AuthPending {
		// Consumed or expired. A replayed redirect lands here, which is the
		// other half of "one-time".
		return store.AuthSession{}, &InvalidStateError{Reason: "ticket already used"}
	}
	if !s.now().Before(session.ExpiresAt) {
		return store.AuthSession{}, &InvalidStateError{Reason: "ticket expired"}
	}
	return session, nil
}

// ---------------------------------------------------------------------------
// Refresh
// ---------------------------------------------------------------------------

// RefreshResult is the outcome of one rotation.
type RefreshResult struct {
	Outcome       store.RefreshOutcome
	AccessToken   string
	RefreshToken  string
	ExpiresAt     time.Time
	Account       *store.Account
	RevokedChain  bool
	TransientHint bool
}

// Refresh rotates a refresh token.
//
// The rotation itself is local and atomic; WeChat is not involved, because
// the WeChat access token never entered the client's auth chain in the first
// place. What still matters is the classification below, because "something
// upstream was unhappy" and "your credentials are dead" produce the same
// exception from Go's perspective and mean opposite things to a paying user.
//
//   - Reuse: the presented token was already consumed. Someone has it. The
//     whole chain goes, because a chain that survives a replay is a chain the
//     attacker keeps using.
//
//   - Unknown, revoked, or expired: normal end of life. The client signs out.
//
//   - Everything else: transient. The client keeps its refresh token and tries
//     again. This is the branch that must never be widened, because getting it
//     wrong signs people out over a database hiccup.
func (s *Service) Refresh(ctx context.Context, rawRefresh string) (RefreshResult, error) {
	if strings.TrimSpace(rawRefresh) == "" {
		return RefreshResult{Outcome: store.RefreshExpired}, nil
	}
	hash := token.HashRefresh(rawRefresh)

	current, err := s.store.GetSessionByRefreshHash(ctx, hash)
	if err != nil {
		if errors.Is(err, store.ErrNotFound) {
			// Either never existed or already consumed and swept. Both mean
			// the client must re-authenticate.
			return RefreshResult{Outcome: store.RefreshExpired}, nil
		}
		s.log.Warn("refresh could not be resolved", "error", err)
		return RefreshResult{Outcome: store.RefreshTransient, TransientHint: true}, nil
	}

	if current.RevokedAt != nil || !s.now().Before(current.RefreshExpiresAt) {
		return RefreshResult{Outcome: store.RefreshExpired, Account: nil}, nil
	}

	// Keep the chain. A rotation must not start a new family, or a leaked
	// refresh token would escape the revocation meant for it.
	next, err := s.issuer.Issue(current.AccountID, current.ChainID)
	if err != nil {
		return RefreshResult{}, fmt.Errorf("auth: issue tokens: %w", err)
	}

	session, outcome, err := s.store.RotateRefresh(ctx, hash, next.RefreshTokenHash, next.AccessTokenHash, next.Access.ExpiresAt)
	switch {
	case errors.Is(err, store.ErrReuseDetected):
		s.revokeChain(ctx, current.ChainID, "refresh token replayed")
		return RefreshResult{Outcome: store.RefreshExpired, RevokedChain: true}, &ReuseDetectedError{ChainID: current.ChainID}

	case errors.Is(err, store.ErrNotFound), errors.Is(err, store.ErrConflict):
		// The token vanished between the lookup and the rotation — two
		// concurrent refreshes racing, where one won. The loser is holding a
		// spent token and must re-authenticate; that is the cost of single-
		// flight being the client's job (§10.9), and the reason that section
		// requires it.
		return RefreshResult{Outcome: store.RefreshExpired}, nil

	case err != nil:
		// A store we could not reach, or a transaction that rolled back.
		// The old refresh token is untouched: RotateRefresh is atomic, and a
		// failure here is not evidence the client's credentials are bad.
		s.log.Warn("refresh rotation failed, session left intact",
			"chain", token.Fingerprint(current.ChainID),
			"error", err)
		return RefreshResult{Outcome: store.RefreshTransient, TransientHint: true}, nil
	}

	_ = session

	account, err := s.store.GetAccount(ctx, current.AccountID)
	if err != nil && !errors.Is(err, store.ErrNotFound) {
		s.log.Warn("refreshed token has no account row", "error", err)
	}
	var acct *store.Account
	if err == nil {
		acct = &account
	}

	s.log.Info("tokens rotated",
		"chain", token.Fingerprint(next.ChainID),
		"account", token.Fingerprint(current.AccountID),
		"outcome", string(outcome))

	return RefreshResult{
		Outcome:      store.RefreshRotated,
		AccessToken:  next.Access.Token,
		RefreshToken: next.Refresh.Token,
		ExpiresAt:    next.Access.ExpiresAt,
		Account:      acct,
	}, nil
}

// revokeChain tears down a family of tokens and says why, without saying to
// whom. The chain id is fingerprinted, never printed.
func (s *Service) revokeChain(ctx context.Context, chainID, reason string) {
	if chainID == "" {
		return
	}
	if err := s.store.RevokeChain(ctx, chainID, s.now()); err != nil {
		s.log.Error("chain revocation failed", "chain", token.Fingerprint(chainID), "error", err)
		return
	}
	s.log.Warn("chain revoked", "chain", token.Fingerprint(chainID), "reason", reason)
}

// Revoke signs a chain out. The caller supplies the chain, found from an
// access token's claims or from a refresh token's row.
func (s *Service) Revoke(ctx context.Context, chainID string) error {
	if strings.TrimSpace(chainID) == "" {
		return nil
	}
	if err := s.store.RevokeChain(ctx, chainID, s.now()); err != nil {
		return fmt.Errorf("auth: revoke chain: %w", err)
	}
	s.log.Info("session revoked", "chain", token.Fingerprint(chainID))
	return nil
}

// ChainForRefresh resolves a presented refresh token to its chain.
func (s *Service) ChainForRefresh(ctx context.Context, rawRefresh string) (string, error) {
	if strings.TrimSpace(rawRefresh) == "" {
		return "", nil
	}
	session, err := s.store.GetSessionByRefreshHash(ctx, token.HashRefresh(rawRefresh))
	if err != nil {
		if errors.Is(err, store.ErrNotFound) {
			return "", nil
		}
		return "", fmt.Errorf("auth: get session: %w", err)
	}
	return session.ChainID, nil
}

// ---------------------------------------------------------------------------
// Authenticated reads
// ---------------------------------------------------------------------------

// Account reads one account by id.
func (s *Service) Account(ctx context.Context, accountID string) (store.Account, error) {
	account, err := s.store.GetAccount(ctx, accountID)
	if err != nil {
		return store.Account{}, err
	}
	return account, nil
}

// ---------------------------------------------------------------------------
// Typed failures the HTTP layer maps
// ---------------------------------------------------------------------------

// InvalidStateError is a state parameter that does not name a usable ticket.
// The reason is for the log; the reply says only that it was not valid.
type InvalidStateError struct{ Reason string }

func (e *InvalidStateError) Error() string { return "auth: invalid state: " + e.Reason }

// InvalidCodeError is a code WeChat would not accept. Not retried: codes are
// single-use, so a second attempt with the same code is a guaranteed failure.
type InvalidCodeError struct{ Cause error }

func (e *InvalidCodeError) Error() string { return "auth: invalid code: " + e.Cause.Error() }

func (e *InvalidCodeError) Unwrap() error { return e.Cause }

// UpstreamUnavailableError is a failure that is somebody else's and might not
// be there in a minute.
type UpstreamUnavailableError struct{ Cause error }

func (e *UpstreamUnavailableError) Error() string {
	return "auth: upstream unavailable: " + e.Cause.Error()
}

func (e *UpstreamUnavailableError) Unwrap() error { return e.Cause }

// ReuseDetectedError is a refresh token presented twice. The chain is already
// revoked by the time this is returned.
type ReuseDetectedError struct{ ChainID string }

func (e *ReuseDetectedError) Error() string { return "auth: refresh token reuse detected" }

// ---------------------------------------------------------------------------
// Errors
// ---------------------------------------------------------------------------

// IsRateLimited reports whether err is the too-many-tickets condition.
func IsRateLimited(err error) bool {
	var e *RateLimitedError
	return errors.As(err, &e)
}

// IsInvalidState reports whether err is a rejected state parameter.
func IsInvalidState(err error) bool {
	var e *InvalidStateError
	return errors.As(err, &e)
}

// IsInvalidCode reports whether err is a code WeChat refused.
func IsInvalidCode(err error) bool {
	var e *InvalidCodeError
	return errors.As(err, &e)
}

// IsUpstreamUnavailable reports whether err is a retryable upstream failure.
func IsUpstreamUnavailable(err error) bool {
	var e *UpstreamUnavailableError
	return errors.As(err, &e)
}

// IsReuseDetected reports whether err is a refresh token replay.
func IsReuseDetected(err error) bool {
	var e *ReuseDetectedError
	return errors.As(err, &e)
}

// wechatErrCode pulls the numeric code out for a log line, or -1.
func wechatErrCode(err error) int {
	var apiErr *wechat.APIError
	if errors.As(err, &apiErr) {
		return apiErr.ErrCode
	}
	return -1
}
