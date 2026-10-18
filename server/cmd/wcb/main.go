// Command wcb is the WeChatBridge API server: WeChat QR sign-in, the
// buy-out activation flow, and the seller's console.
//
// The boot order is the dependency order, and every step that can fail
// fails here rather than on the first customer:
//
//	config → migrate → store → signer → services → routes → listen
//
// Shutdown is the same list in reverse: stop taking requests, let the
// in-flight ones finish, close the pool. SIGINT and SIGTERM both start it.
package main

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/gin-gonic/gin"
	"github.com/jackc/pgx/v5/pgxpool"

	"github.com/freestylefly/WeChatBridge/server/internal/activation"
	"github.com/freestylefly/WeChatBridge/server/internal/admin"
	"github.com/freestylefly/WeChatBridge/server/internal/auth"
	"github.com/freestylefly/WeChatBridge/server/internal/config"
	"github.com/freestylefly/WeChatBridge/server/internal/httpx"
	"github.com/freestylefly/WeChatBridge/server/internal/license"
	"github.com/freestylefly/WeChatBridge/server/internal/store/postgres"
	"github.com/freestylefly/WeChatBridge/server/internal/token"
	"github.com/freestylefly/WeChatBridge/server/internal/wechat"
	"github.com/freestylefly/WeChatBridge/server/migrations"
)

func main() {
	if err := run(); err != nil {
		// The logger is not up yet on the earliest failures, so the error
		// goes to stderr plainly; everything after setup goes through slog.
		fmt.Fprintf(os.Stderr, "wcb: %v\n", err)
		os.Exit(1)
	}
}

func run() error {
	cfg, err := config.Load()
	if err != nil {
		return err
	}
	logger := newLogger(cfg)
	slog.SetDefault(logger)
	if cfg.IsProd() {
		gin.SetMode(gin.ReleaseMode)
	}

	// A cancelled context is the shutdown signal, and every phase below
	// takes a context — a database that will not answer during boot stops
	// the boot instead of the first request.
	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()

	if err := migrate(ctx, cfg.DatabaseURL, logger); err != nil {
		return fmt.Errorf("migrate: %w", err)
	}

	st, err := postgres.New(ctx, cfg.DatabaseURL)
	if err != nil {
		return err
	}
	defer st.Close()

	signer, err := newSigner(cfg, logger)
	if err != nil {
		return err
	}

	// The issuer cannot fail on a validated config — Load has already
	// checked the secret's length, the only thing NewIssuer refuses — but
	// an error here is still an error, and the boot path already knows how
	// to print one.
	issuer, err := token.NewIssuer(cfg.TokenSigningSecret, cfg.AccessTokenTTL, cfg.RefreshTokenTTL)
	if err != nil {
		return fmt.Errorf("token issuer: %w", err)
	}

	authService, err := auth.NewService(
		auth.Config{
			PublicBaseURL:  cfg.PublicBaseURL,
			AuthSessionTTL: cfg.AuthSessionTTL,
		},
		auth.Options{
			Store:  st,
			Issuer: issuer,
			WeChat: wechat.NewClient(wechat.Config{
				AppID:     cfg.WeChatAppID,
				AppSecret: cfg.WeChatAppSecret,
				AuthURL:   cfg.WeChatAuthURL,
				APIBase:   cfg.WeChatAPIBase,
			}),
			Logger: logger,
		},
	)
	if err != nil {
		return fmt.Errorf("auth service: %w", err)
	}

	activationService, err := activation.NewService(activation.Options{
		Store:     st,
		AccountOf: auth.AccountIDFrom,
		Logger:    logger,
	})
	if err != nil {
		return fmt.Errorf("activation service: %w", err)
	}

	engine := gin.New()

	// Which peers' X-Forwarded-For to believe. Left unset, gin trusts every
	// source, and a header any client can write becomes the address in the
	// request log — so an unauthenticated caller could write whatever it
	// liked into the only record of who called. Naming the proxy restores
	// the real client address; naming nothing logs the connection's own
	// origin, which is less precise but never a lie.
	//
	// A bad entry is a start-up failure rather than a silent fallback: a
	// typo here would otherwise leave the service running and quietly
	// logging proxy addresses forever.
	if err := engine.SetTrustedProxies(cfg.TrustedProxies); err != nil {
		return fmt.Errorf("trusted proxies: %w", err)
	}
	if len(cfg.TrustedProxies) > 0 {
		logger.Info("forwarded headers trusted from", "proxies", cfg.TrustedProxies)
	} else {
		logger.Warn("no trusted proxies configured: client_ip in the log is the connecting peer, " +
			"not the client behind a proxy")
	}

	engine.Use(
		httpx.RequestIDMiddleware(),
		httpx.Logger(logger),
		httpx.RecoveryMiddleware(logger),
	)

	// Everything the outside world reaches is mounted under the base path, so
	// a deployment that answers on https://host/wechat_auth/ routes there
	// rather than at the root. The prefix is its own setting rather than
	// derived from WCB_PUBLIC_BASE_URL because the two legitimately differ:
	// behind a proxy that strips the prefix, the public URL carries the path
	// and this is empty. config.validate rejects the arrangements that are not
	// one of those two.
	//
	// /healthz is the exception and stays at the root. It is liveness for the
	// process and the reverse proxy in front of it, neither of which has any
	// reason to know the public path; a probe that 404s because a path was
	// configured is a probe that takes the deployment down.
	engine.GET("/healthz", handleHealthz)

	// gin.IRouter, not *gin.Engine: the packages below take a router to mount
	// on and have no business knowing whether it is the engine or a group
	// under it. The engine still implements it, so an empty base path changes
	// nothing about what they do.
	var api gin.IRouter = engine
	if cfg.BasePath != "" {
		api = engine.Group(cfg.BasePath)
	}

	authService.RegisterRoutes(api)
	activationService.RegisterRoutes(api, authService.RequireAuth())

	// An empty whitelist is "no console", not "console that trusts
	// everybody": the routes are not mounted at all, so a deployment that
	// forgot WCB_ADMIN_OPENIDS answers 404 on the admin API rather than
	// signing licences for whoever asks.
	if cfg.AdminEnabled() {
		adminService, err := admin.NewService(admin.Options{
			Auth:           st,
			Activation:     st,
			Signer:         signer,
			AdminOpenIDs:   cfg.AdminOpenIDs,
			BuyoutLifetime: cfg.BuyoutLifetime(),
			AccountOf:      auth.AccountIDFrom,
			Logger:         logger,
		})
		if err != nil {
			return fmt.Errorf("admin service: %w", err)
		}
		adminService.RegisterRoutes(api, authService.RequireAuth())
		logger.Info("admin API mounted", "sellers", len(cfg.AdminOpenIDs))
	} else {
		logger.Warn("admin API not mounted: WCB_ADMIN_OPENIDS is empty, no console this run")
	}

	logger.Info("routes mounted", "base_path", orRoot(cfg.BasePath))

	srv := &http.Server{
		Addr: cfg.HTTPAddr,
		// No WriteTimeout: nothing here streams, and a timeout that fires
		// mid-response is a corrupted licence from the client's point of
		// view. ReadHeaderTimeout and IdleTimeout cover the slowloris case,
		// which is what these are for.
		Handler:           engine,
		ReadHeaderTimeout: 10 * time.Second,
		IdleTimeout:       60 * time.Second,
	}

	serveErr := make(chan error, 1)
	go func() {
		logger.Info("listening", "addr", srv.Addr, "env", cfg.Env)
		if err := srv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			serveErr <- err
			return
		}
		serveErr <- nil
	}()

	select {
	case err := <-serveErr:
		return err
	case <-ctx.Done():
		logger.Info("shutdown signal received, draining")
	}

	shutdownCtx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	if err := srv.Shutdown(shutdownCtx); err != nil {
		return fmt.Errorf("shutdown: %w", err)
	}
	if err := <-serveErr; err != nil {
		return err
	}
	logger.Info("stopped")
	return nil
}

