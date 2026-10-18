package postgres

import (
	"time"

	"github.com/jackc/pgx/v5/pgtype"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// This file holds the row scanners. They are separate from the queries for
// one reason: a column list and its Scan destinations have to be edited
// together, and when they live in the same function the pairing is a matter of
// counting arguments by eye. Every scanner here also normalises timestamps to
// UTC on the way out, so no caller ever receives a local-zone time.Time and
// serialises it into a licence.

// nullTime converts a nullable timestamptz column into the domain type's
// pointer. The copy is deliberate: pgtype hands back a value the driver may
// reuse.
func nullTime(ts pgtype.Timestamptz) *time.Time {
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
		unionID pgtype.Text
		install pgtype.Text
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

// scanSession reads the session projection: the device row joined to one
// refresh token. The same shape serves GetSessionByRefreshHash and the result
// of a rotation, so both report identical state for identical inputs.
func scanSession(row rowScanner) (store.Session, error) {
	var (
		s        store.Session
		refresh  string
		rexpires time.Time
		revoked  pgtype.Timestamptz
	)
	err := row.Scan(
		&s.ID,
		&s.AccountID,
		&s.DeviceID,
		&s.AccessTokenHash,
		&s.AccessExpiresAt,
		&refresh,
		&rexpires,
		&s.ChainID,
		&revoked,
		&s.CreatedAt,
	)
	if err != nil {
		return store.Session{}, err
	}
	s.RefreshTokenHash = refresh
	s.RefreshExpiresAt = utc(rexpires)
	s.AccessExpiresAt = utc(s.AccessExpiresAt)
	s.RevokedAt = nullTime(revoked)
	s.CreatedAt = utc(s.CreatedAt)
	return s, nil
}

// scanOrder reads the order projection. Null paid_at is the difference between
// "not paid" and "paid and we know when", so it stays a pointer.
func scanOrder(row rowScanner) (store.Order, error) {
	var (
		o       store.Order
		paidAt  pgtype.Timestamptz
		account string
	)
	err := row.Scan(
		&o.ID,
		&o.OutTradeNo,
		&o.ProductID,
		&o.Plan,
		&o.AmountFen,
		&o.Currency,
		&o.Status,
		&account,
		&o.CodeURL,
		&o.CreatedAt,
		&o.ExpiresAt,
		&paidAt,
		&o.TransactionID,
		&o.LicenseID,
		&o.LicenseKey,
	)
	if err != nil {
		return store.Order{}, err
	}
	o.AccountID = account
	o.PaidAt = nullTime(paidAt)
	o.CreatedAt = utc(o.CreatedAt)
	o.ExpiresAt = utc(o.ExpiresAt)
	return o, nil
}

// rowScanner is pgx.Row without the query: scanners need to work on both a
// single row and the first row of a result set.
type rowScanner interface {
	Scan(dest ...any) error
}

// text flattens a nullable text column. Empty text and NULL mean the same
// thing to the domain types, which use plain strings for "unknown".
//
// It exists because scanning into **string is a driver feature rather than a
// language one, and it is silently ignored by anything that is not the real
// driver — a mock, a future pool, an instrumented wrapper. Going through
// pgtype.Text makes the nullability visible at the scan site instead of
// depending on what the reader happens to understand.
func text(t pgtype.Text) string {
	if !t.Valid {
		return ""
	}
	return t.String
}
