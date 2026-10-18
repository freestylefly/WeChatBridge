package postgres

import (
	"context"
	"errors"
	"regexp"
	"testing"

	"github.com/pashagolub/pgxmock/v3"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

func TestMarkPaidIsIdempotent(t *testing.T) {
	ctx := context.Background()

	t.Run("the first delivery flips the order", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectQuery(regexp.QuoteMeta("status = 'pending'")).
			WithArgs(anyArgs(3)...).
			WillReturnRows(orderRow("ord-1", "paid"))

		o, err := s.MarkPaid(ctx, "ord-1", "wx-txn-1", testNow)
		if err != nil {
			t.Fatalf("MarkPaid: %v", err)
		}
		if o.Status != store.OrderPaid {
			t.Fatalf("want paid, got %q", o.Status)
		}
	})

	t.Run("a redelivery returns the same order and no error", func(t *testing.T) {
		// WeChat re-delivers until it gets a success response, so this runs
		// several times by design. A retry is not a failure: it must not be
		// reported as one, or the handler will answer with an error code and
		// the retries will continue forever.
		s, mock := newMock(t)
		mock.ExpectQuery(regexp.QuoteMeta("status = 'pending'")).
			WithArgs(anyArgs(3)...).
			WillReturnError(noRows)
		mock.ExpectQuery(regexp.QuoteMeta("SELECT status FROM orders")).
			WithArgs("ord-1").
			WillReturnRows(pgxmock.NewRows([]string{"status"}).AddRow("paid"))
		mock.ExpectQuery(regexp.QuoteMeta("FROM orders")).
			WithArgs("ord-1").
			WillReturnRows(orderRow("ord-1", "paid"))

		o, err := s.MarkPaid(ctx, "ord-1", "wx-txn-1", testNow)
		if err != nil {
			t.Fatalf("a redelivered callback must not be an error, got %v", err)
		}
		if o.Status != store.OrderPaid {
			t.Fatalf("want paid, got %q", o.Status)
		}
	})

	t.Run("a callback for an order this server never made is a miss", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectQuery(regexp.QuoteMeta("status = 'pending'")).
			WithArgs(anyArgs(3)...).
			WillReturnError(noRows)
		mock.ExpectQuery(regexp.QuoteMeta("SELECT status FROM orders")).
			WithArgs("ord-forged").
			WillReturnError(noRows)

		_, err := s.MarkPaid(ctx, "ord-forged", "wx-txn-x", testNow)
		if !errors.Is(err, store.ErrNotFound) {
			t.Fatalf("want store.ErrNotFound, got %v", err)
		}
	})
}

func TestCreateOrderRejectsADuplicateOutTradeNo(t *testing.T) {
	ctx := context.Background()
	s, mock := newMock(t)
	mock.ExpectExec(regexp.QuoteMeta("INSERT INTO orders")).
		WithArgs(anyArgs(11)...).
		WillReturnError(uniqueErr(constraintOrderOutTradeNo))

	// One checkout, one order, one QR code. A duplicate is somebody reloading
	// the pay page, not somebody buying twice.
	err := s.CreateOrder(ctx, store.Order{ID: "ord-2", OutTradeNo: "OTN-1", Plan: store.PlanSubscription})
	if !errors.Is(err, store.ErrConflict) {
		t.Fatalf("want store.ErrConflict, got %v", err)
	}
}