// migrate brings the database up to the embedded schema through its own
// short-lived pool, which is closed before the server starts serving. DDL
// does not need the application pool's sizing or its UTC session setting,
// and keeping the two apart means a failed migration never leaves a pool
// behind for the deploy to wonder about.
func migrate(ctx context.Context, dsn string, logger *slog.Logger) error {
	pool, err := pgxpool.New(ctx, dsn)
	if err != nil {
		return fmt.Errorf("open migration pool: %w", err)
	}
	defer pool.Close()

	applied, err := migrateFrom(ctx, pool, migrations.FS)
	if err != nil {
		return err
	}
	if len(applied) == 0 {
		logger.Info("schema up to date")
		return nil
	}
	for _, name := range applied {
		logger.Info("migration applied", "version", name)
	}
	return nil
}

// newSigner loads the issuing key and logs the matching public key — the
// exact string the desktop client embeds, and safe to print, which is what
// makes a support conversation about "wrong key" a one-line answer.
func newSigner(cfg config.Config, logger *slog.Logger) (*license.Signer, error) {
	key, err := license.LoadPrivateKey(cfg.LicensePrivateKeyPath)
	if err != nil {
		return nil, err
	}
	signer, err := license.NewSigner(key)
	if err != nil {
		return nil, err
	}
	public, err := signer.PublicBase64()
	if err != nil {
		return nil, fmt.Errorf("license: render public key: %w", err)
	}
	logger.Info("licence issuer ready", "public_key", public)
	return signer, nil
}

type healthResponse struct {
	Status string `json:"status"`
}

// handleHealthz is liveness: the process is up and the routes are mounted.
// Readiness — the database answering — is what every real endpoint already
// proves and what the boot-time migration and ping proved before this
// process ever listened; a separate readiness probe belongs with the
// deployment story (design §11.5, item F).
func handleHealthz(c *gin.Context) {
	c.JSON(http.StatusOK, healthResponse{Status: "ok"})
}

// orRoot renders an empty base path for the log line, so "routes mounted
// base_path=/" reads as a fact about this deployment rather than looking like
// a field that failed to populate.
func orRoot(p string) string {
	if p == "" {
		return "/"
	}
	return p
}

// newLogger picks the handler and the level from the environment: JSON in
// production because that is what a log pipeline eats, text in development
// because that is what a human reads.
func newLogger(cfg config.Config) *slog.Logger {
	level := slog.LevelInfo
	switch cfg.LogLevel {
	case "debug":
		level = slog.LevelDebug
	case "warn":
		level = slog.LevelWarn
	case "error":
		level = slog.LevelError
	}
	opts := &slog.HandlerOptions{Level: level}
	var handler slog.Handler
	if cfg.IsProd() {
		handler = slog.NewJSONHandler(os.Stdout, opts)
	} else {
		handler = slog.NewTextHandler(os.Stdout, opts)
	}
	return slog.New(handler)
}
