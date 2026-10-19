package sqlite

import (
	"context"
	"database/sql"
	"errors"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// The Store has to be usable as the one thing the service depends on.
func TestStoreImplementsTheWholeInterface(t *testing.T) {
	var _ store.Store = (*Store)(nil)

	s := newTestStore(t)
	// The compile-time assertion above is the real test; this one makes a
	// regression show up as a named failure rather than as a build error
	// somewhere else in the service.
	var _ store.AuthStore = s
	var _ store.ActivationStore = s
}

func TestNewRejectsAnEmptyDSN(t *testing.T) {
	// Failing here is better than starting a service that answers its first
	// request with a database error.
	if _, err := New(context.Background(), "   "); err == nil {
		t.Fatal("expected an error for an empty dsn")
	}
}

func TestNewRejectsAPostgreSQLURL(t *testing.T) {
	// The variable used to hold a PostgreSQL URL. A deployment that still
	// passes one must be told what changed, not handed the driver's
	// "unable to open database file", which reads like a permissions problem.
	_, err := New(context.Background(), "postgres://wcb:wcb@127.0.0.1:5432/wcb")
	if err == nil {
		t.Fatal("a PostgreSQL URL was accepted")
	}
	if !strings.Contains(err.Error(), "SQLite") {
		t.Fatalf("the rejection does not name the migration: %v", err)
	}
}

func TestNewFailsWhenTheDatabaseFileCannotBeOpened(t *testing.T) {
	// The parent directory does not exist, so the file cannot be created. The
	// point is the shape of the failure: a start-up that pings fails, rather
	// than a service that looks healthy until the first customer.
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	_, err := New(ctx, filepath.Join(t.TempDir(), "no-such-dir", "wcb.db"))
	if err == nil {
		t.Fatal("expected an error against an unopenable path")
	}
	if !strings.Contains(err.Error(), "sqlite:") {
		t.Fatalf("the failure is not attributed to start-up: %v", err)
	}
}

func TestCloseIsRepeatable(t *testing.T) {
	s := newTestStore(t)

	// main defers Close and the error paths call it explicitly; doing that
	// twice must not panic on a closed database.
	for i := 0; i < 3; i++ {
		if err := s.Close(); err != nil {
			t.Fatalf("Close #%d: %v", i+1, err)
		}
	}
}

func TestInTxRollsBackAndKeepsTheReason(t *testing.T) {
	s := newTestStore(t)
	ctx := context.Background()

	boom := errors.New("disk I/O error")
	err := s.inTx(ctx, func(tx querier) error {
		if _, err := tx.ExecContext(ctx,
			`INSERT INTO accounts (open_id) VALUES ('in-a-rolled-back-tx')`); err != nil {
			return err
		}
		return boom
	})
	if !errors.Is(err, boom) {
		t.Fatalf("the caller's reason did not survive the rollback: %v", err)
	}

	// The insert is gone, which is the half the mock suite could never show:
	// a rollback that reports success while leaving the row behind passes
	// every expectation-based test.
	var n int
	if err := s.db.QueryRowContext(ctx,
		`SELECT count(*) FROM accounts WHERE open_id = 'in-a-rolled-back-tx'`).Scan(&n); err != nil {
		t.Fatalf("count: %v", err)
	}
	if n != 0 {
		t.Fatalf("the rolled-back insert survived: %d rows", n)
	}
}

func TestInTxCommitsOnSuccess(t *testing.T) {
	s := newTestStore(t)
	ctx := context.Background()

	if err := s.inTx(ctx, func(tx querier) error {
		_, err := tx.ExecContext(ctx, `INSERT INTO accounts (open_id) VALUES ('in-a-committed-tx')`)
		return err
	}); err != nil {
		t.Fatalf("inTx: %v", err)
	}

	var n int
	if err := s.db.QueryRowContext(ctx,
		`SELECT count(*) FROM accounts WHERE open_id = 'in-a-committed-tx'`).Scan(&n); err != nil {
		t.Fatalf("count: %v", err)
	}
	if n != 1 {
		t.Fatalf("the committed insert is missing: %d rows", n)
	}
}

func TestInTxRollsBackOnPanic(t *testing.T) {
	s := newTestStore(t)
	ctx := context.Background()

	// A panic inside the transaction must still release the write lock and
	// undo the work; the panic itself propagates unchanged.
	func() {
		defer func() {
			if recover() == nil {
				t.Fatal("the panic was swallowed")
			}
		}()
		_ = s.inTx(ctx, func(tx querier) error {
			if _, err := tx.ExecContext(ctx,
				`INSERT INTO accounts (open_id) VALUES ('in-a-panicking-tx')`); err != nil {
				return err
			}
			panic("boom")
		})
	}()

	var n int
	if err := s.db.QueryRowContext(ctx,
		`SELECT count(*) FROM accounts WHERE open_id = 'in-a-panicking-tx'`).Scan(&n); err != nil {
		t.Fatalf("count: %v", err)
	}
	if n != 0 {
		t.Fatalf("the panicking transaction left its row behind: %d rows", n)
	}
}

// TestPragmasAreSetOnEveryConnection is the R6 self-check's other half: the
// DSN fragment is what makes the settings real, so a second handle opened
// from the same path must show them without anybody running a PRAGMA by
// hand. A misspelled pragma name is silently ignored by the driver — this
// test is what turns that silence into a red build.
func TestPragmasAreSetOnEveryConnection(t *testing.T) {
	ctx := context.Background()
	db := newTestDB(t)

	second, err := Open(ctx, dbPathOf(t, db))
	if err != nil {
		t.Fatalf("open a second handle: %v", err)
	}
	defer second.Close()

	var foreignKeys, busyTimeout int
	var journalMode string
	if err := second.QueryRowContext(ctx, `PRAGMA foreign_keys`).Scan(&foreignKeys); err != nil {
		t.Fatalf("foreign_keys: %v", err)
	}
	if err := second.QueryRowContext(ctx, `PRAGMA busy_timeout`).Scan(&busyTimeout); err != nil {
		t.Fatalf("busy_timeout: %v", err)
	}
	if err := second.QueryRowContext(ctx, `PRAGMA journal_mode`).Scan(&journalMode); err != nil {
		t.Fatalf("journal_mode: %v", err)
	}

	if foreignKeys != 1 {
		t.Errorf("foreign_keys = %d on a fresh connection, want 1: the DSN pragma did not take", foreignKeys)
	}
	if busyTimeout != 5000 {
		t.Errorf("busy_timeout = %d on a fresh connection, want 5000", busyTimeout)
	}
	if journalMode != "wal" {
		t.Errorf("journal_mode = %q on a fresh connection, want wal", journalMode)
	}
}

// TestSchemaForeignKeysCascade proves the pragma is not decoration: with
// foreign_keys on, deleting an account takes its activation requests with it,
// which is what the schema's ON DELETE CASCADE promises and what a database
// opened without the pragma would silently not do.
func TestSchemaForeignKeysCascade(t *testing.T) {
	s := newTestStore(t)
	ctx := context.Background()

	acct := seedAccount(t, s, "open-cascade")
	seedActivationRequest(t, s, "req-cascade", acct.ID, "install-cascade", nil)

	execRaw(t, s.db, `DELETE FROM accounts WHERE id = $1`, acct.ID)

	var n int
	if err := s.db.QueryRowContext(ctx,
		`SELECT count(*) FROM activation_requests WHERE request_id = 'req-cascade'`).Scan(&n); err != nil {
		t.Fatalf("count: %v", err)
	}
	if n != 0 {
		t.Fatalf("the orphaned request survived the account's deletion: %d rows", n)
	}
}

// TestBeginImmediateSerialisesTwoWriters is the concurrency claim from the
// design doc, measured rather than asserted: two sellers pressing the same
// button at the same moment must produce one licence and one ErrConflict —
// never a SQLITE_BUSY that would surface as a 500, and never two licences.
func TestBeginImmediateSerialisesTwoWriters(t *testing.T) {
	s := newTestStore(t)
	ctx := context.Background()

	acct := seedAccount(t, s, "open-race")
	seedActivationRequest(t, s, "req-race", acct.ID, "install-race", nil)

	const attempts = 8
	var (
		wg        sync.WaitGroup
		mu        sync.Mutex
		confirmed int
		conflicts int
		failures  []string
	)
	for i := 0; i < attempts; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			_, err := s.ConfirmActivationRequest(ctx, "req-race", "lic-race", []byte(`{"licenseId":"lic-race"}`), testLater)
			mu.Lock()
			defer mu.Unlock()
			switch {
			case err == nil:
				confirmed++
			case errors.Is(err, store.ErrConflict):
				conflicts++
			default:
				failures = append(failures, err.Error())
			}
		}(i)
	}
	wg.Wait()

	if confirmed != 1 {
		t.Errorf("confirmed = %d, want exactly 1", confirmed)
	}
	if conflicts != attempts-1 {
		t.Errorf("conflicts = %d, want %d", conflicts, attempts-1)
	}
	if len(failures) > 0 {
		t.Errorf("presses failed with something other than the designed conflict: %v", failures)
	}

	// And exactly one licence exists, which is the data-plane half of the
	// same claim.
	var licences int
	if err := s.db.QueryRowContext(ctx,
		`SELECT count(*) FROM licence_bindings WHERE request_id = 'req-race'`).Scan(&licences); err != nil {
		t.Fatalf("count: %v", err)
	}
	if licences != 1 {
		t.Errorf("bindings = %d, want 1", licences)
	}
}

// dbPathOf recovers the file a handle was opened with, so a test can open a
// second one against the same database.
func dbPathOf(t *testing.T, db *sql.DB) string {
	t.Helper()
	var file string
	if err := db.QueryRow(`PRAGMA database_list`).Scan(new(string), new(string), &file); err != nil {
		t.Fatalf("database_list: %v", err)
	}
	if file == "" {
		t.Fatal("the scratch database has no file")
	}
	return file
}
