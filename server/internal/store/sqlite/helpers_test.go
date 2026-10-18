package sqlite

import (
	"context"
	"database/sql"
	"path/filepath"
	"testing"
	"time"

	"github.com/freestylefly/WeChatBridge/server/internal/migrate"
	"github.com/freestylefly/WeChatBridge/server/internal/store"
	"github.com/freestylefly/WeChatBridge/server/migrations"
)

// These tests drive the whole store against a real SQLite database on a
// scratch file.
//
// They replace a suite that ran the same code against pgxmock: every query
// was matched by regexp against a hand-fed row, which proved the SQL text
// looked right and nothing about what the database would answer. The
// migration to SQLite made the real thing cheaper than the fake — a scratch
// file is opened in microseconds and needs no server — so the fake is gone
// and the assertions below are about behaviour, not about text.
//
// What this buys concretely: the `created` flag on the sign-in upsert is now
// computed by SQLite rather than typed into a fixture, the constraint
// classification is exercised against real driver errors, and the
// transaction boundaries are proven by what survives a rollback.

// newTestStore opens a scratch database, applies the real embedded schema,
// and wraps it in the Store under test.
//
// A file rather than :memory: because the pool hands out one connection and
// holds it; a memory database is per-connection state, and the day the pool
// grows to two, every test after the first would quietly find an empty
// schema.
func newTestStore(t *testing.T) *Store {
	t.Helper()
	db := newTestDB(t)
	return &Store{db: db}
}

// newTestDB is newTestStore's database half, for the tests that need to reach
// past the Store's API — seeding a state the API cannot produce, or asserting
// on one it does not expose.
func newTestDB(t *testing.T) *sql.DB {
	t.Helper()
	path := filepath.Join(t.TempDir(), "store-test.db")
	db, err := Open(context.Background(), path)
	if err != nil {
		t.Fatalf("open scratch database: %v", err)
	}
	if _, err := migrate.From(context.Background(), db, migrations.FS); err != nil {
		_ = db.Close()
		t.Fatalf("apply embedded schema: %v", err)
	}
	t.Cleanup(func() { _ = db.Close() })
	return db
}

// The clock is anchored to now rather than to a fixed date, because the store
// compares the caller's instants against the real one: a test fixture from a
// fixed date stops meaning "not yet expired" on the day it passes.
var (
	testNow     = time.Now().UTC().Truncate(time.Second)
	testLater   = testNow.Add(time.Hour)
	testExpired = testNow.Add(-365 * 24 * time.Hour)
)

// seedAccount signs an account in and returns it. The id is the database's,
// not the test's: the whole point of exercising the real upsert.
func seedAccount(t *testing.T, s *Store, openID string) store.Account {
	t.Helper()
	acct, _, err := s.UpsertAccountByOpenID(context.Background(), openID, "union-"+openID, "Nick", "https://avatar.invalid/a.png")
	if err != nil {
		t.Fatalf("seed account %s: %v", openID, err)
	}
	return acct
}

// seedAuthSession files a pending QR session.
func seedAuthSession(t *testing.T, s *Store, ticket, installID string, expiresAt time.Time) store.AuthSession {
	t.Helper()
	sess := store.AuthSession{
		Ticket:       ticket,
		State:        store.AuthPending,
		InstallID:    installID,
		AuthorizeURL: "https://open.weixin.qq.com/connect/qrconnect",
		ExpiresAt:    expiresAt,
		CreatedAt:    testNow,
	}
	if err := s.CreateAuthSession(context.Background(), sess); err != nil {
		t.Fatalf("seed auth session %s: %v", ticket, err)
	}
	return sess
}

// seedActivationRequest files a registration the way the buyer's client does.
func seedActivationRequest(t *testing.T, s *Store, requestID, accountID, installID string, amount *int64) store.ActivationRequest {
	t.Helper()
	r, err := s.CreateActivationRequest(context.Background(), store.ActivationRequest{
		RequestID:    requestID,
		AccountID:    accountID,
		InstallID:    installID,
		AnchorDigest: "anchor-" + requestID,
		AmountFen:    amount,
		CreatedAt:    testNow,
	})
	if err != nil {
		t.Fatalf("seed activation request %s: %v", requestID, err)
	}
	return r
}

// seedLiveBinding confirms a request, which installs its binding — the state
// a reissue and a second-press conflict both start from.
func seedLiveBinding(t *testing.T, s *Store, requestID, licenseID string) store.ActivationRequest {
	t.Helper()
	r, err := s.ConfirmActivationRequest(context.Background(), requestID, licenseID, []byte(`{"licenseId":"`+licenseID+`"}`), testLater)
	if err != nil {
		t.Fatalf("seed live binding for %s: %v", requestID, err)
	}
	return r
}

// fenPtr is how a test says "the buyer said an amount". A nil *int64 is the
// buyer who did not, and the two must stay distinguishable: the schema's
// column is nullable and the scanner keeps it a pointer for exactly that
// reason.
func fenPtr(v int64) *int64 { return &v }

// execRaw runs a statement the Store's API has no verb for — seeding a state
// the product cannot produce (a plan upgrade, a revoked row) or asserting on
// one it does not expose (the literal text a timestamp was stored as).
func execRaw(t *testing.T, db *sql.DB, query string, args ...any) {
	t.Helper()
	if _, err := db.Exec(query, args...); err != nil {
		t.Fatalf("exec %q: %v", query, err)
	}
}

// queryRaw is execRaw's reading counterpart.
func queryRaw(t *testing.T, db *sql.DB, query string, args ...any) *sql.Row {
	t.Helper()
	return db.QueryRow(query, args...)
}
