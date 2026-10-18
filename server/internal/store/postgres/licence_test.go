package postgres

import (
	"context"
	"errors"
	"regexp"
	"sync"
	"testing"

	"github.com/jackc/pgx/v5/pgtype"
	"github.com/pashagolub/pgxmock/v3"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// TestBurnRedeemCodeIsMutuallyExclusive is the test this product is judged on.
//
// Two customers race for the same code. Exactly one may walk away with a
// licence. The store's job is to make that true under concurrency, and the
// mechanism is the `redeemed_at IS NULL` predicate on the UPDATE: the database
// evaluates it while holding the row lock, so the loser updates zero rows and
// is told so.
//
// The mock cannot prove that PostgreSQL locks; it proves the half that is
// ours — that zero rows becomes store.ErrConflict and never a success, that
// only one caller is told it won, and that both callers issued the same
// statement. The locking half is asserted statically in TestCriticalSQL below
// and is the reason the predicate is written where it is.
func TestBurnRedeemCodeIsMutuallyExclusive(t *testing.T) {
	ctx := context.Background()
	s, mock := newUnorderedMock(t)

	// Two identical expectations, unordered. Whichever goroutine claims which
	// one, the totals must come out the same: one winner, one loser.
	mock.ExpectExec(regexp.QuoteMeta("UPDATE redeem_codes SET redeemed_at")).
		WithArgs(anyArgs(4)...).
		WillReturnResult(pgxmock.NewResult("UPDATE", 1))
	mock.ExpectExec(regexp.QuoteMeta("UPDATE redeem_codes SET redeemed_at")).
		WithArgs(anyArgs(4)...).
		WillReturnResult(pgxmock.NewResult("UPDATE", 0))
	// The loser needs to be told *why* it lost: the follow-up read that
	// separates "somebody else got there first" from "no such code".
	mock.ExpectQuery(regexp.QuoteMeta("SELECT EXISTS")).
		WithArgs(anyArgs(1)...).
		WillReturnRows(pgxmock.NewRows([]string{"exists"}).AddRow(true))

	start := make(chan struct{})
	var (
		wg   sync.WaitGroup
		mu   sync.Mutex
		errs = map[string]error{}
	)
	for _, name := range []string{"first", "second"} {
		wg.Add(1)
		go func(name string) {
			defer wg.Done()
			<-start
			err := s.BurnRedeemCode(ctx, "HASH", "install-1", "lic-1", testNow)
			mu.Lock()
			errs[name] = err
			mu.Unlock()
		}(name)
	}
	close(start)
	wg.Wait()

	won, lost := 0, 0
	for name, err := range errs {
		switch {
		case err == nil:
			won++
		case errors.Is(err, store.ErrConflict):
			lost++
		default:
			t.Fatalf("%s: unexpected error: %v", name, err)
		}
	}
	if won != 1 || lost != 1 {
		t.Fatalf("want exactly one winner and one loser, got %d winners and %d losers (%v)", won, lost, errs)
	}
	if err := mock.ExpectationsWereMet(); err != nil {
		t.Fatalf("unmet expectations: %v", err)
	}
}

func TestBurnRedeemCodeDistinguishesAbsentFromSpent(t *testing.T) {
	ctx := context.Background()

	t.Run("a code that was never issued is a miss", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectExec(regexp.QuoteMeta("UPDATE redeem_codes SET redeemed_at")).
			WithArgs(anyArgs(4)...).
			WillReturnResult(pgxmock.NewResult("UPDATE", 0))
		mock.ExpectQuery(regexp.QuoteMeta("SELECT EXISTS")).
			WithArgs(anyArgs(1)...).
			WillReturnRows(pgxmock.NewRows([]string{"exists"}).AddRow(false))

		err := s.BurnRedeemCode(ctx, "HASH", "install-1", "lic-1", testNow)
		if !errors.Is(err, store.ErrNotFound) {
			t.Fatalf("want store.ErrNotFound, got %v", err)
		}
	})

	t.Run("a code that exists but is spent is a conflict", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectExec(regexp.QuoteMeta("UPDATE redeem_codes SET redeemed_at")).
			WithArgs(anyArgs(4)...).
			WillReturnResult(pgxmock.NewResult("UPDATE", 0))
		mock.ExpectQuery(regexp.QuoteMeta("SELECT EXISTS")).
			WithArgs(anyArgs(1)...).
			WillReturnRows(pgxmock.NewRows([]string{"exists"}).AddRow(true))

		err := s.BurnRedeemCode(ctx, "HASH", "install-1", "lic-1", testNow)
		if !errors.Is(err, store.ErrConflict) {
			t.Fatalf("want store.ErrConflict, got %v", err)
		}
	})

	t.Run("a database failure is neither", func(t *testing.T) {
		s, mock := newMock(t)
		boom := errors.New("connection reset by peer")
		mock.ExpectExec(regexp.QuoteMeta("UPDATE redeem_codes SET redeemed_at")).
			WithArgs(anyArgs(4)...).
			WillReturnError(boom)

		err := s.BurnRedeemCode(ctx, "HASH", "install-1", "lic-1", testNow)
		if errors.Is(err, store.ErrConflict) || errors.Is(err, store.ErrNotFound) {
			t.Fatalf("an outage was reported as a business outcome: %v", err)
		}
		if !errors.Is(err, boom) {
			t.Fatalf("the driver error was swallowed: %v", err)
		}
	})

	t.Run("a partial burn is refused before it reaches the database", func(t *testing.T) {
		// A burn with no licence id would produce a row the schema's CHECK
		// rejects, or — worse — a code marked spent with nothing to show the
		// customer. Refusing here keeps that a caller bug, loudly.
		s, mock := newMock(t)
		for _, tc := range []struct{ hash, install, licence string }{
			{"", "i", "l"},
			{"HASH", "", "l"},
			{"HASH", "i", ""},
		} {
			if err := s.BurnRedeemCode(ctx, tc.hash, tc.install, tc.licence, testNow); err == nil {
				t.Fatalf("expected a rejection for %+v", tc)
			}
		}
		if err := mock.ExpectationsWereMet(); err != nil {
			t.Fatalf("a partial burn reached the database: %v", err)
		}
	})
}

