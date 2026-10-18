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
			name: "the buyer's poller waits on the newest request",
			sql:  latestActivationRequestSQL,
			must: []string{"ORDER BY created_at DESC", "LIMIT 1"},
		},
		{
			name: "the ledger reads every binding, newest first",
			sql:  listBindingsSQL,
			must: []string{"ORDER BY b.issued_at DESC"},
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
