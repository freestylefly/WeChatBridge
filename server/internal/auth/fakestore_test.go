package auth

import (
	"context"
	"errors"
	"sync"
	"time"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// fakeStore is an in-memory AuthStore for these tests.
//
// It is written to be strict rather than convenient, because the tests it
// carries are about the places a convenient fake would lie. In particular:
//
//   - A consumed refresh token stays findable. GetSessionByRefreshHash
//     resolves a hash that has already been rotated, so the caller can reach
//     the chain it belongs to and revoke it. A fake that deleted it would make
//     replay detection untestable, because the replay would look like an
//     unknown token and the revocation path would never run.
//
//   - Rotation is a compare-and-swap on the current hash, under one lock. That
//     is the whole of the reuse check, and the lock is what stops two
//     concurrent rotations from both winning — a property the real transaction
//     gets for free and a test that does not model it cannot check.
type fakeStore struct {
	mu sync.Mutex

	// authSessions are QR scans in flight; sessions are the credentials
	// descended from a completed one. They are separate maps because they are
	// separate things and their keys come from different random spaces.
	authSessions map[string]store.AuthSession
	sessions     map[string]store.Session

	accounts map[string]store.Account
	// byOpenID resolves a WeChat identity to our account id. OpenID is not the
	// primary key and the design document is explicit about why; the fake
	// honours that so nothing here can come to depend on it.
	byOpenID map[string]string

	// refreshIndex maps every refresh hash ever issued to a session id.
	// Retired hashes stay in here on purpose — see the type comment.
	refreshIndex map[string]string
	// currentRefresh is the only hash RotateRefresh will accept for a session.
	currentRefresh map[string]string

	quotas map[string]store.Quota

	// Counters and fault injection, for tests that need to see which branch
	// ran.
	rotateCalls   int
	revokeCalls   int
	completeCalls int
	migrations    map[string]string

	// rotateErr, when set, is returned by RotateRefresh before any state
	// changes. It models a database that was briefly unreachable.
	rotateErr error
	// getSessionErr models the same for the lookup half of a refresh.
	getSessionErr error
}

func newFakeStore() *fakeStore {
	return &fakeStore{
		authSessions:   make(map[string]store.AuthSession),
		sessions:       make(map[string]store.Session),
		accounts:       make(map[string]store.Account),
		byOpenID:       make(map[string]string),
		refreshIndex:   make(map[string]string),
		currentRefresh: make(map[string]string),
		quotas:         make(map[string]store.Quota),
		migrations:     make(map[string]string),
	}
}

func (f *fakeStore) CreateAuthSession(_ context.Context, s store.AuthSession) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	if _, exists := f.authSessions[s.Ticket]; exists {
		return store.ErrConflict
	}
	f.authSessions[s.Ticket] = s
	return nil
}

func (f *fakeStore) GetAuthSession(_ context.Context, ticket string) (store.AuthSession, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	s, ok := f.authSessions[ticket]
	if !ok {
		return store.AuthSession{}, store.ErrNotFound
	}
	return s, nil
}

func (f *fakeStore) CompleteAuthSession(_ context.Context, ticket, openID, unionID, nickname, avatar, installID string) (store.Account, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.completeCalls++

	s, ok := f.authSessions[ticket]
	if !ok {
		return store.Account{}, store.ErrNotFound
	}
	if s.State != store.AuthPending {
		// One-time. A second redirect on the same ticket lands here, and that
		// is the other half of the replay defence.
		return store.Account{}, store.ErrConflict
	}

	account, _, err := f.upsertLocked(openID, unionID, nickname, avatar)
	if err != nil {
		return store.Account{}, err
	}
	// The anonymous history migration is inside the same transaction as the
	// flip, by contract. Recording the install id is how a test asserts the
	// caller did not skip it.
	f.migrations[ticket] = installID

	s.State = store.AuthAuthorized
	s.AccountID = account.ID
	f.authSessions[ticket] = s

	account.LastLoginAt = time.Now().UTC()
	account.InstallID = installID
	f.accounts[account.ID] = account
	return account, nil
}

