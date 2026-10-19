package sqlite

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// The sign-in path is the one every buyer passes through, so these tests
// drive the real database rather than a mock: the `created` flag below is
// computed by SQLite's upsert, not typed into a fixture by the test.

func TestCompleteAuthSessionIsOneTransaction(t *testing.T) {
	ctx := context.Background()
	s := newTestStore(t)

	seedAuthSession(t, s, "ticket-1", "install-1", testLater)

	acct, err := s.CompleteAuthSession(ctx, "ticket-1", "open-1", "union-1", "Nick", "avatar", "install-1")
	if err != nil {
		t.Fatalf("CompleteAuthSession: %v", err)
	}
	if acct.ID == "" {
		t.Fatal("the account came back without an id")
	}
	if acct.OpenID != "open-1" {
		t.Fatalf("open id = %q, want open-1", acct.OpenID)
	}
	if acct.Plan != store.PlanFree {
		t.Fatalf("a new account must start on the free plan, got %q", acct.Plan)
	}

	// The ticket is spent and names the account — both halves of the one
	// fact, readable back through the public API.
	sess, err := s.GetAuthSession(ctx, "ticket-1")
	if err != nil {
		t.Fatalf("GetAuthSession: %v", err)
	}
	if sess.State != store.AuthAuthorized {
		t.Fatalf("ticket state = %q, want authorized", sess.State)
	}
	if sess.AccountID != acct.ID {
		t.Fatalf("ticket names account %q, want %q", sess.AccountID, acct.ID)
	}
}

func TestCompleteAuthSessionRollsBackOnASpentTicket(t *testing.T) {
	ctx := context.Background()
	s := newTestStore(t)

	seedAuthSession(t, s, "ticket-1", "install-1", testLater)
	if _, err := s.CompleteAuthSession(ctx, "ticket-1", "open-1", "", "Nick", "avatar", "install-1"); err != nil {
		t.Fatalf("first completion: %v", err)
	}

	// A ticket that is already authorized: the row is there, so this is a
	// conflict, not a miss. Nothing is written.
	_, err := s.CompleteAuthSession(ctx, "ticket-1", "open-1", "", "Nick", "avatar", "install-1")
	if !errors.Is(err, store.ErrConflict) {
		t.Fatalf("want store.ErrConflict, got %v", err)
	}

	// The second attempt created nothing: one account, one session row.
	var accounts int
	if err := s.db.QueryRowContext(ctx, `SELECT count(*) FROM accounts`).Scan(&accounts); err != nil {
		t.Fatalf("count accounts: %v", err)
	}
	if accounts != 1 {
		t.Fatalf("the refused completion wrote an account: %d rows", accounts)
	}
}

func TestCompleteAuthSessionUnknownTicketIsAMiss(t *testing.T) {
	s := newTestStore(t)
	if _, err := s.CompleteAuthSession(context.Background(), "ticket-x", "open-1", "", "Nick", "avatar", ""); !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("want store.ErrNotFound, got %v", err)
	}
}

func TestCompleteAuthSessionRejectsAnExpiredTicket(t *testing.T) {
	ctx := context.Background()
	s := newTestStore(t)

	// A QR code left open past its TTL: the row is there but the window is
	// gone, and completing it would authorize a session nobody is watching.
	seedAuthSession(t, s, "ticket-1", "install-1", testExpired)

	if _, err := s.CompleteAuthSession(ctx, "ticket-1", "open-1", "", "Nick", "avatar", "install-1"); !errors.Is(err, store.ErrConflict) {
		t.Fatalf("want store.ErrConflict, got %v", err)
	}
	var accounts int
	if err := s.db.QueryRowContext(ctx, `SELECT count(*) FROM accounts`).Scan(&accounts); err != nil {
		t.Fatalf("count accounts: %v", err)
	}
	if accounts != 0 {
		t.Fatalf("an expired ticket produced an account: %d rows", accounts)
	}
}

