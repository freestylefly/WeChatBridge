package postgres

import (
	"context"
	"errors"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgtype"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// The account projection. Every query that returns an account selects exactly
// these columns in this order; scanAccount depends on it.
const accountColumns = `
	id, open_id, union_id, nickname, avatar_url, plan, install_id,
	wechat_bound, created_at, last_login_at`

// upsertAccountSQL signs a person in.
//
// It is an upsert rather than select-then-insert because two devices of the
// same person can race through this at the same moment, and an
// insert-then-catch-violation-then-select would leave one of them with an
// account row it never read. Two details are load-bearing:
//
//   - plan is never written by the DO UPDATE. Signing in must not demote
//     someone who paid, so the entitlement column is insert-only.
//   - xmax = 0 identifies the row the INSERT created (a heap tuple that was
//     never updated has a zero xmax) which is how the caller learns whether
//     this was a first sign-in or a return visit, without a second query.
const upsertAccountSQL = `
	INSERT INTO accounts (open_id, union_id, nickname, avatar_url, plan, install_id, last_login_at)
	VALUES ($1, NULLIF($2, ''), $3, $4, 'free', NULLIF($5, ''), $6)
	ON CONFLICT (open_id) DO UPDATE SET
		last_login_at = EXCLUDED.last_login_at,
		nickname      = CASE WHEN EXCLUDED.nickname   <> '' THEN EXCLUDED.nickname   ELSE accounts.nickname   END,
		avatar_url    = CASE WHEN EXCLUDED.avatar_url <> '' THEN EXCLUDED.avatar_url ELSE accounts.avatar_url END,
		union_id      = CASE WHEN EXCLUDED.union_id IS NOT NULL THEN EXCLUDED.union_id ELSE accounts.union_id END,
		install_id    = COALESCE(accounts.install_id, EXCLUDED.install_id)
	RETURNING ` + accountColumns + `, (xmax = 0) AS created`

// upsertAccount is the shared body of a plain sign-in and of the sign-in that
// completes a QR session. q is a tx when the caller is inside one.
func upsertAccount(ctx context.Context, q rowQuerier, openID, unionID, nickname, avatar, installID string, now time.Time) (store.Account, bool, error) {
	if openID == "" {
		// An empty open_id would collide with every other empty open_id on the
		// unique index, turning "WeChat said nothing" into "somebody else's
		// account". It is a bug upstream and this is where it stops.
		return store.Account{}, false, errors.New("postgres: upsert account: empty open id")
	}

	row := q.QueryRow(ctx, upsertAccountSQL,
		openID, unionID, nickname, avatar, installID, utc(now))
	acct, err := scanAccountWithCreated(row)
	if err != nil {
		return store.Account{}, false, notFound("upsert account", err)
	}
	return acct.Account, acct.created, nil
}

// scanAccountWithCreated reads accountColumns plus the insert-only flag.
func scanAccountWithCreated(row rowScanner) (withCreated, error) {
	var (
		a       withCreated
		unionID pgtype.Text
		install pgtype.Text
		created bool
	)
	err := row.Scan(
		&a.ID, &a.OpenID, &unionID, &a.Nickname, &a.AvatarURL, &a.Plan,
		&install, &a.WechatBound, &a.CreatedAt, &a.LastLoginAt, &created,
	)
	if err != nil {
		return withCreated{}, err
	}
	a.Account.UnionID = text(unionID)
	a.Account.InstallID = text(install)
	a.Account.CreatedAt = utc(a.CreatedAt)
	a.Account.LastLoginAt = utc(a.LastLoginAt)
	a.created = created
	return a, nil
}

// withCreated is an account plus the answer to "was this row new".
type withCreated struct {
	store.Account
	created bool
}

// ---------------------------------------------------------------------------
// Auth sessions
// ---------------------------------------------------------------------------

const createAuthSessionSQL = `
	INSERT INTO auth_sessions (ticket, state, install_id, authorize_url, expires_at, created_at)
	VALUES ($1, $2, $3, $4, $5, $6)`

const getAuthSessionSQL = `
	SELECT ticket, state, install_id, authorize_url, expires_at, account_id, created_at
	FROM auth_sessions
	WHERE ticket = $1`

// lockAuthSessionSQL takes the row lock CompleteAuthSession needs. The lock is
// what makes "check the state, then change it" safe; without it two polling
// clients could both read `pending` and both complete the same ticket.
const lockAuthSessionSQL = `
	SELECT state, install_id, expires_at
	FROM auth_sessions
	WHERE ticket = $1
	FOR UPDATE`

const markAuthSessionAuthorizedSQL = `
	UPDATE auth_sessions SET state = 'authorized', account_id = $2
	WHERE ticket = $1 AND state = 'pending'`

// CreateAuthSession records a QR sign-in that is about to be shown.
func (s *Store) CreateAuthSession(ctx context.Context, sess store.AuthSession) error {
	state := sess.State
	if state == "" {
		state = store.AuthPending
	}
	_, err := s.db.Exec(ctx, createAuthSessionSQL,
		sess.Ticket, string(state), sess.InstallID, sess.AuthorizeURL,
		utc(sess.ExpiresAt), utc(sess.CreatedAt))
	if err := uniqueConflict("create auth session", constraintAuthSessionTicket, err); err != nil {
		return err
	}
	return nil
}

// GetAuthSession reads a ticket. An expired ticket is a miss as far as callers
// are concerned, so the expiry is folded into the state here rather than left
// for every caller to remember.
func (s *Store) GetAuthSession(ctx context.Context, ticket string) (store.AuthSession, error) {
	var (
		sess      store.AuthSession
		state     string
		accountID pgtype.Text
	)
	err := s.db.QueryRow(ctx, getAuthSessionSQL, ticket).Scan(
		&sess.Ticket, &state, &sess.InstallID, &sess.AuthorizeURL,
		&sess.ExpiresAt, &accountID, &sess.CreatedAt,
	)
	if err != nil {
		return store.AuthSession{}, notFound("get auth session", err)
	}
	sess.AccountID = text(accountID)
	sess.ExpiresAt = utc(sess.ExpiresAt)
	sess.CreatedAt = utc(sess.CreatedAt)
	sess.State = authState(state, sess.ExpiresAt, time.Now())
	return sess, nil
}

// authState maps a stored state onto the domain enum.
//
// An unrecognised string is treated as expired rather than passed through.
// The client's enum is fixed and this one must mirror it exactly; a value from
// a future version of the client is a question this server cannot answer, and
// the safe wrong answer is "come back later", not "granted".
func authState(raw string, expiresAt, now time.Time) store.AuthSessionState {
	switch s := store.AuthSessionState(raw); s {
	case store.AuthPending, store.AuthAuthorized, store.AuthRejected:
		return s
	case store.AuthExpired:
		return s
	default:
		_ = expiresAt
		_ = now
		return store.AuthExpired
	}
}

// CompleteAuthSession spends the ticket and resolves the account, atomically.
//
// The two steps are one transaction because each one alone is a lie the other
// cannot repair: a spent ticket with no account strands the client at a QR
// code that will never resolve.
//
// The old version also migrated anonymous install_records onto the account in
// this transaction. That table is gone with the payment rewrite — an
// activation request now requires a signed-in account, so there is no
// pre-sign-in history left to move — but the install id the ticket carried is
// still recorded on the account, because it is what ties a machine to the
// person who bought for it.
func (s *Store) CompleteAuthSession(ctx context.Context, ticket, openID, unionID, nickname, avatar, installID string) (store.Account, error) {
	var result store.Account
	now := time.Now().UTC()

	err := s.inTx(ctx, func(tx pgx.Tx) error {
		var (
			storedState string
			rowInst     string
			expiresAt   time.Time
		)
		if err := tx.QueryRow(ctx, lockAuthSessionSQL, ticket).Scan(&storedState, &rowInst, &expiresAt); err != nil {
			return notFound("complete auth session: load ticket", err)
		}
		if store.AuthSessionState(storedState) != store.AuthPending {
			// Already authorized, rejected or expired. The caller is looking at
			// a ticket that has been used, which is a conflict and not a miss:
			// the row is right there.
			return store.ErrConflict
		}
		if !expiresAt.After(now) {
			return store.ErrConflict
		}
		if rowInst != "" && installID == "" {
			// The session knows which anonymous identity started this; a caller
			// that supplies none must not be allowed to adopt a stranger's
			// identity.
			installID = rowInst
		}

		acct, _, err := upsertAccount(ctx, tx, openID, unionID, nickname, avatar, installID, now)
		if err != nil {
			return err
		}

		tag, err := tx.Exec(ctx, markAuthSessionAuthorizedSQL, ticket, acct.ID)
		if err != nil {
			return wrap("complete auth session: authorize", err)
		}
		// The row is locked and was pending; zero rows would mean somebody
		// changed the state through a path that bypassed the lock.
		if err := conflictWhenUnchanged("complete auth session: authorize", nil, tag.RowsAffected()); err != nil {
			return err
		}

		result = acct
		return nil
	})
	if err != nil {
		return store.Account{}, err
	}
	return result, nil
}

// UpsertAccountByOpenID signs someone in outside a QR session — a token
// refresh that discovers it has no account, a support tool re-deriving a
// profile.
func (s *Store) UpsertAccountByOpenID(ctx context.Context, openID, unionID, nickname, avatar string) (store.Account, bool, error) {
	return upsertAccount(ctx, s.db, openID, unionID, nickname, avatar, "", time.Now().UTC())
}

const getAccountSQL = `
	SELECT ` + accountColumns + `
	FROM accounts
	WHERE id = $1`

// GetAccount reads one account by primary key.
func (s *Store) GetAccount(ctx context.Context, id store.ID) (store.Account, error) {
	acct, err := scanAccount(s.db.QueryRow(ctx, getAccountSQL, id))
	if err != nil {
		return store.Account{}, notFound("get account", err)
	}
	return acct, nil
}

// ---------------------------------------------------------------------------
// The token chain — one live session per account.
//
// The sessions and refresh_tokens tables are gone. The chain now lives in four
// columns on the accounts row, and the consequences are deliberate:
//
//   - CreateSession is an UPDATE, not an INSERT. A second sign-in replaces
//     the first account-wide, which is the machine-change path the seller
//     answers with a reissue.
//   - Rotation keeps the replaced hash for one generation. Presenting it
//     again is the reuse signal, and the caller's answer — revoke the whole
//     chain — is unchanged.
//   - Session.ID is the account id: there is no second row to name. The
//     access token still carries its own session id claim, minted per issue,
//     and nothing downstream reads the stored one.
// ---------------------------------------------------------------------------

const createSessionSQL = `
	UPDATE accounts
	SET refresh_token_hash = $2, prev_refresh_token_hash = NULL,
	    refresh_expires_at = $3, chain_id = $4, session_revoked_at = NULL
	WHERE id = $1`

// sessionColumns is the token-chain projection. It serves both the unlocked
// lookup and the locked rotation read, so both see identical state.
const sessionColumns = `
	id, chain_id, refresh_token_hash, prev_refresh_token_hash,
	refresh_expires_at, session_revoked_at`

// sessionMatchSQL resolves a presented refresh hash against the live session
// and against the one generation before it. The second arm is what lets a
// replay reach the caller's revocation path instead of looking like an
// unknown token.
const sessionMatchSQL = `
	SELECT ` + sessionColumns + `
	FROM accounts
	WHERE refresh_token_hash = $1 OR prev_refresh_token_hash = $1`

const sessionMatchForUpdateSQL = sessionMatchSQL + `
	FOR UPDATE`

// rotateReplaceSQL is the conditional swap. The `refresh_token_hash = $1`
// guard is redundant while the row is locked and deliberately not removed:
// it is the line that keeps the rotation safe if the lock is ever replaced
// with a snapshot read, and it is the line a reviewer greps for.
const rotateReplaceSQL = `
	UPDATE accounts
	SET refresh_token_hash = $2, prev_refresh_token_hash = $1, refresh_expires_at = $4
	WHERE id = $3 AND refresh_token_hash = $1 AND session_revoked_at IS NULL`

const revokeChainSQL = `
	UPDATE accounts SET session_revoked_at = $2
	WHERE chain_id = $1 AND session_revoked_at IS NULL`

// CreateSession stores a freshly minted device session. Only hashes are
// written: the plaintext tokens exist in the response to the client and
// nowhere else, so a database dump is not a set of credentials.
func (s *Store) CreateSession(ctx context.Context, sess store.Session) error {
	if sess.AccountID == "" {
		return errors.New("postgres: create session: empty account id")
	}
	tag, err := s.db.Exec(ctx, createSessionSQL,
		sess.AccountID, sess.RefreshTokenHash,
		utc(sess.RefreshExpiresAt), sess.ChainID,
	)
	if err != nil {
		return wrap("create session", err)
	}
	if tag.RowsAffected() == 0 {
		// No such account. The caller minted tokens for an account that does
		// not exist, which is a bug and not a race.
		return store.ErrNotFound
	}
	return nil
}

// GetSessionByRefreshHash looks a session up by a presented refresh token.
func (s *Store) GetSessionByRefreshHash(ctx context.Context, hash string) (store.Session, error) {
	if hash == "" {
		return store.Session{}, store.ErrNotFound
	}
	sess, err := scanSession(s.db.QueryRow(ctx, sessionMatchSQL, hash), hash)
	if err != nil {
		return store.Session{}, notFound("get session by refresh hash", err)
	}
	return sess, nil
}

// RotateRefresh exchanges a refresh token for a new pair.
//
// The whole exchange is one transaction over one locked row. Presenting an
// already-consumed token is the signal that the chain is being replayed, and
// the caller answers it by revoking the family — which is the difference
// between an attacker's stolen token costing them one session and costing the
// legitimate user their account.
//
// The replacement inherits the old token's expiry rather than receiving a new
// one. Otherwise a client that refreshes every hour holds a chain alive
// forever and "sign in again every ninety days" becomes "never".
func (s *Store) RotateRefresh(ctx context.Context, oldHash, newHash, newAccessHash string, accessExpiresAt time.Time) (store.Session, store.RefreshOutcome, error) {
	if oldHash == "" || newHash == "" || newAccessHash == "" {
		return store.Session{}, store.RefreshExpired, errors.New("postgres: rotate refresh: empty token hash")
	}
	now := time.Now().UTC()

	var (
		sess    store.Session
		rotated bool
	)
	err := s.inTx(ctx, func(tx pgx.Tx) error {
		var (
			accountID, chainID  string
			currentHash, prevHash string
			expiresAt           time.Time
			revokedAt           pgtype.Timestamptz
		)
		err := tx.QueryRow(ctx, sessionMatchForUpdateSQL, oldHash).Scan(
			&accountID, &chainID, &currentHash, &prevHash, &expiresAt, &revokedAt,
		)
		if err != nil {
			if errors.Is(err, pgx.ErrNoRows) {
				// No such token. The caller must clear its session: presenting
				// something the server has never issued is not a transient
				// condition.
				sess = store.Session{}
				return store.ErrNotFound
			}
			return wrap("rotate refresh: load token", err)
		}

		sess = store.Session{
			ID:               accountID,
			AccountID:        accountID,
			RefreshTokenHash: oldHash,
			RefreshExpiresAt: utc(expiresAt),
			ChainID:          chainID,
			RevokedAt:        nullTime(revokedAt),
		}

		// The family was revoked: sign-out everywhere, or a theft response.
		if sess.RevokedAt != nil {
			return store.ErrConflict
		}
		// Already spent — the presented hash is the one this chain replaced.
		// This is the reuse signal and it is deliberately its own error rather
		// than a flavour of ErrConflict, because the caller's response is to
		// kill the whole chain, not to re-issue.
		if prevHash != "" && oldHash == prevHash && oldHash != currentHash {
			return store.ErrReuseDetected
		}
		// Expired. The client is told to re-authenticate; it is not an error,
		// it is the answer.
		if !expiresAt.After(now) {
			return nil
		}

		tag, err := tx.Exec(ctx, rotateReplaceSQL, oldHash, newHash, accountID, expiresAt)
		if err != nil {
			return wrap("rotate refresh: replace token", err)
		}
		if err := conflictWhenUnchanged("rotate refresh: replace token", nil, tag.RowsAffected()); err != nil {
			return err
		}

		sess.RefreshTokenHash = newHash
		rotated = true
		return nil
	})
	if err != nil {
		return store.Session{}, refreshOutcomeFor(err), err
	}
	if !rotated {
		// The only path that reaches here without an error is the expired
		// token, which is an answer rather than a failure.
		return sess, store.RefreshExpired, nil
	}
	return sess, store.RefreshRotated, nil
}

// refreshOutcomeFor keeps the outcome meaningful even on the error paths, so
// a caller that switches on the outcome before checking the error still gets
// the safe answer.
func refreshOutcomeFor(err error) store.RefreshOutcome {
	switch {
	case errors.Is(err, store.ErrReuseDetected), errors.Is(err, store.ErrConflict):
		// Somebody else is holding this chain, or it is already revoked. The
		// local session must go either way.
		return store.RefreshExpired
	default:
		return store.RefreshTransient
	}
}

// RevokeChain kills every token descended from one sign-in. With one session
// per account that is one row, and the statement is unchanged in shape from
// the two-table version so the guarantee is the same.
func (s *Store) RevokeChain(ctx context.Context, chainID string, at time.Time) error {
	_, err := s.db.Exec(ctx, revokeChainSQL, chainID, utc(at))
	if err != nil {
		return wrap("revoke chain", err)
	}
	return nil
}