func TestGetRedeemCodeReportsStoredState(t *testing.T) {
	ctx := context.Background()
	s, mock := newMock(t)

	mock.ExpectQuery(regexp.QuoteMeta("FROM redeem_codes")).
		WithArgs(anyArgs(1)...).
		WillReturnRows(pgxmock.NewRows([]string{
			"code_hash", "product_id", "plan", "lifetime_seconds", "issued_at",
			"expires_at", "redeemed_at", "redeemed_install_id", "license_id", "revoked_at",
		}).AddRow(
			"HASH", "windows-buyout", store.PlanBuyout, int64(0), testExpired, testLater,
			pgtype.Timestamptz{Time: testNow, Valid: true}, "install-1", "lic-1",
			pgtype.Timestamptz{},
		))

	c, err := s.GetRedeemCode(ctx, "HASH")
	if err != nil {
		t.Fatalf("GetRedeemCode: %v", err)
	}
	if c.IsBurnable() {
		t.Fatal("a code with redeemed_at set must not report itself as burnable")
	}
	if c.RedeemedAt == nil || !c.RedeemedAt.Equal(testNow) {
		t.Fatalf("redeemed_at was not read back: %v", c.RedeemedAt)
	}
	if c.RevokedAt != nil {
		t.Fatalf("a NULL revoked_at must read as nil, got %v", c.RevokedAt)
	}
	if c.Lifetime != 0 {
		t.Fatalf("a perpetual code must have zero lifetime, got %s", c.Lifetime)
	}
}

