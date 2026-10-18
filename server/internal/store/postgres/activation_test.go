package postgres

import (
	"context"
	"errors"
	"regexp"
	"strings"
	"testing"

	"github.com/pashagolub/pgxmock/v3"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// The activation path is the buy-out product's whole online story: the buyer
// registers a machine, the seller answers once, and the answer is a signed
// licence pinned to that machine. What these tests protect is the shape of
// that story under concurrency — one licence per press, a retired binding
// before its replacement, and the difference between "already answered" and
// "never existed", which the console shows as two very different screens.

func TestCreateActivationRequest(t *testing.T) {
	ctx := context.Background()

	t.Run("a registration is one statement, and the row comes back from the database", func(t *testing.T) {
		// RETURNING is the whole point: the caller minted req-2, the row the
		// database holds is req-1, and it is the database's id the customer
		// will quote to the seller.
		s, mock := newMock(t)
		mock.ExpectQuery(regexp.QuoteMeta("ON CONFLICT (install_id) WHERE status = 'pending'")).
			WithArgs(anyArgs(6)...).
			WillReturnRows(activationRequestRow("pending", fenPtr(19900)))

		r, err := s.CreateActivationRequest(ctx, store.ActivationRequest{
			RequestID:    "req-2",
			AccountID:    "acct-1",
			InstallID:    "install-1",
			AnchorDigest: "anchor-1",
			AmountFen:    fenPtr(19900),
			CreatedAt:    testNow,
		})
		if err != nil {
			t.Fatalf("CreateActivationRequest: %v", err)
		}
		if r.RequestID != "req-1" {
			t.Fatalf("request id = %q, want the stored req-1, not the caller's echo", r.RequestID)
		}
		if r.Status != store.ActivationPending {
			t.Fatalf("status = %q, want pending", r.Status)
		}
		if err := mock.ExpectationsWereMet(); err != nil {
			t.Fatalf("%v", err)
		}
	})

	t.Run("a machine whose live request belongs to another account hands that row back", func(t *testing.T) {
		// The upsert's account predicate refused, so RETURNING came back
		// empty. The row is handed back untouched rather than adopted: the
		// caller sees a stranger's account id and refuses the purchase in
		// turn, and the first buyer's row keeps its owner and its id.
		s, mock := newMock(t)
		mock.ExpectQuery(regexp.QuoteMeta("ON CONFLICT (install_id) WHERE status = 'pending'")).
			WithArgs(anyArgs(6)...).
			WillReturnError(noRows)
		mock.ExpectQuery(regexp.QuoteMeta("WHERE install_id = $1")).
			WithArgs("install-1").
			WillReturnRows(activationRequestRow("pending", nil))

		r, err := s.CreateActivationRequest(ctx, store.ActivationRequest{
			RequestID: "req-2", AccountID: "acct-2", InstallID: "install-1", CreatedAt: testNow,
		})
		if err != nil {
			t.Fatalf("CreateActivationRequest: %v", err)
		}
		if r.AccountID != "acct-1" || r.RequestID != "req-1" {
			t.Fatalf("got the wrong row back: %+v", r)
		}
		if err := mock.ExpectationsWereMet(); err != nil {
			t.Fatalf("%v", err)
		}
	})

	t.Run("a minted id reused across machines is a conflict", func(t *testing.T) {
		// The partial index absorbs an install collision; what is left is a
		// primary-key collision, which is a caller reusing a uuid.
		s, mock := newMock(t)
		mock.ExpectQuery(regexp.QuoteMeta("ON CONFLICT (install_id) WHERE status = 'pending'")).
			WithArgs(anyArgs(6)...).
			WillReturnError(uniqueErr(constraintActivationRequestPK))

		_, err := s.CreateActivationRequest(ctx, store.ActivationRequest{
			RequestID: "req-1", AccountID: "acct-1", InstallID: "install-9", CreatedAt: testNow,
		})
		if !errors.Is(err, store.ErrConflict) {
			t.Fatalf("want store.ErrConflict, got %v", err)
		}
	})
}

func TestLatestActivationRequest(t *testing.T) {
	ctx := context.Background()

	t.Run("the newest row is returned", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectQuery(regexp.QuoteMeta("ORDER BY created_at DESC")).
			WithArgs("acct-1").
			WillReturnRows(activationRequestRow("pending", nil))

		r, err := s.LatestActivationRequest(ctx, "acct-1")
		if err != nil {
			t.Fatalf("LatestActivationRequest: %v", err)
		}
		if r.RequestID != "req-1" || r.Status != store.ActivationPending {
			t.Fatalf("unexpected request: %+v", r)
		}
		if r.AmountFen != nil {
			t.Fatalf("a buyer who did not say must not get an amount: %d", *r.AmountFen)
		}
		if r.ConfirmedAt != nil {
			t.Fatal("a pending request has no confirmation instant")
		}
		if err := mock.ExpectationsWereMet(); err != nil {
			t.Fatalf("%v", err)
		}
	})

	t.Run("a buyer with no history is a miss", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectQuery(regexp.QuoteMeta("ORDER BY created_at DESC")).
			WithArgs("acct-1").
			WillReturnError(noRows)

		if _, err := s.LatestActivationRequest(ctx, "acct-1"); !errors.Is(err, store.ErrNotFound) {
			t.Fatalf("want store.ErrNotFound, got %v", err)
		}
	})
}

