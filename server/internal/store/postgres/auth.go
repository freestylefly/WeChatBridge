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

// adoptInstallRecordsSQL is the history migration. It runs inside
// CompleteAuthSession's transaction and is the reason that function exists: a
// person who paid on Tuesday and signed in on Thursday has records sitting
// under their anonymous install id, and once the ticket is spent nobody else
// knows which account they belonged to.
const adoptInstallRecordsSQL = `
	UPDATE install_records SET account_id = $2
	WHERE install_id = $1 AND account_id IS NULL`

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

// CompleteAuthSession spends the ticket, resolves the account and moves the
// anonymous history, atomically.
//
// The three steps are one transaction because each one alone is a lie the
// other two cannot repair: a spent ticket with no account strands the client
// at a QR code that will never resolve, and an account with the history still
// under the install id strands the customer's data in a table nobody reads.
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
			// history.
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

		if _, err := tx.Exec(ctx, adoptInstallRecordsSQL, installID, acct.ID); err != nil {
			return wrap("complete auth session: adopt anonymous history", err)
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
// Device sessions and token rotation
// ---------------------------------------------------------------------------

const createSessionSQL = `
	INSERT INTO sessions (id, account_id, device_id, chain_id, access_token_hash, access_expires_at, revoked_at, created_at)
	VALUES ($1, $2, $3, $4, $5, $6, $7, $8)`

const getSessionByRefreshSQL = `
	SELECT s.id, s.account_id, s.device_id, s.access_token_hash, s.access_expires_at,
	       r.token_hash, r.expires_at, s.chain_id, s.revoked_at, s.created_at
	FROM refresh_tokens r
	JOIN sessions s ON s.id = r.session_id
	WHERE r.token_hash = $1`

// CreateSession stores a freshly minted device session. Only hashes are
// written: the plaintext tokens exist in the response to the client and
// nowhere else, so a database dump is not a set of credentials.
func (s *Store) CreateSession(ctx context.Context, sess store.Session) error {
	_, err := s.db.Exec(ctx, createSessionSQL,
		sess.ID, sess.AccountID, sess.DeviceID, sess.ChainID,
		sess.AccessTokenHash, utc(sess.AccessExpiresAt),
		utcPtr(sess.RevokedAt), utc(sess.CreatedAt))
	if err != nil {
		return wrap("create session", err)
	}
	return nil
}

// GetSessionByRefreshHash looks a session up by a presented refresh token.
func (s *Store) GetSessionByRefreshHash(ctx context.Context, hash string) (store.Session, error) {
	sess, err := scanSession(s.db.QueryRow(ctx, getSessionByRefreshSQL, hash))
	if err != nil {
		return store.Session{}, notFound("get session by refresh hash", err)
	}
	return sess, nil
}

// RotateRefresh exchanges a refresh token for a new pair.
//
// The whole exchange is one transaction over two locked rows. Presenting an
// already-consumed token is the signal that the chain is being replayed, and
// the caller answers it by revoking the family — which is the difference
// between an attacker's stolen token costing them one session and costing the
// legitimate user their account.
//
// The replacement inherits the old token's expiry rather than receiving a new
// one. Otherwise a client that refreshes every hour holds a chain alive
// forever and "sign in again every thirty days" becomes "never".
const (
	rotateLockSQL = `
		SELECT r.id, r.session_id, r.account_id, r.chain_id, r.device_id,
		       r.expires_at, r.consumed_at,
		       s.access_token_hash, s.access_expires_at, s.revoked_at, s.created_at
		FROM refresh_tokens r
		JOIN sessions s ON s.id = r.session_id
		WHERE r.token_hash = $1
		FOR UPDATE OF r, s`

	// The `consumed_at IS NULL` guard is redundant while the rows are locked
	// and deliberately not removed: it is the condition that makes the update
	// safe if the lock is ever replaced with a snapshot read, and it is the
	// line a reviewer greps for. Dropping a condition that is currently
	// redundant is how refresh-token reuse detection quietly stops working.
	rotateConsumeSQL = `
		UPDATE refresh_tokens SET consumed_at = $2, rotated_to = $3
		WHERE id = $1 AND consumed_at IS NULL`

	rotateInsertSQL = `
		INSERT INTO refresh_tokens (session_id, account_id, chain_id, device_id, token_hash, issued_at, expires_at)
		VALUES ($1, $2, $3, $4, $5, $6, $7)
		RETURNING id`

	rotateAccessSQL = `
		UPDATE sessions SET access_token_hash = $2, access_expires_at = $3
		WHERE id = $1`
)

// RotateRefresh implements the rotation described above.
func (s *Store) RotateRefresh(ctx context.Context, oldHash, newHash, newAccessHash string, accessExpiresAt time.Time) (store.Session, store.RefreshOutcome, error) {
	if oldHash == "" || newHash == "" || newAccessHash == "" {
		return store.Session{}, store.RefreshExpired, errors.New("postgres: rotate refresh: empty token hash")
	}
	now := time.Now().UTC()

	var (
		sess     store.Session
		rotated  bool
	)
	err := s.inTx(ctx, func(tx pgx.Tx) error {
		var (
			oldID, sessionID, accountID, chainID, deviceID string
			expiresAt                                      time.Time
			consumedAt                                    pgtype.Timestamptz
			revokedAt                                     pgtype.Timestamptz
			accessHash                                    string
			accessExpires                                 time.Time
			createdAt                                     time.Time
		)
		err := tx.QueryRow(ctx, rotateLockSQL, oldHash).Scan(
			&oldID, &sessionID, &accountID, &chainID, &deviceID,
			&expiresAt, &consumedAt, &accessHash, &accessExpires,
			&revokedAt, &createdAt,
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
			ID:               sessionID,
			AccountID:        accountID,
			DeviceID:         deviceID,
			AccessTokenHash:  accessHash,
			AccessExpiresAt:  utc(accessExpires),
			RefreshTokenHash: oldHash,
			RefreshExpiresAt: utc(expiresAt),
			ChainID:          chainID,
			RevokedAt:        nullTime(revokedAt),
			CreatedAt:        utc(createdAt),
		}

		// The family was revoked: sign-out everywhere, or a theft response.
		if sess.RevokedAt != nil {
			return store.ErrConflict
		}
		// Already spent. This is the reuse signal and it is deliberately its
		// own error rather than a flavour of ErrConflict, because the caller's
		// response is to kill the whole chain, not to re-issue.
		if consumedAt.Valid {
			return store.ErrReuseDetected
		}
		// Expired. The client is told to re-authenticate; it is not an error,
		// it is the answer.
		if !expiresAt.After(now) {
			return nil
		}

		var newID string
		if err := tx.QueryRow(ctx, rotateInsertSQL,
			sessionID, accountID, chainID, deviceID, newHash, now, expiresAt,
		).Scan(&newID); err != nil {
			return wrap("rotate refresh: insert replacement", err)
		}

		tag, err := tx.Exec(ctx, rotateConsumeSQL, oldID, now, newID)
		if err != nil {
			return wrap("rotate refresh: consume old token", err)
		}
		if err := conflictWhenUnchanged("rotate refresh: consume old token", nil, tag.RowsAffected()); err != nil {
			return err
		}

		if _, err := tx.Exec(ctx, rotateAccessSQL, sessionID, newAccessHash, utc(accessExpiresAt)); err != nil {
			return wrap("rotate refresh: update access token", err)
		}

		sess.AccessTokenHash = newAccessHash
		sess.AccessExpiresAt = utc(accessExpiresAt)
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

const revokeChainSessionSQL = `
	UPDATE sessions SET revoked_at = $2
	WHERE chain_id = $1 AND revoked_at IS NULL`

const revokeChainTokenSQL = `
	UPDATE refresh_tokens SET revoked_at = $2
	WHERE chain_id = $1 AND revoked_at IS NULL`

// RevokeChain kills every token descended from one sign-in.
func (s *Store) RevokeChain(ctx context.Context, chainID string, at time.Time) error {
	return s.inTx(ctx, func(tx pgx.Tx) error {
		for _, q := range []string{revokeChainSessionSQL, revokeChainTokenSQL} {
			if _, err := tx.Exec(ctx, q, chainID, utc(at)); err != nil {
				return wrap("revoke chain", err)
			}
		}
		return nil
	})
}

// ---------------------------------------------------------------------------
// Quota
// ---------------------------------------------------------------------------

const getQuotaSQL = `
	SELECT account_id, used, quota_limit, reset_at
	FROM quotas
	WHERE account_id = $1`

// consumeQuotaSQL is the hard stop, expressed as one statement.
//
// There is no read-then-write here and there must not be one: two concurrent
// requests both reading `used = 97` of 100 and both spending 4 is how a
// service sells 8 units of a service it priced for 3. The comparison happens
// inside the UPDATE, so the row lock is the arbitration and the RETURNING
// clause is the receipt.
//
// The CASE on reset_at rolls the window in the same breath. A request that
// lands after the boundary starts from zero, and reset_at advances by
// window_seconds — leaving it in the past would make every later request free,
// which is the failure this guard exists to prevent.
const consumeQuotaSQL = `
	UPDATE quotas SET
		used = CASE WHEN reset_at <= $2 THEN $3::bigint ELSE used + $3::bigint END,
		reset_at = CASE WHEN reset_at <= $2
		                THEN $2 + make_interval(secs => window_seconds)
		                ELSE reset_at END
	WHERE account_id = $1
	  AND (CASE WHEN reset_at <= $2 THEN 0 ELSE used END) + $3::bigint <= quota_limit
	RETURNING account_id, used, quota_limit, reset_at`

// GetQuota reports the current window. A window that has already elapsed
// reads as zero used, because that is what the next ConsumeQuota will charge
// against, and a caller that shows the customer 100/100 for a window that is
// over will not get unrated support.
func (s *Store) GetQuota(ctx context.Context, accountID store.ID) (store.Quota, error) {
	q, err := scanQuota(s.db.QueryRow(ctx, getQuotaSQL, accountID))
	if err != nil {
		return store.Quota{}, notFound("get quota", err)
	}
	if !q.ResetAt.After(time.Now().UTC()) {
		q.Used = 0
	}
	return q, nil
}

// ConsumeQuota spends units and returns the window as it now stands.
func (s *Store) ConsumeQuota(ctx context.Context, accountID store.ID, units int64) (store.Quota, error) {
	if units <= 0 {
		// A zero or negative spend is either a caller bug or an attempt to
		// move the counter backwards. Neither is worth a query.
		return store.Quota{}, errors.New("postgres: consume quota: units must be positive")
	}
	q, err := scanQuota(s.db.QueryRow(ctx, consumeQuotaSQL, accountID, time.Now().UTC(), units))
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			// Either there is no quota row for this account or the spend would
			// exceed the limit; the caller can tell them apart by reading
			// GetQuota first, which it must do anyway to report the number.
			return store.Quota{}, store.ErrConflict
		}
		return store.Quota{}, wrap("consume quota", err)
	}
	return q, nil
}

func scanQuota(row rowScanner) (store.Quota, error) {
	var q store.Quota
	if err := row.Scan(&q.AccountID, &q.Used, &q.Limit, &q.ResetAt); err != nil {
		return store.Quota{}, err
	}
	q.ResetAt = utc(q.ResetAt)
	return q, nil
}

// utcPtr normalises an optional instant for storage.
func utcPtr(t *time.Time) *time.Time {
	if t == nil {
		return nil
	}
	v := t.UTC()
	return &v
}