func TestBindDeviceEnforcesSlotsInOneTransaction(t *testing.T) {
	ctx := context.Background()

	t.Run("the last slot is taken", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectBegin()
		mock.ExpectQuery(regexp.QuoteMeta("FROM orders WHERE id = $1 FOR UPDATE")).
			WithArgs("ord-1").
			WillReturnRows(pgxmock.NewRows([]string{"id"}).AddRow("ord-1"))
		// Two machines already bound, a third is asking.
		mock.ExpectQuery(regexp.QuoteMeta("count(*)")).
			WithArgs("ord-1", "install-3").
			WillReturnRows(pgxmock.NewRows([]string{"count"}).AddRow(2))
		mock.ExpectRollback()

		err := s.BindDevice(ctx, "ord-1", "install-3", store.PlanBuyout, 2, testNow)
		if !errors.Is(err, store.ErrConflict) {
			t.Fatalf("want store.ErrConflict, got %v", err)
		}
		// The limit is enforced by refusing, so the insert must never run.
		if err := mock.ExpectationsWereMet(); err != nil {
			t.Fatalf("a device was bound after the limit was reached: %v", err)
		}
	})

	t.Run("a released slot comes back rather than a new row", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectBegin()
		mock.ExpectQuery(regexp.QuoteMeta("FROM orders WHERE id = $1 FOR UPDATE")).
			WithArgs("ord-1").
			WillReturnRows(pgxmock.NewRows([]string{"id"}).AddRow("ord-1"))
		mock.ExpectQuery(regexp.QuoteMeta("count(*)")).
			WithArgs("ord-1", "install-2").
			WillReturnRows(pgxmock.NewRows([]string{"count"}).AddRow(1))
		mock.ExpectExec(regexp.QuoteMeta("ON CONFLICT (order_id, install_id) DO UPDATE")).
			WithArgs(anyArgs(4)...).
			WillReturnResult(pgxmock.NewResult("INSERT", 1))
		mock.ExpectCommit()

		if err := s.BindDevice(ctx, "ord-1", "install-2", store.PlanBuyout, 2, testNow); err != nil {
			t.Fatalf("BindDevice: %v", err)
		}
		if err := mock.ExpectationsWereMet(); err != nil {
			t.Fatalf("%v", err)
		}
	})

	t.Run("an unknown order is a miss", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectBegin()
		mock.ExpectQuery(regexp.QuoteMeta("FROM orders WHERE id = $1 FOR UPDATE")).
			WithArgs("ord-nope").
			WillReturnError(noRows)
		mock.ExpectRollback()

		err := s.BindDevice(ctx, "ord-nope", "install-1", store.PlanBuyout, 3, testNow)
		if !errors.Is(err, store.ErrNotFound) {
			t.Fatalf("want store.ErrNotFound, got %v", err)
		}
	})

	t.Run("a nonsensical slot limit is refused before the transaction", func(t *testing.T) {
		s, mock := newMock(t)
		if err := s.BindDevice(ctx, "ord-1", "install-1", store.PlanBuyout, 0, testNow); err == nil {
			t.Fatal("expected a rejection for a zero slot limit")
		}
		if err := mock.ExpectationsWereMet(); err != nil {
			t.Fatalf("a transaction was opened anyway: %v", err)
		}
	})
}

func TestReleaseDeviceIsIdempotentButNotInventive(t *testing.T) {
	ctx := context.Background()

	t.Run("already released is a success", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectExec(regexp.QuoteMeta("UPDATE licence_bindings SET released_at")).
			WithArgs(anyArgs(3)...).
			WillReturnResult(pgxmock.NewResult("UPDATE", 0))
		mock.ExpectQuery(regexp.QuoteMeta("SELECT EXISTS")).
			WithArgs(anyArgs(2)...).
			WillReturnRows(pgxmock.NewRows([]string{"exists"}).AddRow(true))

		if err := s.ReleaseDevice(ctx, "ord-1", "install-1", testNow); err != nil {
			t.Fatalf("releasing an already-released machine should succeed, got %v", err)
		}
	})

	t.Run("never bound is a miss", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectExec(regexp.QuoteMeta("UPDATE licence_bindings SET released_at")).
			WithArgs(anyArgs(3)...).
			WillReturnResult(pgxmock.NewResult("UPDATE", 0))
		mock.ExpectQuery(regexp.QuoteMeta("SELECT EXISTS")).
			WithArgs(anyArgs(2)...).
			WillReturnRows(pgxmock.NewRows([]string{"exists"}).AddRow(false))

		err := s.ReleaseDevice(ctx, "ord-1", "install-1", testNow)
		if !errors.Is(err, store.ErrNotFound) {
			t.Fatalf("want store.ErrNotFound, got %v", err)
		}
	})
}

func TestRedemptionRequestKeepsTheBytes(t *testing.T) {
	ctx := context.Background()
	envelope := []byte(`{"kid":"k1","sig":"AAEC","order":{"plan":"buyout"}}`)

	s, mock := newMock(t)
	mock.ExpectQuery(regexp.QuoteMeta("FROM redemption_requests")).
		WithArgs("req-1").
		WillReturnRows(pgxmock.NewRows([]string{
			"request_id", "code_hash", "license_id", "envelope_json", "created_at",
		}).AddRow("req-1", "HASH", "lic-1", envelope, testNow))

	got, err := s.GetRedemptionRequest(ctx, "req-1")
	if err != nil {
		t.Fatalf("GetRedemptionRequest: %v", err)
	}
	if string(got.EnvelopeJSON) != string(envelope) {
		t.Fatalf("the stored bytes did not survive the round trip:\n got %s\nwant %s",
			got.EnvelopeJSON, envelope)
	}
}
