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
			name: "burn redeem code is conditional on being unspent and unrevoked",
			sql:  burnRedeemCodeSQL,
			must: []string{
				"UPDATE redeem_codes",
				"redeemed_at IS NULL",
				"revoked_at IS NULL",
				// The flip and its two payloads must be one statement; the
				// schema's CHECK would reject them apart, which is the point.
				"redeemed_install_id",
				"license_id",
			},
		},
		{
			name: "rotation only consumes a token that is not already consumed",
			sql:  rotateConsumeSQL,
			must: []string{"consumed_at IS NULL", "rotated_to"},
		},
		{
			name: "rotation locks the token and its session",
			sql:  rotateLockSQL,
			must: []string{"FOR UPDATE OF r, s", "consumed_at"},
		},
		{
			name: "a replacement refresh token inherits the old expiry",
			sql:  rotateInsertSQL,
			must: []string{"expires_at", "chain_id"},
		},
		{
			name: "marking an order paid is conditional on it still being pending",
			sql:  markPaidSQL,
			must: []string{"status = 'pending'", "paid_at", "transaction_id", "RETURNING"},
		},
		{
			name: "closing an order only works while it is pending",
			sql:  closeOrderSQL,
			must: []string{"status = 'pending'"},
		},
		{
			name: "a licence is issued once",
			sql:  issueLicenseInsertSQL,
			must: []string{"license_envelope IS NULL"},
		},
		{
			name: "quota cannot be oversold",
			sql:  consumeQuotaSQL,
			must: []string{"quota_limit", "<=", "RETURNING", "used"},
		},
		{
			name: "sign-in never demotes a paying customer",
			sql:  upsertAccountSQL,
			// plan appears only in the INSERT column list; if it turns up in
			// the DO UPDATE, signing in would hand a buy-out back to free.
			must: []string{"ON CONFLICT (open_id) DO UPDATE", "last_login_at"},
		},
		{
			name: "anonymous history is only claimed by whoever still owns it",
			sql:  adoptInstallRecordsSQL,
			must: []string{"account_id IS NULL"},
		},
		{
			name: "completing a ticket is conditional on it being pending",
			sql:  markAuthSessionAuthorizedSQL,
			must: []string{"state = 'pending'", "state = 'authorized'", "account_id"},
		},
		{
			name: "a binding is taken by reviving the row, not by adding one",
			sql:  bindDeviceSQL,
			must: []string{"ON CONFLICT (order_id, install_id) DO UPDATE", "released_at = NULL"},
		},
		{
			name: "the slot count ignores released machines and the machine itself",
			sql:  countLiveBindingsSQL,
			must: []string{"released_at IS NULL", "install_id <> $2"},
		},
		{
			name: "binding serialises on the order row",
			sql:  lockOrderForBindingSQL,
			must: []string{"FOR UPDATE"},
		},
		{
			name: "revoking a chain revokes the sessions and the tokens",
			sql:  revokeChainSessionSQL,
			must: []string{"chain_id = $1", "revoked_at = $2"},
		},
		{
			name: "redeem codes are matched case-insensitively",
			sql:  getRedeemCodeSQL,
			must: []string{"lower(code_hash) = lower($1)"},
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
		"burnRedeemCodeSQL":      burnRedeemCodeSQL,
		"rotateLockSQL":          rotateLockSQL,
		"rotateConsumeSQL":       rotateConsumeSQL,
		"rotateInsertSQL":        rotateInsertSQL,
		"rotateAccessSQL":        rotateAccessSQL,
		"markPaidSQL":            markPaidSQL,
		"closeOrderSQL":          closeOrderSQL,
		"refundOrderSQL":         refundOrderSQL,
		"issueLicenseInsertSQL":  issueLicenseInsertSQL,
		"consumeQuotaSQL":        consumeQuotaSQL,
		"upsertAccountSQL":       upsertAccountSQL,
		"adoptInstallRecordsSQL": adoptInstallRecordsSQL,
		"bindDeviceSQL":          bindDeviceSQL,
		"countLiveBindingsSQL":   countLiveBindingsSQL,
		"getRedeemCodeSQL":       getRedeemCodeSQL,
		"getAuthSessionSQL":      getAuthSessionSQL,
		"createOrderSQL":         createOrderSQL,
		"getOrderSQL":            getOrderSQL,
		"putQrSQL":               putQrSQL,
		"getQrSQL":               getQrSQL,
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