func TestCompleteAuthSessionUsesTheSessionsInstallID(t *testing.T) {
	ctx := context.Background()
	s := newTestStore(t)

	// The ticket remembers which anonymous identity started the sign-in. A
	// caller that supplies no install id must not be allowed to sweep up
	// somebody else's machine, so the session's own value is used — it rides
	// into the account upsert rather than a table that no longer exists.
	seedAuthSession(t, s, "ticket-1", "install-from-session", testLater)

	acct, err := s.CompleteAuthSession(ctx, "ticket-1", "open-1", "", "Nick", "avatar", "")
	if err != nil {
		t.Fatalf("CompleteAuthSession: %v", err)
	}
	if acct.InstallID != "install-from-session" {
		t.Fatalf("install id = %q, want the session's install-from-session", acct.InstallID)
	}
}

// TestUpsertAccountByOpenIDReportsFirstSignIn is the assertion the design doc
// asks for by name: the `created` flag has to be computed by the database,
// not handed to the scanner by a fixture. The PostgreSQL suite fed the mock a
// row that already contained the answer, so it proved the column was scanned
// and nothing about whether the value was right.
func TestUpsertAccountByOpenIDReportsFirstSignIn(t *testing.T) {
	ctx := context.Background()

	t.Run("a new open id is a first sign-in", func(t *testing.T) {
		s := newTestStore(t)
		acct, created, err := s.UpsertAccountByOpenID(ctx, "open-1", "union-1", "Nick", "avatar")
		if err != nil {
			t.Fatalf("UpsertAccountByOpenID: %v", err)
		}
		if !created {
			t.Fatal("want created=true for a first sign-in")
		}
		if acct.Plan != store.PlanFree {
			t.Fatalf("want a new account on the free plan, got %q", acct.Plan)
		}
	})

	t.Run("a returning open id is not a first sign-in", func(t *testing.T) {
		s := newTestStore(t)
		first, created, err := s.UpsertAccountByOpenID(ctx, "open-1", "union-1", "Nick", "avatar")
		if err != nil || !created {
			t.Fatalf("first sign-in: created=%v err=%v", created, err)
		}
		second, created, err := s.UpsertAccountByOpenID(ctx, "open-1", "union-1", "Nick", "avatar")
		if err != nil {
			t.Fatalf("return visit: %v", err)
		}
		if created {
			t.Fatal("want created=false for a return visit")
		}
		if second.ID != first.ID {
			t.Fatalf("the return visit resolved to account %q, want the same %q", second.ID, first.ID)
		}
	})

	t.Run("an empty open id never reaches the database", func(t *testing.T) {
		// "" would collide with every other "" on the unique index and hand
		// the caller somebody else's account.
		s := newTestStore(t)
		if _, _, err := s.UpsertAccountByOpenID(ctx, "", "u", "Nick", "avatar"); err == nil {
			t.Fatal("expected a rejection for an empty open id")
		}
		var n int
		if err := s.db.QueryRowContext(ctx, `SELECT count(*) FROM accounts`).Scan(&n); err != nil {
			t.Fatalf("count: %v", err)
		}
		if n != 0 {
			t.Fatalf("a query was issued for an empty open id: %d accounts", n)
		}
	})
}

// TestUpsertAccountByOpenIDKeepsThePlanOnReturnVisit is the non-demotion
// guarantee as behaviour rather than as SQL text: signing in again must not
// hand a paying customer back the free tier. The old suite could only assert
// the word "plan" was absent from the DO UPDATE clause.
func TestUpsertAccountByOpenIDKeepsThePlanOnReturnVisit(t *testing.T) {
	ctx := context.Background()
	s := newTestStore(t)

	acct, _, err := s.UpsertAccountByOpenID(ctx, "open-1", "union-1", "Nick", "avatar")
	if err != nil {
		t.Fatalf("sign in: %v", err)
	}
	// The upgrade path the payment flow used to own; the store never writes
	// plan, so the test stands in for whoever did.
	execRaw(t, s.db, `UPDATE accounts SET plan = 'buyout' WHERE id = $1`, acct.ID)

	returned, _, err := s.UpsertAccountByOpenID(ctx, "open-1", "union-1", "Nick2", "avatar2")
	if err != nil {
		t.Fatalf("return visit: %v", err)
	}
	if returned.Plan != store.PlanBuyout {
		t.Fatalf("signing in demoted the account to %q", returned.Plan)
	}
}