func TestGetActivationRequest(t *testing.T) {
	ctx := context.Background()

	t.Run("a confirmed row carries the licence", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectQuery(regexp.QuoteMeta("WHERE request_id = $1")).
			WithArgs("req-1").
			WillReturnRows(activationRequestRow("confirmed", fenPtr(19900)))

		r, err := s.GetActivationRequest(ctx, "req-1")
		if err != nil {
			t.Fatalf("GetActivationRequest: %v", err)
		}
		if r.LicenseID != "lic-1" {
			t.Fatalf("license id = %q, want lic-1", r.LicenseID)
		}
		if r.AmountFen == nil || *r.AmountFen != 19900 {
			t.Fatalf("amount = %v, want 19900", r.AmountFen)
		}
		if r.ConfirmedAt == nil {
			t.Fatal("a confirmed request has a confirmation instant")
		}
	})

	t.Run("an unknown request is a miss", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectQuery(regexp.QuoteMeta("WHERE request_id = $1")).
			WithArgs("req-x").
			WillReturnError(noRows)

		if _, err := s.GetActivationRequest(ctx, "req-x"); !errors.Is(err, store.ErrNotFound) {
			t.Fatalf("want store.ErrNotFound, got %v", err)
		}
	})
}

func TestGetRequestByInstallID(t *testing.T) {
	ctx := context.Background()

	t.Run("the machine's most recent request is returned", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectQuery(regexp.QuoteMeta("WHERE install_id = $1")).
			WithArgs("install-1").
			WillReturnRows(activationRequestRow("pending", nil))

		r, err := s.GetRequestByInstallID(ctx, "install-1")
		if err != nil {
			t.Fatalf("GetRequestByInstallID: %v", err)
		}
		if r.RequestID != "req-1" {
			t.Fatalf("request id = %q, want req-1", r.RequestID)
		}
		if err := mock.ExpectationsWereMet(); err != nil {
			t.Fatalf("%v", err)
		}
	})

	t.Run("an unknown machine is a miss", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectQuery(regexp.QuoteMeta("WHERE install_id = $1")).
			WithArgs("install-x").
			WillReturnError(noRows)

		if _, err := s.GetRequestByInstallID(ctx, "install-x"); !errors.Is(err, store.ErrNotFound) {
			t.Fatalf("want store.ErrNotFound, got %v", err)
		}
	})
}

