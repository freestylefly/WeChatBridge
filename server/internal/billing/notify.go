package billing

import (
	"context"
	"crypto/aes"
	"crypto/cipher"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"strconv"
	"strings"
	"time"
)

// Callback headers. Names are case-insensitive on the wire; http.Header's
// canonical form is what these constants produce.
const (
	HeaderSerial    = "Wechatpay-Serial"
	HeaderSignature = "Wechatpay-Signature"
	HeaderTimestamp = "Wechatpay-Timestamp"
	HeaderNonce     = "Wechatpay-Nonce"
)

// NotificationSkew is how far a callback's timestamp may be from ours. WeChat
// retries for hours, so this cannot be tight — it is here to stop a captured
// notification being replayed by someone who can reach the endpoint, not to police
// the clock.
const NotificationSkew = 5 * time.Minute

// TradeStateSuccess is the only transaction state that means the customer paid.
const TradeStateSuccess = "SUCCESS"

// EventTypeTransactionSuccess is the notification event type WeChat sends for a
// paid order. It is a different string space from TradeStateSuccess: the
// envelope's event_type says what happened, the decrypted resource's
// trade_state says the outcome. Comparing one against the other rejects every
// real notification.
const EventTypeTransactionSuccess = "TRANSACTION.SUCCESS"

// notifyEnvelope is the callback body before decryption.
type notifyEnvelope struct {
	ID        string         `json:"id"`
	EventType string         `json:"event_type"`
	Resource  notifyResource `json:"resource"`
	Summary   string         `json:"summary"`
}

type notifyResource struct {
	Algorithm      string `json:"algorithm"`
	Ciphertext     string `json:"ciphertext"`
	AssociatedData string `json:"associated_data"`
	Nonce          string `json:"nonce"`
	OriginalType   string `json:"original_type"`
}

// TransactionAmount is money as WeChat reports it: integer minor units.
type TransactionAmount struct {
	Total         int64  `json:"total"`
	PayerTotal    int64  `json:"payer_total"`
	Currency      string `json:"currency"`
	PayerCurrency string `json:"payer_currency"`
}

// Transaction is the decrypted callback resource for a transaction notification.
type Transaction struct {
	AppID          string            `json:"appid"`
	MchID          string            `json:"mchid"`
	OutTradeNo     string            `json:"out_trade_no"`
	TransactionID  string            `json:"transaction_id"`
	TradeState     string            `json:"trade_state"`
	TradeStateDesc string            `json:"trade_state_desc"`
	BankType       string            `json:"bank_type"`
	Attach         string            `json:"attach"`
	SuccessTime    string            `json:"success_time"`
	Payer          map[string]string `json:"payer"`
	Amount         TransactionAmount `json:"amount"`
}

// VerifyNotification checks the APIv3 signature and then decrypts the resource.
//
// The order is the whole point. Nothing that changes state may run before this
// returns nil: not MarkPaid, not licence issuance, not the order lookup that a
// signature check would otherwise follow. The body is attacker-controlled until
// the signature verifies, and the resource is ciphertext until it decrypts — an
// out_trade_no read before decryption is a forgery that marks somebody's order
// paid.
func VerifyNotification(ctx context.Context, verifier PlatformVerifier, apiV3Key []byte, headers http.Header, body []byte, now time.Time) (*Transaction, error) {
	if verifier == nil {
		return nil, ErrNoVerifier
	}
	if len(apiV3Key) != 32 {
		// The APIv3 key is a 32-character string. Anything else fails at the
		// cipher, and the message here is the one a support conversation needs.
		return nil, fmt.Errorf("billing: api v3 key must be 32 bytes, got %d", len(apiV3Key))
	}

	serial := strings.TrimSpace(headers.Get(HeaderSerial))
	timestamp := strings.TrimSpace(headers.Get(HeaderTimestamp))
	nonce := strings.TrimSpace(headers.Get(HeaderNonce))
	signature := strings.TrimSpace(headers.Get(HeaderSignature))
	switch {
	case serial == "" || timestamp == "" || nonce == "" || signature == "":
		return nil, errors.New("billing: callback is missing a signature header")
	}

	unix, err := strconv.ParseInt(timestamp, 10, 64)
	if err != nil {
		return nil, fmt.Errorf("billing: callback timestamp is not a unix time: %w", err)
	}
	delta := now.Sub(time.Unix(unix, 0))
	if delta < 0 {
		delta = -delta
	}
	if delta > NotificationSkew {
		return nil, fmt.Errorf("billing: callback timestamp is %s away from ours, limit %s", delta, NotificationSkew)
	}

	sig, err := base64.StdEncoding.DecodeString(signature)
	if err != nil {
		return nil, fmt.Errorf("billing: callback signature is not base64: %w", err)
	}

	// timestamp, nonce, body, each followed by a newline, plus one more.
	message := timestamp + "\n" + nonce + "\n" + string(body) + "\n"
	if err := verifier.Verify(ctx, serial, []byte(message), sig); err != nil {
		return nil, err
	}

	return decryptTransaction(apiV3Key, body)
}

// decryptTransaction unwraps the AEAD_AES_256_GCM resource.
func decryptTransaction(apiV3Key, body []byte) (*Transaction, error) {
	var env notifyEnvelope
	if err := json.Unmarshal(body, &env); err != nil {
		return nil, fmt.Errorf("billing: callback body: %w", err)
	}
	if env.EventType != "" && !strings.EqualFold(env.EventType, EventTypeTransactionSuccess) {
		return nil, fmt.Errorf("billing: unsupported callback event %q", env.EventType)
	}
	if !strings.EqualFold(env.Resource.Algorithm, "AEAD_AES_256_GCM") {
		return nil, fmt.Errorf("billing: unsupported resource algorithm %q", env.Resource.Algorithm)
	}
	ciphertext, err := base64.StdEncoding.DecodeString(env.Resource.Ciphertext)
	if err != nil {
		return nil, fmt.Errorf("billing: callback ciphertext: %w", err)
	}

	block, err := aes.NewCipher(apiV3Key)
	if err != nil {
		return nil, fmt.Errorf("billing: api v3 key: %w", err)
	}
	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return nil, fmt.Errorf("billing: gcm: %w", err)
	}
	// WeChat appends the 16-byte tag to the ciphertext; Go's Open expects exactly
	// that, so there is nothing to split here.
	plaintext, err := gcm.Open(nil, []byte(env.Resource.Nonce), ciphertext, []byte(env.Resource.AssociatedData))
	if err != nil {
		return nil, fmt.Errorf("billing: callback decrypt: %w", err)
	}

	var tx Transaction
	if err := json.Unmarshal(plaintext, &tx); err != nil {
		return nil, fmt.Errorf("billing: callback resource: %w", err)
	}
	return &tx, nil
}

// NotifyResult is the body WeChat wants back on success. Anything else — a non-200
// or a different body — and WeChat keeps re-delivering until its retries run out,
// so this is the exact shape and there is nowhere else to put a nicer message.
type NotifyResult struct {
	Code    string `json:"code"`
	Message string `json:"message"`
}

// NotifySuccess is the only success reply.
func NotifySuccess() NotifyResult { return NotifyResult{Code: "SUCCESS", Message: "成功"} }
