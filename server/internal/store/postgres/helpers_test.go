package postgres

import (
	"strings"
	"testing"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgtype"
	"github.com/pashagolub/pgxmock/v3"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// These tests drive the whole store through a mock pool. No PostgreSQL is
// needed and none is assumed: a test that needs a live database is a test
// nobody runs before a deploy, and the parts of this package that are worth
// protecting — the WHERE clauses, the transaction boundaries, the error
// classification — are all decided before a single row is touched.
//
// One quirk of the mock is worth knowing before you write a row by hand: on
// QueryRow it discards the error Scan returns, so a value whose type does not
// match its destination is silently left at the zero value. The row builders
// below therefore use the domain types (store.Plan, store.ActivationStatus)
// rather than plain strings, which is also a useful reminder that those
// columns are enums and not free text.

// newMock returns a Store wired to a mock pool.
//
// `ordered` defaults to true, which is what most of these tests want: the
// mock then asserts the exact statement sequence, so a query added in the
// wrong place or a transaction that quietly stopped being one fails here
// instead of in production.
func newMock(t *testing.T) (*Store, pgxmock.PgxPoolIface) {
	t.Helper()
	mock, err := pgxmock.NewPool(pgxmock.QueryMatcherOption(pgxmock.QueryMatcherRegexp))
	if err != nil {
		t.Fatalf("pgxmock.NewPool: %v", err)
	}
	return &Store{db: mock}, mock
}

// newUnorderedMock returns a Store whose mock does not care about sequence,
// for the tests that drive several goroutines at once.
func newUnorderedMock(t *testing.T) (*Store, pgxmock.PgxPoolIface) {
	t.Helper()
	s, mock := newMock(t)
	mock.MatchExpectationsInOrder(false)
	return s, mock
}

// The clock is anchored to now rather than to a fixed date, because the store
// compares the caller's instants against the real one: a test fixture from a
// fixed date stops meaning "not yet expired" on the day it passes.
var (
	testNow     = time.Now().UTC().Truncate(time.Second)
	testLater   = testNow.Add(time.Hour)
	testExpired = testNow.Add(-365 * 24 * time.Hour)
)

// nullTimeValue is what a mock row must contain for a NULL timestamptz. The
// real driver leaves Valid false; the mock nils the destination outright if it
// is handed a nil, which would take a pointer with it.
func nullTimeValue() pgtype.Timestamptz { return pgtype.Timestamptz{} }

// accountRow builds a single row matching accountColumns, plus the insert-only
// `created` flag when withFlag is set.
func accountRow(withFlag bool, id, openID string) *pgxmock.Rows {
	cols := []string{
		"id", "open_id", "union_id", "nickname", "avatar_url", "plan",
		"install_id", "wechat_bound", "created_at", "last_login_at",
	}
	vals := []any{
		id, openID, "union-1", "Nick", "https://avatar.invalid/a.png", store.PlanFree,
		"install-1", false, testExpired, testNow,
	}
	if withFlag {
		cols = append(cols, "created")
		vals = append(vals, true)
	}
	return pgxmock.NewRows(cols).AddRow(vals...)
}

// activationRequestRow builds a row matching activationRequestColumns. A
// non-pending status carries the licence and the confirmation instant the
// schema's CHECK requires; a nil amount is a buyer who did not say.
func activationRequestRow(status string, amount *int64) *pgxmock.Rows {
	var (
		amountValue any
		licenseID   string
		confirmed   any = nullTimeValue()
	)
	if amount != nil {
		amountValue = *amount
	}
	if store.ActivationStatus(status) != store.ActivationPending {
		licenseID = "lic-1"
		confirmed = testLater
	}
	return pgxmock.NewRows([]string{
		"request_id", "account_id", "install_id", "anchor_digest", "amount_fen",
		"status", "envelope_json", "license_id", "created_at", "confirmed_at",
	}).AddRow(
		"req-1", "acct-1", "install-1", "anchor-1", amountValue,
		store.ActivationStatus(status), []byte(`{"licenseId":"lic-1"}`), licenseID,
		testNow, confirmed,
	)
}

// activationRequestViewRow is activationRequestRow plus the two account
// columns the console's list joins in. The column list is repeated rather
// than derived for the same reason the scanners repeat it: pgx has consumed
// the row by the time the base builder returns.
func activationRequestViewRow(status string, amount *int64) *pgxmock.Rows {
	var (
		amountValue any
		licenseID   string
		confirmed   any = nullTimeValue()
	)
	if amount != nil {
		amountValue = *amount
	}
	if store.ActivationStatus(status) != store.ActivationPending {
		licenseID = "lic-1"
		confirmed = testLater
	}
	return pgxmock.NewRows([]string{
		"request_id", "account_id", "install_id", "anchor_digest", "amount_fen",
		"status", "envelope_json", "license_id", "created_at", "confirmed_at",
		"nickname", "avatar_url",
	}).AddRow(
		"req-1", "acct-1", "install-1", "anchor-1", amountValue,
		store.ActivationStatus(status), []byte(`{"licenseId":"lic-1"}`), licenseID,
		testNow, confirmed, "Nick", "https://avatar.invalid/a.png",
	)
}

// licenceBindingRow builds a row matching licenceBindingColumns.
func licenceBindingRow(licenseID, installID, accountID string, revoked bool) *pgxmock.Rows {
	revokedValue := nullTimeValue()
	if revoked {
		revokedValue = pgtype.Timestamptz{Time: testLater, Valid: true}
	}
	return pgxmock.NewRows([]string{
		"license_id", "install_id", "account_id", "request_id", "issued_at", "revoked_at",
	}).AddRow(licenseID, installID, accountID, "req-1", testNow, revokedValue)
}

// licenceBindingViewRow is licenceBindingRow plus the two account columns the
// ledger's join brings along. As with the request view, the column list is
// repeated rather than derived: pgx has consumed the row by the time the base
// builder returns.
func licenceBindingViewRow(licenseID, installID, accountID string, revoked bool) *pgxmock.Rows {
	revokedValue := nullTimeValue()
	if revoked {
		revokedValue = pgtype.Timestamptz{Time: testLater, Valid: true}
	}
	return pgxmock.NewRows([]string{
		"license_id", "install_id", "account_id", "request_id", "issued_at", "revoked_at",
		"nickname", "open_id",
	}).AddRow(licenseID, installID, accountID, "req-1", testNow, revokedValue, "Nick", "open-1")
}

// fenPtr is how a test says "the buyer said an amount". A nil *int64 is the
// buyer who did not, and the two must stay distinguishable: the schema's
// column is nullable and the scanner keeps it a pointer for exactly that
// reason.
func fenPtr(v int64) *int64 { return &v }

// noRows is the driver's "there was nothing there", which this package turns
// into store.ErrNotFound.
var noRows = pgx.ErrNoRows

// anyArgs builds a WithArgs list of n wildcards. pgxmock treats an expectation
// with no WithArgs as expecting zero arguments, so every expectation has to
// state its arity — which is a small bonus: a query that grows or loses a
// parameter fails here rather than at runtime.
func anyArgs(n int) []any {
	out := make([]any, n)
	for i := range out {
		out[i] = pgxmock.AnyArg()
	}
	return out
}

// norm collapses whitespace so the SQL assertions below do not depend on the
// alignment gofmt imposes inside the query constants.
func norm(s string) string { return strings.Join(strings.Fields(s), " ") }
