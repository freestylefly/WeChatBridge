package sqlite

import (
	"database/sql"
	"errors"
	"fmt"
	"strings"

	sqlitedriver "modernc.org/sqlite"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// Constraint identities the application reasons about.
//
// PostgreSQL named its constraints and the driver handed the name back in a
// dedicated field; SQLite has no constraint names to give — the error message
// names the table and the column instead. These constants are therefore
// (table, column) pairs, and they are created in
// migrations/0001_init.up.sql; renaming a column there means renaming it
// here, and a mismatch is a silent loss of error classification rather than
// a build failure, which is why they live in one place.
//
// The orders, redeem_codes and refresh_tokens names are gone with the
// payment rewrite; what is left is the sign-in path and the buy-out path.
const (
	constraintActivationRequestPK = "activation_requests.request_id"
	constraintAuthSessionTicket   = "auth_sessions.ticket"
	constraintAccountOpenID       = "accounts.open_id"
)

// bindingConflictColumns is the closed whitelist of licence_bindings columns
// whose unique violation means "somebody else got there first": the licence
// written twice (primary key), a second live binding on one machine, and a
// second live binding on one account. All three are the seller's to resolve.
//
// This list is deliberately closed — an allow-list, not a pattern. The
// PostgreSQL version asked "is this the constraint I named?"; SQLite cannot
// answer that question, because one INSERT exposes at most one violated
// constraint even when two were violated, and because the column is not the
// constraint's identity (see the failure mode below). So the only honest
// question left is "is this one of the three collisions this store means by
// a binding conflict", and anything else must keep its own identity: a code
// path that answers "somebody beat you to it" when the real problem is a
// corrupted data file is worse than no classification at all, because it
// hides the failure behind a 409 that reads like normal traffic.
var bindingConflictColumns = map[string]bool{
	"licence_bindings.license_id": true,
	"licence_bindings.install_id": true,
	"licence_bindings.account_id": true,
}

// SQLite extended result codes this package distinguishes. The driver turns
// extended codes on for every connection, so Error.Code() carries the
// extended value: 2067 is SQLITE_CONSTRAINT_UNIQUE and 1555 is
// SQLITE_CONSTRAINT_PRIMARYKEY (both share primary code 19, which is why the
// extended form is the one worth reading).
//
// The two codes separate "primary key" from "unique index" but nothing
// finer — two different unique indexes on the same column report the same
// string. That is the boundary of what classification can promise here.
const (
	sqliteConstraintUnique     = 2067
	sqliteConstraintPrimaryKey = 1555
)

// Failure mode, written down because it produces no compile error and no
// test failure: the whitelist's keys are column names, and a column name is
// not a constraint's identity. Today licence_bindings.account_id carries two
// indexes — the unique partial one above and a plain non-unique one beside
// it — and only the unique one can produce this error. Add a UNIQUE index
// on an already-whitelisted column and two different constraints collapse
// into the same error string: the mapping loses its ability to tell them
// apart, silently. Adding an index is therefore not the safe operation it
// was under PostgreSQL, where an unrecognised constraint name fell through
// to the default branch. Renaming a column stays safe — it fails loudly,
// as a missing whitelist entry.

// notFound turns the driver's "no rows" into the store's miss.
//
// It is the only place sql.ErrNoRows is matched. Everything else — a locked
// database, a corrupt file, a syntax error — keeps its own identity, because
// "the database is unhappy" and "that request does not exist" are answers
// that must never be confused: the first is a page from the operator, the
// second is an HTTP 404.
func notFound(op string, err error) error {
	if errors.Is(err, sql.ErrNoRows) {
		return store.ErrNotFound
	}
	return wrap(op, err)
}

// conflictWhenUnchanged maps the outcome of a conditional UPDATE.
//
// Zero rows does not mean "nothing happened" here — it means the row is not in
// the state the caller required, which is a different answer from "no such
// row" and the caller is the only party that knows which of the two it is
// looking at. It returns ErrConflict, and callers that need to tell the two
// apart run a follow-up read first (see ConfirmActivationRequest).
func conflictWhenUnchanged(op string, err error, affected int64) error {
	if err != nil {
		return wrap(op, err)
	}
	if affected == 0 {
		return store.ErrConflict
	}
	return nil
}

// uniqueConflict classifies a unique-constraint violation that the caller has
// declared meaningful — "another sign-in already created this account".
//
// The named target is a (table, column) pair. Only a violation on that
// target becomes ErrConflict; a violation anywhere else, and every other
// driver error, comes back wrapped and intact.
//
// One PostgreSQL guarantee does not survive the move and is priced in here:
// a single INSERT exposes at most one violated constraint, so when two were
// violated the one SQLite reports may not be the one this call named. The
// whitelist answers for what it can see; nothing can reconstruct what it
// cannot.
func uniqueConflict(op, constraint string, err error) error {
	targets, ok := uniqueViolation(err)
	if !ok {
		return wrap(op, err)
	}
	for _, t := range targets {
		if t == constraint {
			return store.ErrConflict
		}
	}
	return wrap(op, fmt.Errorf("unique violation on %s: %w", strings.Join(targets, ", "), err))
}

// bindingConflict classifies the three ways a licence binding insert can
// collide: the same licence id written twice, or a second live binding for one
// machine or for one account. All three mean the same thing to the caller —
// somebody else got there first — and all three are the seller's to resolve,
// because the two partial unique indexes are the machine-change policy in
// schema form.
func bindingConflict(op string, err error) error {
	targets, ok := uniqueViolation(err)
	if !ok {
		return wrap(op, err)
	}
	for _, t := range targets {
		if bindingConflictColumns[t] {
			return store.ErrConflict
		}
	}
	return wrap(op, fmt.Errorf("unique violation on %s: %w", strings.Join(targets, ", "), err))
}

// uniqueViolation reports the (table, column) pairs a unique-constraint
// violation names, if the error is one at all.
//
// The message shape is the driver's: "constraint failed: UNIQUE constraint
// failed: licence_bindings.license_id (1555)". A multi-column index lists
// every column, comma separated, and any of them matching is a match — the
// violation is on the index, not on one column of it.
//
// Errors that share the primary code but are not unique violations — NOT
// NULL (1299), CHECK (275), FOREIGN KEY (787) — do not carry the "UNIQUE
// constraint failed" text and are rejected here, which is what keeps a
// check-constraint failure from ever reading as a conflict.
func uniqueViolation(err error) ([]string, bool) {
	var sqliteErr *sqlitedriver.Error
	if !errors.As(err, &sqliteErr) {
		return nil, false
	}
	if code := sqliteErr.Code(); code != sqliteConstraintUnique && code != sqliteConstraintPrimaryKey {
		return nil, false
	}

	const marker = "UNIQUE constraint failed: "
	i := strings.Index(sqliteErr.Error(), marker)
	if i < 0 {
		return nil, false
	}
	rest := sqliteErr.Error()[i+len(marker):]
	// The trailing " (1555)" is the extended code, already read off the
	// error itself; it is not part of the column list.
	if j := strings.Index(rest, " ("); j >= 0 {
		rest = rest[:j]
	}

	var targets []string
	for _, part := range strings.Split(rest, ", ") {
		if t := strings.TrimSpace(part); t != "" {
			targets = append(targets, t)
		}
	}
	if len(targets) == 0 {
		return nil, false
	}
	return targets, true
}

// wrap adds the operation's identity without ever adding a value. Every driver
// error that leaves this package goes through here, so no raw driver error
// reaches the HTTP layer unwrapped.
func wrap(op string, err error) error {
	if err == nil {
		return nil
	}
	if errors.Is(err, sql.ErrNoRows) {
		// Reaching here means a caller used wrap on a read that expected a row;
		// turning it into a miss here keeps that mistake from surfacing as a
		// 500 with a driver message.
		return store.ErrNotFound
	}
	return fmt.Errorf("sqlite: %s: %w", op, err)
}
