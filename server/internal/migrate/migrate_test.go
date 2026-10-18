package migrate

import (
	"context"
	"database/sql"
	"path/filepath"
	"strings"
	"testing"
	"testing/fstest"

	_ "modernc.org/sqlite"

	"github.com/freestylefly/WeChatBridge/server/migrations"
)

// These tests run against a real SQLite database on a scratch file. The
// runner used to live in cmd/wcb with no tests at all; the parts that were
// never exercised — the bookkeeping read, the per-migration transaction, the
// multi-statement exec — are exactly the parts where a silent mistake leaves
// a database half-migrated with a bookkeeping row that says otherwise.

// newDB opens a scratch database. Each test gets its own file so a failure
// cannot leak into the next test's schema.
func newDB(t *testing.T) *sql.DB {
	t.Helper()
	db, err := sql.Open("sqlite", "file:"+filepath.Join(t.TempDir(), "migrate-test.db")+
		"?_pragma=foreign_keys(ON)&_pragma=busy_timeout(5000)&_txlock=immediate")
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	db.SetMaxOpenConns(1)
	db.SetMaxIdleConns(1)
	t.Cleanup(func() { _ = db.Close() })
	if err := db.Ping(); err != nil {
		t.Fatalf("ping: %v", err)
	}
	return db
}

// tableExists reports whether a table made it into the schema.
func tableExists(t *testing.T, db *sql.DB, name string) bool {
	t.Helper()
	var n int
	if err := db.QueryRow(
		`SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = ?`, name,
	).Scan(&n); err != nil {
		t.Fatalf("sqlite_master: %v", err)
	}
	return n > 0
}

func TestPlanMigrationsAppliesOnlyWhatIsPending(t *testing.T) {
	fsys := fstest.MapFS{
		"0001_init.up.sql":   &fstest.MapFile{Data: []byte("CREATE TABLE one ();")},
		"0002_index.up.sql":  &fstest.MapFile{Data: []byte("CREATE INDEX i ON one (x);")},
		"0003_later.up.sql":  &fstest.MapFile{Data: []byte("CREATE TABLE later ();")},
		"0001_init.down.sql": &fstest.MapFile{Data: []byte("DROP TABLE one;")},
	}

	migs, err := Plan(fsys, map[int]bool{1: true})
	if err != nil {
		t.Fatalf("Plan: %v", err)
	}
	if len(migs) != 2 {
		t.Fatalf("got %d migrations, want 2 (version 1 applied, down file ignored)", len(migs))
	}
	if migs[0].Version != 2 || migs[1].Version != 3 {
		t.Errorf("versions [%d %d], want [2 3]", migs[0].Version, migs[1].Version)
	}
	if !strings.Contains(migs[0].Up, "CREATE INDEX") {
		t.Errorf("migration 2 does not carry its SQL: %q", migs[0].Up)
	}
	if migs[1].Name != "0003_later" {
		t.Errorf("migration name %q, want 0003_later", migs[1].Name)
	}
}

func TestPlanMigrationsOrdersByVersionNotByName(t *testing.T) {
	// The version prefix is parsed as a number, not compared as a string:
	// "0009" before "0010" is the whole reason the names are zero-padded.
	fsys := fstest.MapFS{
		"0010_ten.up.sql":  &fstest.MapFile{Data: []byte("SELECT 10;")},
		"0009_nine.up.sql": &fstest.MapFile{Data: []byte("SELECT 9;")},
	}
	migs, err := Plan(fsys, nil)
	if err != nil {
		t.Fatalf("Plan: %v", err)
	}
	if len(migs) != 2 || migs[0].Version != 9 || migs[1].Version != 10 {
		t.Fatalf("versions %v, want [9 10]", versionsOf(migs))
	}
}

func TestPlanMigrationsRejectsMalformedMigrationNames(t *testing.T) {
	for _, name := range []string{
		"1_init.up.sql",    // too few digits
		"0001.up.sql",      // no stem
		"0001_.up.sql",     // empty stem
		"init.up.sql",      // no version at all
		"abcd_init.up.sql", // non-numeric version
	} {
		fsys := fstest.MapFS{name: &fstest.MapFile{Data: []byte("SELECT 1;")}}
		if _, err := Plan(fsys, nil); err == nil {
			t.Errorf("%q was accepted as a migration name", name)
		}
	}
}

