package sqlite

import (
	"context"
	"database/sql"
	"errors"
	"strings"
	"testing"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// The activation path is the buy-out product's whole online story: the buyer
// registers a machine, the seller answers once, and the answer is a signed
// licence pinned to that machine. What these tests protect is the shape of
// that story under concurrency — one licence per press, a retired binding
// before its replacement, and the difference between "already answered" and
// "never existed", which the console shows as two very different screens.
//
// Every assertion here runs against a real database: the idempotency of the
// registration upsert, the exact bytes of the envelope, and the partial
// unique indexes that make the machine-change policy are all behaviour, not
// SQL text.

func TestCreateActivationRequest(t *testing.T) {
	ctx := context.Background()

	t.Run("a registration returns the stored row", func(t *testing.T) {
		// RETURNING is the whole point: the caller minted req-2, the row the
		// database holds is req-1, and it is the database's id the customer
		// will quote to the seller.
		s := newTestStore(t)
		acct := seedAccount(t, s, "open-1")

		r, err := s.CreateActivationRequest(ctx, store.ActivationRequest{
			RequestID:    "req-1",
			AccountID:    acct.ID,
			InstallID:    "install-1",
			AnchorDigest: "anchor-1",
			AmountFen:    fenPtr(19900),
			CreatedAt:    testNow,
		})
		if err != nil {
			t.Fatalf("CreateActivationRequest: %v", err)
		}
		if r.RequestID != "req-1" {
			t.Fatalf("request id = %q, want the stored req-1", r.RequestID)
		}
		if r.Status != store.ActivationPending {
			t.Fatalf("status = %q, want pending", r.Status)
		}
		if r.AmountFen == nil || *r.AmountFen != 19900 {
			t.Fatalf("amount = %v, want 19900", r.AmountFen)
		}
	})

	t.Run("a second tap for one machine refreshes the row and keeps its id", func(t *testing.T) {
		// The upsert's UPDATE branch: same machine, same account, so the row
		// is refreshed in place — one line on the seller's console, and the
		// customer's quoted 单号 survives.
		s := newTestStore(t)
		acct := seedAccount(t, s, "open-1")
		seedActivationRequest(t, s, "req-1", acct.ID, "install-1", fenPtr(100))

		r, err := s.CreateActivationRequest(ctx, store.ActivationRequest{
			RequestID:    "req-2",
			AccountID:    acct.ID,
			InstallID:    "install-1",
			AnchorDigest: "anchor-2",
			AmountFen:    fenPtr(200),
			CreatedAt:    testLater,
		})
		if err != nil {
			t.Fatalf("CreateActivationRequest: %v", err)
		}
		if r.RequestID != "req-1" {
			t.Fatalf("request id = %q, want the stored req-1, not the caller's echo", r.RequestID)
		}
		if r.AnchorDigest != "anchor-2" || r.AmountFen == nil || *r.AmountFen != 200 {
			t.Fatalf("the refreshed fields did not land: %+v", r)
		}
		var n int
		if err := s.db.QueryRowContext(ctx,
			`SELECT count(*) FROM activation_requests WHERE install_id = 'install-1'`).Scan(&n); err != nil {
			t.Fatalf("count: %v", err)
		}
		if n != 1 {
			t.Fatalf("the machine has %d rows, want 1", n)
		}
	})

	t.Run("a machine whose live request belongs to another account hands that row back", func(t *testing.T) {
		// The upsert's account predicate refused, so the DO UPDATE was a
		// no-op and RETURNING came back empty. The row is handed back
		// untouched rather than adopted: the caller sees a stranger's account
		// id and refuses the purchase in turn, and the first buyer's row
		// keeps its owner and its id.
		s := newTestStore(t)
		acct1 := seedAccount(t, s, "open-1")
		acct2 := seedAccount(t, s, "open-2")
		seedActivationRequest(t, s, "req-1", acct1.ID, "install-1", nil)

		r, err := s.CreateActivationRequest(ctx, store.ActivationRequest{
			RequestID: "req-2", AccountID: acct2.ID, InstallID: "install-1", CreatedAt: testNow,
		})
		if err != nil {
			t.Fatalf("CreateActivationRequest: %v", err)
		}
		if r.AccountID != acct1.ID || r.RequestID != "req-1" {
			t.Fatalf("got the wrong row back: %+v", r)
		}
	})

	t.Run("a minted id reused across machines is a conflict", func(t *testing.T) {
		// The partial index absorbs an install collision; what is left is a
		// primary-key collision, which is a caller reusing a minted id.
		s := newTestStore(t)
		acct := seedAccount(t, s, "open-1")
		seedActivationRequest(t, s, "req-1", acct.ID, "install-1", nil)

		_, err := s.CreateActivationRequest(ctx, store.ActivationRequest{
			RequestID: "req-1", AccountID: acct.ID, InstallID: "install-9", CreatedAt: testNow,
		})
		if !errors.Is(err, store.ErrConflict) {
			t.Fatalf("want store.ErrConflict, got %v", err)
		}
	})
}

func TestLatestActivationRequest(t *testing.T) {
	ctx := context.Background()

	t.Run("the newest row is returned", func(t *testing.T) {
		// Two rows for one account means two purchases: the first is answered
		// and takes its row out of pending — which is also what frees the
		// machine to register again, the upsert being one live request per
		// install id — and the buyer's poller waits on the second.
		s := newTestStore(t)
		acct := seedAccount(t, s, "open-1")
		seedActivationRequestAt(t, s, "req-1", acct.ID, "install-1", nil, testNow)
		seedLiveBinding(t, s, "req-1", "lic-1")
		seedActivationRequestAt(t, s, "req-2", acct.ID, "install-1", nil, testLater)

		r, err := s.LatestActivationRequest(ctx, acct.ID)
		if err != nil {
			t.Fatalf("LatestActivationRequest: %v", err)
		}
		if r.RequestID != "req-2" {
			t.Fatalf("request id = %q, want the newest req-2", r.RequestID)
		}
		if r.AmountFen != nil {
			t.Fatalf("a buyer who did not say must not get an amount: %d", *r.AmountFen)
		}
		if r.ConfirmedAt != nil {
			t.Fatal("a pending request has no confirmation instant")
		}
	})

	t.Run("a buyer with no history is a miss", func(t *testing.T) {
		s := newTestStore(t)
		if _, err := s.LatestActivationRequest(ctx, "acct-x"); !errors.Is(err, store.ErrNotFound) {
			t.Fatalf("want store.ErrNotFound, got %v", err)
		}
	})
}

func TestGetActivationRequest(t *testing.T) {
	ctx := context.Background()

	t.Run("a confirmed row carries the licence", func(t *testing.T) {
		s := newTestStore(t)
		acct := seedAccount(t, s, "open-1")
		seedActivationRequest(t, s, "req-1", acct.ID, "install-1", fenPtr(19900))
		seedLiveBinding(t, s, "req-1", "lic-1")

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
		s := newTestStore(t)
		if _, err := s.GetActivationRequest(ctx, "req-x"); !errors.Is(err, store.ErrNotFound) {
			t.Fatalf("want store.ErrNotFound, got %v", err)
		}
	})
}

func TestGetRequestByInstallID(t *testing.T) {
	ctx := context.Background()

	t.Run("the machine's most recent request is returned", func(t *testing.T) {
		// As above: the answered request frees the machine to register
		// again, and the machine's view is the row in flight, not the
		// history behind it.
		s := newTestStore(t)
		acct := seedAccount(t, s, "open-1")
		seedActivationRequestAt(t, s, "req-1", acct.ID, "install-1", nil, testNow)
		seedLiveBinding(t, s, "req-1", "lic-1")
		seedActivationRequestAt(t, s, "req-2", acct.ID, "install-1", nil, testLater)

		r, err := s.GetRequestByInstallID(ctx, "install-1")
		if err != nil {
			t.Fatalf("GetRequestByInstallID: %v", err)
		}
		if r.RequestID != "req-2" {
			t.Fatalf("request id = %q, want the newest req-2", r.RequestID)
		}
	})

	t.Run("an unknown machine is a miss", func(t *testing.T) {
		s := newTestStore(t)
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
		s := newTestStore(t)
		acct := seedAccount(t, s, "open-1")
		seedActivationRequest(t, s, "req-1", acct.ID, "install-1", nil)
		seedLiveBinding(t, s, "req-1", "lic-1")

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
	})

	t.Run("a status filter is passed through", func(t *testing.T) {
		s := newTestStore(t)
		acct := seedAccount(t, s, "open-1")
		seedActivationRequest(t, s, "req-1", acct.ID, "install-1", nil)

		views, err := s.ListActivationRequests(ctx, store.ActivationPending)
		if err != nil {
			t.Fatalf("ListActivationRequests: %v", err)
		}
		if len(views) != 1 {
			t.Fatalf("want 1 pending request, got %d", len(views))
		}

		views, err = s.ListActivationRequests(ctx, store.ActivationConfirmed)
		if err != nil {
			t.Fatalf("ListActivationRequests: %v", err)
		}
		if len(views) != 0 {
			t.Fatalf("a filter for confirmed returned %d pending rows", len(views))
		}
	})
}

func TestConfirmActivationRequestIsOneTransaction(t *testing.T) {
	ctx := context.Background()
	s := newTestStore(t)

	acct := seedAccount(t, s, "open-1")
	seedActivationRequest(t, s, "req-1", acct.ID, "install-1", nil)

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

	// The binding is installed in the same transaction: the machine holds the
	// licence, and the bytes on the binding's request are the ones written.
	b, err := s.LiveBindingByInstallID(ctx, "install-1")
	if err != nil {
		t.Fatalf("LiveBindingByInstallID: %v", err)
	}
	if b.LicenseID != "lic-new" || b.AccountID != acct.ID || b.RequestID != "req-1" {
		t.Fatalf("unexpected binding: %+v", b)
	}
}

func TestConfirmActivationRequestRefusesAnAlreadyAnsweredRow(t *testing.T) {
	ctx := context.Background()

	// Two sellers, one button. The second press finds the row already
	// answered, writes nothing, and reads ErrConflict — the console's cue to
	// re-read the row and show the idempotent answer. Confirmed and rejected
	// are the same story here: the row is no longer pending, and neither
	// state is the caller's to overwrite.
	for _, status := range []store.ActivationStatus{store.ActivationConfirmed, store.ActivationRejected} {
		t.Run(string(status), func(t *testing.T) {
			s := newTestStore(t)
			acct := seedAccount(t, s, "open-1")
			seedActivationRequest(t, s, "req-1", acct.ID, "install-1", nil)
			if status == store.ActivationConfirmed {
				seedLiveBinding(t, s, "req-1", "lic-1")
			} else {
				execRaw(t, s.db, `UPDATE activation_requests SET status = 'rejected' WHERE request_id = 'req-1'`)
			}

			// What the press must not do is add to what is already there —
			// in the confirmed case that includes the first answer's own
			// binding, which is the row being protected, not a leak.
			var before int
			if err := s.db.QueryRowContext(ctx,
				`SELECT count(*) FROM licence_bindings WHERE install_id = 'install-1'`).Scan(&before); err != nil {
				t.Fatalf("count: %v", err)
			}

			_, err := s.ConfirmActivationRequest(ctx, "req-1", "lic-new", nil, testNow)
			if !errors.Is(err, store.ErrConflict) {
				t.Fatalf("want store.ErrConflict, got %v", err)
			}

			// And nothing was written: no second binding for the machine.
			var after int
			if err := s.db.QueryRowContext(ctx,
				`SELECT count(*) FROM licence_bindings WHERE install_id = 'install-1'`).Scan(&after); err != nil {
				t.Fatalf("count: %v", err)
			}
			if after != before {
				t.Fatalf("the refused press installed a binding: %d rows before, %d after", before, after)
			}
		})
	}
}

func TestConfirmActivationRequestOnAnUnknownRowIsAMiss(t *testing.T) {
	s := newTestStore(t)
	if _, err := s.ConfirmActivationRequest(context.Background(), "req-x", "lic-new", nil, testNow); !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("want store.ErrNotFound, got %v", err)
	}
}