func TestListActivationRequests(t *testing.T) {
	ctx := context.Background()

	t.Run("an empty status asks for every state", func(t *testing.T) {
		// The ledger page wants everything; the guard is in the SQL, and
		// what reaches the driver is the empty string, not a second query.
		s, mock := newMock(t)
		mock.ExpectQuery(regexp.QuoteMeta("WHERE $1 = '' OR r.status = $1")).
			WithArgs("").
			WillReturnRows(activationRequestViewRow("pending", nil))

		views, err := s.ListActivationRequests(ctx, "")
		if err != nil {
			t.Fatalf("ListActivationRequests: %v", err)
		}
		if len(views) != 1 {
			t.Fatalf("want 1 request, got %d", len(views))
		}
		if views[0].Nickname != "Nick" {
			t.Fatalf("the account columns did not come along: %+v", views[0])
		}
		if err := mock.ExpectationsWereMet(); err != nil {
			t.Fatalf("%v", err)
		}
	})

	t.Run("a status filter is passed through", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectQuery(regexp.QuoteMeta("WHERE $1 = '' OR r.status = $1")).
			WithArgs(string(store.ActivationConfirmed)).
			WillReturnRows(activationRequestViewRow("confirmed", fenPtr(19900)))

		views, err := s.ListActivationRequests(ctx, store.ActivationConfirmed)
		if err != nil {
			t.Fatalf("ListActivationRequests: %v", err)
		}
		if len(views) != 1 {
			t.Fatalf("want 1 request, got %d", len(views))
		}
		if views[0].Status != store.ActivationConfirmed || views[0].LicenseID != "lic-1" {
			t.Fatalf("unexpected row: %+v", views[0])
		}
		if views[0].ConfirmedAt == nil {
			t.Fatal("a confirmed row carries its confirmation instant")
		}
	})
}

func TestConfirmActivationRequestIsOneTransaction(t *testing.T) {
	ctx := context.Background()
	s, mock := newMock(t)

	// The order of these expectations is the transaction: the row is locked,
	// the binding is installed, the flip happens, and only then does the
	// commit. A regression that drops the lock, or installs the binding
	// after the flip, fails here rather than in a seller's afternoon.
	mock.ExpectBegin()
	mock.ExpectQuery(regexp.QuoteMeta("FOR UPDATE")).
		WithArgs("req-1").
		WillReturnRows(activationRequestRow("pending", nil))
	mock.ExpectExec(regexp.QuoteMeta("INSERT INTO licence_bindings")).
		WithArgs("lic-new", "install-1", "acct-1", "req-1", pgxmock.AnyArg()).
		WillReturnResult(pgxmock.NewResult("INSERT", 1))
	mock.ExpectExec(regexp.QuoteMeta("UPDATE activation_requests")).
		WithArgs("req-1", "lic-new", pgxmock.AnyArg(), pgxmock.AnyArg()).
		WillReturnResult(pgxmock.NewResult("UPDATE", 1))
	mock.ExpectCommit()

	envelope := []byte(`{"licenseId":"lic-new"}`)
	r, err := s.ConfirmActivationRequest(ctx, "req-1", "lic-new", envelope, testNow)
	if err != nil {
		t.Fatalf("ConfirmActivationRequest: %v", err)
	}
	// The returned request is the seller's answer: the same row, confirmed,
	// carrying the envelope that was written exactly once.
	if r.Status != store.ActivationConfirmed {
		t.Fatalf("status = %q, want confirmed", r.Status)
	}
	if r.LicenseID != "lic-new" {
		t.Fatalf("license id = %q, want lic-new", r.LicenseID)
	}
	if string(r.EnvelopeJSON) != string(envelope) {
		t.Fatalf("envelope = %q, want the exact bytes written", r.EnvelopeJSON)
	}
	if r.ConfirmedAt == nil || !r.ConfirmedAt.Equal(testNow) {
		t.Fatalf("confirmed at = %v, want %v", r.ConfirmedAt, testNow)
	}
	if err := mock.ExpectationsWereMet(); err != nil {
		t.Fatalf("%v", err)
	}
}

