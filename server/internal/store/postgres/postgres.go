// Package postgres implements store.Store on PostgreSQL.
//
// The interesting part of this package is not the queries, it is where the
// transaction boundaries are. Four operations must be atomic and the rest are
// single statements:
//
//	ConfirmActivationRequest  the flip and the binding it installs are one fact
//	ReissueLicence            the retire and the re-bind are one fact
//	RotateRefresh             the old token is consumed and its replacement exists
//	CompleteAuthSession       the ticket is spent and the account is resolved
//
// Everything else is written as a single conditional statement whose row count
// is the answer. See errors.go's notes on error mapping for why the row count is
// read rather than the row.
//
// Nothing in this package logs, and nothing puts a token, a licence or a
// secret into an error: every error path either returns a sentinel from the
// store package or wraps the driver's error with operation context that names
// a table, never a value.
package postgres

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"sync"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgxpool"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// Compile-time proof that this type is what the rest of the service expects.
// If an interface in store changes, this line is where the build should break.
var _ store.Store = (*Store)(nil)

// Store is the PostgreSQL-backed implementation of store.Store.
type Store struct {
	db db

	closeOnce sync.Once
}

// New opens a pool and checks it is actually reachable before returning.
//
// The ping matters: a service that starts "successfully" against an
// unreachable database fails on the first customer instead of on the deploy,
// and the two have very different consequences.
func New(ctx context.Context, dsn string) (*Store, error) {
	if strings.TrimSpace(dsn) == "" {
		return nil, errors.New("postgres: New: empty dsn")
	}

	cfg, err := pgxpool.ParseConfig(dsn)
	if err != nil {
		// Parse errors from pgx quote the offending fragment of the DSN, which
		// is where the password lives.
		return nil, redact(err, secretsOf(dsn, cfg))
	}

	applyPoolDefaults(cfg)

	pool, err := pgxpool.NewWithConfig(ctx, cfg)
	if err != nil {
		return nil, redact(fmt.Errorf("postgres: New: create pool: %w", err), secretsOf(dsn, cfg))
	}

	pingCtx, cancel := context.WithTimeout(ctx, connectTimeout)
	defer cancel()
	if err := pool.Ping(pingCtx); err != nil {
		pool.Close()
		return nil, redact(fmt.Errorf("postgres: New: ping: %w", err), secretsOf(dsn, cfg))
	}

	return &Store{db: pool}, nil
}

// Close releases the pool. It is safe to call more than once, including from a
// defer that runs after an explicit close on the error path.
func (s *Store) Close() error {
	s.closeOnce.Do(func() { s.db.Close() })
	return nil
}

// db is the slice of *pgxpool.Pool this package actually uses.
//
// It is an interface so the tests can drive the whole store without a
// PostgreSQL server. Anything added here has to exist on both *pgxpool.Pool
// and the mock, which is a small enough surface to keep honest by hand.
type db interface {
	Exec(ctx context.Context, sql string, args ...any) (pgconn.CommandTag, error)
	Query(ctx context.Context, sql string, args ...any) (pgx.Rows, error)
	QueryRow(ctx context.Context, sql string, args ...any) pgx.Row
	Begin(ctx context.Context) (pgx.Tx, error)
	BeginTx(ctx context.Context, opts pgx.TxOptions) (pgx.Tx, error)
	Close()
}

// rowQuerier is the part of both pgx.Tx and *pgxpool.Pool that a single
// row-returning query needs. It exists so the account upsert can run inside a
// transaction during sign-in and outside one otherwise.
type rowQuerier interface {
	QueryRow(ctx context.Context, sql string, args ...any) pgx.Row
}