func TestConfirmActivationRequestCollidesOnALiveBinding(t *testing.T) {
	ctx := context.Background()

	// Somebody else already bound a licence to this machine. The partial
	// unique index is the machine-change policy in schema form, and the
	// seller's press must read as a conflict rather than produce a second
	// live binding for one install id.
	t.Run("the machine already holds a live licence", func(t *testing.T) {
		s := newTestStore(t)
		acct := seedAccount(t, s, "open-1")
		seedActivationRequest(t, s, "req-1", acct.ID, "install-1", nil)
		seedLiveBinding(t, s, "req-1", "lic-1")
		// The machine is free to register again — the live index only covers
		// pending requests — and the second registration is what the seller
		// is answering when the collision happens.
		seedActivationRequest(t, s, "req-2", acct.ID, "install-1", nil)

		if _, err := s.ConfirmActivationRequest(ctx, "req-2", "lic-2", nil, testNow); !errors.Is(err, store.ErrConflict) {
			t.Fatalf("want store.ErrConflict, got %v", err)
		}
	})

	t.Run("the account already holds a live licence", func(t *testing.T) {
		// Paying twice is a new purchase, and a second machine for one buyer
		// is a reissue. Neither is something the schema allows to happen
		// silently, so the press reads as a conflict.
		s := newTestStore(t)
		acct := seedAccount(t, s, "open-1")
		seedActivationRequest(t, s, "req-1", acct.ID, "install-1", nil)
		seedLiveBinding(t, s, "req-1", "lic-1")
		seedActivationRequest(t, s, "req-2", acct.ID, "install-2", nil)

		if _, err := s.ConfirmActivationRequest(ctx, "req-2", "lic-2", nil, testNow); !errors.Is(err, store.ErrConflict) {
			t.Fatalf("want store.ErrConflict, got %v", err)
		}
	})

	t.Run("the same licence written twice", func(t *testing.T) {
		// The primary key guards a licence written twice: a re-used id is a
		// caller bug, and it must not read as a second machine's conflict.
		s := newTestStore(t)
		acct1 := seedAccount(t, s, "open-1")
		acct2 := seedAccount(t, s, "open-2")
		seedActivationRequest(t, s, "req-1", acct1.ID, "install-1", nil)
		seedLiveBinding(t, s, "req-1", "lic-1")
		seedActivationRequest(t, s, "req-2", acct2.ID, "install-2", nil)

		if _, err := s.ConfirmActivationRequest(ctx, "req-2", "lic-1", nil, testNow); !errors.Is(err, store.ErrConflict) {
			t.Fatalf("want store.ErrConflict, got %v", err)
		}
	})
}