func TestConfirmActivationRequestRefusesAnAlreadyAnsweredRow(t *testing.T) {
	// Two sellers, one button. The second press finds the row already
	// answered, writes nothing, and reads ErrConflict — the console's cue to
	// re-read the row and show the idempotent answer. Confirmed and rejected
	// are the same story here: the row is no longer pending, and neither
	// state is the caller's to overwrite.
	for _, status := range []string{"confirmed", "rejected"} {
		t.Run(status, func(t *testing.T) {
			s, mock := newMock(t)
			mock.ExpectBegin()
			mock.ExpectQuery(regexp.QuoteMeta("FOR UPDATE")).
				WithArgs("req-1").
				WillReturnRows(activationRequestRow(status, nil))
			mock.ExpectRollback()

			if _, err := s.ConfirmActivationRequest(context.Background(), "req-1", "lic-new", nil, testNow); !errors.Is(err, store.ErrConflict) {
				t.Fatalf("want store.ErrConflict, got %v", err)
			}
			if err := mock.ExpectationsWereMet(); err != nil {
				t.Fatalf("%v", err)
			}
		})
	}
}

func TestConfirmActivationRequestOnAnUnknownRowIsAMiss(t *testing.T) {
	s, mock := newMock(t)
	mock.ExpectBegin()
	mock.ExpectQuery(regexp.QuoteMeta("FOR UPDATE")).
		WithArgs("req-x").
		WillReturnError(noRows)
	mock.ExpectRollback()

	if _, err := s.ConfirmActivationRequest(context.Background(), "req-x", "lic-new", nil, testNow); !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("want store.ErrNotFound, got %v", err)
	}
}

func TestConfirmActivationRequestCollidesOnALiveBinding(t *testing.T) {
	// Somebody else already bound a licence to this machine. The partial
	// unique index is the machine-change policy in schema form, and the
	// seller's press must read as a conflict rather than produce a second
	// live binding for one install id.
	s, mock := newMock(t)
	mock.ExpectBegin()
	mock.ExpectQuery(regexp.QuoteMeta("FOR UPDATE")).
		WithArgs("req-1").
		WillReturnRows(activationRequestRow("pending", nil))
	mock.ExpectExec(regexp.QuoteMeta("INSERT INTO licence_bindings")).
		WithArgs(anyArgs(5)...).
		WillReturnError(uniqueErr(constraintBindingLiveInstall))
	mock.ExpectRollback()

	if _, err := s.ConfirmActivationRequest(context.Background(), "req-1", "lic-new", nil, testNow); !errors.Is(err, store.ErrConflict) {
		t.Fatalf("want store.ErrConflict, got %v", err)
	}
}

func TestConfirmActivationRequestFailsWhenTheFlipChangesNothing(t *testing.T) {
	// The lock was held and the row was pending; a flip that changes no rows
	// means the state moved through a path that bypassed the lock.
	// ErrConflict is the honest answer — the caller re-reads and finds the
	// row already answered.
	s, mock := newMock(t)
	mock.ExpectBegin()
	mock.ExpectQuery(regexp.QuoteMeta("FOR UPDATE")).
		WithArgs("req-1").
		WillReturnRows(activationRequestRow("pending", nil))
	mock.ExpectExec(regexp.QuoteMeta("INSERT INTO licence_bindings")).
		WithArgs(anyArgs(5)...).
		WillReturnResult(pgxmock.NewResult("INSERT", 1))
	mock.ExpectExec(regexp.QuoteMeta("UPDATE activation_requests")).
		WithArgs(anyArgs(4)...).
		WillReturnResult(pgxmock.NewResult("UPDATE", 0))
	mock.ExpectRollback()

	if _, err := s.ConfirmActivationRequest(context.Background(), "req-1", "lic-new", nil, testNow); !errors.Is(err, store.ErrConflict) {
		t.Fatalf("want store.ErrConflict, got %v", err)
	}
}

