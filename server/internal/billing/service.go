package billing

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/freestylefly/WeChatBridge/server/internal/httpx"
	"github.com/freestylefly/WeChatBridge/server/internal/license"
	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// Period is what the customer bought.
type Period string

const (
	// PeriodMonthly and PeriodYearly are the two subscription terms.
	PeriodMonthly Period = "monthly"
	PeriodYearly  Period = "yearly"
)

// Currency is the only currency this service prices in.
const Currency = "CNY"

// CreateOrderRequest is the body of POST /v1/billing/orders.
type CreateOrderRequest struct {
	// ClientRequestId is the client's idempotency key. It never changes over the
	// life of one purchase attempt — not on a retry, not after a restart — which
	// is what lets a repeat request find the order it already made instead of
	// making a second one.
	ClientRequestID string `json:"clientRequestId" binding:"required"`

	// ProductID names the product being bought. A mismatch is refused rather than
	// silently charging the wrong price for it.
	ProductID string `json:"productId"`

	// Period is "monthly" or "yearly".
	Period Period `json:"period" binding:"required"`
}

// OrderView is the snapshot the client's poller consumes. The shape is §3's:
// orderId, status, paidAt, licenseKey.
type OrderView struct {
	OrderID   string            `json:"orderId"`
	Status    store.OrderStatus `json:"status"`
	AmountFen int64             `json:"amountFen"`
	Currency  string            `json:"currency"`
	PaidAt    *string           `json:"paidAt"`
	// LicenseKey is an opaque handle on the issued licence. The client's own
	// design says an empty one here is normal and to call /redeem anyway, which
	// is right: the snapshot is a hint, /redeem is the authority.
	LicenseKey string `json:"licenseKey,omitempty"`
	// QRImageURL is where the client downloads the rendered code. It is on this
	// service, not a CDN, so the client must not attach a bearer token to the
	// request that fetches it.
	QRImageURL string `json:"qrImageUrl"`
	ExpiresAt  string `json:"expiresAt"`
	// HTTPStatus is 201 for a new order and 200 when an existing one is returned.
	HTTPStatus int `json:"-"`
}

// RedeemResult is the answer to POST /orders/{id}/redeem.
type RedeemResult struct {
	// LicenseToken is base64url over the envelope JSON, the same encoding the
	// buy-out endpoint uses.
	LicenseToken string `json:"licenseToken"`
	LicenseKey   string `json:"licenseKey"`
	LicenseID    string `json:"licenseId"`
	Subject      string `json:"subject"`
	ExpiresAt    string `json:"expiresAt"`

	HTTPStatus int `json:"-"`
}

// Options is the service's configuration.
type Options struct {
	// ProductID is the subscription product. A request naming another product is
	// refused rather than priced.
	ProductID string

	// MonthlyFen and YearlyFen are prices in integer minor units. There is no
	// float anywhere near money in this package.
	MonthlyFen int64
	YearlyFen  int64

	// OrderTTL is how long a pending order lives.
	OrderTTL time.Duration

	// LicenseLifetime is the term of the licence a paid order buys.
	LicenseLifetime time.Duration

	// MaxDevices is the subscription's device cap, same shape as the buy-out's.
	MaxDevices int

	ClockSkew    time.Duration
	Entitlements []string

	// PublicBaseURL prefixes the QR image URL. Without a trailing slash.
	PublicBaseURL string

	Clock  func() time.Time
	Logger *slog.Logger
}

func (o *Options) now() time.Time {
	if o.Clock != nil {
		return o.Clock()
	}
	return time.Now().UTC()
}

func (o *Options) log() *slog.Logger {
	if o.Logger != nil {
		return o.Logger
	}
	return slog.Default()
}

