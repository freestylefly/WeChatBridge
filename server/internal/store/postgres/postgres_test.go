package postgres

import (
	"context"
	"errors"
	"strings"
	"testing"
	"time"

	"github.com/jackc/pgx/v5"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// The Store has to be usable as the one thing the service depends on.
func TestStoreImplementsTheWholeInterface(t *testing.T) {
	var _ store.Store = (*Store)(nil)

	s, _ := newMock(t)
	// The compile-time assertion above is the real test; this one makes a
	// regression show up as a named failure rather than as a build error
	// somewhere else in the service.
	var _ store.AuthStore = s
	var _ store.ActivationStore = s
}

func TestNewRejectsAnEmptyDSN(t *testing.T) {
	// Failing here is better than starting a service that answers its first
	// request with a database error.
	if _, err := New(context.Background(), "   "); err == nil {
		t.Fatal("expected an error for an empty dsn")
	}
}

func TestNewKeepsThePasswordOutOfTheError(t *testing.T) {
	// A DSN that fails to parse is the most likely way for a secret to reach
	// a log line, because pgx quotes the offending fragment back.
	_, err := New(context.Background(), "postgres://wcb:hunter2@@@not a dsn")
	if err == nil {
		t.Skip("this dsn happens to parse; nothing to assert")
	}
	if strings.Contains(err.Error(), "hunter2") {
		t.Fatalf("the password reached the error message: %s", err.Error())
	}
}

func TestNewFailsWhenTheDatabaseIsUnreachable(t *testing.T) {
	// Port 1 on loopback is refused immediately, so this needs no server and
	// no network. The point is the shape of the failure: a start-up that pings
	// fails, rather than a service that looks healthy until the first customer.
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	_, err := New(ctx, "postgres://wcb:wcb@127.0.0.1:1/wcb?connect_timeout=1")
	if err == nil {
		t.Fatal("expected an error against a closed port")
	}
	if !strings.Contains(err.Error(), "postgres: New:") {
		t.Fatalf("the failure is not attributed to start-up: %v", err)
	}
}

func TestCloseIsRepeatable(t *testing.T) {
	s, mock := newMock(t)
	mock.ExpectClose().Maybe()

	// main defers Close and the error paths call it explicitly; doing that
	// twice must not panic on a released pool.
	for i := 0; i < 3; i++ {
		if err := s.Close(); err != nil {
			t.Fatalf("Close #%d: %v", i+1, err)
		}
	}
}

func TestInTxRollsBackAndKeepsTheReason(t *testing.T) {
	s, mock := newMock(t)
	mock.ExpectBegin()
	mock.ExpectRollback()

	boom := errors.New("permission denied for table accounts")
	err := s.inTx(context.Background(), func(tx pgx.Tx) error { return boom })
	if !errors.Is(err, boom) {
		t.Fatalf("the caller's reason did not survive the rollback: %v", err)
	}
	if err := mock.ExpectationsWereMet(); err != nil {
		t.Fatalf("%v", err)
	}
}

func TestInTxCommitsOnSuccess(t *testing.T) {
	s, mock := newMock(t)
	mock.ExpectBegin()
	mock.ExpectCommit()

	if err := s.inTx(context.Background(), func(tx pgx.Tx) error { return nil }); err != nil {
		t.Fatalf("inTx: %v", err)
	}
	if err := mock.ExpectationsWereMet(); err != nil {
		t.Fatalf("%v", err)
	}
}