func TestReissueLicenceRetiresTheOldBindingAndConfirmsThePendingRequest(t *testing.T) {
	// The whole point of the reissue: the machine's live binding is retired,
	// the new one is installed, and the buyer's outstanding request is
	// confirmed inside the same transaction — that is how their poller
	// learns about the new licence at all.
	s, mock := newMock(t)
	mock.ExpectBegin()
	mock.ExpectQuery(regexp.QuoteMeta("UPDATE licence_bindings")).
		WithArgs("install-1", pgxmock.AnyArg()).
		WillReturnRows(pgxmock.NewRows([]string{"account_id"}).AddRow("acct-1"))
	mock.ExpectQuery(regexp.QuoteMeta("WHERE install_id = $1")).
		WithArgs("install-1").
		WillReturnRows(activationRequestRow("pending", nil))
	mock.ExpectExec(regexp.QuoteMeta("INSERT INTO licence_bindings")).
		WithArgs("lic-new", "install-1", "acct-1", "req-1", pgxmock.AnyArg()).
		WillReturnResult(pgxmock.NewResult("INSERT", 1))
	mock.ExpectExec(regexp.QuoteMeta("UPDATE activation_requests")).
		WithArgs(anyArgs(4)...).
		WillReturnResult(pgxmock.NewResult("UPDATE", 1))
	mock.ExpectCommit()

	r, err := s.ReissueLicence(context.Background(), "install-1", "lic-new", []byte(`{"licenseId":"lic-new"}`), testNow)
	if err != nil {
		t.Fatalf("ReissueLicence: %v", err)
	}
	if r.RequestID != "req-1" || r.Status != store.ActivationConfirmed || r.LicenseID != "lic-new" {
		t.Fatalf("unexpected request: %+v", r)
	}
	if err := mock.ExpectationsWereMet(); err != nil {
		t.Fatalf("%v", err)
	}
}

func TestReissueLicenceWithoutAPendingRequestFilesItsOwnRow(t *testing.T) {
	// A reissue after a restore: the machine holds a live binding and its
	// only request is long confirmed. The reissue gets a row of its own —
	// a licence with no row is a licence nobody can poll for — carrying the
	// anchor and amount of the earlier registration so the seller's console
	// keeps showing what was bought, and the new binding links back to it.
	s, mock := newMock(t)
	mock.ExpectBegin()
	mock.ExpectQuery(regexp.QuoteMeta("UPDATE licence_bindings")).
		WithArgs("install-1", pgxmock.AnyArg()).
		WillReturnRows(pgxmock.NewRows([]string{"account_id"}).AddRow("acct-1"))
	mock.ExpectQuery(regexp.QuoteMeta("WHERE install_id = $1")).
		WithArgs("install-1").
		WillReturnRows(activationRequestRow("confirmed", fenPtr(19900)))
	mock.ExpectExec(regexp.QuoteMeta("INSERT INTO licence_bindings")).
		WithArgs("lic-new", "install-1", "acct-1", pgxmock.AnyArg(), pgxmock.AnyArg()).
		WillReturnResult(pgxmock.NewResult("INSERT", 1))
	mock.ExpectExec(regexp.QuoteMeta("INSERT INTO activation_requests")).
		WithArgs(anyArgs(9)...).
		WillReturnResult(pgxmock.NewResult("INSERT", 1))
	mock.ExpectCommit()

	r, err := s.ReissueLicence(context.Background(), "install-1", "lic-new", []byte(`{"licenseId":"lic-new"}`), testNow)
	if err != nil {
		t.Fatalf("ReissueLicence: %v", err)
	}
	if r.Status != store.ActivationConfirmed || r.LicenseID != "lic-new" {
		t.Fatalf("unexpected request: %+v", r)
	}
	if r.AccountID != "acct-1" {
		t.Fatalf("account = %q, want the retired binding's acct-1", r.AccountID)
	}
	if !strings.HasPrefix(r.RequestID, "act_") {
		t.Fatalf("the reissue row's id = %q, want the act_ prefix the mock and the client logs use", r.RequestID)
	}
	if r.AnchorDigest != "anchor-1" {
		t.Fatalf("anchor = %q, want the earlier registration's anchor-1", r.AnchorDigest)
	}
	if r.AmountFen == nil || *r.AmountFen != 19900 {
		t.Fatalf("amount = %v, want the earlier registration's 19900", r.AmountFen)
	}
	if err := mock.ExpectationsWereMet(); err != nil {
		t.Fatalf("%v", err)
	}
}