func TestGetAuthSessionFoldsExpiryIntoState(t *testing.T) {
	ctx := context.Background()

	t.Run("a live pending ticket reads as pending", func(t *testing.T) {
		s := newTestStore(t)
		seedAuthSession(t, s, "ticket-1", "install-1", testLater)

		sess, err := s.GetAuthSession(ctx, "ticket-1")
		if err != nil {
			t.Fatalf("GetAuthSession: %v", err)
		}
		if sess.State != store.AuthPending {
			t.Fatalf("state = %q, want pending", sess.State)
		}
		if sess.ExpiresAt.Location() != time.UTC {
			t.Fatalf("timestamps must come back in UTC, got %s", sess.ExpiresAt.Location())
		}
	})

	t.Run("an expired-state ticket reads as expired", func(t *testing.T) {
		s := newTestStore(t)
		seedAuthSession(t, s, "ticket-1", "install-1", testLater)
		// The sweeper's write; the store has no verb for it.
		execRaw(t, s.db, `UPDATE auth_sessions SET state = 'expired' WHERE ticket = 'ticket-1'`)

		sess, err := s.GetAuthSession(ctx, "ticket-1")
		if err != nil {
			t.Fatalf("GetAuthSession: %v", err)
		}
		if sess.State != store.AuthExpired {
			t.Fatalf("state = %q, want expired", sess.State)
		}
	})

	t.Run("an unknown ticket is a miss", func(t *testing.T) {
		s := newTestStore(t)
		if _, err := s.GetAuthSession(ctx, "ticket-x"); !errors.Is(err, store.ErrNotFound) {
			t.Fatalf("want store.ErrNotFound, got %v", err)
		}
	})
}

func TestAuthStateTreatsUnknownAsExpired(t *testing.T) {
	// The client's enum is fixed. A value from a future version is a question
	// this server cannot answer, and the safe wrong answer is "come back".
	if got := authState("pending", testLater, testNow); got != store.AuthPending {
		t.Fatalf("pending was not preserved: %q", got)
	}
	if got := authState("something-new", testLater, testNow); got != store.AuthExpired {
		t.Fatalf("an unknown state must read as expired, got %q", got)
	}
	if got := authState("", testLater, testNow); got != store.AuthExpired {
		t.Fatalf("an empty state must read as expired, got %q", got)
	}
}

// seedSession installs the token chain the rotation tests read: one account
// carrying a live refresh hash and the expiry the replacement will inherit.
// The previous generation is left empty on purpose — a chain that has already
// rotated is produced by rotating, not by seeding.
func seedSession(t *testing.T, s *Store, accountID, chainID, liveHash string, expiresAt time.Time) {
	t.Helper()
	if err := s.CreateSession(context.Background(), store.Session{
		AccountID:        accountID,
		RefreshTokenHash: liveHash,
		RefreshExpiresAt: expiresAt,
		ChainID:          chainID,
	}); err != nil {
		t.Fatalf("seed session for %s: %v", accountID, err)
	}
}

func TestRotateRefreshRotates(t *testing.T) {
	ctx := context.Background()
	s := newTestStore(t)

	acct := seedAccount(t, s, "open-1")
	seedSession(t, s, acct.ID, "chain-1", "old-hash", testLater)

	sess, outcome, err := s.RotateRefresh(ctx, "old-hash", "new-hash", "new-access", testLater)
	if err != nil {
		t.Fatalf("RotateRefresh: %v", err)
	}
	if outcome != store.RefreshRotated {
		t.Fatalf("want rotated, got %q", outcome)
	}
	if sess.RefreshTokenHash != "new-hash" {
		t.Fatalf("the new refresh token was not returned: %+v", sess)
	}
	if sess.ChainID != "chain-1" {
		t.Fatalf("the chain must survive rotation, got %q", sess.ChainID)
	}

	// The database agrees: the new hash resolves, and the old one still does
	// — as the previous generation, which is what lets a replay reach the
	// revocation path instead of looking like an unknown token.
	if _, err := s.GetSessionByRefreshHash(ctx, "new-hash"); err != nil {
		t.Fatalf("the new hash does not resolve: %v", err)
	}
	old, err := s.GetSessionByRefreshHash(ctx, "old-hash")
	if err != nil {
		t.Fatalf("the replaced hash no longer resolves: %v", err)
	}
	if old.RefreshTokenHash != "old-hash" {
		t.Fatalf("the replaced hash must be reported as presented, got %q", old.RefreshTokenHash)
	}
}

