package postgres

import (
	"context"
	"errors"
	"fmt"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgtype"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// ---------------------------------------------------------------------------
// Redemption codes
// ---------------------------------------------------------------------------

const createRedeemCodeSQL = `
	INSERT INTO redeem_codes (code_hash, product_id, plan, lifetime_seconds, issued_at, expires_at, redeemed_at, redeemed_install_id, license_id, revoked_at)
	VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)`

// getRedeemCodeSQL matches the hash case-insensitively. The comparison is on
// lower(code_hash) to use the matching unique index; the stored value is left
// exactly as it was written.
const getRedeemCodeSQL = `
	SELECT code_hash, product_id, plan, lifetime_seconds, issued_at, expires_at,
	       redeemed_at, COALESCE(redeemed_install_id, ''), COALESCE(license_id, ''), revoked_at
	FROM redeem_codes
	WHERE lower(code_hash) = lower($1)`

// burnRedeemCodeSQL spends a code.
//
// This is the query the whole buy-out product stands on. The WHERE clause is
// the mutual exclusion: PostgreSQL evaluates it while holding the row lock, so
// of two concurrent attempts exactly one updates a row and the other updates
// zero. Reading the row first would leave a window in which both callers have
// seen `redeemed_at IS NULL` and both mint a licence.
//
// revoked_at is in the predicate as well: a code an operator pulled back must
// not be spendable in the moment between the pull and the next cleanup.
const burnRedeemCodeSQL = `
	UPDATE redeem_codes SET redeemed_at = $2, redeemed_install_id = $3, license_id = $4
	WHERE lower(code_hash) = lower($1) AND redeemed_at IS NULL AND revoked_at IS NULL`

const extendRedeemCodeSQL = `
	UPDATE redeem_codes SET expires_at = $2
	WHERE lower(code_hash) = lower($1) AND redeemed_at IS NULL`

const revokeRedeemCodeSQL = `
	UPDATE redeem_codes SET revoked_at = $2
	WHERE lower(code_hash) = lower($1)`

// CreateRedeemCode stores a freshly minted code. Only the hash is stored:
// the plaintext goes into the customer's inbox and exists nowhere else.
func (s *Store) CreateRedeemCode(ctx context.Context, c store.RedeemCode) error {
	_, err := s.db.Exec(ctx, createRedeemCodeSQL,
		c.Hash, c.ProductID, string(c.Plan), int64(c.Lifetime),
		utc(c.IssuedAt), utc(c.ExpiresAt),
		utcPtr(c.RedeemedAt), nullable(c.RedeemedInstallID), nullable(c.LicenseID), utcPtr(c.RevokedAt))
	if err := uniqueConflict("create redeem code", constraintRedeemCodeHash, err); err != nil {
		return err
	}
	return nil
}

// GetRedeemCode reads a code by hash.
func (s *Store) GetRedeemCode(ctx context.Context, hash string) (store.RedeemCode, error) {
	var (
		c        store.RedeemCode
		lifetime int64
		redeemed pgtype.Timestamptz
		revoked  pgtype.Timestamptz
	)
	err := s.db.QueryRow(ctx, getRedeemCodeSQL, hash).Scan(
		&c.Hash, &c.ProductID, &c.Plan, &lifetime,
		&c.IssuedAt, &c.ExpiresAt, &redeemed,
		&c.RedeemedInstallID, &c.LicenseID, &revoked,
	)
	if err != nil {
		return store.RedeemCode{}, notFound("get redeem code", err)
	}
	c.Lifetime = time.Duration(lifetime)
	c.IssuedAt = utc(c.IssuedAt)
	c.ExpiresAt = utc(c.ExpiresAt)
	c.RedeemedAt = nullTime(redeemed)
	c.RevokedAt = nullTime(revoked)
	return c, nil
}

// BurnRedeemCode marks a code spent.
//
// ErrConflict here means "somebody else got there first" and ErrNotFound
// means "no such code", which are different messages to a human: the first is
// "this code has already been used", the second is "we have never issued
// this code". Collapsing them would either accuse a customer of theft or
// hide a genuine double-issue behind a shrug.
func (s *Store) BurnRedeemCode(ctx context.Context, hash, installID, licenceID string, at time.Time) error {
	if hash == "" || installID == "" || licenceID == "" {
		return errors.New("postgres: burn redeem code: hash, install id and licence id are all required")
	}
	tag, err := s.db.Exec(ctx, burnRedeemCodeSQL, hash, utc(at), installID, licenceID)
	if err != nil {
		return wrap("burn redeem code", err)
	}
	if tag.RowsAffected() == 0 {
		// Zero rows is ambiguous on its own: the code may not exist at all.
		// One extra indexed lookup turns it into the answer the caller needs,
		// and it only runs on the path that is already a failure.
		return s.redeemCodeAbsentOrSpent(ctx, hash, "burn redeem code")
	}
	return nil
}

// ExtendRedeemCode pushes a code's expiry out. Support grants this when a
// code was emailed and never arrived.
func (s *Store) ExtendRedeemCode(ctx context.Context, hash string, expiresAt time.Time) error {
	tag, err := s.db.Exec(ctx, extendRedeemCodeSQL, hash, utc(expiresAt))
	if err != nil {
		return wrap("extend redeem code", err)
	}
	if tag.RowsAffected() == 0 {
		return s.redeemCodeAbsentOrSpent(ctx, hash, "extend redeem code")
	}
	return nil
}

// RevokeRedeemCode pulls a code back.
func (s *Store) RevokeRedeemCode(ctx context.Context, hash string, at time.Time) error {
	tag, err := s.db.Exec(ctx, revokeRedeemCodeSQL, hash, utc(at))
	if err != nil {
		return wrap("revoke redeem code", err)
	}
	if tag.RowsAffected() == 0 {
		return s.redeemCodeAbsentOrSpent(ctx, hash, "revoke redeem code")
	}
	return nil
}

// redeemCodeAbsentOrSpent resolves the one ambiguity a conditional update
// leaves behind: zero rows means either "no such code" or "not in a state that
// allows this", and the two deserve different words.
func (s *Store) redeemCodeAbsentOrSpent(ctx context.Context, hash, op string) error {
	var exists bool
	if err := s.db.QueryRow(ctx,
		`SELECT EXISTS (SELECT 1 FROM redeem_codes WHERE lower(code_hash) = lower($1))`, hash,
	).Scan(&exists); err != nil && !errors.Is(err, pgx.ErrNoRows) {
		return wrap(op+": distinguish conflict from absence", err)
	}
	if !exists {
		return store.ErrNotFound
	}
	return store.ErrConflict
}

// ---------------------------------------------------------------------------
// Redemption requests
// ---------------------------------------------------------------------------

const saveRedemptionRequestSQL = `
	INSERT INTO redemption_requests (request_id, code_hash, license_id, envelope_json, created_at)
	VALUES ($1, $2, $3, $4, $5)`

const getRedemptionRequestSQL = `
	SELECT request_id, code_hash, license_id, envelope_json, created_at
	FROM redemption_requests
	WHERE request_id = $1`

// SaveRedemptionRequest records the answer to one user gesture.
//
// envelope_json is []byte on the way in and on the way out, stored as bytea.
// This is the one place where "the bytes we returned the first time" has to
// survive a round trip untouched: the envelope is signed, ECDSA picks a random
// k every time, and re-signing would hand the same licence a different
// signature each time. The client would accept both; every future hash,
// every de-duplication, every audit would fork.
func (s *Store) SaveRedemptionRequest(ctx context.Context, r store.RedemptionRequest) error {
	_, err := s.db.Exec(ctx, saveRedemptionRequestSQL,
		r.RequestID, r.CodeHash, r.LicenseID, r.EnvelopeJSON, utc(r.CreatedAt))
	if err := uniqueConflict("save redemption request", constraintRedemptionRequest, err); err != nil {
		return err
	}
	return nil
}

// GetRedemptionRequest returns the stored envelope for a request id, or
// ErrNotFound so the caller can re-derive it.
func (s *Store) GetRedemptionRequest(ctx context.Context, requestID string) (store.RedemptionRequest, error) {
	var r store.RedemptionRequest
	err := s.db.QueryRow(ctx, getRedemptionRequestSQL, requestID).Scan(
		&r.RequestID, &r.CodeHash, &r.LicenseID, &r.EnvelopeJSON, &r.CreatedAt,
	)
	if err != nil {
		return store.RedemptionRequest{}, notFound("get redemption request", err)
	}
	r.CreatedAt = utc(r.CreatedAt)
	return r, nil
}

// ---------------------------------------------------------------------------
// Device bindings
// ---------------------------------------------------------------------------

const lockOrderForBindingSQL = `
	SELECT id FROM orders WHERE id = $1 FOR UPDATE`

const countLiveBindingsSQL = `
	SELECT count(*) FROM licence_bindings
	WHERE order_id = $1 AND released_at IS NULL AND install_id <> $2`

// bindDeviceSQL upserts the binding.
//
// The unique index on (order_id, install_id) makes a released row revive
// rather than a second row appear. That matters twice over: the device history
// stays intact for support, and re-binding a machine the customer already
// freed is a state change on a row an operator can see, not an accumulation
// of near-duplicates nobody can interpret.
const bindDeviceSQL = `
	INSERT INTO licence_bindings (order_id, install_id, plan, bound_at, last_seen_at, released_at)
	VALUES ($1, $2, $3, $4, $4, NULL)
	ON CONFLICT (order_id, install_id) DO UPDATE SET
		released_at  = NULL,
		last_seen_at = EXCLUDED.last_seen_at,
		plan         = EXCLUDED.plan,
		bound_at     = CASE WHEN licence_bindings.released_at IS NULL
		                     THEN licence_bindings.bound_at
		                     ELSE EXCLUDED.bound_at END
	RETURNING id`

const listDevicesSQL = `
	SELECT id, order_id, install_id, plan, bound_at, last_seen_at, released_at
	FROM licence_bindings
	WHERE order_id = $1
	ORDER BY bound_at`

const releaseDeviceSQL = `
	UPDATE licence_bindings SET released_at = $3
	WHERE order_id = $1 AND install_id = $2 AND released_at IS NULL`

const bindingExistsSQL = `
	SELECT EXISTS (SELECT 1 FROM licence_bindings WHERE order_id = $1 AND install_id = $2)`

// BindDevice pins a machine to a licence and enforces the slot limit.
//
// The count and the upsert are inside one transaction that starts by locking
// the order row. Locking the order rather than the binding rows is
// deliberate: every device for an order serialises on the same row, so two
// machines binding at the same instant cannot both read "2 of 3 used" and both
// take the third slot.
func (s *Store) BindDevice(ctx context.Context, orderID store.ID, installID string, plan store.Plan, maxSlots int, at time.Time) error {
	if maxSlots < 1 {
		return fmt.Errorf("postgres: bind device: max slots must be at least 1, got %d", maxSlots)
	}
	if installID == "" {
		return errors.New("postgres: bind device: empty install id")
	}
	now := utc(at)

	return s.inTx(ctx, func(tx pgx.Tx) error {
		var id string
		if err := tx.QueryRow(ctx, lockOrderForBindingSQL, orderID).Scan(&id); err != nil {
			return notFound("bind device: lock order", err)
		}

		var live int
		if err := tx.QueryRow(ctx, countLiveBindingsSQL, orderID, installID).Scan(&live); err != nil {
			return wrap("bind device: count live devices", err)
		}
		if live >= maxSlots {
			// The caller can put the number in the reply, which is the whole
			// reason the interface asks for a single error here.
			return store.ErrConflict
		}

		if _, err := tx.Exec(ctx, bindDeviceSQL, orderID, installID, string(plan), now); err != nil {
			return wrap("bind device", err)
		}
		return nil
	})
}

// ListDevices returns every machine bound to an order, released ones included
// — support needs to see the dead laptop, not just the live ones.
func (s *Store) ListDevices(ctx context.Context, orderID store.ID) ([]store.LicenceBinding, error) {
	rows, err := s.db.Query(ctx, listDevicesSQL, orderID)
	if err != nil {
		return nil, wrap("list devices", err)
	}
	defer rows.Close()

	out := make([]store.LicenceBinding, 0, 4)
	for rows.Next() {
		var (
			b        store.LicenceBinding
			released pgtype.Timestamptz
		)
		if err := rows.Scan(&b.ID, &b.OrderID, &b.InstallID, &b.Plan,
			&b.BoundAt, &b.LastSeenAt, &released); err != nil {
			return nil, wrap("list devices: scan", err)
		}
		b.BoundAt = utc(b.BoundAt)
		b.LastSeenAt = utc(b.LastSeenAt)
		b.ReleasedAt = nullTime(released)
		out = append(out, b)
	}
	if err := rows.Err(); err != nil {
		return nil, wrap("list devices: iterate", err)
	}
	return out, nil
}

// ReleaseDevice frees a slot. Releasing an already-released machine is not an
// error — the caller's intent is satisfied either way — but releasing a
// machine that was never bound is a miss.
func (s *Store) ReleaseDevice(ctx context.Context, orderID store.ID, installID string, at time.Time) error {
	tag, err := s.db.Exec(ctx, releaseDeviceSQL, orderID, installID, utc(at))
	if err != nil {
		return wrap("release device", err)
	}
	if tag.RowsAffected() > 0 {
		return nil
	}
	var exists bool
	if err := s.db.QueryRow(ctx, bindingExistsSQL, orderID, installID).Scan(&exists); err != nil && !errors.Is(err, pgx.ErrNoRows) {
		return wrap("release device: check binding", err)
	}
	if !exists {
		return store.ErrNotFound
	}
	return nil
}

// nullable turns an empty domain string into SQL NULL, so "" and "never set"
// are the same thing in the database as they are in Go.
func nullable(s string) *string {
	if s == "" {
		return nil
	}
	return &s
}
