package postgres

import (
	"errors"
	"fmt"
	"strings"
	"testing"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// The whole point of these mappings is that "the database is unhappy" and
// "somebody else got there first" never collapse into one answer. A store
// that returns ErrConflict for everything looks fine until it is down, and
// then it lies about why.

func TestNotFoundMapping(t *testing.T) {
	t.Run("no rows is a miss, not a failure", func(t *testing.T) {
		err := notFound("get activation request", pgx.ErrNoRows)
		if !errors.Is(err, store.ErrNotFound) {
			t.Fatalf("want store.ErrNotFound, got %v", err)
		}
	})

	t.Run("a dead connection stays a driver error", func(t *testing.T) {
		// Connection refused must not be reported as a 404, or a database
		// outage silently becomes "your order does not exist" for every
		// customer at once.
		err := notFound("get activation request", fmt.Errorf("dial tcp 10.0.0.4:5432: connect: connection refused"))
		if errors.Is(err, store.ErrNotFound) {
			t.Fatal("a network failure was classified as a miss")
		}
		if !strings.Contains(err.Error(), "connect: connection refused") {
			t.Fatalf("the underlying cause was lost: %v", err)
		}
		if !strings.HasPrefix(err.Error(), "postgres: get activation request: ") {
			t.Fatalf("error is not wrapped with operation context: %v", err)
		}
	})

	t.Run("the sentinel survives wrapping", func(t *testing.T) {
		err := fmt.Errorf("http: 500: %w", notFound("get account", pgx.ErrNoRows))
		if !errors.Is(err, store.ErrNotFound) {
			t.Fatalf("sentinel did not survive the caller's wrapping: %v", err)
		}
	})
}

func TestConflictWhenUnchanged(t *testing.T) {
	t.Run("zero rows is a conflict", func(t *testing.T) {
		err := conflictWhenUnchanged("confirm activation request", nil, 0)
		if !errors.Is(err, store.ErrConflict) {
			t.Fatalf("want store.ErrConflict, got %v", err)
		}
	})

	t.Run("one row is success", func(t *testing.T) {
		if err := conflictWhenUnchanged("confirm activation request", nil, 1); err != nil {
			t.Fatalf("want nil, got %v", err)
		}
	})

	t.Run("an error is not a conflict", func(t *testing.T) {
		boom := errors.New("connection reset")
		err := conflictWhenUnchanged("confirm activation request", boom, 0)
		if errors.Is(err, store.ErrConflict) {
			t.Fatal("a driver failure was reported as somebody-else-got-there-first")
		}
		if !errors.Is(err, boom) {
			t.Fatalf("the driver error was swallowed: %v", err)
		}
	})
}

func uniqueErr(constraint string) error {
	return &pgconn.PgError{Code: pgUniqueViolation, ConstraintName: constraint}
}

func TestUniqueConflictMapping(t *testing.T) {
	t.Run("the constraint we mean is a conflict", func(t *testing.T) {
		err := uniqueConflict("create activation request", constraintActivationRequestPK,
			uniqueErr(constraintActivationRequestPK))
		if !errors.Is(err, store.ErrConflict) {
			t.Fatalf("want store.ErrConflict, got %v", err)
		}
	})

	t.Run("a different constraint is not", func(t *testing.T) {
		// Somebody's sign-in ticket is not what stopped this insert. Reporting
		// the conflict here would tell a client "this request already exists"
		// when the truth is a data problem nobody has looked at yet.
		cause := uniqueErr(constraintAuthSessionTicket)
		err := uniqueConflict("create activation request", constraintActivationRequestPK, cause)
		if errors.Is(err, store.ErrConflict) {
			t.Fatal("an unrelated unique violation was reported as ErrConflict")
		}
		if !errors.Is(err, cause) {
			t.Fatalf("the driver error was swallowed: %v", err)
		}
		if !strings.Contains(err.Error(), constraintAuthSessionTicket) {
			t.Fatalf("the failing constraint is not named for the operator: %v", err)
		}
	})

	t.Run("a non-unique driver error is not", func(t *testing.T) {
		err := uniqueConflict("create activation request", constraintActivationRequestPK,
			&pgconn.PgError{Code: "57014", Message: "canceling statement due to statement timeout"})
		if errors.Is(err, store.ErrConflict) {
			t.Fatal("a statement timeout was reported as ErrConflict")
		}
	})

	t.Run("values are not echoed", func(t *testing.T) {
		// PostgreSQL's Detail is "Key (install_id)=(...) already exists" and
		// the value is an install id that has no business in a log line.
		e := &pgconn.PgError{
			Code:           pgUniqueViolation,
			ConstraintName: constraintActivationRequestPK,
			Detail:         "Key (install_id)=(SUPER-SECRET-INSTALL) already exists.",
		}
		got := uniqueConflict("create activation request", constraintActivationRequestPK, e).Error()
		if strings.Contains(got, "SUPER-SECRET-INSTALL") {
			t.Fatalf("the offending value reached the error message: %s", got)
		}
	})
}

func TestBindingConflictMapping(t *testing.T) {
	// The machine-change policy lives in the two partial unique indexes, and
	// the primary key guards a licence written twice. All three collision
	// shapes must read as "somebody else got there first" to the caller.
	cases := []struct {
		name       string
		constraint string
	}{
		{"the same licence written twice", constraintLicenceBindingPK},
		{"a second live binding on one machine", constraintBindingLiveInstall},
		{"a second live binding on one account", constraintBindingLiveAccount},
	}
	for _, tc := range cases {
		t.Run(tc.name+" is a conflict", func(t *testing.T) {
			err := bindingConflict("confirm activation request: bind", uniqueErr(tc.constraint))
			if !errors.Is(err, store.ErrConflict) {
				t.Fatalf("want store.ErrConflict, got %v", err)
			}
		})
	}

	t.Run("an unrelated constraint is not", func(t *testing.T) {
		cause := uniqueErr(constraintAccountOpenID)
		err := bindingConflict("confirm activation request: bind", cause)
		if errors.Is(err, store.ErrConflict) {
			t.Fatal("an unrelated unique violation was reported as ErrConflict")
		}
		if !errors.Is(err, cause) {
			t.Fatalf("the driver error was swallowed: %v", err)
		}
		if !strings.Contains(err.Error(), constraintAccountOpenID) {
			t.Fatalf("the failing constraint is not named for the operator: %v", err)
		}
	})

	t.Run("a non-unique driver error is not", func(t *testing.T) {
		err := bindingConflict("reissue licence: bind",
			&pgconn.PgError{Code: "57014", Message: "canceling statement due to statement timeout"})
		if errors.Is(err, store.ErrConflict) {
			t.Fatal("a statement timeout was reported as ErrConflict")
		}
	})
}

func TestWrapDoesNotSwallow(t *testing.T) {
	if err := wrap("do a thing", nil); err != nil {
		t.Fatalf("wrap(nil) must stay nil, got %v", err)
	}
	// A caller that forgot to use notFound still gets a miss rather than a 500
	// carrying a driver message.
	if err := wrap("do a thing", pgx.ErrNoRows); !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("want store.ErrNotFound, got %v", err)
	}
}

func TestRedactionKeepsUnwrap(t *testing.T) {
	cause := errors.New(`cannot parse "postgres://user:hunter2@host/db": invalid port`)
	got := redact(cause, []string{"hunter2", `postgres://user:hunter2@host/db`})

	if strings.Contains(got.Error(), "hunter2") {
		t.Fatalf("the password survived redaction: %s", got.Error())
	}
	if !errors.Is(got, cause) {
		t.Fatal("redaction broke the unwrap chain")
	}
}