// inTx runs fn inside a transaction and commits its work.
//
// fn is responsible for returning a meaningful error: a sentinel from the
// store package when the caller needs to tell "not found" from "somebody else
// got there first", or a wrapped driver error when the database itself is the
// problem. The distinction survives the rollback, because rollback errors are
// joined rather than substituted.
func (s *Store) inTx(ctx context.Context, fn func(tx pgx.Tx) error) error {
	tx, err := s.db.Begin(ctx)
	if err != nil {
		return fmt.Errorf("postgres: begin transaction: %w", err)
	}

	// A panic inside fn must still release the connection. The rollback uses
	// a context detached from the caller's, because the caller's may well be
	// the thing that panicked.
	defer func() {
		if p := recover(); p != nil {
			_ = tx.Rollback(context.WithoutCancel(ctx))
			panic(p)
		}
	}()

	if err := fn(tx); err != nil {
		rbErr := tx.Rollback(context.WithoutCancel(ctx))
		if rbErr != nil && !errors.Is(rbErr, pgx.ErrTxClosed) {
			// Join, not replace: the caller still gets to match on the real
			// reason, and the operator still sees that the rollback misbehaved.
			return errors.Join(err, fmt.Errorf("postgres: rollback: %w", rbErr))
		}
		return err
	}

	if err := tx.Commit(ctx); err != nil {
		return fmt.Errorf("postgres: commit transaction: %w", err)
	}
	return nil
}

const (
	// connectTimeout bounds the start-up ping. Long enough for a slow TLS
	// handshake across a region, short enough that a black-holed address does
	// not hang the deploy.
	connectTimeout = 10 * time.Second

	// Defaults for the pool. These are deliberately modest: the service is
	// mostly waiting on WeChat, not on PostgreSQL, and an oversized pool on a
	// small instance turns a traffic spike into a connection-limit outage.
	defaultMaxConns          = 10
	defaultMinConns          = 2
	defaultMaxConnLifetime   = 30 * time.Minute
	defaultMaxConnIdleTime   = 5 * time.Minute
	defaultHealthCheckPeriod = time.Minute
)

// applyPoolDefaults leaves any value the DSN set alone and only fills in the
// blanks, so an operator can still tune the pool from the connection string.
func applyPoolDefaults(cfg *pgxpool.Config) {
	if cfg.MaxConns <= 0 {
		cfg.MaxConns = defaultMaxConns
	}
	if cfg.MinConns <= 0 {
		cfg.MinConns = defaultMinConns
	}
	if cfg.MinConns > cfg.MaxConns {
		cfg.MinConns = cfg.MaxConns
	}
	if cfg.MaxConnLifetime == 0 {
		cfg.MaxConnLifetime = defaultMaxConnLifetime
	}
	if cfg.MaxConnIdleTime == 0 {
		cfg.MaxConnIdleTime = defaultMaxConnIdleTime
	}
	if cfg.HealthCheckPeriod == 0 {
		cfg.HealthCheckPeriod = defaultHealthCheckPeriod
	}

	// The one setting the DSN does not get to argue with. Every instant in the
	// schema is timestamptz and every instant is UTC; pinning the session
	// time zone means a server timezone change, or a deployment in another
	// region, cannot quietly shift what "expires at 03:00" means.
	if cfg.ConnConfig.RuntimeParams == nil {
		cfg.ConnConfig.RuntimeParams = map[string]string{}
	}
	cfg.ConnConfig.RuntimeParams["timezone"] = "UTC"
	if cfg.ConnConfig.RuntimeParams["application_name"] == "" {
		cfg.ConnConfig.RuntimeParams["application_name"] = "wechatbridge"
	}
}

// redact wraps err so its message cannot carry a secret, while keeping the
// unwrap chain intact so callers can still match with errors.Is.
type redacted struct {
	err     error
	secrets []string
}

func (r redacted) Error() string {
	msg := r.err.Error()
	for _, s := range r.secrets {
		if s != "" {
			msg = strings.ReplaceAll(msg, s, "[redacted]")
		}
	}
	return msg
}

func (r redacted) Unwrap() error { return r.err }

func redact(err error, secrets []string) error {
	if err == nil {
		return nil
	}
	return redacted{err: err, secrets: secrets}
}

// secretsOf collects the substrings that must never reach a log line. The DSN
// itself is included because a parse error may quote the whole string.
func secretsOf(dsn string, cfg *pgxpool.Config) []string {
	out := make([]string, 0, 2)
	if cfg != nil && cfg.ConnConfig != nil && cfg.ConnConfig.Password != "" {
		out = append(out, cfg.ConnConfig.Password)
	}
	if dsn != "" {
		out = append(out, dsn)
	}
	return out
}