func TestRotateRefreshDetectsReuse(t *testing.T) {
	ctx := context.Background()
	s := newTestStore(t)

	acct := seedAccount(t, s, "open-1")
	seedSession(t, s, acct.ID, "chain-1", "old-hash", testLater)
	if _, _, err := s.RotateRefresh(ctx, "old-hash", "newer-hash", "new-access", testLater); err != nil {
		t.Fatalf("first rotation: %v", err)
	}

	// The token was already spent: it is the generation this chain replaced,
	// so the live hash is one step ahead of the presented one. This is the
	// signal that the chain is being replayed, and the caller's response is
	// to kill the family — which is exactly why it cannot be folded into a
	// generic conflict.
	_, outcome, err := s.RotateRefresh(ctx, "old-hash", "newest-hash", "new-access", testLater)
	if !errors.Is(err, store.ErrReuseDetected) {
		t.Fatalf("want store.ErrReuseDetected, got %v", err)
	}
	if errors.Is(err, store.ErrConflict) {
		t.Fatal("reuse must be its own error, not a flavour of conflict")
	}
	if outcome != store.RefreshExpired {
		t.Fatalf("want expired, got %q", outcome)
	}

	// And the replay wrote nothing: the live hash is still the winner's.
	live, err := s.GetSessionByRefreshHash(ctx, "newer-hash")
	if err != nil {
		t.Fatalf("the winner's token vanished: %v", err)
	}
	if live.RevokedAt != nil {
		t.Fatal("the chain was revoked by the replay itself")
	}
}

func TestRotateRefreshOnARevokedChain(t *testing.T) {
	ctx := context.Background()
	s := newTestStore(t)

	acct := seedAccount(t, s, "open-1")
	seedSession(t, s, acct.ID, "chain-1", "old-hash", testLater)
	if err := s.RevokeChain(ctx, "chain-1", testNow); err != nil {
		t.Fatalf("RevokeChain: %v", err)
	}

	_, _, err := s.RotateRefresh(ctx, "old-hash", "new-hash", "new-access", testLater)
	if !errors.Is(err, store.ErrConflict) {
		t.Fatalf("want store.ErrConflict for a revoked chain, got %v", err)
	}
}

func TestRotateRefreshOnAnExpiredToken(t *testing.T) {
	ctx := context.Background()
	s := newTestStore(t)

	acct := seedAccount(t, s, "open-1")
	seedSession(t, s, acct.ID, "chain-1", "old-hash", testExpired)

	// Expiry is an answer, not a failure: the client is told to sign in again
	// and nothing is written.
	sess, outcome, err := s.RotateRefresh(ctx, "old-hash", "new-hash", "new-access", testLater)
	if err != nil {
		t.Fatalf("an expired token is not an error: %v", err)
	}
	if outcome != store.RefreshExpired {
		t.Fatalf("want expired, got %q", outcome)
	}
	if sess.RefreshTokenHash != "old-hash" {
		t.Fatalf("the expired token must be reported as presented, got %q", sess.RefreshTokenHash)
	}

	// The stored hash is untouched, so the expired token stays the expired
	// token rather than quietly becoming a live one.
	stored, err := s.GetSessionByRefreshHash(ctx, "old-hash")
	if err != nil {
		t.Fatalf("GetSessionByRefreshHash: %v", err)
	}
	if !stored.RefreshExpiresAt.Before(time.Now()) {
		t.Fatalf("the expiry moved: %v", stored.RefreshExpiresAt)
	}
}

func TestRotateRefreshOnAnUnknownToken(t *testing.T) {
	s := newTestStore(t)
	_, _, err := s.RotateRefresh(context.Background(), "never-issued", "new-hash", "new-access", testLater)
	if !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("want store.ErrNotFound, got %v", err)
	}
}

func TestRotateRefreshRejectsEmptyHashes(t *testing.T) {
	s := newTestStore(t)
	_, outcome, err := s.RotateRefresh(context.Background(), "", "new-hash", "new-access", testLater)
	if err == nil {
		t.Fatal("an empty presented hash was accepted")
	}
	if outcome != store.RefreshExpired {
		t.Fatalf("the failure outcome must be the safe one, got %q", outcome)
	}
}