func TestReissueLicenceRetiresTheOldBindingAndConfirmsThePendingRequest(t *testing.T) {
	ctx := context.Background()

	// The whole point of the reissue: the machine's live binding is retired,
	// the new one is installed, and the buyer's outstanding request is
	// confirmed inside the same transaction — that is how their poller
	// learns about the new licence at all.
	s := newTestStore(t)
	acct := seedAccount(t, s, "open-1")
	seedActivationRequestAt(t, s, "req-0", acct.ID, "install-1", nil, testNow)
	seedLiveBinding(t, s, "req-0", "lic-old")
	seedActivationRequestAt(t, s, "req-1", acct.ID, "install-1", nil, testLater)

	r, err := s.ReissueLicence(ctx, "install-1", "lic-new", []byte(`{"licenseId":"lic-new"}`), testLater)
	if err != nil {
		t.Fatalf("ReissueLicence: %v", err)
	}
	if r.RequestID != "req-1" || r.Status != store.ActivationConfirmed || r.LicenseID != "lic-new" {
		t.Fatalf("unexpected request: %+v", r)
	}
	if string(r.EnvelopeJSON) != `{"licenseId":"lic-new"}` {
		t.Fatalf("envelope = %q", r.EnvelopeJSON)
	}

	// The old binding is retired and the new one is live.
	live, err := s.LiveBindingByInstallID(ctx, "install-1")
	if err != nil {
		t.Fatalf("LiveBindingByInstallID: %v", err)
	}
	if live.LicenseID != "lic-new" {
		t.Fatalf("the machine holds %q, want lic-new", live.LicenseID)
	}
	var revokedAt sql.NullTime
	if err := queryRaw(t, s.db, `SELECT revoked_at FROM licence_bindings WHERE license_id = 'lic-old'`).Scan(&revokedAt); err != nil {
		t.Fatalf("read old binding: %v", err)
	}
	if !revokedAt.Valid {
		t.Fatal("the old binding was not retired")
	}
}