// deviceBinder is the slice of store.LicenceStore that billing actually needs:
// the subscription edition binds machines to orders, and nothing here touches
// redemption codes. Depending on the whole interface would make every billing test
// stand up a redemption store it never calls.
type deviceBinder interface {
	BindDevice(ctx context.Context, orderID store.ID, installID string, plan store.Plan, maxSlots int, at time.Time) error
	ListDevices(ctx context.Context, orderID store.ID) ([]store.LicenceBinding, error)
}

// The real store satisfies the slice.
var _ deviceBinder = (store.LicenceStore)(nil)

// Service is the billing flow. It depends on the two store interfaces it needs
// rather than the whole union, so a test can supply one at a time.
type Service struct {
	billing  store.BillingStore
	licences deviceBinder
	signer   *license.Signer
	client   *Client
	verifier PlatformVerifier
	opts     Options

	apiV3Key []byte

	mu        sync.Mutex
	png       map[string][]byte
	envelopes map[string][]byte
}

// NewService wires billing. The merchant client and the platform verifier are
// both required: without them orders would be created and never confirmed, and
// callbacks would arrive with nothing to check them against.
func NewService(
	billingStore store.BillingStore,
	licenceStore deviceBinder,
	signer *license.Signer,
	client *Client,
	verifier PlatformVerifier,
	apiV3Key string,
	opts Options,
) (*Service, error) {
	if billingStore == nil {
		return nil, errors.New("billing: billing store is nil")
	}
	if licenceStore == nil {
		return nil, errors.New("billing: licence store is nil")
	}
	if signer == nil {
		return nil, errors.New("billing: signer is nil")
	}
	if client == nil {
		return nil, errors.New("billing: pay client is nil")
	}
	if verifier == nil {
		return nil, errors.New("billing: platform verifier is nil")
	}
	if len(apiV3Key) != 32 {
		return nil, fmt.Errorf("billing: api v3 key must be 32 bytes, got %d", len(apiV3Key))
	}
	if strings.TrimSpace(opts.ProductID) == "" {
		return nil, errors.New("billing: product id is empty")
	}
	if opts.MaxDevices < 1 {
		return nil, fmt.Errorf("billing: max devices is %d, want at least 1", opts.MaxDevices)
	}
	return &Service{
		billing:   billingStore,
		licences:  licenceStore,
		signer:    signer,
		client:    client,
		verifier:  verifier,
		opts:      opts,
		apiV3Key:  []byte(apiV3Key),
		png:       make(map[string][]byte),
		envelopes: make(map[string][]byte),
	}, nil
}

// Price resolves a period to a price in fen.
func (o *Options) Price(period Period) (int64, error) {
	switch period {
	case PeriodMonthly:
		return o.MonthlyFen, nil
	case PeriodYearly:
		return o.YearlyFen, nil
	default:
		return 0, fmt.Errorf("billing: unknown period %q", period)
	}
}

