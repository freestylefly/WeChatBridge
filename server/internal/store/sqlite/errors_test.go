package sqlite

import (
	"context"
	"database/sql"
	"errors"
	"strings"
	"testing"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// The classification layer is where PostgreSQL's named constraints had to be
// re-learned as SQLite's column lists, and it is the one place in this package
// where a mistake is invisible: an unclassified collision still writes the row
// or still fails, it just answers the wrong question to the HTTP layer. So
// every test here produces the real driver error first — a duplicate sign-in,
// a licence written twice, a revocation dated before its licence — and only
// then asks what the classifier made of it.

func TestNotFoundSeparatesAMissFromAnUnhappyDatabase(t *testing.T) {
	if err := notFound("latest activation request", sql.ErrNoRows); !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("no rows must read as a miss, got %v", err)
	}

	locked := errors.New("database is locked")
	err := notFound("latest activation request", locked)
	if errors.Is(err, store.ErrNotFound) {
		t.Fatal("a locked database is not a miss")
	}
	if !strings.Contains(err.Error(), "latest activation request") {
		t.Fatalf("the operation's identity was lost: %v", err)
	}
}

func TestWrapNeverLetsNoRowsReachTheHTTPLayer(t *testing.T) {
	// A caller that wraps a read which was allowed to find nothing is a bug
	// upstream; the damage is contained here rather than surfacing as a 500
	// carrying a driver message.
	if err := wrap("list bindings", sql.ErrNoRows); !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("wrap(sql.ErrNoRows) = %v, want store.ErrNotFound", err)
	}
	err := wrap("list bindings", errors.New("disk I/O error"))
	if !strings.Contains(err.Error(), "sqlite: list bindings: disk I/O error") {
		t.Fatalf("the raw error did not survive the wrap intact: %v", err)
	}
}

func TestConflictWhenUnchanged(t *testing.T) {
	if err := conflictWhenUnchanged("flip", nil, 0); !errors.Is(err, store.ErrConflict) {
		t.Fatalf("zero rows must read as a conflict, got %v", err)
	}
	if err := conflictWhenUnchanged("flip", nil, 1); err != nil {
		t.Fatalf("one changed row is success, got %v", err)
	}
	err := conflictWhenUnchanged("flip", errors.New("boom"), 1)
	if errors.Is(err, store.ErrConflict) || !strings.Contains(err.Error(), "flip") {
		t.Fatalf("a driver error must keep its own identity, got %v", err)
	}
}

func TestUniqueViolationNamesTheColumnsOfARealCollision(t *testing.T) {
	s := newTestStore(t)
	seedAccount(t, s, "open-1")

	// The sign-in upsert is idempotent by design, so the collision is filed
	// the way a second writer racing it would: a plain INSERT of a person
	// WeChat already knows.
	_, err := s.db.Exec(`INSERT INTO accounts (open_id) VALUES ('open-1')`)
	if err == nil {
		t.Fatal("the duplicate sign-in was stored — the unique index is not doing its job")
	}

	targets, ok := uniqueViolation(err)
	if !ok {
		t.Fatalf("a UNIQUE violation was not recognised as one: %v", err)
	}
	if len(targets) != 1 || targets[0] != "accounts.open_id" {
		t.Fatalf("targets = %v, want [accounts.open_id]", targets)
	}
}

func TestUniqueConflictAnswersForTheNamedTargetOnly(t *testing.T) {
	s := newTestStore(t)
	seedAccount(t, s, "open-1")
	_, err := s.db.Exec(`INSERT INTO accounts (open_id) VALUES ('open-1')`)
	if err == nil {
		t.Fatal("the duplicate sign-in was stored")
	}

	if got := uniqueConflict("sign in", constraintAccountOpenID, err); !errors.Is(got, store.ErrConflict) {
		t.Fatalf("a second sign-in for one open id must read as a conflict, got %v", got)
	}

	// The same error, named for a constraint it is not: the caller asked the
	// wrong question, and the answer must say so rather than quietly becoming
	// a conflict.
	got := uniqueConflict("sign in", constraintAuthSessionTicket, err)
	if errors.Is(got, store.ErrConflict) {
		t.Fatal("a collision on accounts.open_id was classified as an auth-session collision")
	}
	if !strings.Contains(got.Error(), "accounts.open_id") {
		t.Fatalf("the real target was lost: %v", got)
	}
}

