package postgres

import (
	"context"
	"errors"
	"regexp"
	"testing"
	"time"

	"github.com/jackc/pgx/v5/pgtype"
	"github.com/pashagolub/pgxmock/v3"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

func TestCompleteAuthSessionIsOneTransaction(t *testing.T) {
	ctx := context.Background()
	s, mock := newMock(t)

	// The order of these expectations is the transaction: the ticket is locked,
	// the account is resolved, the ticket is marked, and only then does the
	// commit happen. A regression that drops the account resolution, or moves
	// it after the commit, fails this test rather than a customer's afternoon.
	mock.ExpectBegin()
	mock.ExpectQuery(regexp.QuoteMeta("FROM auth_sessions")).
		WithArgs(anyArgs(1)...).
		WillReturnRows(pgxmock.NewRows([]string{"state", "install_id", "expires_at"}).
			AddRow("pending", "install-1", testLater))
	mock.ExpectQuery(regexp.QuoteMeta("INSERT INTO accounts")).
		WithArgs(anyArgs(6)...).
		WillReturnRows(accountRow(true, "acct-1", "open-1"))
	mock.ExpectExec(regexp.QuoteMeta("UPDATE auth_sessions SET state = 'authorized'")).
		WithArgs(anyArgs(2)...).
		WillReturnResult(pgxmock.NewResult("UPDATE", 1))
	mock.ExpectCommit()

	acct, err := s.CompleteAuthSession(ctx, "ticket-1", "open-1", "union-1", "Nick", "avatar", "install-1")
	if err != nil {
		t.Fatalf("CompleteAuthSession: %v", err)
	}
	if acct.ID != "acct-1" {
		t.Fatalf("want account acct-1, got %q", acct.ID)
	}
	if err := mock.ExpectationsWereMet(); err != nil {
		t.Fatalf("%v", err)
	}
}

func TestCompleteAuthSessionRollsBackOnASpentTicket(t *testing.T) {
	ctx := context.Background()
	s, mock := newMock(t)

	// A ticket that is already authorized: the row is there, so this is a
	// conflict, not a miss. Nothing is written, and the transaction is rolled
	// back rather than committed with a half-finished sign-in.
	mock.ExpectBegin()
	mock.ExpectQuery(regexp.QuoteMeta("FROM auth_sessions")).
		WithArgs(anyArgs(1)...).
		WillReturnRows(pgxmock.NewRows([]string{"state", "install_id", "expires_at"}).
			AddRow("authorized", "install-1", testLater))
	mock.ExpectRollback()

	_, err := s.CompleteAuthSession(ctx, "ticket-1", "open-1", "", "Nick", "avatar", "install-1")
	if !errors.Is(err, store.ErrConflict) {
		t.Fatalf("want store.ErrConflict, got %v", err)
	}
	if err := mock.ExpectationsWereMet(); err != nil {
		t.Fatalf("%v", err)
	}
}

func TestCompleteAuthSessionUnknownTicketIsAMiss(t *testing.T) {
	ctx := context.Background()
	s, mock := newMock(t)
	mock.ExpectBegin()
	mock.ExpectQuery(regexp.QuoteMeta("FROM auth_sessions")).
		WithArgs(anyArgs(1)...).
		WillReturnError(noRows)
	mock.ExpectRollback()

	if _, err := s.CompleteAuthSession(ctx, "ticket-x", "open-1", "", "Nick", "avatar", "install-1"); !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("want store.ErrNotFound, got %v", err)
	}
}

func TestCompleteAuthSessionUsesTheSessionsInstallID(t *testing.T) {
	ctx := context.Background()
	s, mock := newMock(t)

	// The ticket remembers which anonymous identity started the sign-in. A
	// caller that supplies no install id must not be allowed to sweep up
	// somebody else's machine, so the session's own value is used — it rides
	// into the account upsert rather than a table that no longer exists.
	mock.ExpectBegin()
	mock.ExpectQuery(regexp.QuoteMeta("FROM auth_sessions")).
		WithArgs(anyArgs(1)...).
		WillReturnRows(pgxmock.NewRows([]string{"state", "install_id", "expires_at"}).
			AddRow("pending", "install-from-session", testLater))
	mock.ExpectQuery(regexp.QuoteMeta("INSERT INTO accounts")).
		WithArgs("open-1", "", "Nick", "avatar", "install-from-session", pgxmock.AnyArg()).
		WillReturnRows(accountRow(true, "acct-1", "open-1"))
	mock.ExpectExec(regexp.QuoteMeta("UPDATE auth_sessions SET state = 'authorized'")).
		WithArgs("ticket-1", "acct-1").
		WillReturnResult(pgxmock.NewResult("UPDATE", 1))
	mock.ExpectCommit()

	if _, err := s.CompleteAuthSession(ctx, "ticket-1", "open-1", "", "Nick", "avatar", ""); err != nil {
		t.Fatalf("CompleteAuthSession: %v", err)
	}
	if err := mock.ExpectationsWereMet(); err != nil {
		t.Fatalf("%v", err)
	}
}