func TestPlanMigrationsIgnoresFilesThatAreNotMigrations(t *testing.T) {
	// The naming convention is the whole contract: a file that does not end
	// in .up.sql is not a migration and is skipped, not second-guessed. The
	// same convention golang-migrate and every other tool of this shape
	// uses — a stray README or a down file must not break the boot.
	fsys := fstest.MapFS{
		"README.md":          &fstest.MapFile{Data: []byte("how to roll back, badly")},
		"0001_init.down.sql": &fstest.MapFile{Data: []byte("DROP TABLE one;")},
		"0001_init.sql":      &fstest.MapFile{Data: []byte("CREATE TABLE one ();")},
		"0001_init.up.SQL":   &fstest.MapFile{Data: []byte("CREATE TABLE one ();")},
		"0002_real.up.sql":   &fstest.MapFile{Data: []byte("CREATE TABLE two ();")},
	}
	migs, err := Plan(fsys, nil)
	if err != nil {
		t.Fatalf("Plan: %v", err)
	}
	if len(migs) != 1 || migs[0].Version != 2 {
		t.Fatalf("planned %v, want just [2]", versionsOf(migs))
	}
}

func TestPlanMigrationsRejectsDuplicateVersions(t *testing.T) {
	fsys := fstest.MapFS{
		"0001_first.up.sql":  &fstest.MapFile{Data: []byte("SELECT 1;")},
		"0001_second.up.sql": &fstest.MapFile{Data: []byte("SELECT 1;")},
	}
	if _, err := Plan(fsys, nil); err == nil {
		t.Fatal("two migrations claiming version 0001 were accepted")
	}
}

func TestPlanMigrationsOnAnEmptyDirectory(t *testing.T) {
	migs, err := Plan(fstest.MapFS{}, nil)
	if err != nil {
		t.Fatalf("Plan: %v", err)
	}
	if len(migs) != 0 {
		t.Fatalf("got %d migrations, want 0", len(migs))
	}
}

func TestPlanMigrationsReadsTheRealEmbeddedFiles(t *testing.T) {
	// The embedded migrations are the ones that will actually run; a
	// planning test against a synthetic FS proves the planner, this proves
	// the embed still holds parseable files.
	migs, err := Plan(migrations.FS, nil)
	if err != nil {
		t.Fatalf("Plan(embedded): %v", err)
	}
	if len(migs) == 0 {
		t.Fatal("the embedded migration set is empty")
	}
	for _, m := range migs {
		if !strings.Contains(strings.ToUpper(m.Up), "CREATE TABLE") {
			t.Errorf("migration %s has no CREATE TABLE — is it really the schema?", m.Name)
		}
	}
}

func TestAppliedVersionsOnAFreshDatabaseIsEmpty(t *testing.T) {
	// The first run has no bookkeeping table. That is the same answer as an
	// empty set, not an error — every deployment's first boot depends on it.
	db := newDB(t)
	done, err := AppliedVersions(context.Background(), db)
	if err != nil {
		t.Fatalf("AppliedVersions: %v", err)
	}
	if len(done) != 0 {
		t.Fatalf("a fresh database reports %v, want nothing applied", done)
	}
}