func (f *fakeStore) UpsertAccountByOpenID(_ context.Context, openID, unionID, nickname, avatar string) (store.Account, bool, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.upsertLocked(openID, unionID, nickname, avatar)
}

func (f *fakeStore) upsertLocked(openID, unionID, nickname, avatar string) (store.Account, bool, error) {
	if openID == "" {
		return store.Account{}, false, errors.New("fakeStore: openid is required")
	}
	if id, ok := f.byOpenID[openID]; ok {
		existing := f.accounts[id]
		if unionID != "" {
			existing.UnionID = unionID
		}
		// A blank field in an upsert must not erase what is stored. Under
		// snsapi_login the profile is usually absent, and a sign-in that
		// wiped the nickname would be a visible regression.
		if nickname != "" {
			existing.Nickname = nickname
		}
		if avatar != "" {
			existing.AvatarURL = avatar
		}
		f.accounts[id] = existing
		return existing, false, nil
	}

	now := time.Now().UTC()
	id := "acct_" + openID
	f.accounts[id] = store.Account{
		ID:          id,
		OpenID:      openID,
		UnionID:     unionID,
		Nickname:    nickname,
		AvatarURL:   avatar,
		Plan:        store.PlanFree,
		CreatedAt:   now,
		LastLoginAt: now,
		WechatBound: true,
	}
	f.byOpenID[openID] = id
	return f.accounts[id], true, nil
}

func (f *fakeStore) GetAccount(_ context.Context, id store.ID) (store.Account, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	a, ok := f.accounts[id]
	if !ok {
		return store.Account{}, store.ErrNotFound
	}
	return a, nil
}

func (f *fakeStore) CreateSession(_ context.Context, s store.Session) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.sessions[s.ID] = s
	f.refreshIndex[s.RefreshTokenHash] = s.ID
	f.currentRefresh[s.ID] = s.RefreshTokenHash
	return nil
}

// GetSessionByRefreshHash resolves any hash the chain ever issued, consumed or
// not. That is what lets a replay reach the revocation path instead of being
// mistaken for an unknown token.
func (f *fakeStore) GetSessionByRefreshHash(_ context.Context, hash string) (store.Session, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.getSessionErr != nil {
		return store.Session{}, f.getSessionErr
	}
	id, ok := f.refreshIndex[hash]
	if !ok {
		return store.Session{}, store.ErrNotFound
	}
	s, ok := f.sessions[id]
	if !ok {
		return store.Session{}, store.ErrNotFound
	}
	return s, nil
}

func (f *fakeStore) RotateRefresh(_ context.Context, oldHash, newHash, newAccessHash string, accessExpiresAt time.Time) (store.Session, store.RefreshOutcome, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.rotateCalls++

	if f.rotateErr != nil {
		// Nothing has changed: the old token is still current, which is what
		// makes a transient failure safe to retry.
		return store.Session{}, store.RefreshTransient, f.rotateErr
	}

	id, ok := f.refreshIndex[oldHash]
	if !ok {
		return store.Session{}, store.RefreshExpired, store.ErrNotFound
	}
	s, ok := f.sessions[id]
	if !ok {
		return store.Session{}, store.RefreshExpired, store.ErrNotFound
	}
	if current, live := f.currentRefresh[id]; live && current != oldHash {
		// The presented token is one this chain already spent. That is a
		// replay, and it is not a conflict to be resolved: the chain goes.
		return s, store.RefreshExpired, store.ErrReuseDetected
	}

	f.currentRefresh[id] = newHash
	f.refreshIndex[newHash] = id
	s.RefreshTokenHash = newHash
	s.AccessTokenHash = newAccessHash
	s.AccessExpiresAt = accessExpiresAt
	f.sessions[id] = s
	return s, store.RefreshRotated, nil
}