func TestReissueLicenceWithoutAPendingRequestFilesItsOwnRow(t *testing.T) {
	ctx := context.Background()

	// A reissue after a restore: the machine holds a live binding and its
	// only request is long confirmed. The reissue gets a row of its own —
	// a licence with no row is a licence nobody can poll for — carrying the
	// anchor and amount of the earlier registration so the seller's console
	// keeps showing what was bought, and the new binding links back to it.
	s := newTestStore(t)
	acct := seedAccount(t, s, "open-1")
	seedActivationRequest(t, s, "req-1", acct.ID, "install-1", fenPtr(19900))
	seedLiveBinding(t, s, "req-1", "lic-old")

	r, err := s.ReissueLicence(ctx, "install-1", "lic-new", []byte(`{"licenseId":"lic-new"}`), testLater)
	if err != nil {
		t.Fatalf("ReissueLicence: %v", err)
	}
	if r.Status != store.ActivationConfirmed || r.LicenseID != "lic-new" {
		t.Fatalf("unexpected request: %+v", r)
	}
	if r.AccountID != acct.ID {
		t.Fatalf("account = %q, want the retired binding's %q", r.AccountID, acct.ID)
	}
	if !strings.HasPrefix(r.RequestID, "act_") {
		t.Fatalf("the reissue row's id = %q, want the act_ prefix the mock and the client logs use", r.RequestID)
	}
	if r.AnchorDigest != "anchor-req-1" {
		t.Fatalf("anchor = %q, want the earlier registration's", r.AnchorDigest)
	}
	if r.AmountFen == nil || *r.AmountFen != 19900 {
		t.Fatalf("amount = %v, want the earlier registration's 19900", r.AmountFen)
	}

	// The new binding links back to the fresh row, so a re-delivery finds
	// the envelope through it.
	live, err := s.LiveBindingByInstallID(ctx, "install-1")
	if err != nil {
		t.Fatalf("LiveBindingByInstallID: %v", err)
	}
	if live.RequestID != r.RequestID {
		t.Fatalf("the new binding links to %q, want the reissue row %q", live.RequestID, r.RequestID)
	}
}