func TestReissueLicenceAfterARestoreUsesThePendingRequest(t *testing.T) {
	// The machine holds nothing live but has an open request — the buyer is
	// mid-purchase on a restored machine. The new binding takes the account
	// from the request, and the request is confirmed with the new licence.
	s, mock := newMock(t)
	mock.ExpectBegin()
	mock.ExpectQuery(regexp.QuoteMeta("UPDATE licence_bindings")).
		WithArgs("install-1", pgxmock.AnyArg()).
		WillReturnError(noRows)
	mock.ExpectQuery(regexp.QuoteMeta("WHERE install_id = $1")).
		WithArgs("install-1").
		WillReturnRows(activationRequestRow("pending", nil))
	mock.ExpectExec(regexp.QuoteMeta("INSERT INTO licence_bindings")).
		WithArgs("lic-new", "install-1", "acct-1", "req-1", pgxmock.AnyArg()).
		WillReturnResult(pgxmock.NewResult("INSERT", 1))
	mock.ExpectExec(regexp.QuoteMeta("UPDATE activation_requests")).
		WithArgs(anyArgs(4)...).
		WillReturnResult(pgxmock.NewResult("UPDATE", 1))
	mock.ExpectCommit()

	r, err := s.ReissueLicence(context.Background(), "install-1", "lic-new", []byte(`{"licenseId":"lic-new"}`), testNow)
	if err != nil {
		t.Fatalf("ReissueLicence: %v", err)
	}
	if r.RequestID != "req-1" || r.Status != store.ActivationConfirmed || r.LicenseID != "lic-new" {
		t.Fatalf("unexpected request: %+v", r)
	}
	if err := mock.ExpectationsWereMet(); err != nil {
		t.Fatalf("%v", err)
	}
}

func TestReissueLicenceForAnUnknownMachineIsAMiss(t *testing.T) {
	// No live binding and no request of any kind: the seller is looking at a
	// machine code this service has never heard of. A reissue that invented
	// a licence out of thin air would be a support call nobody can explain.
	s, mock := newMock(t)
	mock.ExpectBegin()
	mock.ExpectQuery(regexp.QuoteMeta("UPDATE licence_bindings")).
		WithArgs("install-x", pgxmock.AnyArg()).
		WillReturnError(noRows)
	mock.ExpectQuery(regexp.QuoteMeta("WHERE install_id = $1")).
		WithArgs("install-x").
		WillReturnError(noRows)
	mock.ExpectRollback()

	if _, err := s.ReissueLicence(context.Background(), "install-x", "lic-new", nil, testNow); !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("want store.ErrNotFound, got %v", err)
	}
	if err := mock.ExpectationsWereMet(); err != nil {
		t.Fatalf("%v", err)
	}
}

func TestRevokeBinding(t *testing.T) {
	ctx := context.Background()

	t.Run("the first revoke writes the date", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectExec(regexp.QuoteMeta("UPDATE licence_bindings")).
			WithArgs("lic-1", pgxmock.AnyArg()).
			WillReturnResult(pgxmock.NewResult("UPDATE", 1))

		if err := s.RevokeBinding(ctx, "lic-1", testNow); err != nil {
			t.Fatalf("RevokeBinding: %v", err)
		}
		if err := mock.ExpectationsWereMet(); err != nil {
			t.Fatalf("%v", err)
		}
	})

	t.Run("revoking twice is a misclick, not a 404", func(t *testing.T) {
		// A stale console tab: the second press must succeed. Zero rows
		// updated is two different stories, and the re-read is what tells
		// them apart.
		s, mock := newMock(t)
		mock.ExpectExec(regexp.QuoteMeta("UPDATE licence_bindings")).
			WithArgs("lic-1", pgxmock.AnyArg()).
			WillReturnResult(pgxmock.NewResult("UPDATE", 0))
		mock.ExpectQuery(regexp.QuoteMeta("FROM licence_bindings")).
			WithArgs("lic-1").
			WillReturnRows(pgxmock.NewRows([]string{"license_id"}).AddRow("lic-1"))

		if err := s.RevokeBinding(ctx, "lic-1", testNow); err != nil {
			t.Fatalf("RevokeBinding (second press): %v", err)
		}
		if err := mock.ExpectationsWereMet(); err != nil {
			t.Fatalf("%v", err)
		}
	})

	t.Run("revoking a licence that never existed is a miss", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectExec(regexp.QuoteMeta("UPDATE licence_bindings")).
			WithArgs("lic-x", pgxmock.AnyArg()).
			WillReturnResult(pgxmock.NewResult("UPDATE", 0))
		mock.ExpectQuery(regexp.QuoteMeta("FROM licence_bindings")).
			WithArgs("lic-x").
			WillReturnError(noRows)

		if err := s.RevokeBinding(ctx, "lic-x", testNow); !errors.Is(err, store.ErrNotFound) {
			t.Fatalf("want store.ErrNotFound, got %v", err)
		}
	})
}

