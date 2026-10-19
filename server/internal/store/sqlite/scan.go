package sqlite

import (
	"database/sql"
	"time"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// This file holds the row scanners. They are separate from the queries for
// one reason: a column list and its Scan destinations have to be edited
// together, and when they live in the same function the pairing is a matter
// of counting arguments by eye. Every scanner here also normalises timestamps to
// UTC on the way out, so no caller ever receives a local-zone time.Time and
// serialises it into a licence.
//
// The nullable columns go through sql.Null* rather than through **T, the
// driver-level double pointer: the double pointer is a driver feature, not a
// language one, and anything that is not the real driver — a mock, a future
// pool, an instrumented wrapper — silently ignores it. sql.Null* makes the
// nullability visible at the scan site instead.

// nullTime converts a nullable timestamp column into the domain type's
// pointer. The copy is deliberate: the driver hands back a value it may
// reuse.
func nullTime(ts sql.NullTime) *time.Time {
	if !ts.Valid {
		return nil
	}
	v := ts.Time.UTC()
	return &v
}

// utc normalises an instant for storage or for handing to a caller.
func utc(t time.Time) time.Time { return t.UTC() }

// scanAccount reads the account projection produced by every query in this
// package that returns an account. The column order is fixed by the constants
// below and must match exactly.
func scanAccount(row rowScanner) (store.Account, error) {
	var (
		a       store.Account
		unionID sql.NullString
		install sql.NullString
	)
	err := row.Scan(
		&a.ID,
		&a.OpenID,
		&unionID,
		&a.Nickname,
		&a.AvatarURL,
		&a.Plan,
		&install,
		&a.WechatBound,
		&a.CreatedAt,
		&a.LastLoginAt,
	)
	if err != nil {
		return store.Account{}, err
	}
	a.UnionID = text(unionID)
	a.InstallID = text(install)
	a.CreatedAt = utc(a.CreatedAt)
	a.LastLoginAt = utc(a.LastLoginAt)
	return a, nil
}

// scanSession reads the token-chain projection off the accounts row.
//
// The presented hash is what the caller is holding, and it is what the
// returned session reports as its refresh token: the row's stored hash is the
// live one, which after a rotation is not the token the caller holds, and
// a caller comparing the two would conclude a perfectly good token had been
// replaced. Telling "replayed" from "replaced" is RotateRefresh's job, and it
// reads the row directly rather than trusting this projection.
//
// The two hash columns are scanned but only one is read: the WHERE clause
// matches either generation, so the row cannot say which one the caller
// presented, and the answer does not matter here — the paths that care ask
// the database again under a lock.
//
// Both hash columns are nullable in the schema — a session that has never
// rotated has no previous generation — so they are scanned through
// sql.NullString. Scanning a NULL into a plain string is unsupported by
// database/sql, and the PostgreSQL suite never noticed the shape because its
// mock always fed the column a value.
func scanSession(row rowScanner, presented string) (store.Session, error) {
	var (
		s       store.Session
		live    sql.NullString
		prev    sql.NullString
		expires time.Time
		revoked sql.NullTime
	)
	err := row.Scan(
		&s.ID, &s.ChainID, &live, &prev, &expires, &revoked,
	)
	if err != nil {
		return store.Session{}, err
	}
	// Session.ID is the account id: the chain lives on the accounts row, so
	// there is no second row to name.
	s.AccountID = s.ID
	s.RefreshTokenHash = presented
	s.RefreshExpiresAt = utc(expires)
	s.RevokedAt = nullTime(revoked)
	return s, nil
}

// scanActivationRequest reads the activation_requests projection. The nullable
// amount and confirmation time stay pointers: "the buyer did not say" and
// "the seller has not answered yet" are different facts from zero.
func scanActivationRequest(row rowScanner) (store.ActivationRequest, error) {
	var (
		r         store.ActivationRequest
		amount    sql.NullInt64
		envelope  []byte
		confirmed sql.NullTime
	)
	err := row.Scan(
		&r.RequestID, &r.AccountID, &r.InstallID, &r.AnchorDigest, &amount,
		&r.Status, &envelope, &r.LicenseID, &r.CreatedAt, &confirmed,
	)
	if err != nil {
		return store.ActivationRequest{}, err
	}
	if amount.Valid {
		fen := amount.Int64
		r.AmountFen = &fen
	}
	r.EnvelopeJSON = envelope
	r.ConfirmedAt = nullTime(confirmed)
	r.CreatedAt = utc(r.CreatedAt)
	return r, nil
}

// scanActivationRequestView is scanActivationRequest plus the two account
// columns the console shows beside each row. The duplication of the base
// scanner is deliberate: database/sql has consumed the row by the time the
// base returns, so a view cannot be assembled from one.
func scanActivationRequestView(row rowScanner) (store.ActivationRequestView, error) {
	var (
		v         store.ActivationRequestView
		amount    sql.NullInt64
		envelope  []byte
		confirmed sql.NullTime
	)
	err := row.Scan(
		&v.RequestID, &v.AccountID, &v.InstallID, &v.AnchorDigest, &amount,
		&v.Status, &envelope, &v.LicenseID, &v.CreatedAt, &confirmed,
		&v.Nickname, &v.AvatarURL,
	)
	if err != nil {
		return store.ActivationRequestView{}, err
	}
	if amount.Valid {
		fen := amount.Int64
		v.AmountFen = &fen
	}
	v.EnvelopeJSON = envelope
	v.ConfirmedAt = nullTime(confirmed)
	v.CreatedAt = utc(v.CreatedAt)
	return v, nil
}

// scanLicenceBinding reads the licence_bindings projection. The request link
// is nullable because a reissue that found no open request still produces a
// binding — the licence exists, the paperwork does not.
func scanLicenceBinding(row rowScanner) (store.LicenceBinding, error) {
	var (
		b       store.LicenceBinding
		request sql.NullString
		revoked sql.NullTime
	)
	err := row.Scan(
		&b.LicenseID, &b.InstallID, &b.AccountID, &request, &b.IssuedAt, &revoked,
	)
	if err != nil {
		return store.LicenceBinding{}, err
	}
	b.RequestID = text(request)
	b.IssuedAt = utc(b.IssuedAt)
	b.RevokedAt = nullTime(revoked)
	return b, nil
}

// scanLicenceBindingView is scanLicenceBinding plus who the licence belongs
// to, for the ledger page's after-the-fact reconciliation.
func scanLicenceBindingView(row rowScanner) (store.LicenceBindingView, error) {
	var (
		v       store.LicenceBindingView
		request sql.NullString
		revoked sql.NullTime
	)
	err := row.Scan(
		&v.LicenseID, &v.InstallID, &v.AccountID, &request,
		&v.IssuedAt, &revoked, &v.Nickname, &v.OpenID,
	)
	if err != nil {
		return store.LicenceBindingView{}, err
	}
	v.RequestID = text(request)
	v.IssuedAt = utc(v.IssuedAt)
	v.RevokedAt = nullTime(revoked)
	return v, nil
}

// rowScanner is sql.Row without the query: scanners need to work on both a
// single row and the first row of a result set.
type rowScanner interface {
	Scan(dest ...any) error
}

// text flattens a nullable text column. Empty text and NULL mean the same
// thing to the domain types, which use plain strings for "unknown".
func text(t sql.NullString) string {
	if !t.Valid {
		return ""
	}
	return t.String
}
