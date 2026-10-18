package admin

import (
	"context"
	"fmt"
	"sync"
	"time"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// fakeStore is an in-memory store for these tests. It implements the two
// narrow interfaces the service takes — AuthStore for the whitelist's
// account read, ActivationStore for the seller's four answers — and models
// the database behaviours the handlers depend on:
//
//   - One live (pending) request per install id; a confirmed or rejected row
//     is not live, so a later registration starts a new row.
//   - Confirm is a conditional flip: a second confirm of one request is a
//     conflict, and the race injection below models the winner landing
//     first.
//   - A reissue retires the machine's live binding and installs a new one,
//     always leaving a confirmed request row carrying the new envelope.
//   - Revocation is a date on the binding; revoking twice succeeds.
//
// The AuthStore methods outside GetAccount are not exercised by this
// package's tests and answer with a loud error rather than a plausible zero
// value: a test that reaches one has found a dependency nobody knew about,
// and a stub that lied would hide it.
type fakeStore struct {
	mu       sync.Mutex
	accounts map[store.ID]store.Account
	requests []fakeRequest
	bindings []fakeBinding
	seq      int

	// Fault injection, fired before any state change, like a database that
	// was briefly unreachable.
	getAccountErr error
	listErr       error
	confirmErr    error
	reissueErr    error
	revokeErr     error

	// raceWinner models another seller's confirm landing between this
	// service's read and its write: the flip refuses, and the row is already
	// confirmed under somebody else's licence by the time the handler
	// re-reads it.
	raceWinner bool
}

type fakeRequest struct {
	store.ActivationRequest
	seq int
}

type fakeBinding struct {
	store.LicenceBinding
	seq int
}

func newFakeStore() *fakeStore {
	return &fakeStore{accounts: map[store.ID]store.Account{}}
}

func (f *fakeStore) seedAccount(a store.Account) store.Account {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.accounts[a.ID] = a
	return a
}

// seedRequest puts a request row in directly, bypassing the upsert rules —
// for tests that start from "the seller is looking at a list". A caller-set
// CreatedAt survives, so a test can pin the rendered timestamp format; a
// zero one is stamped now.
func (f *fakeStore) seedRequest(r store.ActivationRequest) store.ActivationRequest {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.seq++
	if r.CreatedAt.IsZero() {
		r.CreatedAt = time.Now().UTC()
	}
	f.requests = append(f.requests, fakeRequest{ActivationRequest: r, seq: f.seq})
	return r
}

// seedBinding puts a binding in directly: a confirmed request whose licence
// nobody can collect is the one state the handlers must refuse.
func (f *fakeStore) seedBinding(b store.LicenceBinding) store.LicenceBinding {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.seq++
	b.IssuedAt = time.Now().UTC()
	f.bindings = append(f.bindings, fakeBinding{LicenceBinding: b, seq: f.seq})
	return b
}

func (f *fakeStore) liveBinding(installID string) *fakeBinding {
	for i := range f.bindings {
		b := &f.bindings[i]
		if b.InstallID == installID && b.RevokedAt == nil {
			return b
		}
	}
	return nil
}

func (f *fakeStore) latestByInstall(installID string) *fakeRequest {
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

// ---------------------------------------------------------------------------
// store.AuthStore
// ---------------------------------------------------------------------------

func (f *fakeStore) GetAccount(_ context.Context, id store.ID) (store.Account, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.getAccountErr != nil {
		return store.Account{}, f.getAccountErr
	}
	a, ok := f.accounts[id]
	if !ok {
		return store.Account{}, store.ErrNotFound
	}
	return a, nil
}

func (f *fakeStore) CreateAuthSession(context.Context, store.AuthSession) error {
	return fmt.Errorf("fakeStore: CreateAuthSession is not exercised by admin tests")
}

func (f *fakeStore) GetAuthSession(context.Context, string) (store.AuthSession, error) {
	return store.AuthSession{}, fmt.Errorf("fakeStore: GetAuthSession is not exercised by admin tests")
}

func (f *fakeStore) CompleteAuthSession(context.Context, string, string, string, string, string, string) (store.Account, error) {
	return store.Account{}, fmt.Errorf("fakeStore: CompleteAuthSession is not exercised by admin tests")
}

func (f *fakeStore) UpsertAccountByOpenID(context.Context, string, string, string, string) (store.Account, bool, error) {
	return store.Account{}, false, fmt.Errorf("fakeStore: UpsertAccountByOpenID is not exercised by admin tests")
}

func (f *fakeStore) CreateSession(context.Context, store.Session) error {
	return fmt.Errorf("fakeStore: CreateSession is not exercised by admin tests")
}

func (f *fakeStore) GetSessionByRefreshHash(context.Context, string) (store.Session, error) {
	return store.Session{}, fmt.Errorf("fakeStore: GetSessionByRefreshHash is not exercised by admin tests")
}

func (f *fakeStore) RotateRefresh(context.Context, string, string, string, time.Time) (store.Session, store.RefreshOutcome, error) {
	return store.Session{}, "", fmt.Errorf("fakeStore: RotateRefresh is not exercised by admin tests")
}

func (f *fakeStore) RevokeChain(context.Context, string, time.Time) error {
	return fmt.Errorf("fakeStore: RevokeChain is not exercised by admin tests")
}

// ---------------------------------------------------------------------------
// store.ActivationStore
// ---------------------------------------------------------------------------

func (f *fakeStore) CreateActivationRequest(_ context.Context, r store.ActivationRequest) (store.ActivationRequest, error) {
	f.mu.Lock()
	defer f.mu.Unlock()

	var live *fakeRequest
	for i := range f.requests {
		cand := &f.requests[i]
		if cand.InstallID == r.InstallID && cand.Status == store.ActivationPending {
			if live == nil || cand.seq > live.seq {
				live = cand
			}
		}
	}
	if live != nil {
		if live.AccountID != r.AccountID {
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

func (f *fakeStore) LatestActivationRequest(_ context.Context, accountID store.ID) (store.ActivationRequest, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
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

func (f *fakeStore) GetActivationRequest(_ context.Context, requestID string) (store.ActivationRequest, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	for i := range f.requests {
		if f.requests[i].RequestID == requestID {
			return f.requests[i].ActivationRequest, nil
		}
	}
	return store.ActivationRequest{}, store.ErrNotFound
}

func (f *fakeStore) GetRequestByInstallID(_ context.Context, installID string) (store.ActivationRequest, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	latest := f.latestByInstall(installID)
	if latest == nil {
		return store.ActivationRequest{}, store.ErrNotFound
	}
	return latest.ActivationRequest, nil
}

func (f *fakeStore) ListActivationRequests(_ context.Context, status store.ActivationStatus) ([]store.ActivationRequestView, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.listErr != nil {
		return nil, f.listErr
	}
	out := make([]store.ActivationRequestView, 0, len(f.requests))
	for i := len(f.requests) - 1; i >= 0; i-- {
		r := f.requests[i].ActivationRequest
		if status != "" && r.Status != status {
			continue
		}
		nickname := "Nick"
		if a, ok := f.accounts[r.AccountID]; ok {
			nickname = a.Nickname
		}
		out = append(out, store.ActivationRequestView{
			ActivationRequest: r,
			Nickname:          nickname,
			AvatarURL:         "https://avatar.invalid/a.png",
		})
	}
	return out, nil
}

func (f *fakeStore) ConfirmActivationRequest(_ context.Context, requestID, licenseID string, envelopeJSON []byte, at time.Time) (store.ActivationRequest, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.confirmErr != nil {
		return store.ActivationRequest{}, f.confirmErr
	}
	for i := range f.requests {
		r := &f.requests[i]
		if r.RequestID != requestID {
			continue
		}
		if r.Status != store.ActivationPending {
			// Somebody else's confirm landed first — or the row was already
			// confirmed when this one arrived.
			return store.ActivationRequest{}, store.ErrConflict
		}
		if f.raceWinner {
			// Model the race at its most realistic: the other seller's
			// transaction commits before ours, so the flip finds the row
			// confirmed rather than pending.
			r.Status = store.ActivationConfirmed
			r.LicenseID = "lic_racewinner"
			r.EnvelopeJSON = []byte(`{"v":1}`)
			confirmedAt := at.UTC()
			r.ConfirmedAt = &confirmedAt
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

func (f *fakeStore) ReissueLicence(_ context.Context, installID, licenseID string, envelopeJSON []byte, at time.Time) (store.ActivationRequest, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.reissueErr != nil {
		return store.ActivationRequest{}, f.reissueErr
	}

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

func (f *fakeStore) RevokeBinding(_ context.Context, licenseID string, at time.Time) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.revokeErr != nil {
		return f.revokeErr
	}
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

func (f *fakeStore) LiveBindingByInstallID(_ context.Context, installID string) (store.LicenceBinding, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if b := f.liveBinding(installID); b != nil {
		return b.LicenceBinding, nil
	}
	return store.LicenceBinding{}, store.ErrNotFound
}

func (f *fakeStore) ListBindings(_ context.Context) ([]store.LicenceBindingView, error) {
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