func (f *fakeStore) RevokeChain(_ context.Context, chainID string, at time.Time) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.revokeCalls++
	for id, s := range f.sessions {
		if s.ChainID == chainID && s.RevokedAt == nil {
			revoked := at
			s.RevokedAt = &revoked
			f.sessions[id] = s
		}
	}
	return nil
}

func (f *fakeStore) GetQuota(_ context.Context, accountID store.ID) (store.Quota, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	q, ok := f.quotas[accountID]
	if !ok {
		return store.Quota{}, store.ErrNotFound
	}
	return q, nil
}

func (f *fakeStore) ConsumeQuota(_ context.Context, accountID store.ID, units int64) (store.Quota, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	q, ok := f.quotas[accountID]
	if !ok {
		return store.Quota{}, store.ErrNotFound
	}
	q.Used += units
	f.quotas[accountID] = q
	return q, nil
}

// ---------------------------------------------------------------------------
// Test-side controls
// ---------------------------------------------------------------------------

func (f *fakeStore) seedAccount(id store.ID) store.Account {
	f.mu.Lock()
	defer f.mu.Unlock()
	a := store.Account{
		ID:          id,
		OpenID:      "openid-" + id,
		Nickname:    "测试用户",
		Plan:        store.PlanSubscription,
		CreatedAt:   time.Now().UTC(),
		LastLoginAt: time.Now().UTC(),
		WechatBound: true,
	}
	f.accounts[id] = a
	f.byOpenID[a.OpenID] = id
	return a
}

func (f *fakeStore) seedQuota(accountID store.ID, used, limit int64, resetAt time.Time) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.quotas[accountID] = store.Quota{AccountID: accountID, Used: used, Limit: limit, ResetAt: resetAt}
}

func (f *fakeStore) setRotateErr(err error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.rotateErr = err
}

func (f *fakeStore) setGetSessionErr(err error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.getSessionErr = err
}

func (f *fakeStore) setSessionExpiry(id store.ID, refreshExpiresAt time.Time) {
	f.mu.Lock()
	defer f.mu.Unlock()
	s := f.sessions[id]
	s.RefreshExpiresAt = refreshExpiresAt
	f.sessions[id] = s
}

func (f *fakeStore) expireAuthSession(ticket string, at time.Time) {
	f.mu.Lock()
	defer f.mu.Unlock()
	s := f.authSessions[ticket]
	s.ExpiresAt = at
	f.authSessions[ticket] = s
}

// forceAuthSessionState writes a state no code path should produce. It exists
// so a test can check the poller against a row this process did not write.
func (f *fakeStore) forceAuthSessionState(ticket string, state store.AuthSessionState) {
	f.mu.Lock()
	defer f.mu.Unlock()
	s := f.authSessions[ticket]
	s.State = state
	f.authSessions[ticket] = s
}

func (f *fakeStore) authSessionByTicket(ticket string) (store.AuthSession, bool) {
	f.mu.Lock()
	defer f.mu.Unlock()
	s, ok := f.authSessions[ticket]
	return s, ok
}

func (f *fakeStore) sessionByID(id store.ID) (store.Session, bool) {
	f.mu.Lock()
	defer f.mu.Unlock()
	s, ok := f.sessions[id]
	return s, ok
}

func (f *fakeStore) migrationFor(ticket string) (string, bool) {
	f.mu.Lock()
	defer f.mu.Unlock()
	id, ok := f.migrations[ticket]
	return id, ok
}

func (f *fakeStore) isRevoked(chainID string) bool {
	f.mu.Lock()
	defer f.mu.Unlock()
	for _, s := range f.sessions {
		if s.ChainID == chainID && s.RevokedAt != nil {
			return true
		}
	}
	return false
}

func (f *fakeStore) revokeCount() int {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.revokeCalls
}

func (f *fakeStore) completeCount() int {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.completeCalls
}

var _ store.AuthStore = (*fakeStore)(nil)