func TestLiveBindingByInstallID(t *testing.T) {
	ctx := context.Background()

	t.Run("the machine's live licence is found", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectQuery(regexp.QuoteMeta("WHERE install_id = $1 AND revoked_at IS NULL")).
			WithArgs("install-1").
			WillReturnRows(licenceBindingRow("lic-1", "install-1", "acct-1", false))

		b, err := s.LiveBindingByInstallID(ctx, "install-1")
		if err != nil {
			t.Fatalf("LiveBindingByInstallID: %v", err)
		}
		if b.LicenseID != "lic-1" || b.RevokedAt != nil {
			t.Fatalf("unexpected binding: %+v", b)
		}
		if err := mock.ExpectationsWereMet(); err != nil {
			t.Fatalf("%v", err)
		}
	})

	t.Run("a machine holding nothing is a miss", func(t *testing.T) {
		s, mock := newMock(t)
		mock.ExpectQuery(regexp.QuoteMeta("WHERE install_id = $1 AND revoked_at IS NULL")).
			WithArgs("install-1").
			WillReturnError(noRows)

		if _, err := s.LiveBindingByInstallID(ctx, "install-1"); !errors.Is(err, store.ErrNotFound) {
			t.Fatalf("want store.ErrNotFound, got %v", err)
		}
	})
}

func TestListBindingsIsTheLedger(t *testing.T) {
	// Everything ever issued, newest first, with the person it belongs to:
	// the join is what makes the seller's after-the-fact reconciliation
	// possible without a query per row.
	s, mock := newMock(t)
	mock.ExpectQuery(regexp.QuoteMeta("FROM licence_bindings")).
		WillReturnRows(licenceBindingViewRow("lic-2", "install-2", "acct-2", false))

	views, err := s.ListBindings(context.Background())
	if err != nil {
		t.Fatalf("ListBindings: %v", err)
	}
	if len(views) != 1 {
		t.Fatalf("want 1 binding, got %d", len(views))
	}
	v := views[0]
	if v.Nickname != "Nick" || v.OpenID != "open-1" {
		t.Fatalf("the account columns did not come along: %+v", v)
	}
	if v.LicenseID != "lic-2" || v.RevokedAt != nil {
		t.Fatalf("unexpected binding: %+v", v)
	}
	if err := mock.ExpectationsWereMet(); err != nil {
		t.Fatalf("%v", err)
	}
}

func TestListBindingsWithAnEmptyLedger(t *testing.T) {
	// An empty ledger is a young service, not an error: nil rows, nil slice,
	// no failure.
	s, mock := newMock(t)
	mock.ExpectQuery(regexp.QuoteMeta("FROM licence_bindings")).
		WillReturnRows(pgxmock.NewRows([]string{
			"license_id", "install_id", "account_id", "request_id", "issued_at", "revoked_at",
			"nickname", "open_id",
		}))

	views, err := s.ListBindings(context.Background())
	if err != nil {
		t.Fatalf("ListBindings: %v", err)
	}
	if len(views) != 0 {
		t.Fatalf("want an empty ledger, got %d rows", len(views))
	}
}