func TestReissueLicenceAfterARestoreUsesThePendingRequest(t *testing.T) {
	ctx := context.Background()

	// The machine holds nothing live but has an open request — the buyer is
	// mid-purchase on a restored machine. The new binding takes the account
	// from the request, and the request is confirmed with the new licence.
	s := newTestStore(t)
	acct := seedAccount(t, s, "open-1")
	seedActivationRequest(t, s, "req-1", acct.ID, "install-1", nil)

	r, err := s.ReissueLicence(ctx, "install-1", "lic-new", []byte(`{"licenseId":"lic-new"}`), testLater)
	if err != nil {
		t.Fatalf("ReissueLicence: %v", err)
	}
	if r.RequestID != "req-1" || r.Status != store.ActivationConfirmed || r.LicenseID != "lic-new" {
		t.Fatalf("unexpected request: %+v", r)
	}

	live, err := s.LiveBindingByInstallID(ctx, "install-1")
	if err != nil {
		t.Fatalf("LiveBindingByInstallID: %v", err)
	}
	if live.LicenseID != "lic-new" || live.RequestID != "req-1" {
		t.Fatalf("unexpected binding: %+v", live)
	}
}

func TestReissueLicenceForAnUnknownMachineIsAMiss(t *testing.T) {
	ctx := context.Background()

	// No live binding and no request of any kind: the seller is looking at a
	// machine code this service has never heard of. A reissue that invented
	// a licence out of thin air would be a support call nobody can explain.
	s := newTestStore(t)
	if _, err := s.ReissueLicence(ctx, "install-x", "lic-new", nil, testNow); !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("want store.ErrNotFound, got %v", err)
	}
}

func TestRevokeBinding(t *testing.T) {
	ctx := context.Background()

	t.Run("the first revoke writes the date", func(t *testing.T) {
		s := newTestStore(t)
		acct := seedAccount(t, s, "open-1")
		seedActivationRequest(t, s, "req-1", acct.ID, "install-1", nil)
		seedLiveBinding(t, s, "req-1", "lic-1")

		if err := s.RevokeBinding(ctx, "lic-1", testNow); err != nil {
			t.Fatalf("RevokeBinding: %v", err)
		}
		if _, err := s.LiveBindingByInstallID(ctx, "install-1"); !errors.Is(err, store.ErrNotFound) {
			t.Fatalf("a revoked licence is still live: %v", err)
		}
	})

	t.Run("revoking twice is a misclick, not a 404", func(t *testing.T) {
		// A stale console tab: the second press must succeed.
		s := newTestStore(t)
		acct := seedAccount(t, s, "open-1")
		seedActivationRequest(t, s, "req-1", acct.ID, "install-1", nil)
		seedLiveBinding(t, s, "req-1", "lic-1")

		if err := s.RevokeBinding(ctx, "lic-1", testNow); err != nil {
			t.Fatalf("first press: %v", err)
		}
		if err := s.RevokeBinding(ctx, "lic-1", testNow); err != nil {
			t.Fatalf("second press: %v", err)
		}
	})

	t.Run("revoking a licence that never existed is a miss", func(t *testing.T) {
		s := newTestStore(t)
		if err := s.RevokeBinding(ctx, "lic-x", testNow); !errors.Is(err, store.ErrNotFound) {
			t.Fatalf("want store.ErrNotFound, got %v", err)
		}
	})
}

