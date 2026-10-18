package activation

import (
	"context"
	"fmt"
	"sync"
	"time"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// fakeActivationStore is an in-memory ActivationStore for these tests.
//
// It models the database behaviours the handlers depend on, and is
// deliberately strict about them:
//
//   - One live (pending) request per install id. A second registration for
//     the same machine by the same account refreshes that row and keeps its
//     request id — the upsert path. By a different account, the row comes
//     back untouched, with its original owner and id: the account predicate
//     refuses, and the caller must notice rather than adopt.
//
//   - A confirmed or rejected row is not live, so a later registration starts
//     a new row rather than conflicting. That is the partial unique index's
//     exact shape.
//
//   - A licence is live only while the machine's live binding names it.
//     Revocation is a date on the binding in this schema, so the fake marks
//     bindings, not requests: a reissue retires the old binding and installs
//     a new one, and the request row the poller reads is always left
//     carrying the newest licence.
//
//   - Confirm is a conditional flip. Two confirms of one request is one
//     licence too many, and the second one is a conflict, not a success.
//
// Ordering is by insertion sequence rather than by created_at, because the
// handlers ask for "latest" and a fixture written in the same clock tick
// would otherwise be ambiguous.
type fakeActivationStore struct {
	mu       sync.Mutex
	requests []fakeRequest
	bindings []fakeBinding
	seq      int

	// Fault injection. A test that needs to see a handler's 500 path sets
	// one of these; they fire before any state changes, like a database
	// that was briefly unreachable.
	createErr error
	latestErr error
}

type fakeRequest struct {
	store.ActivationRequest
	seq int
}

type fakeBinding struct {
	store.LicenceBinding
	seq int
}

func newFakeStore() *fakeActivationStore {
	return &fakeActivationStore{}
}

// seed puts a request row in directly, bypassing the upsert rules — for tests
// that start from "the seller already confirmed".
func (f *fakeActivationStore) seed(r store.ActivationRequest) store.ActivationRequest {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.seq++
	r.CreatedAt = time.Now().UTC()
	f.requests = append(f.requests, fakeRequest{ActivationRequest: r, seq: f.seq})
	return r
}

// seedBinding puts a binding in directly. A confirmed request whose licence
// nobody can collect is the one state the handlers must refuse, and this is
// how a test builds it.
func (f *fakeActivationStore) seedBinding(b store.LicenceBinding) store.LicenceBinding {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.seq++
	b.IssuedAt = time.Now().UTC()
	f.bindings = append(f.bindings, fakeBinding{LicenceBinding: b, seq: f.seq})
	return b
}

func (f *fakeActivationStore) CreateActivationRequest(_ context.Context, r store.ActivationRequest) (store.ActivationRequest, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.createErr != nil {
		return store.ActivationRequest{}, f.createErr
	}

	live := f.latestPendingByInstall(r.InstallID)
	if live != nil {
		if live.AccountID != r.AccountID {
			// The DO UPDATE's account predicate refused. The row keeps its
			// owner and its id; the caller decides what that means.
			return live.ActivationRequest, nil
		}
		live.AnchorDigest = r.AnchorDigest
		live.AmountFen = r.AmountFen
		return live.ActivationRequest, nil
	}

	f.seq++
	r.Status = store.ActivationPending
	r.CreatedAt = time.Now().UTC()
	f.requests = append(f.requests, fakeRequest{ActivationRequest: r, seq: f.seq})
	return r, nil
}

func (f *fakeActivationStore) LatestActivationRequest(_ context.Context, accountID store.ID) (store.ActivationRequest, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.latestErr != nil {
		return store.ActivationRequest{}, f.latestErr
	}
	var best *fakeRequest
	for i := range f.requests {
		r := &f.requests[i]
		if r.AccountID != accountID {
			continue
		}
		if best == nil || r.seq > best.seq {
			best = r
		}
	}
	if best == nil {
		return store.ActivationRequest{}, store.ErrNotFound
	}
	return best.ActivationRequest, nil
}

func (f *fakeActivationStore) GetActivationRequest(_ context.Context, requestID string) (store.ActivationRequest, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	for i := range f.requests {
		if f.requests[i].RequestID == requestID {
			return f.requests[i].ActivationRequest, nil
		}
	}
	return store.ActivationRequest{}, store.ErrNotFound
}

func (f *fakeActivationStore) GetRequestByInstallID(_ context.Context, installID string) (store.ActivationRequest, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	latest := f.latestByInstall(installID)
	if latest == nil {
		return store.ActivationRequest{}, store.ErrNotFound
	}
	return latest.ActivationRequest, nil
}

func (f *fakeActivationStore) ListActivationRequests(_ context.Context, status store.ActivationStatus) ([]store.ActivationRequestView, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	out := make([]store.ActivationRequestView, 0, len(f.requests))
	for i := len(f.requests) - 1; i >= 0; i-- {
		r := f.requests[i].ActivationRequest
		if status != "" && r.Status != status {
			continue
		}
		out = append(out, store.ActivationRequestView{
			ActivationRequest: r,
			Nickname:          "Nick",
			AvatarURL:         "https://avatar.invalid/a.png",
		})
	}
	return out, nil
}

func (f *fakeActivationStore) ConfirmActivationRequest(_ context.Context, requestID, licenseID string, envelopeJSON []byte, at time.Time) (store.ActivationRequest, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	for i := range f.requests {
		r := &f.requests[i]
		if r.RequestID != requestID {
			continue
		}
		if r.Status != store.ActivationPending {
			return store.ActivationRequest{}, store.ErrConflict
		}
		r.Status = store.ActivationConfirmed
		r.LicenseID = licenseID
		r.EnvelopeJSON = append([]byte(nil), envelopeJSON...)
		confirmedAt := at.UTC()
		r.ConfirmedAt = &confirmedAt
		f.seq++
		f.bindings = append(f.bindings, fakeBinding{LicenceBinding: store.LicenceBinding{
			LicenseID: licenseID,
			InstallID: r.InstallID,
			AccountID: r.AccountID,
			RequestID: requestID,
			IssuedAt:  at.UTC(),
		}, seq: f.seq})
		return r.ActivationRequest, nil
	}
	return store.ActivationRequest{}, store.ErrNotFound
}

func (f *fakeActivationStore) ReissueLicence(_ context.Context, installID, licenseID string, envelopeJSON []byte, at time.Time) (store.ActivationRequest, error) {
	f.mu.Lock()
	defer f.mu.Unlock()

	retiredAccount := ""
	for i := range f.bindings {
		b := &f.bindings[i]
		if b.InstallID != installID || b.RevokedAt != nil {
			continue
		}
		revokedAt := at.UTC()
		b.RevokedAt = &revokedAt
		retiredAccount = b.AccountID
	}

	latest := f.latestByInstall(installID)
	account := retiredAccount
	if account == "" && latest != nil {
		account = latest.AccountID
	}
	if account == "" {
		return store.ActivationRequest{}, store.ErrNotFound
	}

	if latest != nil && latest.Status == store.ActivationPending {
		// Confirm the open request with the new licence: the poller is
		// already waiting on this row.
		latest.Status = store.ActivationConfirmed
		latest.LicenseID = licenseID
		latest.EnvelopeJSON = append([]byte(nil), envelopeJSON...)
		confirmedAt := at.UTC()
		latest.ConfirmedAt = &confirmedAt
		f.seq++
		f.bindings = append(f.bindings, fakeBinding{LicenceBinding: store.LicenceBinding{
			LicenseID: licenseID,
			InstallID: installID,
			AccountID: account,
			RequestID: latest.RequestID,
			IssuedAt:  at.UTC(),
		}, seq: f.seq})
		return latest.ActivationRequest, nil
	}

	// No open request: file a confirmed row of the reissue's own, copying
	// the machine's earlier registration so the console keeps showing what
	// was bought.
	f.seq++
	fresh := store.ActivationRequest{
		RequestID:    fmt.Sprintf("act_fake-%d", f.seq),
		AccountID:    account,
		InstallID:    installID,
		Status:       store.ActivationConfirmed,
		LicenseID:    licenseID,
		EnvelopeJSON: append([]byte(nil), envelopeJSON...),
		CreatedAt:    at.UTC(),
	}
	confirmedAt := at.UTC()
	fresh.ConfirmedAt = &confirmedAt
	if latest != nil {
		fresh.AnchorDigest = latest.AnchorDigest
		fresh.AmountFen = latest.AmountFen
	}
	f.requests = append(f.requests, fakeRequest{ActivationRequest: fresh, seq: f.seq})
	f.seq++
	f.bindings = append(f.bindings, fakeBinding{LicenceBinding: store.LicenceBinding{
		LicenseID: licenseID,
		InstallID: installID,
		AccountID: account,
		RequestID: fresh.RequestID,
		IssuedAt:  at.UTC(),
	}, seq: f.seq})
	return fresh, nil
}

func (f *fakeActivationStore) RevokeBinding(_ context.Context, licenseID string, at time.Time) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	for i := range f.bindings {
		b := &f.bindings[i]
		if b.LicenseID != licenseID {
			continue
		}
		if b.RevokedAt != nil {
			// Already revoked: a misclick on a stale console succeeds.
			return nil
		}
		revokedAt := at.UTC()
		b.RevokedAt = &revokedAt
		return nil
	}
	return store.ErrNotFound
}