func TestBindingConflictReadsTheThreeCollisionsAsOneAnswer(t *testing.T) {
	ctx := context.Background()

	// All three collisions mean the same thing to the seller — somebody else
	// got there first — and all three arrive as different SQLite errors: a
	// primary-key code for a re-used licence id, a unique-index code for each
	// partial index that is the machine-change policy in schema form.
	cases := []struct {
		name      string
		licenseID string
		installID string
	}{
		{"the licence written twice", "lic-1", "install-9"},
		{"a second live binding on the machine", "lic-2", "install-1"},
		{"a second live binding on the account", "lic-3", "install-9"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			s := newTestStore(t)
			acct := seedAccount(t, s, "open-1")
			seedActivationRequest(t, s, "req-1", acct.ID, "install-1", nil)
			seedLiveBinding(t, s, "req-1", "lic-1")

			_, err := s.db.ExecContext(ctx,
				`INSERT INTO licence_bindings (license_id, install_id, account_id, issued_at)
				 VALUES ($1, $2, $3, $4)`,
				tc.licenseID, tc.installID, acct.ID, testNow)
			if err == nil {
				t.Fatal("the second binding was stored — the machine-change policy is not being enforced")
			}
			if got := bindingConflict("bind", err); !errors.Is(got, store.ErrConflict) {
				t.Fatalf("bindingConflict = %v, want store.ErrConflict", got)
			}
		})
	}
}

func TestBindingConflictKeepsEverythingElseItsOwnError(t *testing.T) {
	ctx := context.Background()

	t.Run("a foreign key that points nowhere", func(t *testing.T) {
		// A binding for an account that does not exist is a corrupted write,
		// not a race: answering "somebody beat you to it" would hide a data
		// bug behind a 409 that reads like normal traffic.
		s := newTestStore(t)
		_, err := s.db.ExecContext(ctx,
			`INSERT INTO licence_bindings (license_id, install_id, account_id, issued_at)
			 VALUES ('lic-1', 'install-1', 'acct-missing', $1)`, testNow)
		if err == nil {
			t.Fatal("the orphaned binding was stored — foreign keys are not enforced")
		}
		got := bindingConflict("bind", err)
		if errors.Is(got, store.ErrConflict) {
			t.Fatalf("a foreign-key failure read as a conflict: %v", got)
		}
		if !strings.Contains(got.Error(), "sqlite: bind:") {
			t.Fatalf("the error did not keep the operation's identity: %v", got)
		}
	})

	t.Run("a revocation dated before its licence", func(t *testing.T) {
		// The CHECK constraint is the schema saying the fixture lied. It is a
		// constraint failure and nothing else, and it must never collapse
		// into the conflict answer.
		s := newTestStore(t)
		acct := seedAccount(t, s, "open-1")
		_, err := s.db.ExecContext(ctx,
			`INSERT INTO licence_bindings (license_id, install_id, account_id, issued_at, revoked_at)
			 VALUES ('lic-1', 'install-1', $1, $2, $3)`,
			acct.ID, testNow, testExpired)
		if err == nil {
			t.Fatal("the impossible revocation was stored — the CHECK is not doing its job")
		}
		if got := bindingConflict("bind", err); errors.Is(got, store.ErrConflict) {
			t.Fatalf("a CHECK failure read as a conflict: %v", got)
		}
	})

	t.Run("an error that is not from the driver at all", func(t *testing.T) {
		got := bindingConflict("bind", errors.New("boom"))
		if errors.Is(got, store.ErrConflict) || !strings.Contains(got.Error(), "boom") {
			t.Fatalf("a plain error must pass through intact, got %v", got)
		}
	})
}

func TestUniqueViolationIgnoresAnErrorThatIsNotAConstraint(t *testing.T) {
	if _, ok := uniqueViolation(errors.New("boom")); ok {
		t.Fatal("a plain error was read as a unique violation")
	}
}