// CreateOrder returns an order to pay, creating one if this account has none
// outstanding.
//
// Two different callers can want "the order to pay" and neither of them is wrong:
// the same device retrying after a dropped response, and a second device of the
// same person opening the purchase window. Both are answered with the *same*
// order rather than a conflict. A 409 here would be the one error in this flow
// that a customer cannot act on — the money is already theirs, the second device
// is legitimately theirs, and nothing about the situation is wrong.
//
// Idempotency key: ClientRequestId. It is folded into out_trade_no, which is what
// WeChat enforces uniqueness on, so a repeated request finds its own order even if
// two of them race past the lookups below.
func (s *Service) CreateOrder(ctx context.Context, accountID string, req CreateOrderRequest) (OrderView, error) {
	if strings.TrimSpace(accountID) == "" {
		return OrderView{}, httpx.Unauthorized("请先登录。")
	}
	if req.ProductID != "" && req.ProductID != s.opts.ProductID {
		return OrderView{}, &httpx.APIError{
			Status: http.StatusUnprocessableEntity, Code: httpx.CodeWrongProduct,
			Message: "该商品不是本服务可售卖的商品。",
		}
	}
	amount, err := s.opts.Price(req.Period)
	if err != nil {
		return OrderView{}, httpx.BadRequest("购买周期无效。")
	}
	if amount <= 0 {
		// An unpriced product is a misconfiguration, and taking money for it
		// would be a charge we cannot reconcile.
		return OrderView{}, &httpx.APIError{
			Status: http.StatusServiceUnavailable, Code: httpx.CodeUnavailable,
			Message: "该购买周期尚未开放。",
		}
	}

	outTradeNo := OutTradeNo(accountID, req.ClientRequestID)

	// ① This exact attempt, already made.
	if existing, err := s.billing.GetOrderByOutTradeNo(ctx, outTradeNo); err == nil {
		return s.view(ctx, existing, http.StatusOK), nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return OrderView{}, httpx.Internal(err)
	}

	// ② Another attempt or another device of the same account.
	if existing, err := s.billing.FindLiveOrderByAccount(ctx, accountID); err == nil {
		return s.view(ctx, existing, http.StatusOK), nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return OrderView{}, httpx.Internal(err)
	}

	now := s.opts.now()
	expires := now.Add(s.opts.OrderTTL)
	period := PeriodMonthly
	if req.Period == PeriodYearly {
		period = PeriodYearly
	}

	// ③ WeChat first, the row second.
	//
	// The other order is tempting — reserve out_trade_no locally, then call — but
	// BillingStore has no way to attach the code_url to an order that already
	// exists, so a row created before the call would have no QR in it. Paying the
	// cost of a duplicate call instead: WeChat enforces out_trade_no uniqueness
	// itself, and the conflict below resolves back to the winner's order.
	codeURL, err := s.client.NativeOrder(ctx, NativeOrderRequest{
		Description: description(period),
		OutTradeNo:  outTradeNo,
		Amount:      Amount{Total: amount, Currency: Currency},
		TimeExpire:  expires,
	})
	if err != nil {
		var payErr *ErrPay
		if errors.As(err, &payErr) {
			s.opts.log().Error("wechat rejected the order", "code", payErr.Code, "message", payErr.Message)
			return OrderView{}, httpx.Unavailable("暂时无法下单，请稍后重试。")
		}
		return OrderView{}, httpx.Internal(err)
	}

	order := store.Order{
		ID:         newOrderID(),
		OutTradeNo: outTradeNo,
		ProductID:  s.opts.ProductID,
		Plan:       store.PlanSubscription,
		AmountFen:  amount,
		Currency:   Currency,
		Status:     store.OrderPending,
		AccountID:  accountID,
		CodeURL:    codeURL,
		CreatedAt:  now,
		ExpiresAt:  expires,
	}
	if err := s.billing.CreateOrder(ctx, order); err != nil {
		if !errors.Is(err, store.ErrConflict) {
			return OrderView{}, httpx.Internal(err)
		}
		// Somebody created this order between the lookups and here. Return theirs.
		existing, gerr := s.billing.GetOrderByOutTradeNo(ctx, outTradeNo)
		if gerr != nil {
			return OrderView{}, httpx.Internal(gerr)
		}
		return s.view(ctx, existing, http.StatusOK), nil
	}

	s.opts.log().Info("order created",
		"order_id", order.ID, "out_trade_no", outTradeNo, "amount_fen", amount, "period", period)
	return s.view(ctx, order, http.StatusCreated), nil
}

// OutTradeNo derives the merchant-side trade number from the account and the
// client's request id.
//
// It is derived rather than random so that a retry cannot produce a second one,
// which is what makes the uniqueness WeChat enforces do the idempotency work. It
// carries no personal data: only a digest leaves the function, and the account id
// that goes into it is a server-side identifier.
func OutTradeNo(accountID, clientRequestID string) string {
	sum := sha256.Sum256([]byte("wechatbridge.outtradeno.v1\n" + accountID + "\n" + clientRequestID))
	// "WCB" plus 29 hex characters is 32, WeChat's ceiling. Filling the whole
	// budget costs nothing and the prefix keeps it recognisable in a WeChat
	// dashboard.
	return "WCB" + strings.ToUpper(hex.EncodeToString(sum[:]))[:MaxOutTradeNoLength-3]
}

