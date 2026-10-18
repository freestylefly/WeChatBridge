package main

import (
	"context"
	"errors"
	"fmt"
	"io/fs"
	"sort"
	"strconv"
	"strings"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgxpool"
)

// The migration runner is deliberately small: read the embedded files, apply
// the ones the database has not seen, in version order, each in its own
// transaction together with the bookkeeping row that says it was applied.
//
// It is not a general-purpose tool. There is no down path on the boot route
// (the .down.sql files are for a human with a backup and a plan), no
// checksum verification, and no advisory locking beyond the transaction
// itself — two instances racing to migrate a fresh database is a deploy bug,
// not something this file should paper over.

// migration is one versioned schema change.
type migration struct {
	Version int
	Name    string // "0001_init" — version plus stem, for the log line
	Up      string
}

// planMigrations reads the embedded SQL and returns what still has to run,
// in version order. It touches no database, which is what makes it testable.
//
// Only .up.sql files apply; a .down.sql in the directory is documentation
// for a human, not something the boot path executes.
func planMigrations(fsys fs.FS, applied map[int]bool) ([]migration, error) {
	entries, err := fs.ReadDir(fsys, ".")
	if err != nil {
		return nil, fmt.Errorf("read migrations: %w", err)
	}

	seen := map[int]string{}
	var migs []migration
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
func parseMigrationName(filename string) (migration, error) {
	bad := func() (migration, error) {
		return migration{}, fmt.Errorf("migration %q does not look like NNNN_name.up.sql", filename)
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
	return migration{Version: version, Name: stem}, nil
}

// appliedVersions is the bookkeeping read: which versions the database says
// it has run. A database that has never been migrated has no table yet, which
// is the same answer as an empty set.
func appliedVersions(ctx context.Context, pool *pgxpool.Pool) (map[int]bool, error) {
	out := map[int]bool{}
	rows, err := pool.Query(ctx, `SELECT version FROM schema_migrations`)
	if err != nil {
		// 42P01 is undefined_table: the first run, before the bookkeeping
		// table exists. Everything else is a real fault.
		var pgErr *pgconn.PgError
		if errors.As(err, &pgErr) && pgErr.Code == "42P01" {
			return out, nil
		}
		return nil, fmt.Errorf("read schema_migrations: %w", err)
	}
	defer rows.Close()
	for rows.Next() {
		var version int
		if err := rows.Scan(&version); err != nil {
			return nil, fmt.Errorf("scan schema_migrations: %w", err)
		}
		out[version] = true
	}
	return out, rows.Err()
}

// applyMigrations runs each planned migration in its own transaction, with
// the bookkeeping row inside the same transaction: a crash between the two
// cannot leave a version half-applied, which is the only failure mode that
// matters here.
func applyMigrations(ctx context.Context, pool *pgxpool.Pool, migs []migration) error {
	if _, err := pool.Exec(ctx, `
		CREATE TABLE IF NOT EXISTS schema_migrations (
			version    integer PRIMARY KEY,
			name       text        NOT NULL,
			applied_at timestamptz NOT NULL DEFAULT now()
		)`); err != nil {
		return fmt.Errorf("create schema_migrations: %w", err)
	}

	for _, m := range migs {
		err := pgx.BeginFunc(ctx, pool, func(tx pgx.Tx) error {
			if _, err := tx.Exec(ctx, m.Up); err != nil {
				return fmt.Errorf("exec: %w", err)
			}
			_, err := tx.Exec(ctx,
				`INSERT INTO schema_migrations (version, name) VALUES ($1, $2)`,
				m.Version, m.Name)
			return err
		})
		if err != nil {
			return fmt.Errorf("apply %s: %w", m.Name, err)
		}
	}
	return nil
}

// migrateFrom brings the database up to the embedded schema and reports what
// it did. The caller owns the pool's lifetime.
func migrateFrom(ctx context.Context, pool *pgxpool.Pool, fsys fs.FS) (applied []string, err error) {
	done, err := appliedVersions(ctx, pool)
	if err != nil {
		return nil, err
	}
	pending, err := planMigrations(fsys, done)
	if err != nil {
		return nil, err
	}
	if err := applyMigrations(ctx, pool, pending); err != nil {
		return nil, err
	}
	for _, m := range pending {
		applied = append(applied, m.Name)
	}
	return applied, nil
}
