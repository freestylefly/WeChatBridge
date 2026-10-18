package main

import (
	"strings"
	"testing"
	"testing/fstest"

	"github.com/freestylefly/WeChatBridge/server/migrations"
)

func TestPlanMigrationsAppliesOnlyWhatIsPending(t *testing.T) {
	fsys := fstest.MapFS{
		"0001_init.up.sql":   &fstest.MapFile{Data: []byte("CREATE TABLE one ();")},
		"0002_index.up.sql":  &fstest.MapFile{Data: []byte("CREATE INDEX i ON one (x);")},
		"0003_later.up.sql":  &fstest.MapFile{Data: []byte("CREATE TABLE later ();")},
		"0001_init.down.sql": &fstest.MapFile{Data: []byte("DROP TABLE one;")},
	}

	migs, err := planMigrations(fsys, map[int]bool{1: true})
	if err != nil {
		t.Fatalf("planMigrations: %v", err)
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
	migs, err := planMigrations(fsys, nil)
	if err != nil {
		t.Fatalf("planMigrations: %v", err)
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
		if _, err := planMigrations(fsys, nil); err == nil {
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
	migs, err := planMigrations(fsys, nil)
	if err != nil {
		t.Fatalf("planMigrations: %v", err)
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
	if _, err := planMigrations(fsys, nil); err == nil {
		t.Fatal("two migrations claiming version 0001 were accepted")
	}
}

func TestPlanMigrationsOnAnEmptyDirectory(t *testing.T) {
	migs, err := planMigrations(fstest.MapFS{}, nil)
	if err != nil {
		t.Fatalf("planMigrations: %v", err)
	}
	if len(migs) != 0 {
		t.Fatalf("got %d migrations, want 0", len(migs))
	}
}

func TestPlanMigrationsReadsTheRealEmbeddedFiles(t *testing.T) {
	// The embedded migrations are the ones that will actually run; a
	// planning test against a synthetic FS proves the planner, this proves
	// the embed still holds parseable files.
	migs, err := planMigrations(migrations.FS, nil)
	if err != nil {
		t.Fatalf("planMigrations(embedded): %v", err)
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

// versionsOf is a tiny helper for readable failure messages.
func versionsOf(migs []migration) []int {
	out := make([]int, 0, len(migs))
	for _, m := range migs {
		out = append(out, m.Version)
	}
	return out
}
