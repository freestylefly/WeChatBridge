package auth

import (
	"context"
	"sync"
	"time"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// Credentials is the plaintext pair handed from the redirect handler to the
// poller.
//
// It exists because the store keeps only hashes — correctly, since a database
// that can produce a usable refresh token is a database whose backup is a
// session for whoever restores it. The consequence is that the moment the
// redirect completes, the two tokens exist nowhere but in this map, and the
// client polls with nothing but a ticket.
//
// It is a gap between two requests, not a session. It is readable once, it
// expires with the ticket, and losing it costs a re-scan.
type Credentials struct {
	AccessToken     string
	RefreshToken    string
	AccessExpiresAt time.Time
	Account         store.Account
}

// Handoff is the seam between those two requests. It is an interface so a
// multi-instance deployment can put a shared implementation behind it; the
// in-memory one below is correct for a single process and is *not* correct for
// several, because a redirect that lands on instance A and a poll that lands on
// instance B would find nothing.
type Handoff interface {
	Put(ctx context.Context, ticket string, creds Credentials, ttl time.Duration)
	// Take removes and returns the credentials. The second call for the same
	// ticket reports false. That is the point: whoever holds the ticket gets
	// one chance, and the ticket is 192 bits of randomness the client already
	// holds.
	Take(ctx context.Context, ticket string) (Credentials, bool)
}

// MemoryHandoff is the in-process implementation.
type MemoryHandoff struct {
	mu   sync.Mutex
	now  func() time.Time
	held map[string]handoffEntry
}

type handoffEntry struct {
	creds Credentials
	at    time.Time
}

// NewMemoryHandoff builds an empty handoff. now may be nil, in which case the
// wall clock is used; tests pass one so expiry does not need a sleep.
func NewMemoryHandoff(now func() time.Time) *MemoryHandoff {
	if now == nil {
		now = func() time.Time { return time.Now().UTC() }
	}
	return &MemoryHandoff{now: now, held: make(map[string]handoffEntry)}
}

func (h *MemoryHandoff) Put(_ context.Context, ticket string, creds Credentials, ttl time.Duration) {
	expiry := h.now().Add(ttl)
	h.mu.Lock()
	defer h.mu.Unlock()
	h.held[ticket] = handoffEntry{creds: creds, at: expiry}
	h.sweepLocked()
}

func (h *MemoryHandoff) Take(_ context.Context, ticket string) (Credentials, bool) {
	h.mu.Lock()
	defer h.mu.Unlock()
	entry, ok := h.held[ticket]
	if !ok {
		return Credentials{}, false
	}
	delete(h.held, ticket)
	if !h.now().Before(entry.at) {
		// Expired without ever being collected. The caller sees a ticket in a
		// state it cannot use, which is exactly what the expiry means.
		return Credentials{}, false
	}
	return entry.creds, true
}

// Len reports how many credentials are still held. Tests use it to assert the
// map does not become a leak.
func (h *MemoryHandoff) Len() int {
	h.mu.Lock()
	defer h.mu.Unlock()
	h.sweepLocked()
	return len(h.held)
}

// sweepLocked drops anything past its expiry. Entries are few — one per login
// in flight — so a full pass per Put is cheap, and it means a process that
// stops logging people in stops holding their tokens even if nothing ever
// reads them.
func (h *MemoryHandoff) sweepLocked() {
	now := h.now()
	for k, v := range h.held {
		if !now.Before(v.at) {
			delete(h.held, k)
		}
	}
}
