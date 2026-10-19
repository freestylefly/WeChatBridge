// Package sqlite implements store.Store on SQLite.
//
// The interesting part of this package is not the queries, it is where the
// transaction boundaries are. Four operations must be atomic and the rest are
// single statements:
//
//	ConfirmActivationRequest  the flip and the binding it installs are one fact
//	ReissueLicence            the retire and the re-bind are one fact
//	RotateRefresh             the old token is consumed and its replacement exists
//	CompleteAuthSession       the ticket is spent and the account is resolved
//
// Everything else is written as a single conditional statement whose row count
// is the answer. See errors.go's notes on error mapping for why the row count is
// read rather than the row.
//
// Nothing in this package logs, and nothing puts a token, a licence or a
// secret into an error: every error path either returns a sentinel from the
// store package or wraps the driver's error with operation context that names
// a table, never a value.
//
// The migration from PostgreSQL is documented in
// docs/design/sqlite-migration.zh-CN.md; the three places where the dialect
// forced a design decision rather than a rewrite are marked in auth.go
// (the insert marker), errors.go (the constraint whitelist) and inTx below
// (BEGIN IMMEDIATE).
package sqlite

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"strings"
	"sync"
	"time"

	// The driver registers itself under this name; the import is blank
	// because nothing outside sql.Open ever names it.
	_ "modernc.org/sqlite"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// Compile-time proof that this type is what the rest of the service expects.
// If an interface in store changes, this line is where the build should break.
var _ store.Store = (*Store)(nil)

// Store is the SQLite-backed implementation of store.Store.
type Store struct {
	db *sql.DB

	closeOnce sync.Once
}

// New opens the database and checks it is actually reachable before
// returning.
//
// The ping matters: a service that starts "successfully" against an
// unreachable database fails on the first customer instead of on the deploy,
// and the two have very different consequences. For SQLite it does double
// duty — a misspelled DSN parameter (the pragma names are silently ignored
// when wrong) only surfaces on the first real statement, so the ping is the
// earliest honest answer about the assembly.
func New(ctx context.Context, dsn string) (*Store, error) {
	if strings.TrimSpace(dsn) == "" {
		return nil, errors.New("sqlite: New: empty dsn")
	}

	db, err := Open(ctx, dsn)
	if err != nil {
		return nil, err
	}
	return &Store{db: db}, nil
}

// Open is New's database half, exposed so the migration runner can bring a
// database up to schema on its own short-lived handle before the store wraps
// one. The two share the DSN and the pool on purpose: two handles to the same
// file is how "another connection holds the write lock" happens.
func Open(ctx context.Context, dsn string) (*sql.DB, error) {
	full, err := buildDSN(dsn)
	if err != nil {
		return nil, err
	}

	db, err := sql.Open("sqlite", full)
	if err != nil {
		return nil, fmt.Errorf("sqlite: open: %w", err)
	}

	// One connection, held for the life of the process. The write lock is
	// database-wide, so a second open connection does not add throughput —
	// it adds a queue of connections waiting for the same lock, each burning
	// its busy_timeout to discover what the first one already knew.
	db.SetMaxOpenConns(1)
	db.SetMaxIdleConns(1)
	db.SetConnMaxLifetime(0)

	pingCtx, cancel := context.WithTimeout(ctx, connectTimeout)
	defer cancel()
	if err := db.PingContext(pingCtx); err != nil {
		_ = db.Close()
		return nil, fmt.Errorf("sqlite: ping: %w", err)
	}

	// The assembly self-check: a pragma the driver did not understand is
	// ignored without a sound, so the one setting the schema's foreign keys
	// depend on is verified rather than assumed.
	if err := checkForeignKeys(ctx, db); err != nil {
		_ = db.Close()
		return nil, err
	}

	return db, nil
}

// Close releases the database. It is safe to call more than once, including
// from a defer that runs after an explicit close on the error path.
func (s *Store) Close() error {
	s.closeOnce.Do(func() { _ = s.db.Close() })
	return nil
}

// pragma DSN fragment. Every entry is a measured decision from the design
// doc; the two that are easy to get wrong by "simplifying":
//
//   - journal_mode(WAL) is persisted in the database file, but the first
//     open of a fresh file must still set it — a new file's default is
//     "delete", and the rollback-journal mode is the one where readers block
//     the writer.
//   - foreign_keys(ON) is per-connection. Setting it once with an Exec after
//     opening does nothing for the next connection; only the DSN replays it
//     onto every connection the pool makes.
//
// The old bare-parameter spelling (journal_mode=WAL&busy_timeout=5000) is
// silently ignored by the driver — every one of those settings would appear
// to work while doing nothing.
const pragmaDSN = "_pragma=journal_mode(WAL)" +
	"&_pragma=busy_timeout(5000)" +
	"&_pragma=foreign_keys(ON)" +
	"&_pragma=synchronous(FULL)" +
	"&_pragma=wal_autocheckpoint(1000)"

// txlockDSN makes every driver-level transaction begin as IMMEDIATE. The
// store's own transactions issue BEGIN IMMEDIATE explicitly (see inTx), so
// this is the belt to that braces: any future implicit transaction gets the
// same write-lock-up-front behaviour instead of the deferred default that
// produces SQLITE_BUSY_SNAPSHOT.
const txlockDSN = "_txlock=immediate"

