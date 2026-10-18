package billing

import (
	"context"
	"sync"
	"time"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// fakeStore stands in for both store interfaces. It is deliberately the honest
// version of the contract — MarkPaid is conditional on pending, IssueOrderLicense
// returns the first envelope forever — because a permissive fake would let the
// tests pass over exactly the bug this package exists to prevent.
type fakeStore struct {
	mu       sync.Mutex
	orders   map[string]*store.Order
	byTrade  map[string]string
	devices  map[string][]store.LicenceBinding
	envelope map[string][]byte

	markPaidCalls int
	issuedOnce    bool
}

func newFakeStore() *fakeStore {
	return &fakeStore{
		orders:   make(map[string]*store.Order),
		byTrade:  make(map[string]string),
		devices:  make(map[string][]store.LicenceBinding),
		envelope: make(map[string][]byte),
	}
}

func (f *fakeStore) CreateOrder(_ context.Context, o store.Order) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	if _, exists := f.orders[o.ID]; exists {
		return store.ErrConflict
	}
	if id, exists := f.byTrade[o.OutTradeNo]; exists {
		_ = id
		return store.ErrConflict
	}
	clone := o
	f.orders[o.ID] = &clone
	f.byTrade[o.OutTradeNo] = o.ID
	return nil
}

func (f *fakeStore) GetOrder(_ context.Context, id store.ID) (store.Order, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	o, ok := f.orders[id]
	if !ok {
		return store.Order{}, store.ErrNotFound
	}
	return *o, nil
}

func (f *fakeStore) GetOrderByOutTradeNo(_ context.Context, outTradeNo string) (store.Order, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	id, ok := f.byTrade[outTradeNo]
	if !ok {
		return store.Order{}, store.ErrNotFound
	}
	return *f.orders[id], nil
}

// FindLiveOrderByAccount answers "the order this account already has open".
func (f *fakeStore) FindLiveOrderByAccount(_ context.Context, accountID store.ID) (store.Order, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	for _, o := range f.orders {
		if o.AccountID != accountID || o.IsTerminal() {
			continue
		}
		return *o, nil
	}
	return store.Order{}, store.ErrNotFound
}

// MarkPaid is conditional on the order still being pending. WeChat re-delivers
// until it is answered with a success, so this runs more than once per payment.
func (f *fakeStore) MarkPaid(_ context.Context, id store.ID, transactionID string, paidAt time.Time) (store.Order, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	o, ok := f.orders[id]
	if !ok {
		return store.Order{}, store.ErrNotFound
	}
	if o.Status != store.OrderPending {
		return *o, store.ErrConflict
	}
	// Counted only when the conditional update actually applies: WeChat
	// re-delivers for hours, and every delivery attempts the mark.
	f.markPaidCalls++
	o.Status = store.OrderPaid
	paid := paidAt
	o.PaidAt = &paid
	o.TransactionID = transactionID
	return *o, nil
}

func (f *fakeStore) CloseOrder(_ context.Context, id store.ID, _ time.Time) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	if o, ok := f.orders[id]; ok {
		o.Status = store.OrderClosed
	}
	return nil
}

func (f *fakeStore) RefundOrder(_ context.Context, id store.ID, _ string, _ time.Time) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	if o, ok := f.orders[id]; ok {
		o.Status = store.OrderRefunded
	}
	return nil
}

// IssueOrderLicense stores the first envelope and returns the same bytes forever.
// An empty envelope means "give me what you hold".
func (f *fakeStore) IssueOrderLicense(_ context.Context, orderID store.ID, licenseID, licenseKey string, envelopeJSON []byte) ([]byte, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	o, ok := f.orders[orderID]
	if !ok {
		return nil, store.ErrNotFound
	}
	if existing, ok := f.envelope[orderID]; ok {
		return append([]byte(nil), existing...), nil
	}
	if len(envelopeJSON) == 0 {
		return nil, nil
	}
	f.envelope[orderID] = append([]byte(nil), envelopeJSON...)
	o.LicenseID = licenseID
	o.LicenseKey = licenseKey
	return append([]byte(nil), envelopeJSON...), nil
}

// ---- LicenceStore ----

func (f *fakeStore) BindDevice(_ context.Context, orderID store.ID, installID string, plan store.Plan, maxSlots int, at time.Time) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	rows := f.devices[orderID]
	live := 0
	for _, r := range rows {
		if r.ReleasedAt == nil {
			live++
			if r.InstallID == installID {
				return nil
			}
		}
	}
	if live >= maxSlots {
		return store.ErrConflict
	}
	f.devices[orderID] = append(rows, store.LicenceBinding{
		ID: orderID + "-" + installID, OrderID: orderID, InstallID: installID,
		Plan: plan, BoundAt: at,
	})
	return nil
}

func (f *fakeStore) ListDevices(_ context.Context, orderID store.ID) ([]store.LicenceBinding, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]store.LicenceBinding(nil), f.devices[orderID]...), nil
}

func (f *fakeStore) ReleaseDevice(_ context.Context, orderID store.ID, installID string, at time.Time) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	rows := f.devices[orderID]
	for i := range rows {
		if rows[i].InstallID == installID && rows[i].ReleasedAt == nil {
			released := at
			rows[i].ReleasedAt = &released
		}
	}
	return nil
}

func (f *fakeStore) orderCount() int {
	f.mu.Lock()
	defer f.mu.Unlock()
	return len(f.orders)
}

var (
	_ store.BillingStore = (*fakeStore)(nil)
	_ deviceBinder       = (*fakeStore)(nil)
)
