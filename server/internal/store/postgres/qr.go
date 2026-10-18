package postgres

import (
	"context"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// The QR cache is the only part of the store that is allowed to forget.
//
// It is a cache, not a ledger: a miss costs the user one re-scan and nothing
// else, so every failure to find a row comes back as store.ErrNotFound and the
// caller mints a fresh code. That is why nothing here is a transaction and
// why nothing here distinguishes a conflict.

const putQrSQL = `
	INSERT INTO qr_payloads (kind, ref, body, at)
	VALUES ($1, $2, $3, $4)
	ON CONFLICT (kind, ref) DO UPDATE SET body = EXCLUDED.body, at = EXCLUDED.at`

const getQrSQL = `
	SELECT kind, ref, body, at
	FROM qr_payloads
	WHERE kind = $1 AND ref = $2`

const deleteQrSQL = `
	DELETE FROM qr_payloads WHERE kind = $1 AND ref = $2`

// Put stores or refreshes a QR payload. Regenerating a login code reuses the
// same (kind, ref) row rather than accumulating one per refresh.
func (s *Store) Put(ctx context.Context, p store.QrPayload) error {
	_, err := s.db.Exec(ctx, putQrSQL, p.Kind, p.Ref, p.Body, utc(p.At))
	if err != nil {
		return wrap("put qr payload", err)
	}
	return nil
}

// Get reads a QR payload, or ErrNotFound so the caller issues a fresh one.
func (s *Store) Get(ctx context.Context, kind, ref string) (store.QrPayload, error) {
	var p store.QrPayload
	err := s.db.QueryRow(ctx, getQrSQL, kind, ref).Scan(&p.Kind, &p.Ref, &p.Body, &p.At)
	if err != nil {
		return store.QrPayload{}, notFound("get qr payload", err)
	}
	p.At = utc(p.At)
	return p, nil
}

// Delete evicts a QR payload. Deleting one that is not there is a success:
// the caller's intent was "this should not be usable", and it is not.
func (s *Store) Delete(ctx context.Context, kind, ref string) error {
	if _, err := s.db.Exec(ctx, deleteQrSQL, kind, ref); err != nil {
		return wrap("delete qr payload", err)
	}
	return nil
}