// buildDSN turns the operator's WCB_DATABASE_URL into a driver DSN.
//
// The accepted shapes are a filesystem path, ":memory:", or a file: URI
// already carrying parameters of its own. A PostgreSQL URL is rejected with
// the migration named: the variable used to hold one, and a deployment that
// still passes one should be told so plainly rather than be handed a
// confusing "unable to open database file" from the driver.
func buildDSN(raw string) (string, error) {
	v := strings.TrimSpace(raw)
	if v == "" {
		return "", errors.New("sqlite: empty database url")
	}

	lower := strings.ToLower(v)
	if strings.HasPrefix(lower, "postgres://") || strings.HasPrefix(lower, "postgresql://") {
		return "", fmt.Errorf("sqlite: %q is a PostgreSQL URL; this service now runs on "+
			"SQLite and WCB_DATABASE_URL names a database file "+
			"(see docs/design/sqlite-migration.zh-CN.md)", v)
	}

	base := v
	if !strings.HasPrefix(lower, "file:") {
		base = "file:" + escapePath(v)
	}

	sep := "?"
	if strings.Contains(base, "?") {
		sep = "&"
	}
	return base + sep + pragmaDSN + "&" + txlockDSN, nil
}

// escapePath makes a filesystem path safe inside a file: URI. Only the three
// characters SQLite's URI parser gives meaning to are touched; everything
// else, Windows drive colons and backslashes included, passes through as the
// driver expects.
func escapePath(p string) string {
	p = strings.ReplaceAll(p, `\`, "/")
	// '%' first: encoding it later would double-encode the escapes added here.
	p = strings.ReplaceAll(p, "%", "%25")
	p = strings.ReplaceAll(p, "?", "%3F")
	p = strings.ReplaceAll(p, "#", "%23")
	return p
}

// checkForeignKeys is the R6 assembly self-check.
func checkForeignKeys(ctx context.Context, db *sql.DB) error {
	var on int
	if err := db.QueryRowContext(ctx, `PRAGMA foreign_keys`).Scan(&on); err != nil {
		return fmt.Errorf("sqlite: pragma self-check: %w", err)
	}
	if on != 1 {
		return fmt.Errorf("sqlite: pragma self-check: foreign_keys reads %d, want 1 — "+
			"without it every ON DELETE action in the schema is silently inert", on)
	}
	return nil
}

// querier is the slice of *sql.DB and *sql.Conn this package actually uses.
// It exists so the account upsert can run inside a transaction during
// sign-in and outside one otherwise.
type querier interface {
	ExecContext(ctx context.Context, query string, args ...any) (sql.Result, error)
	QueryContext(ctx context.Context, query string, args ...any) (*sql.Rows, error)
	QueryRowContext(ctx context.Context, query string, args ...any) *sql.Row
}

// inTx runs fn inside a transaction and commits its work.
//
// The begin is IMMEDIATE, and that is a correctness requirement rather than
// a tuning knob. Every transaction in this package has the shape "read a
// row, then conditionally update it", which is exactly the shape where a
// deferred transaction takes its read snapshot first and then discovers,
// when upgrading to the write, that another writer has moved the database
// on: SQLite answers that with SQLITE_BUSY_SNAPSHOT immediately, without
// consulting busy_timeout, because waiting could deadlock. Taking the write
// lock up front serialises the four transactions at the door instead, and
// the loser of a race reaches the conditional UPDATE — where the designed
// answer is store.ErrConflict, not a 500.
//
// The lock is held on a dedicated connection for the transaction's duration
// rather than through database/sql's Tx: the pool is one connection deep, so
// holding it is also what keeps a second transaction from starting on the
// same handle while this one is open.
//
// fn is responsible for returning a meaningful error: a sentinel from the
// store package when the caller needs to tell "not found" from "somebody else
// got there first", or a wrapped driver error when the database itself is the
// problem. The distinction survives the rollback, because rollback errors are
// joined rather than substituted.
func (s *Store) inTx(ctx context.Context, fn func(tx querier) error) error {
	conn, err := s.db.Conn(ctx)
	if err != nil {
		return fmt.Errorf("sqlite: acquire connection: %w", err)
	}
	defer conn.Close()

	if _, err := conn.ExecContext(ctx, "BEGIN IMMEDIATE"); err != nil {
		return fmt.Errorf("sqlite: begin immediate: %w", err)
	}

	// A panic inside fn must still release the transaction. The rollback uses
	// a context detached from the caller's, because the caller's may well be
	// the thing that panicked.
	defer func() {
		if p := recover(); p != nil {
			_, _ = conn.ExecContext(context.WithoutCancel(ctx), "ROLLBACK")
			panic(p)
		}
	}()

	if err := fn(conn); err != nil {
		_, rbErr := conn.ExecContext(context.WithoutCancel(ctx), "ROLLBACK")
		if rbErr != nil {
			// Join, not replace: the caller still gets to match on the real
			// reason, and the operator still sees that the rollback misbehaved.
			return errors.Join(err, fmt.Errorf("sqlite: rollback: %w", rbErr))
		}
		return err
	}

	if _, err := conn.ExecContext(ctx, "COMMIT"); err != nil {
		return fmt.Errorf("sqlite: commit transaction: %w", err)
	}
	return nil
}

// connectTimeout bounds the start-up ping. The database is a local file, so
// this is not a network budget — it is the ceiling on "the file is on a
// mount that has stopped answering", which is a real failure and not one to
// wait half a minute for.
const connectTimeout = 10 * time.Second
