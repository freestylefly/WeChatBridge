package postgres

import (
	"strings"
	"testing"
)

// TestCriticalSQL is cheap insurance.
//
// Each of these fragments is a business rule. If somebody tidies the SQL —
// removes a predicate "because the lock already covers it", drops a status
// check "because the caller checks it first" — the tests above keep passing
// (the mock is happy with whatever it was told) and the behaviour changes in
// production. Asserting the text costs nothing and turns that class of
// accident into a red build.
func TestCriticalSQL(t *testing.T) {
	cases := []struct {
		name string
		sql  string
		must []string
	}{
		{
			name: "sign-in never demotes a paying customer",
			sql:  upsertAccountSQL,
			// plan appears only in the INSERT column list; if it turns up in
			// the DO UPDATE, signing in would hand a buy-out back to free.
			must: []string{"ON CONFLICT (open_id) DO UPDATE", "last_login_at"},
		},
		{
			name: "completing a ticket is conditional on it being pending",
			sql:  markAuthSessionAuthorizedSQL,
			must: []string{"state = 'pending'", "state = 'authorized'", "account_id"},
		},
		{
			name: "the ticket lock is a real lock",
			sql:  lockAuthSessionSQL,
			must: []string{"FOR UPDATE"},
		},
		{
			name: "a replayed refresh token is still findable",
			sql:  sessionMatchSQL,
			// Without the second arm a replay looks like an unknown token and
			// the revocation path never runs.
			must: []string{"prev_refresh_token_hash = $1"},
		},
		{
			name: "rotation only replaces a token that is live and unrevoked",
			sql:  rotateReplaceSQL,
			must: []string{"refresh_token_hash = $1", "session_revoked_at IS NULL"},
		},
		{
			name: "a confirmation is conditional on the request still being pending",
			sql:  confirmFlipSQL,
			// Two sellers, one button, one licence: the loser reads a conflict
			// and re-reads the row rather than minting a second time.
			must: []string{"status = 'pending'", "status = 'confirmed'", "license_id = $2"},
		},
		{
			name: "a reissue retires the old binding before installing the new one",
			sql:  reissueRetireSQL,
			must: []string{"revoked_at IS NULL", "RETURNING account_id"},
		},
		{
			name: "a reissued licence is filed as a confirmed request of its own",
			sql:  reissueInsertRequestSQL,
			// The row is what the buyer's poller reads after a reissue: a
			// licence with no row is a licence nobody can collect.
			must: []string{"'confirmed'", "license_id", "envelope_json", "confirmed_at"},
		},
		{
			name: "registration is idempotent per machine, per account",
			sql:  createActivationRequestSQL,
			// The partial index is the machine's live registration; the
			// predicate is what stops a stranger's machine code being
			// adopted; RETURNING is what stops the caller's echoed id.
			must: []string{"ON CONFLICT (install_id) WHERE status = 'pending'",
				"WHERE activation_requests.account_id = $2", "RETURNING"},
		},
		{
			name: "revoking only touches a binding that is still live",
			sql:  revokeBindingSQL,
			must: []string{"revoked_at IS NULL"},
		},
		{
			name: "the console's list can ask for every state",
			sql:  listActivationRequestsSQL,
			must: []string{"$1 = '' OR r.status = $1"},
		},
		{
			name: "the console's list qualifies every column it joins",
			sql:  listActivationRequestsSQL,
			// install_id and created_at exist on both tables in this join.
			// An unqualified reference there is SQLSTATE 42702 at runtime —
			// a 500 in the one endpoint the seller depends on — and every
			// other test in this package runs against a fake store, so
			// nothing else would notice. See TestJoinedProjectionsAreQualified.
			must: []string{"r.request_id", "r.install_id", "r.created_at"},
		},
		{
			name: "the buyer's poller waits on the newest request",
			sql:  latestActivationRequestSQL,
			must: []string{"ORDER BY created_at DESC", "LIMIT 1"},
		},
		{
			name: "the ledger reads every binding, newest first",
			sql:  listBindingsSQL,
			must: []string{"ORDER BY b.issued_at DESC"},
		},
		{
			name: "the ledger qualifies every column it joins",
			sql:  listBindingsSQL,
			// The same ambiguity as the console's list, one query over.
			must: []string{"b.license_id", "b.install_id", "b.issued_at"},
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			// Compared with whitespace collapsed: the alignment gofmt imposes
			// inside these constants is not a business rule, and a test that
			// broke on it would train people to reformat rather than to read.
			got := norm(tc.sql)
			for _, want := range tc.must {
				if !strings.Contains(got, norm(want)) {
					t.Errorf("query is missing %q\n---\n%s", want, tc.sql)
				}
			}
		})
	}
}