func TestUpsertAccountByOpenIDReportsFirstSignIn(t *testing.T) {
	ctx := context.Background()

	t.Run("a new open id is a first sign-in", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectQuery(regexp.QuoteMeta("INSERT INTO accounts")).
			WithArgs(anyArgs(6)...).
			WillReturnRows(accountRow(true, "acct-1", "open-1"))

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
		s, mock := newMock(t)
		mock.ExpectQuery(regexp.QuoteMeta("INSERT INTO accounts")).
			WithArgs(anyArgs(6)...).
			WillReturnRows(accountRow(false, "acct-1", "open-1"))

		_, created, err := s.UpsertAccountByOpenID(ctx, "open-1", "union-1", "Nick", "avatar")
		if err != nil {
			t.Fatalf("UpsertAccountByOpenID: %v", err)
		}
		if created {
			t.Fatal("want created=false for a return visit")
		}
	})

	t.Run("an empty open id never reaches the database", func(t *testing.T) {
		// "" would collide with every other "" on the unique index and hand
		// the caller somebody else's account.
		s, mock := newMock(t)
		if _, _, err := s.UpsertAccountByOpenID(ctx, "", "u", "Nick", "avatar"); err == nil {
			t.Fatal("expected a rejection for an empty open id")
		}
		if err := mock.ExpectationsWereMet(); err != nil {
			t.Fatalf("a query was issued for an empty open id: %v", err)
		}
	})
}