func TestFromAppliesTheRealEmbeddedSchema(t *testing.T) {
	db := newDB(t)
	ctx := context.Background()

	applied, err := From(ctx, db, migrations.FS)
	if err != nil {
		t.Fatalf("From: %v", err)
	}
	if len(applied) != 1 || applied[0] != "0001_init" {
		t.Fatalf("applied %v, want [0001_init]", applied)
	}

	for _, table := range []string{"accounts", "auth_sessions", "activation_requests", "licence_bindings"} {
		if !tableExists(t, db, table) {
			t.Errorf("table %s is missing after the migration ran", table)
		}
	}
	// The sentinel column the sign-in upsert's created flag depends on.
	var marker int
	if err := db.QueryRow(
		`SELECT count(*) FROM pragma_table_info('accounts') WHERE name = 'insert_marker'`,
	).Scan(&marker); err != nil {
		t.Fatalf("pragma_table_info: %v", err)
	}
	if marker != 1 {
		t.Error("accounts.insert_marker is missing: the sign-in upsert's created flag would break")
	}

	done, err := AppliedVersions(ctx, db)
	if err != nil {
		t.Fatalf("AppliedVersions: %v", err)
	}
	if !done[1] {
		t.Fatalf("bookkeeping says %v, want version 1 applied", done)
	}

	// The bookkeeping row names the migration, so an operator reading the
	// table sees a file name and not just a number.
	var name string
	if err := db.QueryRow(`SELECT name FROM schema_migrations WHERE version = 1`).Scan(&name); err != nil {
		t.Fatalf("read bookkeeping row: %v", err)
	}
	if name != "0001_init" {
		t.Errorf("bookkeeping name = %q, want 0001_init", name)
	}
}

func TestFromIsIdempotent(t *testing.T) {
	db := newDB(t)
	ctx := context.Background()

	if _, err := From(ctx, db, migrations.FS); err != nil {
		t.Fatalf("first From: %v", err)
	}
	applied, err := From(ctx, db, migrations.FS)
	if err != nil {
		t.Fatalf("second From: %v", err)
	}
	if len(applied) != 0 {
		t.Fatalf("second From re-applied %v, want nothing", applied)
	}
}

func TestApplyRollsBackAFailingMigrationWhole(t *testing.T) {
	// A migration file that creates a table and then fails must leave
	// neither the table nor the bookkeeping row: the whole file is one
	// transaction, and "half-applied with a version that claims done" is
	// the one failure mode this design exists to prevent.
	db := newDB(t)
	ctx := context.Background()

	fsys := fstest.MapFS{
		"0001_broken.up.sql": &fstest.MapFile{Data: []byte(
			"CREATE TABLE before_the_failure (id INTEGER PRIMARY KEY);\n" +
				"THIS IS NOT SQL;",
		)},
	}
	if _, err := From(ctx, db, fsys); err == nil {
		t.Fatal("a migration containing invalid SQL was accepted")
	}

	if tableExists(t, db, "before_the_failure") {
		t.Error("the statements before the failure survived: the file was not atomic")
	}
	// The bookkeeping table itself is created outside the per-file
	// transaction, so its existence proves nothing either way; what matters
	// is that it holds no row for the failed version.
	if tableExists(t, db, "schema_migrations") {
		var rows int
		if err := db.QueryRow(`SELECT count(*) FROM schema_migrations`).Scan(&rows); err != nil {
			t.Fatalf("read bookkeeping: %v", err)
		}
		if rows != 0 {
			t.Errorf("bookkeeping holds %d rows after a failed migration, want 0", rows)
		}
	}
}

func TestApplyRecordsEachVersionItRuns(t *testing.T) {
	// Two migrations, both applied, both recorded — the loop in Apply must
	// not stop at the first file.
	db := newDB(t)
	ctx := context.Background()

	fsys := fstest.MapFS{
		"0001_one.up.sql": &fstest.MapFile{Data: []byte("CREATE TABLE one (id INTEGER PRIMARY KEY);")},
		"0002_two.up.sql": &fstest.MapFile{Data: []byte("CREATE TABLE two (id INTEGER PRIMARY KEY);")},
	}
	applied, err := From(ctx, db, fsys)
	if err != nil {
		t.Fatalf("From: %v", err)
	}
	if len(applied) != 2 {
		t.Fatalf("applied %v, want both files", applied)
	}
	if !tableExists(t, db, "one") || !tableExists(t, db, "two") {
		t.Error("a planned migration did not run")
	}
}

// versionsOf is a tiny helper for readable failure messages.
func versionsOf(migs []Migration) []int {
	out := make([]int, 0, len(migs))
	for _, m := range migs {
		out = append(out, m.Version)
	}
	return out
}
