package postgres

import (
	"context"
	"errors"
	"time"

	"github.com/jackc/pgx/v5"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

const orderColumns = `
	id, out_trade_no, product_id, plan, amount_fen, currency, status, account_id,
	code_url, created_at, expires_at, paid_at, transaction_id, license_id, license_key`

const createOrderSQL = `
	INSERT INTO orders (id, out_trade_no, product_id, plan, amount_fen, currency, status,
	                    account_id, code_url, created_at, expires_at)
	VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)`

const getOrderSQL = `
	SELECT ` + orderColumns + `
	FROM orders
	WHERE id = $1`

const getOrderByOutTradeNoSQL = `
	SELECT ` + orderColumns + `
	FROM orders
	WHERE out_trade_no = $1`

// findLiveOrderByAccountSQL is the "you already have a payment open" lookup.
// It deliberately does not filter on a plan: a person with a pending buy-out
// order should hear about it before they start a subscription order on the
// same device.
const findLiveOrderByAccountSQL = `
	SELECT ` + orderColumns + `
	FROM orders
	WHERE account_id = $1 AND status = 'pending'
	ORDER BY created_at
	LIMIT 1`

// markPaidSQL is the idempotency boundary for the payment callback.
//
// WeChat re-delivers a callback until it gets a success response, so this
// statement runs as many times as their retry policy decides. The predicate
// `status = 'pending'` is the whole trick: PostgreSQL re-evaluates it while
// holding the row lock, so of two concurrent deliveries exactly one flips the
// order and the other updates zero rows and is told "somebody else already
// did this". One licence is issued, not two.
//
// paid_at and transaction_id are written in the same statement as the status
// because the table's CHECK constraint refuses a paid order without them: a
// chargeback cannot be settled against a row with no payment reference, and a
// callback carrying a transaction id nobody asked for is a forgery attempt.
const markPaidSQL = `
	UPDATE orders SET status = 'paid', paid_at = $3, transaction_id = $2
	WHERE id = $1 AND status = 'pending'
	RETURNING ` + orderColumns

const closeOrderSQL = `
	UPDATE orders SET status = 'closed'
	WHERE id = $1 AND status = 'pending'`

const refundOrderSQL = `
	UPDATE orders SET status = 'refunded', transaction_id = $2
	WHERE id = $1 AND status = 'paid'`

const orderStatusSQL = `
	SELECT status FROM orders WHERE id = $1`

// issueLicenseLookupSQL and issueLicenseInsertSQL together implement "issue
// once, forever". The lookup takes the row lock first, which is what lets the
// answer be "here is the bytes I gave you the first time" instead of a second
// signature.
const (
	issueLicenseLookupSQL = `
		SELECT license_envelope, license_id, license_key
		FROM orders
		WHERE id = $1
		FOR UPDATE`

	issueLicenseInsertSQL = `
		UPDATE orders SET license_id = $2, license_key = $3, license_envelope = $4
		WHERE id = $1 AND license_envelope IS NULL`
)

// CreateOrder records a purchase attempt.
func (s *Store) CreateOrder(ctx context.Context, o store.Order) error {
	status := o.Status
	if status == "" {
		status = store.OrderPending
	}
	currency := o.Currency
	if currency == "" {
		currency = "CNY"
	}
	_, err := s.db.Exec(ctx, createOrderSQL,
		o.ID, o.OutTradeNo, o.ProductID, string(o.Plan), o.AmountFen, currency,
		string(status), o.AccountID, o.CodeURL, utc(o.CreatedAt), utc(o.ExpiresAt))
	// A repeated out_trade_no is the unique index doing its job: one checkout,
	// one order, one QR code.
	if err := uniqueConflict("create order", constraintOrderOutTradeNo, err); err != nil {
		return err
	}
	return nil
}

// GetOrder reads an order by primary key.
func (s *Store) GetOrder(ctx context.Context, id store.ID) (store.Order, error) {
	o, err := scanOrder(s.db.QueryRow(ctx, getOrderSQL, id))
	if err != nil {
		return store.Order{}, notFound("get order", err)
	}
	return o, nil
}

// GetOrderByOutTradeNo reads an order by the number WeChat echoes back.
func (s *Store) GetOrderByOutTradeNo(ctx context.Context, outTradeNo string) (store.Order, error) {
	o, err := scanOrder(s.db.QueryRow(ctx, getOrderByOutTradeNoSQL, outTradeNo))
	if err != nil {
		return store.Order{}, notFound("get order by out trade no", err)
	}
	return o, nil
}

// FindLiveOrderByAccount finds the customer's unfinished purchase, so a second
// device can be pointed at it instead of being handed a bare conflict.
func (s *Store) FindLiveOrderByAccount(ctx context.Context, accountID store.ID) (store.Order, error) {
	o, err := scanOrder(s.db.QueryRow(ctx, findLiveOrderByAccountSQL, accountID))
	if err != nil {
		return store.Order{}, notFound("find live order", err)
	}
	return o, nil
}

// MarkPaid records a successful payment callback.
//
// It is conditional on the order still being pending, which makes it safe to
// run on every redelivery WeChat sends. On the second delivery the row is
// already paid and the caller gets the stored order back rather than an error:
// a retry is not a failure, and the callback handler answers "success" either
// way. The distinction between "already paid" (fine) and "no such order"
// (a forged callback) is made by the follow-up read.
func (s *Store) MarkPaid(ctx context.Context, id store.ID, transactionID string, paidAt time.Time) (store.Order, error) {
	o, err := scanOrder(s.db.QueryRow(ctx, markPaidSQL, id, transactionID, utc(paidAt)))
	if err == nil {
		return o, nil
	}
	if !errors.Is(err, pgx.ErrNoRows) {
		return store.Order{}, wrap("mark paid", err)
	}

	// Zero rows. Either the order is not there — a callback for an order this
	// server never created is a forgery and must be answered as one — or it is
	// in a state that payment cannot move out of, which for a redelivery is
	// the expected outcome and must not be reported as an error.
	var status string
	scanErr := s.db.QueryRow(ctx, orderStatusSQL, id).Scan(&status)
	switch {
	case errors.Is(scanErr, pgx.ErrNoRows):
		return store.Order{}, store.ErrNotFound
	case scanErr != nil:
		return store.Order{}, wrap("mark paid: distinguish conflict from absence", scanErr)
	}

	// The order exists but was already paid — or is closed, or refunded. All
	// three are answers the callback handler reports as success.
	return s.GetOrder(ctx, id)
}

// CloseOrder expires an unpaid order. The conditional update keeps a late
// callback from resurrecting an order the customer was told had expired.
func (s *Store) CloseOrder(ctx context.Context, id store.ID, at time.Time) error {
	tag, err := s.db.Exec(ctx, closeOrderSQL, id)
	if err != nil {
		return wrap("close order", err)
	}
	if tag.RowsAffected() == 0 {
		return s.orderAbsentOrSettled(ctx, id, "close order")
	}
	return nil
}

// RefundOrder marks a paid order refunded.
func (s *Store) RefundOrder(ctx context.Context, id store.ID, transactionID string, at time.Time) error {
	tag, err := s.db.Exec(ctx, refundOrderSQL, id, transactionID)
	if err != nil {
		return wrap("refund order", err)
	}
	if tag.RowsAffected() == 0 {
		return s.orderAbsentOrSettled(ctx, id, "refund order")
	}
	return nil
}

// orderAbsentOrSettled resolves the ambiguity a conditional update leaves: the
// order is either not there, or already in a terminal state.
func (s *Store) orderAbsentOrSettled(ctx context.Context, id store.ID, op string) error {
	var status string
	err := s.db.QueryRow(ctx, orderStatusSQL, id).Scan(&status)
	if errors.Is(err, pgx.ErrNoRows) {
		return store.ErrNotFound
	}
	if err != nil {
		return wrap(op+": distinguish conflict from absence", err)
	}
	return store.ErrConflict
}

// IssueOrderLicense stores the signed envelope against an order and returns
// the bytes.
//
// The first caller gets its own envelope back. Every later caller — a retried
// callback, an impatient client refreshing — gets the identical bytes, never a
// fresh signature. ECDSA picks a random k per signature, so re-signing would
// produce a different envelope for the same licence; the client would verify
// either one, and then every hash, every de-duplication and every audit built
// on top of it would fork on the difference.
func (s *Store) IssueOrderLicense(ctx context.Context, orderID store.ID, licenseID, licenseKey string, envelopeJSON []byte) ([]byte, error) {
	if len(envelopeJSON) == 0 {
		return nil, errors.New("postgres: issue order licence: empty envelope")
	}
	if licenseID == "" {
		return nil, errors.New("postgres: issue order licence: empty licence id")
	}

	var issued []byte
	err := s.inTx(ctx, func(tx pgx.Tx) error {
		var (
			existing    []byte
			existingID  string
			existingKey string
		)
		err := tx.QueryRow(ctx, issueLicenseLookupSQL, orderID).Scan(&existing, &existingID, &existingKey)
		if err != nil {
			if errors.Is(err, pgx.ErrNoRows) {
				return store.ErrNotFound
			}
			return wrap("issue order licence: lock order", err)
		}
		if len(existing) > 0 {
			issued = existing
			return nil
		}

		tag, err := tx.Exec(ctx, issueLicenseInsertSQL, orderID, licenseID, licenseKey, envelopeJSON)
		if err != nil {
			return wrap("issue order licence", err)
		}
		if tag.RowsAffected() == 0 {
			// The row is locked and had no envelope; anything else here is a bug
			// or an external writer, and issuing anyway would break the promise
			// this function makes.
			return store.ErrConflict
		}
		issued = envelopeJSON
		return nil
	})
	if err != nil {
		return nil, err
	}
	return issued, nil
}