// OrderSnapshot answers the poller.
func (s *Service) OrderSnapshot(ctx context.Context, accountID, orderID string) (OrderView, error) {
	order, err := s.billing.GetOrder(ctx, orderID)
	if err != nil {
		if errors.Is(err, store.ErrNotFound) {
			return OrderView{}, httpx.NotFound("订单不存在。")
		}
		return OrderView{}, httpx.Internal(err)
	}
	// Someone else's order is reported as missing rather than forbidden: the
	// client treats 404 as "this order is over" (§9.2 row 8), which is the truth
	// from where it is standing.
	if accountID != "" && order.AccountID != accountID {
		return OrderView{}, httpx.NotFound("订单不存在。")
	}
	return s.view(ctx, order, http.StatusOK), nil
}

func (s *Service) view(ctx context.Context, order store.Order, status int) OrderView {
	v := OrderView{
		OrderID:    order.ID,
		Status:     order.Status,
		AmountFen:  order.AmountFen,
		Currency:   order.Currency,
		LicenseKey: order.LicenseKey,
		QRImageURL: s.qrImageURL(order.ID),
		HTTPStatus: status,
	}
	if order.CreatedAt.IsZero() {
		v.ExpiresAt = formatTime(s.opts.now().Add(s.opts.OrderTTL))
	} else {
		v.ExpiresAt = formatTime(order.ExpiresAt)
	}
	if order.PaidAt != nil {
		v.PaidAt = timePtr(formatTime(*order.PaidAt))
	}
	return v
}

// Redeem turns a paid order into a licence, idempotently.
//
// The order id is the idempotency key and the store holds the envelope bytes, so
// a second call returns the first call's licence rather than a new signature. The
// client's "retry collect" button is only a safe button because of this line, and
// it is why the licence is signed before the store call but never re-signed after
// one.
func (s *Service) Redeem(ctx context.Context, accountID, orderID, installID string) (RedeemResult, error) {
	order, err := s.billing.GetOrder(ctx, orderID)
	if err != nil {
		if errors.Is(err, store.ErrNotFound) {
			return RedeemResult{}, httpx.NotFound("订单不存在。")
		}
		return RedeemResult{}, httpx.Internal(err)
	}
	if accountID != "" && order.AccountID != accountID {
		return RedeemResult{}, httpx.NotFound("订单不存在。")
	}
	if order.Status != store.OrderPaid {
		return RedeemResult{}, &httpx.APIError{
			Status: http.StatusConflict, Code: httpx.CodeConflict,
			Message: "订单尚未支付完成。",
		}
	}

	// The licence's subject is the machine when the client said which machine,
	// and the account otherwise. The client's resolver binds on subject ==
	// install id, so a subscription licence minted without one would not survive
	// the first launch.
	subject := strings.TrimSpace(installID)
	if subject == "" {
		subject = order.AccountID
	}

	licenseID := order.LicenseID
	licenseKey := order.LicenseKey

	if licenseID == "" {
		var err error
		if licenseID, err = newID("lic"); err != nil {
			return RedeemResult{}, httpx.Internal(err)
		}
		if licenseKey, err = newID("lk"); err != nil {
			return RedeemResult{}, httpx.Internal(err)
		}
		now := s.opts.now()
		env, err := s.signer.Build(license.IssueParams{
			Subject:      subject,
			Plan:         license.PlanSubscription,
			Entitlements: s.opts.Entitlements,
			Lifetime:     s.opts.LicenseLifetime,
			IssuedAt:     now,
			ClockSkew:    s.opts.ClockSkew,
		})
		if err != nil {
			return RedeemResult{}, httpx.Internal(err)
		}
		raw, err := env.JSON()
		if err != nil {
			return RedeemResult{}, httpx.Internal(err)
		}

		// The store keeps whichever envelope landed first and hands the same
		// bytes back on every later call.
		stored, err := s.billing.IssueOrderLicense(ctx, order.ID, licenseID, licenseKey, raw)
		if err != nil {
			return RedeemResult{}, httpx.Internal(err)
		}
		s.mu.Lock()
		s.envelopes[order.ID] = stored
		s.mu.Unlock()
		return s.finishRedeem(ctx, order.ID, subject, licenseID, licenseKey, stored)
	}

	// The order already carries an issued licence. Nothing is signed here: the
	// bytes live in the store and asking for them again is the whole contract.
	stored, err := s.storedEnvelope(ctx, order)
	if err != nil {
		return RedeemResult{}, httpx.Internal(err)
	}
	return s.finishRedeem(ctx, order.ID, subject, licenseID, licenseKey, stored)
}

