// Package migrate applies versioned schema files to the service's database.
//
// The runner is deliberately small: read the embedded files, apply the ones
// the database has not seen, in version order, each in its own transaction
// together with the bookkeeping row that says it was applied.
//
// It is not a general-purpose tool. There is no down path on the boot route
// (the .down.sql files are for a human with a backup and a plan), no
// checksum verification, and no advisory locking beyond the transaction
// itself — two instances racing to migrate a fresh database is a deploy bug,
// not something this package should paper over.
//
// It lives in internal/ rather than beside the command so the store's tests
// can bring up a real schema on a scratch database; the previous home,
// cmd/wcb, is package main and nothing else can import it.
package migrate

import (
	"context"
	"database/sql"
	"fmt"
	"io/fs"
	"sort"
	"strconv"
	"strings"
)

// Migration is one versioned schema change.
type Migration struct {
	Version int
	Name    string // "0001_init" — version plus stem, for the log line
	Up      string
}

// Plan reads the embedded SQL and returns what still has to run, in version
// order. It touches no database, which is what makes it testable.
//
// Only .up.sql files apply; a .down.sql in the directory is documentation
// for a human, not something the boot path executes.
func Plan(fsys fs.FS, applied map[int]bool) ([]Migration, error) {
	entries, err := fs.ReadDir(fsys, ".")
	if err != nil {
		return nil, fmt.Errorf("read migrations: %w", err)
	}

	seen := map[int]string{}
	var migs []Migration
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".up.sql") {
			continue
		}
		m, err := parseMigrationName(e.Name())
		if err != nil {
			return nil, err
		}
		if dup, ok := seen[m.Version]; ok {
			return nil, fmt.Errorf("migrations %s and %s both claim version %04d", dup, m.Name, m.Version)
		}
		seen[m.Version] = m.Name
		if applied[m.Version] {
			continue
		}
		raw, err := fs.ReadFile(fsys, e.Name())
		if err != nil {
			return nil, fmt.Errorf("read migration %s: %w", e.Name(), err)
		}
		m.Up = string(raw)
		migs = append(migs, m)
	}

	sort.Slice(migs, func(i, j int) bool { return migs[i].Version < migs[j].Version })
	return migs, nil
}

// parseMigrationName reads "0001_init.up.sql": four digits, a stem, and the
// up suffix. Anything else is a typo that would otherwise apply in whatever
// order the directory listing happened to return.
func parseMigrationName(filename string) (Migration, error) {
	bad := func() (Migration, error) {
		return Migration{}, fmt.Errorf("migration %q does not look like NNNN_name.up.sql", filename)
	}
	stem := strings.TrimSuffix(filename, ".up.sql")
	if len(stem) < 6 || stem[4] != '_' {
		return bad()
	}
	version, err := strconv.Atoi(stem[:4])
	if err != nil {
		return bad()
	}
	if strings.TrimSpace(stem[5:]) == "" {
		return bad()
	}
	return Migration{Version: version, Name: stem}, nil
}

// bookkeepingTable is where applied versions live. The name is SQLite's
// reserved namespace: a table called sqlite_* cannot be created by hand, so
// this one can never collide with a migration's own tables.
const bookkeepingTable = "schema_migrations"

// AppliedVersions is the bookkeeping read: which versions the database says
// it has run. A database that has never been migrated has no table yet, which
// is the same answer as an empty set.
//
// The table's existence is checked in sqlite_master rather than by catching
// the "no such table" error: error-string matching is a habit that survives
// exactly until the driver rewords its message, and a missed match would
// turn every boot into a failed one.
func AppliedVersions(ctx context.Context, db *sql.DB) (map[int]bool, error) {
	out := map[int]bool{}

	var present int
	if err := db.QueryRowContext(ctx,
		`SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = ?`,
		bookkeepingTable).Scan(&present); err != nil {
		return nil, fmt.Errorf("read sqlite_master: %w", err)
	}
	if present == 0 {
		return out, nil
	}

	rows, err := db.QueryContext(ctx, `SELECT version FROM `+bookkeepingTable)
	if err != nil {
		return nil, fmt.Errorf("read %s: %w", bookkeepingTable, err)
	}
	defer rows.Close()
	for rows.Next() {
		var version int
		if err := rows.Scan(&version); err != nil {
			return nil, fmt.Errorf("scan %s: %w", bookkeepingTable, err)
		}
		out[version] = true
	}
	return out, rows.Err()
}

// Apply runs each planned migration in its own transaction, with the
// bookkeeping row inside the same transaction: a crash between the two
// cannot leave a version half-applied, which is the only failure mode that
// matters here.
//
// The transaction is BEGIN IMMEDIATE, not the default deferred begin: the
// deferred shape is what produces SQLITE_BUSY_SNAPSHOT when a reader
// upgrades to a write, and busy_timeout does not cover it (see the design
// doc, §4.3). Migration time is the one moment two processes are most likely
// to race, so the write lock is taken up front instead.
//
// The migration SQL itself carries no parameters, so one Exec runs the whole
// multi-statement file; the driver executes the statements in order and the
// surrounding transaction is what makes them atomic.
func Apply(ctx context.Context, db *sql.DB, migs []Migration) error {
	conn, err := db.Conn(ctx)
	if err != nil {
		return fmt.Errorf("acquire connection: %w", err)
	}
	defer conn.Close()

	if _, err := conn.ExecContext(ctx, `
		CREATE TABLE IF NOT EXISTS `+bookkeepingTable+` (
			version    INTEGER PRIMARY KEY,
			name       TEXT NOT NULL,
			applied_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
		)`); err != nil {
		return fmt.Errorf("create %s: %w", bookkeepingTable, err)
	}

	for _, m := range migs {
		if err := applyOne(ctx, conn, m); err != nil {
			return fmt.Errorf("apply %s: %w", m.Name, err)
		}
	}
	return nil
}

// applyOne is one migration inside one explicit transaction. It is separate
// from Apply so the rollback path has exactly one owner.
func applyOne(ctx context.Context, conn *sql.Conn, m Migration) error {
	if _, err := conn.ExecContext(ctx, "BEGIN IMMEDIATE"); err != nil {
		return fmt.Errorf("begin: %w", err)
	}

	committed := false
	defer func() {
		if !committed {
			// The context may be the thing that failed; a detached one still
			// gets the rollback out.
			_, _ = conn.ExecContext(context.WithoutCancel(ctx), "ROLLBACK")
		}
	}()

	if _, err := conn.ExecContext(ctx, m.Up); err != nil {
		return fmt.Errorf("exec: %w", err)
	}
	if _, err := conn.ExecContext(ctx,
		`INSERT INTO `+bookkeepingTable+` (version, name) VALUES ($1, $2)`,
		m.Version, m.Name); err != nil {
		return fmt.Errorf("record: %w", err)
	}
	if _, err := conn.ExecContext(ctx, "COMMIT"); err != nil {
		return fmt.Errorf("commit: %w", err)
	}
	committed = true
	return nil
}

// From brings the database up to the embedded schema and reports what it
// did. The caller owns the database's lifetime.
func From(ctx context.Context, db *sql.DB, fsys fs.FS) (applied []string, err error) {
	done, err := AppliedVersions(ctx, db)
	if err != nil {
		return nil, err
	}
	pending, err := Plan(fsys, done)
	if err != nil {
		return nil, err
	}
	if err := Apply(ctx, db, pending); err != nil {
		return nil, err
	}
	for _, m := range pending {
		applied = append(applied, m.Name)
	}
	return applied, nil
}