func TestRevokeChainCoversTheFamily(t *testing.T) {
	ctx := context.Background()
	s := newTestStore(t)

	acct := seedAccount(t, s, "open-1")
	seedSession(t, s, acct.ID, "chain-1", "hash-1", testLater)

	if err := s.RevokeChain(ctx, "chain-1", testNow); err != nil {
		t.Fatalf("RevokeChain: %v", err)
	}

	sess, err := s.GetSessionByRefreshHash(ctx, "hash-1")
	if err != nil {
		t.Fatalf("GetSessionByRefreshHash: %v", err)
	}
	if sess.RevokedAt == nil {
		t.Fatal("the chain was not revoked")
	}

	// Revoking twice is idempotent: the second call finds nothing live and
	// succeeds anyway.
	if err := s.RevokeChain(ctx, "chain-1", testNow); err != nil {
		t.Fatalf("second RevokeChain: %v", err)
	}

	// And a rotation on the revoked family is a conflict, which is the
	// caller's cue to clear the local session.
	if _, _, err := s.RotateRefresh(ctx, "hash-1", "new-hash", "new-access", testLater); !errors.Is(err, store.ErrConflict) {
		t.Fatalf("want store.ErrConflict rotating a revoked chain, got %v", err)
	}
}

func TestCreateSessionRejectsAnUnknownAccount(t *testing.T) {
	// The caller minted tokens for an account that does not exist, which is
	// a bug and not a race.
	s := newTestStore(t)
	err := s.CreateSession(context.Background(), store.Session{
		AccountID:        "acct-does-not-exist",
		RefreshTokenHash: "hash-1",
		RefreshExpiresAt: testLater,
		ChainID:          "chain-1",
	})
	if !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("want store.ErrNotFound, got %v", err)
	}
}

// TestTimestampsRoundTripUTCAndCompareAcrossFormats covers R-new: the
// timestamp columns can hold two literal shapes — the driver's
// "2006-01-02 15:04:05.999999999 +0000 UTC" for bound values and
// CURRENT_TIMESTAMP's bare "2006-01-02 15:04:05" for defaults. Both are UTC,
// so lexicographic order matches time order whenever the two rows sit in
// different seconds, which is what this test measures.
//
// The same-second edge is real and deliberately not papered over: a bare
// value is a prefix of any driver value in the same second, so it sorts
// first — correct for a default written at the top of that second, wrong for
// one written at its end. That is why the write path binds every instant the
// application writes (see the upsert's comment and the design doc's
// mitigation (b)), and why the times below are a year apart rather than a
// millisecond.
func TestTimestampsRoundTripUTCAndCompareAcrossFormats(t *testing.T) {
	ctx := context.Background()
	s := newTestStore(t)

	// A row the application never wrote: created_at comes from the DEFAULT.
	execRaw(t, s.db, `INSERT INTO accounts (open_id) VALUES ('open-default')`)
	// A row written through the store, then moved to a known instant in the
	// driver's shape.
	bound := seedAccount(t, s, "open-bound")
	execRaw(t, s.db, `UPDATE accounts SET created_at = $1 WHERE id = $2`, testExpired, bound.ID)

	// A range query whose parameter is driver-shaped, against a column that
	// also holds a default-shaped value: both rows are in range.
	var n int
	if err := s.db.QueryRowContext(ctx,
		`SELECT count(*) FROM accounts WHERE created_at >= $1`, testExpired).Scan(&n); err != nil {
		t.Fatalf("range query: %v", err)
	}
	if n != 2 {
		t.Fatalf("range query returned %d rows, want 2: the formats do not compare", n)
	}

	// Ordering across formats: the bound row (a year ago) sorts first.
	var first string
	if err := s.db.QueryRowContext(ctx,
		`SELECT id FROM accounts ORDER BY created_at ASC LIMIT 1`).Scan(&first); err != nil {
		t.Fatalf("ordering query: %v", err)
	}
	if first != bound.ID {
		t.Fatalf("ordering put %q first, want %q", first, bound.ID)
	}

	// And the driver's literal round-trips as the instant it was, in UTC.
	var got time.Time
	if err := s.db.QueryRowContext(ctx,
		`SELECT created_at FROM accounts WHERE id = $1`, bound.ID).Scan(&got); err != nil {
		t.Fatalf("scan created_at: %v", err)
	}
	if got.Location() != time.UTC {
		t.Fatalf("created_at came back in %s, want UTC", got.Location())
	}
	if !got.Equal(testExpired) {
		t.Fatalf("created_at = %v, want %v", got, testExpired)
	}
}