// storedEnvelope fetches an order's issued envelope.
//
// It never signs. The process cache covers the common case — a user pressing
// "retry collect" twice — and a cache miss after a restart asks the store, whose
// IssueOrderLicense is documented to return the identical bytes for any later call
// on the same order. That call is made with the order's own licence id and an
// empty envelope: the store must treat an empty envelope as "give me what you
// already hold" rather than as "store nothing", which is the one requirement this
// path places on it.
func (s *Service) storedEnvelope(ctx context.Context, order store.Order) ([]byte, error) {
	s.mu.Lock()
	cached, ok := s.envelopes[order.ID]
	s.mu.Unlock()
	if ok {
		return cached, nil
	}
	stored, err := s.billing.IssueOrderLicense(ctx, order.ID, order.LicenseID, order.LicenseKey, nil)
	if err != nil {
		return nil, err
	}
	if len(stored) == 0 {
		return nil, fmt.Errorf("billing: order %s reports an issued licence but stored no envelope", order.ID)
	}
	return stored, nil
}

// finishRedeem binds the machine and shapes the reply.
func (s *Service) finishRedeem(ctx context.Context, orderID, subject, licenseID, licenseKey string, envelope []byte) (RedeemResult, error) {
	if err := s.licences.BindDevice(ctx, orderID, subject, store.PlanSubscription, s.opts.MaxDevices, s.opts.now()); err != nil {
		if errors.Is(err, store.ErrConflict) {
			live, lerr := s.liveDevices(ctx, orderID)
			if lerr != nil {
				return RedeemResult{}, httpx.Internal(lerr)
			}
			return RedeemResult{}, &httpx.APIError{
				Status: http.StatusConflict, Code: httpx.CodeInstallMismatch,
				Message: fmt.Sprintf("该账号已绑定 %d 台设备，最多可绑定 %d 台。", live, s.opts.MaxDevices),
			}
		}
		return RedeemResult{}, httpx.Internal(err)
	}

	result := RedeemResult{
		LicenseToken: base64.RawURLEncoding.EncodeToString(envelope),
		LicenseKey:   licenseKey,
		LicenseID:    licenseID,
		Subject:      subject,
		HTTPStatus:   http.StatusOK,
	}
	if env, err := license.Parse(envelope); err == nil && env.ExpiresAt != nil {
		result.ExpiresAt = formatTime(time.Unix(*env.ExpiresAt, 0))
	}
	return result, nil
}

// NotifyResult carries what the callback handler puts on the wire, plus the
// status to use when it is not a success.
type NotifyOutcome struct {
	Status int
	Body   NotifyResult
}