func TestLiveBindingByInstallID(t *testing.T) {
	ctx := context.Background()

	t.Run("the machine's live licence is found", func(t *testing.T) {
		s := newTestStore(t)
		acct := seedAccount(t, s, "open-1")
		seedActivationRequest(t, s, "req-1", acct.ID, "install-1", nil)
		seedLiveBinding(t, s, "req-1", "lic-1")

		b, err := s.LiveBindingByInstallID(ctx, "install-1")
		if err != nil {
			t.Fatalf("LiveBindingByInstallID: %v", err)
		}
		if b.LicenseID != "lic-1" || b.RevokedAt != nil {
			t.Fatalf("unexpected binding: %+v", b)
		}
	})

	t.Run("a revoked licence is not live", func(t *testing.T) {
		s := newTestStore(t)
		acct := seedAccount(t, s, "open-1")
		seedActivationRequest(t, s, "req-1", acct.ID, "install-1", nil)
		seedLiveBinding(t, s, "req-1", "lic-1")
		if err := s.RevokeBinding(ctx, "lic-1", testNow); err != nil {
			t.Fatalf("RevokeBinding: %v", err)
		}

		if _, err := s.LiveBindingByInstallID(ctx, "install-1"); !errors.Is(err, store.ErrNotFound) {
			t.Fatalf("want store.ErrNotFound, got %v", err)
		}
	})

	t.Run("a machine holding nothing is a miss", func(t *testing.T) {
		s := newTestStore(t)
		if _, err := s.LiveBindingByInstallID(ctx, "install-1"); !errors.Is(err, store.ErrNotFound) {
			t.Fatalf("want store.ErrNotFound, got %v", err)
		}
	})
}

func TestListBindingsIsTheLedger(t *testing.T) {
	ctx := context.Background()

	// Everything ever issued, newest first, with the person it belongs to:
	// the join is what makes the seller's after-the-fact reconciliation
	// possible without a query per row.
	t.Run("two bindings, newest first, with their owners", func(t *testing.T) {
		s := newTestStore(t)
		acct1 := seedAccount(t, s, "open-1")
		acct2 := seedAccount(t, s, "open-2")
		seedActivationRequestAt(t, s, "req-1", acct1.ID, "install-1", nil, testNow)
		seedLiveBindingAt(t, s, "req-1", "lic-1", testNow)
		seedActivationRequestAt(t, s, "req-2", acct2.ID, "install-2", nil, testLater)
		seedLiveBindingAt(t, s, "req-2", "lic-2", testLater)

		views, err := s.ListBindings(ctx)
		if err != nil {
			t.Fatalf("ListBindings: %v", err)
		}
		if len(views) != 2 {
			t.Fatalf("want 2 bindings, got %d", len(views))
		}
		if views[0].LicenseID != "lic-2" {
			t.Fatalf("newest first: got %q, want lic-2", views[0].LicenseID)
		}
		if views[0].Nickname != "Nick" || views[0].OpenID != "open-2" {
			t.Fatalf("the account columns did not come along: %+v", views[0])
		}
		if views[1].LicenseID != "lic-1" || views[1].OpenID != "open-1" {
			t.Fatalf("unexpected second row: %+v", views[1])
		}
	})

	t.Run("an empty ledger is a young service, not an error", func(t *testing.T) {
		// Nil rows, nil slice, no failure.
		s := newTestStore(t)
		views, err := s.ListBindings(ctx)
		if err != nil {
			t.Fatalf("ListBindings: %v", err)
		}
		if len(views) != 0 {
			t.Fatalf("want an empty ledger, got %d rows", len(views))
		}
	})
}