// TestNoStringBuiltSQL guards the other half of the injection rule: a query
// that was assembled from a value would show up here as a format verb or a
// concatenation, and no test would otherwise notice.
func TestNoStringBuiltSQL(t *testing.T) {
	queries := map[string]string{
		"upsertAccountSQL":             upsertAccountSQL,
		"createAuthSessionSQL":         createAuthSessionSQL,
		"getAuthSessionSQL":            getAuthSessionSQL,
		"lockAuthSessionSQL":           lockAuthSessionSQL,
		"markAuthSessionAuthorizedSQL": markAuthSessionAuthorizedSQL,
		"getAccountSQL":                getAccountSQL,
		"createSessionSQL":             createSessionSQL,
		"sessionMatchSQL":              sessionMatchSQL,
		"rotateReplaceSQL":             rotateReplaceSQL,
		"revokeChainSQL":               revokeChainSQL,
		"createActivationRequestSQL":   createActivationRequestSQL,
		"latestActivationRequestSQL":   latestActivationRequestSQL,
		"getActivationRequestSQL":      getActivationRequestSQL,
		"requestByInstallIDSQL":        requestByInstallIDSQL,
		"listActivationRequestsSQL":    listActivationRequestsSQL,
		"confirmLockSQL":               confirmLockSQL,
		"confirmInsertBindingSQL":      confirmInsertBindingSQL,
		"confirmFlipSQL":               confirmFlipSQL,
		"reissueRetireSQL":             reissueRetireSQL,
		"reissueLatestSQL":             reissueLatestSQL,
		"reissueInsertRequestSQL":      reissueInsertRequestSQL,
		"revokeBindingSQL":             revokeBindingSQL,
		"bindingExistsSQL":             bindingExistsSQL,
		"liveBindingSQL":               liveBindingSQL,
		"listBindingsSQL":              listBindingsSQL,
	}
	for name, q := range queries {
		for _, forbidden := range []string{"%s", "%v", "Sprintf"} {
			if strings.Contains(q, forbidden) {
				t.Errorf("%s looks like it was built from a value (contains %q)", name, forbidden)
			}
		}
	}
}

// TestSignInNeverDemotesPlan is the check that cannot be read off the SQL
// string alone: the DO UPDATE clause must not touch plan, because signing in
// may not hand a paying customer back their free tier.
func TestSignInNeverDemotesPlan(t *testing.T) {
	start := strings.Index(upsertAccountSQL, "DO UPDATE SET")
	if start < 0 {
		t.Fatal("the sign-in upsert is no longer an upsert")
	}
	clause := upsertAccountSQL[start:]
	if end := strings.Index(clause, "RETURNING"); end >= 0 {
		clause = clause[:end]
	}
	if strings.Contains(clause, "plan") {
		t.Fatalf("sign-in writes plan, which would demote a paying customer:\n%s", clause)
	}
}

// TestJoinedProjectionsAreQualified is the check for the queries in this
// package that join accounts, which shares column names with both tables it is
// joined to.
//
// install_id, created_at and issued_at exist on more than one side of those
// joins, so an unqualified reference in the SELECT is SQLSTATE 42702 at
// runtime — a 500 in the endpoints the seller console depends on — and only
// against a real Postgres. Every other test in this package runs on a fake
// store, so nothing else in the suite can see it.
//
// Asserting on the text is the right level: the package deliberately has no
// database in its tests, so this cannot be checked by running the query, and
// checking it with a parser would be more machinery than the invariant is
// worth. What it does hold is that each joined projection is its unqualified
// twin with a prefix on every column, in the same order — which is exactly
// what broke, a list written for a single-table scan reused under a join.
func TestJoinedProjectionsAreQualified(t *testing.T) {
	cases := []struct {
		name         string
		unqualified  string
		qualified    string
		query        string
		prefix       string
		sharedColumn string
	}{
		{
			name:         "activation requests joined to accounts",
			unqualified:  activationRequestColumns,
			qualified:    qualifiedActivationRequestColumns,
			query:        listActivationRequestsSQL,
			prefix:       "r.",
			sharedColumn: "install_id",
		},
		{
			name:         "licence bindings joined to accounts",
			unqualified:  licenceBindingColumns,
			qualified:    qualifiedLicenceBindingColumns,
			query:        listBindingsSQL,
			prefix:       "b.",
			sharedColumn: "install_id",
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if !strings.Contains(tc.unqualified, tc.sharedColumn) {
				t.Fatalf("setup changed: %q is no longer a column both tables have, "+
					"so this case no longer describes a real ambiguity", tc.sharedColumn)
			}

			unqualified := strings.Split(norm(tc.unqualified), ", ")
			qualified := strings.Split(norm(tc.qualified), ", ")

			if len(unqualified) != len(qualified) {
				t.Fatalf("projections disagree on column count: %d unqualified, %d qualified — "+
					"the scanners in scan.go read positionally, so a mismatch misreads rows",
					len(unqualified), len(qualified))
			}

			for i, col := range unqualified {
				if !strings.HasPrefix(qualified[i], tc.prefix) {
					t.Errorf("column %d (%q) is not qualified with %q in the joined projection; "+
						"%q exists on both tables and an unqualified reference is an ambiguity "+
						"error at runtime", i, col, tc.prefix, tc.sharedColumn)
					continue
				}
				if got := strings.TrimPrefix(qualified[i], tc.prefix); got != col {
					t.Errorf("column %d is %q in the joined projection but %q in the single-table one; "+
						"same column, same position — a rename here silently shifts every field after it",
						i, got, col)
				}
			}

			if !strings.Contains(norm(tc.query), norm(tc.qualified)) {
				t.Errorf("the joined query no longer selects the qualified projection")
			}
		})
	}
}