// HandleNotify processes a WeChat callback.
//
// The order of the operations is the design, and none of it is negotiable:
//
//	verify signature → decrypt → find our order → check the amount → mark paid.
//
// MarkPaid is conditional on the order still being pending, and WeChat re-delivers
// until it is answered with a success body, so this runs more than once per payment
// by design. That is the difference between one licence and several.
//
// Every failure path returns a non-200 and marks nothing. A non-200 here is the
// correct answer to "I could not process this": WeChat retries, an alert fires,
// and the customer keeps their money. Returning 200 on an unprocessed callback
// would be the one response that loses a payment.
func (s *Service) HandleNotify(ctx context.Context, headers http.Header, body []byte) (NotifyOutcome, error) {
	tx, err := VerifyNotification(ctx, s.verifier, s.apiV3Key, headers, body, s.opts.now())
	if err != nil {
		s.opts.log().Error("callback rejected", "error", err)
		return NotifyOutcome{Status: http.StatusUnauthorized, Body: NotifyResult{Code: "FAIL", Message: "验签失败"}}, err
	}

	if !strings.EqualFold(tx.TradeState, TradeStateSuccess) {
		// A state we do not act on is still a state we have handled. Answering
		// success stops the redelivery loop; marking nothing keeps the order
		// pending for its next state.
		s.opts.log().Info("callback for a non-success state", "state", tx.TradeState, "out_trade_no", tx.OutTradeNo)
		return NotifyOutcome{Status: http.StatusOK, Body: NotifySuccess()}, nil
	}

	order, err := s.billing.GetOrderByOutTradeNo(ctx, tx.OutTradeNo)
	if err != nil {
		if errors.Is(err, store.ErrNotFound) {
			// We issued no such trade number. Retrying is right: it may be a
			// replay of a real order whose row this deployment cannot see, and a
			// silent success here would drop a customer's money on the floor.
			s.opts.log().Error("callback names an order we never issued", "out_trade_no", tx.OutTradeNo)
			return NotifyOutcome{Status: http.StatusInternalServerError, Body: NotifyResult{Code: "FAIL", Message: "订单不存在"}}, err
		}
		return NotifyOutcome{Status: http.StatusInternalServerError, Body: NotifyResult{Code: "FAIL", Message: "内部错误"}}, httpx.Internal(err)
	}

	if tx.Amount.Total != order.AmountFen {
		// The amount is checked against what we asked for, not against what we
		// happened to store. A callback for the right trade number with the
		// wrong amount is either a misconfigured price or an attempt to buy a
		// year for the price of a month.
		s.opts.log().Error("callback amount does not match the order",
			"out_trade_no", tx.OutTradeNo, "expected_fen", order.AmountFen, "got_fen", tx.Amount.Total)
		return NotifyOutcome{Status: http.StatusBadRequest, Body: NotifyResult{Code: "FAIL", Message: "金额不符"}},
			fmt.Errorf("billing: amount mismatch: want %d fen, got %d", order.AmountFen, tx.Amount.Total)
	}
	if order.Currency != "" && tx.Amount.Currency != "" && !strings.EqualFold(tx.Amount.Currency, order.Currency) {
		s.opts.log().Error("callback currency does not match the order",
			"out_trade_no", tx.OutTradeNo, "expected", order.Currency, "got", tx.Amount.Currency)
		return NotifyOutcome{Status: http.StatusBadRequest, Body: NotifyResult{Code: "FAIL", Message: "币种不符"}},
			fmt.Errorf("billing: currency mismatch: want %s, got %s", order.Currency, tx.Amount.Currency)
	}

	paidAt := s.opts.now()
	if tx.SuccessTime != "" {
		if parsed, perr := time.Parse(time.RFC3339, tx.SuccessTime); perr == nil {
			paidAt = parsed.UTC()
		}
	}

	if _, err := s.billing.MarkPaid(ctx, order.ID, tx.TransactionID, paidAt); err != nil {
		if !errors.Is(err, store.ErrConflict) {
			return NotifyOutcome{Status: http.StatusInternalServerError, Body: NotifyResult{Code: "FAIL", Message: "内部错误"}}, httpx.Internal(err)
		}
		// Already paid: the redelivery of a callback we already processed. This
		// is the normal path for the second and later deliveries, and it must be
		// answered with success or WeChat keeps coming back.
		s.opts.log().Info("callback redelivered for a paid order", "order_id", order.ID)
		return NotifyOutcome{Status: http.StatusOK, Body: NotifySuccess()}, nil
	}

	s.opts.log().Info("order paid",
		"order_id", order.ID, "transaction_id", tx.TransactionID, "amount_fen", tx.Amount.Total)
	return NotifyOutcome{Status: http.StatusOK, Body: NotifySuccess()}, nil
}

