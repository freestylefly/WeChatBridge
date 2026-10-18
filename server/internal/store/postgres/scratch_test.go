package postgres

import (
	"context"
	"testing"

	"github.com/pashagolub/pgxmock/v3"
)

func TestScratchCreated(t *testing.T) {
	s, mock := newMock(t)
	mock.ExpectQuery("INSERT INTO accounts").
		WithArgs(anyArgs(6)...).
		WillReturnRows(accountRow(true, "acct-1", "open-1"))

	acct, created, err := s.UpsertAccountByOpenID(context.Background(), "open-1", "u", "n", "a")
	t.Logf("err=%v created=%v acct=%+v", err, created, acct)
	row := accountRow(true, "x", "y")
	t.Logf("scratch built")
	_ = row
	_ = pgxmock.NewRows
}