func TestIssueOrderLicenseNeverReSigns(t *testing.T) {
	ctx := context.Background()
	first := []byte(`{"sig":"FIRST"}`)

	t.Run("the first call stores and returns its own bytes", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectBegin()
		mock.ExpectQuery(regexp.QuoteMeta("FOR UPDATE")).
			WithArgs("ord-1").
			WillReturnRows(pgxmock.NewRows([]string{
				"license_envelope", "license_id", "license_key",
			}).AddRow(nil, "", ""))
		mock.ExpectExec(regexp.QuoteMeta("license_envelope IS NULL")).
			WithArgs("ord-1", "lic-1", "KEY-1", pgxmock.AnyArg()).
			WillReturnResult(pgxmock.NewResult("UPDATE", 1))
		mock.ExpectCommit()

		got, err := s.IssueOrderLicense(ctx, "ord-1", "lic-1", "KEY-1", first)
		if err != nil {
			t.Fatalf("IssueOrderLicense: %v", err)
		}
		if string(got) != string(first) {
			t.Fatalf("want the caller's own bytes back, got %s", got)
		}
	})

	t.Run("a later call returns the stored bytes and writes nothing", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectBegin()
		mock.ExpectQuery(regexp.QuoteMeta("FOR UPDATE")).
			WithArgs("ord-1").
			WillReturnRows(pgxmock.NewRows([]string{
				"license_envelope", "license_id", "license_key",
			}).AddRow(first, "lic-1", "KEY-1"))
		mock.ExpectCommit()

		got, err := s.IssueOrderLicense(ctx, "ord-1", "lic-1", "KEY-1", []byte(`{"sig":"SECOND"}`))
		if err != nil {
			t.Fatalf("IssueOrderLicense: %v", err)
		}
		if string(got) != string(first) {
			t.Fatalf("the envelope was re-signed instead of returned:\n got %s\nwant %s", got, first)
		}
		if err := mock.ExpectationsWereMet(); err != nil {
			t.Fatalf("a second issuance wrote something: %v", err)
		}
	})

	t.Run("an unknown order is a miss", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectBegin()
		mock.ExpectQuery(regexp.QuoteMeta("FOR UPDATE")).
			WithArgs("ord-x").
			WillReturnError(noRows)
		mock.ExpectRollback()

		if _, err := s.IssueOrderLicense(ctx, "ord-x", "lic-1", "KEY-1", first); !errors.Is(err, store.ErrNotFound) {
			t.Fatalf("want store.ErrNotFound, got %v", err)
		}
	})

	t.Run("an empty envelope is refused before it reaches the database", func(t *testing.T) {
		s, _ := newMock(t)
		if _, err := s.IssueOrderLicense(ctx, "ord-1", "lic-1", "KEY-1", nil); err == nil {
			t.Fatal("expected a rejection for an empty envelope")
		}
	})
}

func TestCloseAndRefundDistinguishAbsenceFromSettled(t *testing.T) {
	ctx := context.Background()

	t.Run("closing an order that never existed is a miss", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectExec(regexp.QuoteMeta("UPDATE orders SET status = 'closed'")).
			WithArgs("ord-x").
			WillReturnResult(pgxmock.NewResult("UPDATE", 0))
		mock.ExpectQuery(regexp.QuoteMeta("SELECT status FROM orders")).
			WithArgs("ord-x").
			WillReturnError(noRows)

		if err := s.CloseOrder(ctx, "ord-x", testNow); !errors.Is(err, store.ErrNotFound) {
			t.Fatalf("want store.ErrNotFound, got %v", err)
		}
	})

	t.Run("closing an already-paid order is a conflict", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectExec(regexp.QuoteMeta("UPDATE orders SET status = 'closed'")).
			WithArgs("ord-1").
			WillReturnResult(pgxmock.NewResult("UPDATE", 0))
		mock.ExpectQuery(regexp.QuoteMeta("SELECT status FROM orders")).
			WithArgs("ord-1").
			WillReturnRows(pgxmock.NewRows([]string{"status"}).AddRow("paid"))

		if err := s.CloseOrder(ctx, "ord-1", testNow); !errors.Is(err, store.ErrConflict) {
			t.Fatalf("want store.ErrConflict, got %v", err)
		}
	})

	t.Run("refunding only moves a paid order", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectExec(regexp.QuoteMeta("status = 'paid'")).
			WithArgs("ord-1", "wx-txn-1").
			WillReturnResult(pgxmock.NewResult("UPDATE", 1))

		if err := s.RefundOrder(ctx, "ord-1", "wx-txn-1", testNow); err != nil {
			t.Fatalf("RefundOrder: %v", err)
		}
	})
}

func TestQrCacheMissesAreNormal(t *testing.T) {
	ctx := context.Background()
	s, mock := newMock(t)
	mock.ExpectQuery(regexp.QuoteMeta("FROM qr_payloads")).
		WithArgs("login", "ref-1").
		WillReturnError(noRows)

	// A miss costs one re-scan and nothing else, so it comes back as
	// ErrNotFound and the caller mints a fresh code. It is not an error state.
	if _, err := s.Get(ctx, "login", "ref-1"); !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("want store.ErrNotFound, got %v", err)
	}
}

func TestQrPutIsAnUpsert(t *testing.T) {
	ctx := context.Background()
	s, mock := newMock(t)
	mock.ExpectExec(regexp.QuoteMeta("ON CONFLICT (kind, ref) DO UPDATE")).
		WithArgs("login", "ref-1", "https://qr.invalid/x", testNow).
		WillReturnResult(pgxmock.NewResult("INSERT", 1))

	if err := s.Put(ctx, store.QrPayload{Kind: "login", Ref: "ref-1", Body: "https://qr.invalid/x", At: testNow}); err != nil {
		t.Fatalf("Put: %v", err)
	}
}