func (s *Service) liveDevices(ctx context.Context, orderID string) (int, error) {
	rows, err := s.licences.ListDevices(ctx, orderID)
	if err != nil {
		if errors.Is(err, store.ErrNotFound) {
			return 0, nil
		}
		return 0, err
	}
	live := 0
	for _, r := range rows {
		if r.ReleasedAt == nil {
			live++
		}
	}
	return live, nil
}

// QRPNG renders the order's code_url as a PNG.
//
// The client downloads bytes and never renders a QR itself, so this is the whole
// of the QR feature. It is public on purpose — the design requires the download
// to carry no bearer token, so the URL is a capability and the order id is 128
// random bits, which is what makes that acceptable.
func (s *Service) QRPNG(ctx context.Context, orderID string) ([]byte, error) {
	s.mu.Lock()
	if png, ok := s.png[orderID]; ok {
		s.mu.Unlock()
		return png, nil
	}
	s.mu.Unlock()

	order, err := s.billing.GetOrder(ctx, orderID)
	if err != nil {
		if errors.Is(err, store.ErrNotFound) {
			return nil, httpx.NotFound("订单不存在。")
		}
		return nil, httpx.Internal(err)
	}
	if order.CodeURL == "" {
		return nil, httpx.NotFound("该订单还没有二维码。")
	}

	// Error correction M rather than L: a phone photographing a screen in a shop
	// with a window behind the reader is exactly the case the extra redundancy is
	// for, and the extra modules cost nothing at this size.
	png, err := EncodeQRPNG(order.CodeURL, ecMedium)
	if err != nil {
		return nil, httpx.Internal(err)
	}

	s.mu.Lock()
	if len(s.png) > 128 {
		// A tiny bounded cache. The QR never changes for an order, so the only
		// cost of an entry is memory.
		s.png = make(map[string][]byte)
	}
	s.png[orderID] = png
	s.mu.Unlock()
	return png, nil
}

func (s *Service) qrImageURL(orderID string) string {
	base := strings.TrimRight(s.opts.PublicBaseURL, "/")
	if base == "" {
		return "/v1/billing/orders/" + orderID + "/qr.png"
	}
	return base + "/v1/billing/orders/" + orderID + "/qr.png"
}

func description(period Period) string {
	if period == PeriodYearly {
		return "WeChatBridge Windows 订阅版 · 年付"
	}
	return "WeChatBridge Windows 订阅版 · 月付"
}

func newOrderID() string {
	id, err := newID("ord")
	if err != nil {
		// crypto/rand failing is not a condition this service can continue past,
		// but taking the process down over one order id is worse than a
		// timestamp-derived one that is still unique to the nanosecond.
		return "ord_" + formatTime(time.Now().UTC())
	}
	return id
}

func newID(prefix string) (string, error) {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		return "", fmt.Errorf("billing: random source failed: %w", err)
	}
	return prefix + "_" + base64.RawURLEncoding.EncodeToString(b[:]), nil
}

func timePtr(v string) *string { return &v }

// formatTime is yyyy-MM-ddTHH:mm:ssZ, in UTC.
func formatTime(t time.Time) string { return t.UTC().Format("2006-01-02T15:04:05Z") }
