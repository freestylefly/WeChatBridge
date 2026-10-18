// Package httpx holds the HTTP layer's shared vocabulary: the error shape the
// client parses, the request id every log line carries, and the middleware
// that attaches them.
//
// The error shape is a contract with two clients written in two languages, so
// the field names are pinned rather than derived from Go names.
package httpx

import (
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"strconv"
	"time"

	"github.com/gin-gonic/gin"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// RequestIDHeader is echoed back so a user can quote it in a bug report and
// find the matching log lines without anyone guessing at timestamps.
const RequestIDHeader = "X-Request-Id"

// ErrorBody is the only error shape the client sees. Code is the part that is
// branched on; Message is for humans and must never be parsed.
type ErrorBody struct {
	Error struct {
		Code      string `json:"code"`
		Message   string `json:"message"`
		RequestID string `json:"request_id"`
	} `json:"error"`
}

// Error codes. These strings are contract; renaming one is a client break.
const (
	CodeBadRequest      = "bad_request"
	CodeUnauthorized    = "unauthorized"
	CodeForbidden       = "forbidden"
	CodeNotFound        = "not_found"
	CodeConflict        = "conflict"
	CodeGone            = "gone"
	CodeRateLimited     = "rate_limited"
	CodeInternal        = "internal"
	CodeUnavailable     = "service_unavailable"
	CodeInvalidCode     = "invalid_code"
	CodeCodeRedeemed    = "code_already_redeemed"
	CodeCodeExpired     = "code_expired"
	CodeCodeRevoked     = "code_revoked"
	CodeInstallMismatch = "install_mismatch"
	CodeWrongProduct    = "wrong_product"
)

// APIError is what a handler returns to produce a specific status and code.
// Everything else that reaches the middleware is an internal error and is
// reported as such — a stack trace or a constraint name in a reply body tells
// an attacker the shape of the database and helps nobody.
type APIError struct {
	Status  int
	Code    string
	Message string
	// Cause is logged, never serialised.
	Cause error
}

func (e *APIError) Error() string {
	if e.Cause != nil {
		return e.Code + ": " + e.Cause.Error()
	}
	return e.Code + ": " + e.Message
}

func (e *APIError) Unwrap() error { return e.Cause }

// Constructors for the failures that recur, so a handler is a sentence rather
// than a status-code table.
func BadRequest(msg string) *APIError {
	return &APIError{Status: http.StatusBadRequest, Code: CodeBadRequest, Message: msg}
}

func Unauthorized(msg string) *APIError {
	return &APIError{Status: http.StatusUnauthorized, Code: CodeUnauthorized, Message: msg}
}

func Forbidden(msg string) *APIError {
	return &APIError{Status: http.StatusForbidden, Code: CodeForbidden, Message: msg}
}

func NotFound(msg string) *APIError {
	return &APIError{Status: http.StatusNotFound, Code: CodeNotFound, Message: msg}
}

func Conflict(msg string) *APIError {
	return &APIError{Status: http.StatusConflict, Code: CodeConflict, Message: msg}
}

func Unavailable(msg string) *APIError {
	return &APIError{Status: http.StatusServiceUnavailable, Code: CodeUnavailable, Message: msg}
}

func Internal(cause error) *APIError {
	return &APIError{Status: http.StatusInternalServerError, Code: CodeInternal,
		Message: "服务器内部错误，请稍后重试。", Cause: cause}
}

// FromStore translates a store sentinel into the reply a client can act on.
//
// The three sentinels stay distinct all the way to the wire: "no such code",
// "that code is spent" and "that code is spent by someone else" are three
// different situations and the client treats them differently.
func FromStore(err error) *APIError {
	switch {
	case err == nil:
		return nil
	case errors.Is(err, store.ErrNotFound):
		return NotFound("请求的内容不存在。")
	case errors.Is(err, store.ErrReuseDetected):
		return Unauthorized("登录凭证已被使用，请重新登录。")
	case errors.Is(err, store.ErrConflict):
		return Conflict("资源状态已变化，请刷新后重试。")
	default:
		return Internal(err)
	}
}

// RequestID pulls the per-request id set by RequestIDMiddleware.
func RequestID(c *gin.Context) string {
	if v, ok := c.Get("request_id"); ok {
		if s, ok := v.(string); ok {
			return s
		}
	}
	return ""
}

// Fail writes an error reply. It is the only place a status code and a body are
// put on the wire.
func Fail(c *gin.Context, err *APIError) {
	if err == nil {
		err = Internal(errors.New("nil APIError passed to Fail"))
	}
	if err.Cause != nil {
		slog.Error("request failed",
			"request_id", RequestID(c),
			"path", c.FullPath(),
			"method", c.Request.Method,
			"code", err.Code,
			"error", err.Cause)
	}

	var body ErrorBody
	body.Error.Code = err.Code
	body.Error.Message = err.Message
	body.Error.RequestID = RequestID(c)

	c.AbortWithStatusJSON(err.Status, body)
}

// OK writes a successful reply with the given status.
func OK(c *gin.Context, status int, payload any) {
	c.JSON(status, payload)
}

// Logger replaces Gin's default logger with one that emits structured lines
// carrying the request id.
func Logger(log *slog.Logger) gin.HandlerFunc {
	return func(c *gin.Context) {
		start := time.Now()
		c.Next()
		log.Info("http",
			"request_id", RequestID(c),
			"method", c.Request.Method,
			"path", c.Request.URL.Path,
			"status", c.Writer.Status(),
			"duration_ms", time.Since(start).Milliseconds(),
			"client_ip", c.ClientIP())
	}
}

// RecoveryMiddleware turns a panic into a 500 with the request id rather than a
// dropped connection, so a user can still report what happened.
func RecoveryMiddleware(log *slog.Logger) gin.HandlerFunc {
	return func(c *gin.Context) {
		defer func() {
			if r := recover(); r != nil {
				log.Error("panic",
					"request_id", RequestID(c),
					"path", c.Request.URL.Path,
					"panic", r)
				Fail(c, Internal(fmt.Errorf("panic: %v", r)))
			}
		}()
		c.Next()
	}
}

// LoggerMiddleware is the structured request logger.
func LoggerMiddleware(log *slog.Logger) gin.HandlerFunc { return Logger(log) }

// RequestIDMiddleware assigns a request id, preferring one the client sent so a
// retry of the same user action stays correlatable across logs.
func RequestIDMiddleware() gin.HandlerFunc {
	return func(c *gin.Context) {
		id := c.GetHeader(RequestIDHeader)
		if id == "" || len(id) > 64 {
			id = newRequestID()
		}
		c.Set("request_id", id)
		c.Writer.Header().Set(RequestIDHeader, id)
		c.Next()
	}
}

func newRequestID() string {
	var b [12]byte
	if _, err := rand.Read(b[:]); err != nil {
		// A request id that is not random is still better than none; the log
		// line stays correlatable even if two requests collide.
		return strconv.FormatInt(time.Now().UnixNano(), 16)
	}
	return hex.EncodeToString(b[:])
}