func TestGetAuthSessionFoldsExpiryIntoState(t *testing.T) {
	ctx := context.Background()
	s, mock := newMock(t)
	mock.ExpectQuery(regexp.QuoteMeta("FROM auth_sessions")).
		WithArgs(anyArgs(1)...).
		WillReturnRows(pgxmock.NewRows([]string{
			"ticket", "state", "install_id", "authorize_url", "expires_at",
			"account_id", "created_at",
		}).AddRow("ticket-1", "pending", "install-1", "https://auth.invalid", testLater, pgtype.Text{}, testNow))

	sess, err := s.GetAuthSession(ctx, "ticket-1")
	if err != nil {
		t.Fatalf("GetAuthSession: %v", err)
	}
	if sess.State != store.AuthPending {
		t.Fatalf("want pending, got %q", sess.State)
	}
	if sess.ExpiresAt.Location() != time.UTC {
		t.Fatalf("timestamps must come back in UTC, got %s", sess.ExpiresAt.Location())
	}
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

// sessionRows builds the accounts projection the locked rotation read
// returns: id, chain_id, refresh_token_hash, prev_refresh_token_hash,
// refresh_expires_at, session_revoked_at. current is the live hash and
// previous the generation before it — the pair is what reuse detection
// reads. The old sessions/refresh_tokens join is gone: the chain is six
// columns on the accounts row.
func sessionRows(current, previous string, revoked bool, expires time.Time) *pgxmock.Rows {
	var revokedAt pgtype.Timestamptz
	if revoked {
		revokedAt = pgtype.Timestamptz{Time: testNow, Valid: true}
	}
	return pgxmock.NewRows([]string{
		"id", "chain_id", "refresh_token_hash", "prev_refresh_token_hash",
		"refresh_expires_at", "session_revoked_at",
	}).AddRow("acct-1", "chain-1", current, previous, expires, revokedAt)
}

func TestRotateRefreshRotates(t *testing.T) {
	ctx := context.Background()
	s, mock := newMock(t)

	// The whole exchange is one transaction over one locked row: the row is
	// read under the lock and replaced in place. The access token's hash is
	// not among the columns — access tokens are self-contained, so the store
	// has never had a reason to keep one.
	mock.ExpectBegin()
	mock.ExpectQuery(regexp.QuoteMeta("FOR UPDATE")).
		WithArgs(anyArgs(1)...).
		WillReturnRows(sessionRows("old-hash", "", false, testLater))
	mock.ExpectExec(regexp.QuoteMeta("UPDATE accounts")).
		WithArgs(anyArgs(4)...).
		WillReturnResult(pgxmock.NewResult("UPDATE", 1))
	mock.ExpectCommit()

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
}

func TestRotateRefreshDetectsReuse(t *testing.T) {
	ctx := context.Background()
	s, mock := newMock(t)

	// The token was already spent: it is the generation this chain replaced,
	// so the live hash is one step ahead of the presented one. This is the
	// signal that the chain is being replayed, and the caller's response is
	// to kill the family — which is exactly why it cannot be folded into a
	// generic conflict.
	mock.ExpectBegin()
	mock.ExpectQuery(regexp.QuoteMeta("FOR UPDATE")).
		WithArgs(anyArgs(1)...).
		WillReturnRows(sessionRows("newer-hash", "old-hash", false, testLater))
	mock.ExpectRollback()

	_, outcome, err := s.RotateRefresh(ctx, "old-hash", "new-hash", "new-access", testLater)
	if !errors.Is(err, store.ErrReuseDetected) {
		t.Fatalf("want store.ErrReuseDetected, got %v", err)
	}
	if errors.Is(err, store.ErrConflict) {
		t.Fatal("reuse must be its own error, not a flavour of conflict")
	}
	if outcome != store.RefreshExpired {
		t.Fatalf("want expired, got %q", outcome)
	}
	if err := mock.ExpectationsWereMet(); err != nil {
		t.Fatalf("a consumed token was rotated anyway: %v", err)
	}
}

func TestRotateRefreshOnARevokedChain(t *testing.T) {
	ctx := context.Background()
	s, mock := newMock(t)
	mock.ExpectBegin()
	mock.ExpectQuery(regexp.QuoteMeta("FOR UPDATE")).
		WithArgs(anyArgs(1)...).
		WillReturnRows(sessionRows("old-hash", "", true, testLater))
	mock.ExpectRollback()

	_, _, err := s.RotateRefresh(ctx, "old-hash", "new-hash", "new-access", testLater)
	if !errors.Is(err, store.ErrConflict) {
		t.Fatalf("want store.ErrConflict for a revoked chain, got %v", err)
	}
}

func TestRotateRefreshOnAnExpiredToken(t *testing.T) {
	ctx := context.Background()
	s, mock := newMock(t)

	// Expiry is an answer, not a failure: the client is told to sign in again
	// and nothing is written. The transaction still commits — it read rows and
	// wrote none, and a rollback would be a lie about what happened.
	mock.ExpectBegin()
	mock.ExpectQuery(regexp.QuoteMeta("FOR UPDATE")).
		WithArgs(anyArgs(1)...).
		WillReturnRows(sessionRows("old-hash", "", false, testExpired))
	mock.ExpectCommit()

	_, outcome, err := s.RotateRefresh(ctx, "old-hash", "new-hash", "new-access", testLater)
	if err != nil {
		t.Fatalf("an expired token is not an error, got %v", err)
	}
	if outcome != store.RefreshExpired {
		t.Fatalf("want expired, got %q", outcome)
	}
}

func TestRotateRefreshOnAnUnknownToken(t *testing.T) {
	ctx := context.Background()
	s, mock := newMock(t)
	mock.ExpectBegin()
	mock.ExpectQuery(regexp.QuoteMeta("FOR UPDATE")).
		WithArgs(anyArgs(1)...).
		WillReturnError(noRows)
	mock.ExpectRollback()

	_, _, err := s.RotateRefresh(ctx, "never-issued", "new-hash", "new-access", testLater)
	if !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("want store.ErrNotFound, got %v", err)
	}
}

func TestRevokeChainCoversTheFamily(t *testing.T) {
	ctx := context.Background()
	s, mock := newMock(t)
	// One session per account, so the family is one row: a single conditional
	// UPDATE, not the two-table dance it replaces. A second call revokes
	// nothing, which is the idempotent answer.
	mock.ExpectExec(regexp.QuoteMeta("UPDATE accounts SET session_revoked_at")).
		WithArgs(anyArgs(2)...).
		WillReturnResult(pgxmock.NewResult("UPDATE", 1))

	if err := s.RevokeChain(ctx, "chain-1", testNow); err != nil {
		t.Fatalf("RevokeChain: %v", err)
	}
	if err := mock.ExpectationsWereMet(); err != nil {
		t.Fatalf("%v", err)
	}
}