func (f *fakeActivationStore) LiveBindingByInstallID(_ context.Context, installID string) (store.LicenceBinding, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	for i := range f.bindings {
		b := f.bindings[i]
		if b.InstallID == installID && b.RevokedAt == nil {
			return b.LicenceBinding, nil
		}
	}
	return store.LicenceBinding{}, store.ErrNotFound
}

func (f *fakeActivationStore) ListBindings(_ context.Context) ([]store.LicenceBindingView, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	out := make([]store.LicenceBindingView, 0, len(f.bindings))
	for i := len(f.bindings) - 1; i >= 0; i-- {
		b := f.bindings[i].LicenceBinding
		out = append(out, store.LicenceBindingView{
			LicenceBinding: b,
			Nickname:       "Nick",
			OpenID:         "open-" + b.AccountID,
		})
	}
	return out, nil
}

// latestPendingByInstall returns the machine's live request, or nil. At most
// one exists: the partial unique index is the whole point.
func (f *fakeActivationStore) latestPendingByInstall(installID string) *fakeRequest {
	var best *fakeRequest
	for i := range f.requests {
		r := &f.requests[i]
		if r.InstallID != installID || r.Status != store.ActivationPending {
			continue
		}
		if best == nil || r.seq > best.seq {
			best = r
		}
	}
	return best
}

// latestByInstall returns the most recent row for a machine, or nil.
func (f *fakeActivationStore) latestByInstall(installID string) *fakeRequest {
	var best *fakeRequest
	for i := range f.requests {
		r := &f.requests[i]
		if r.InstallID != installID {
			continue
		}
		if best == nil || r.seq > best.seq {
			best = r
		}
	}
	return best
}
