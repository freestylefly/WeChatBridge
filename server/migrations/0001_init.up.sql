-- 0001_init.up.sql — the whole persistence layer, first cut.
--
-- SQLite dialect. This file was rewritten in place by the PostgreSQL→SQLite
-- migration (docs/design/sqlite-migration.zh-CN.md); no deployment of the
-- previous shape ever existed, so there is no data to carry forward and no
-- second migration to write.
--
-- House rules this file obeys, because breaking any of them is a bug that
-- shows up as money or as a security hole rather than as a stack trace:
--
--   * Every instant is `TIMESTAMP` and every instant is stored in UTC. The
--     declared type is load-bearing, not cosmetic: the driver only parses
--     TEXT into a time.Time when the column is declared TIMESTAMP (or
--     DATE/DATETIME), and SQLite has no native temporal type — the value is
--     text either way. Writing the same column as TEXT would make every
--     scan fail with "unsupported Scan".
--
--   * Every timestamp the application binds is a time.Time, never a string
--     (see R-new in the design doc: two different literal formats in one
--     column silently break lexicographic order). DEFAULT CURRENT_TIMESTAMP
--     is UTC and second-granular, so it sorts before any sub-second value
--     written in the same second by the driver — which is also its correct
--     temporal position. Never write a third format by hand.
--
--   * Nothing that must happen exactly once is implemented as
--     "SELECT then INSERT". Every such rule is a conditional UPDATE that
--     reports how many rows it touched, or a unique index. The application
--     reads the row count; it never reads the row and then decides.
--
--   * Tokens and licences are stored as *hashes* or as exact signed bytes.
--     The plaintext refresh token never reaches this database, so a dump of
--     it does not become a session; the licence envelope is stored as the
--     bytes that were returned the first time, because ECDSA picks a random
--     k and a re-sign would fork on the difference. BLOB, not TEXT: the
--     bytes must round-trip byte for byte.
--
--   * CHECK constraints encode the invariants that are expensive to get wrong
--     under concurrency. Each one says in a comment why it exists, because a
--     constraint nobody understands is a constraint somebody eventually
--     removes.
--
--   * `PRAGMA foreign_keys=ON` is per-connection, not per-database, and it
--     defaults OFF. Without it every ON DELETE action below is silently
--     inert. The store's DSN sets it and start-up asserts it reads back 1;
--     running this file through a sqlite3 shell by hand will not enforce the
--     foreign keys unless you set the pragma first.
--
-- No BEGIN/COMMIT here: internal/migrate wraps each file in its own
-- transaction together with the bookkeeping row, so a crash mid-migration
-- leaves the file unapplied rather than half-applied.

-- ---------------------------------------------------------------------------
-- accounts — one row per WeChat identity, and its one live token session.
-- ---------------------------------------------------------------------------
CREATE TABLE accounts (
    id            text        PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
    open_id       text        NOT NULL,
    union_id      text,
    nickname      text        NOT NULL DEFAULT '',
    avatar_url    text        NOT NULL DEFAULT '',
    plan          text        NOT NULL DEFAULT 'free',
    install_id    text,
    wechat_bound  BOOLEAN     NOT NULL DEFAULT false,
    created_at    TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_login_at TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,

    -- The token chain. One live session per account: the refresh token's
    -- hash, the hash it replaced (whose reappearance is the reuse signal),
    -- the expiry the replacement inherits, the chain it belongs to, and the
    -- family kill switch. A second sign-in overwrites the slot, which is the
    -- machine-change path — see store.Session.
    refresh_token_hash      text,
    prev_refresh_token_hash text,
    refresh_expires_at      TIMESTAMP,
    chain_id                text,
    session_revoked_at      TIMESTAMP,

    -- The insert marker replaces PostgreSQL's `(xmax = 0) AS created`.
    -- SQLite has no system column that says "this row came from the INSERT
    -- branch", so the sign-in upsert writes a fresh random nonce here on
    -- INSERT and the DO UPDATE branch never touches it: comparing the stored
    -- value against the nonce the caller generated answers "was this row
    -- new" with zero extra queries. The value itself is meaningless and is
    -- never read — only compared. See internal/store/sqlite/auth.go.
    insert_marker text        NOT NULL DEFAULT '',

    CONSTRAINT accounts_plan_check
        CHECK (plan IN ('free', 'buyout', 'subscription'))
);

-- open_id is the identity WeChat knows the person by. It is not portable
-- across WeChat surfaces, but within one website app it is stable and unique,
-- and it is the conflict target of the sign-in upsert.
CREATE UNIQUE INDEX accounts_open_id_key ON accounts (open_id);

-- union_id ties the same human across the website app, the mini programme and
-- the official account. It is NULL more often than not (only accounts bound
-- to an open platform account have one) and empty strings mean "unknown" too,
-- so a partial index is the only thing that does not collapse every unbound
-- account onto one value. Empty is normalised to NULL by the application.
CREATE UNIQUE INDEX accounts_union_id_key
    ON accounts (union_id)
    WHERE union_id IS NOT NULL AND union_id <> '';

CREATE INDEX accounts_install_id_idx ON accounts (install_id) WHERE install_id IS NOT NULL;

-- The refresh token's hash is the lookup key for every refresh call, so it
-- carries a unique index: two accounts resolving the same presented token is
-- a state the rotation logic must never have to disambiguate.
CREATE UNIQUE INDEX accounts_refresh_token_hash_key
    ON accounts (refresh_token_hash)
    WHERE refresh_token_hash IS NOT NULL;

-- The replaced hash is not unique: it is only ever compared against the
-- presented token, and a collision with another account's live hash is a
-- SHA-256 collision, not a state.
CREATE INDEX accounts_prev_refresh_token_hash_idx
    ON accounts (prev_refresh_token_hash)
    WHERE prev_refresh_token_hash IS NOT NULL;

-- ---------------------------------------------------------------------------
-- auth_sessions — one QR sign-in in flight.
-- ---------------------------------------------------------------------------
CREATE TABLE auth_sessions (
    ticket        text        PRIMARY KEY,
    state         text        NOT NULL DEFAULT 'pending',
    install_id    text        NOT NULL,
    authorize_url text        NOT NULL DEFAULT '',
    account_id    text        REFERENCES accounts (id) ON DELETE SET NULL,
    expires_at    TIMESTAMP   NOT NULL,
    created_at    TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT auth_sessions_state_check
        CHECK (state IN ('pending', 'authorized', 'expired', 'rejected')),

    -- A session that claims to be authorized but names no account is a lie the
    -- polling client would happily believe, and it can only be produced by a
    -- half-finished write. The store writes state and account_id in one
    -- statement, so this constraint is the database agreeing that they are
    -- inseparable.
    CONSTRAINT auth_sessions_authorized_has_account
        CHECK (state <> 'authorized' OR account_id IS NOT NULL)
);

-- The primary key is already the unique index the ticket lookup needs; it is
-- named here in a comment because "unique on auth_sessions.ticket" is the kind
-- of thing a reader should be able to find without reading the code.
CREATE INDEX auth_sessions_expires_at_idx ON auth_sessions (expires_at);
CREATE INDEX auth_sessions_install_id_idx ON auth_sessions (install_id);

-- ---------------------------------------------------------------------------
-- activation_requests — one purchase registration, and the seller's answer.
--
-- This table replaces orders + redemption_requests + redeem_codes. The whole
-- payment state machine collapsed into one status column because the only
-- state transition left is a person pressing a button after checking WeChat's
-- own ledger.
-- ---------------------------------------------------------------------------
CREATE TABLE activation_requests (
    request_id    text        PRIMARY KEY,
    account_id    text        NOT NULL REFERENCES accounts (id) ON DELETE CASCADE,
    install_id    text        NOT NULL,
    anchor_digest text        NOT NULL DEFAULT '',
    -- Advisory: what the buyer says they were asked to pay. NULL means the
    -- buyer did not say, which is a normal answer and not a missing field.
    amount_fen    INTEGER,
    status        text        NOT NULL DEFAULT 'pending',
    -- BLOB, not TEXT, and that is the whole point: the bytes that were
    -- returned the first time must come back byte for byte. The signature is
    -- the product; a text round-trip that re-encodes anything is a fork.
    envelope_json BLOB,
    license_id    text        NOT NULL DEFAULT '',
    created_at    TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    confirmed_at  TIMESTAMP,

    CONSTRAINT activation_requests_status_check
        CHECK (status IN ('pending', 'confirmed', 'rejected')),
    CONSTRAINT activation_requests_install_not_blank CHECK (install_id <> ''),
    CONSTRAINT activation_requests_amount_check CHECK (amount_fen IS NULL OR amount_fen >= 0),

    -- "Confirmed" is a claim about three columns at once: the seller acted,
    -- at a known time, and a licence came out of it. A row that says
    -- confirmed while any of the three is missing is a buyer whose poller
    -- will wait forever for a token that does not exist.
    CONSTRAINT activation_requests_confirmed_is_complete
        CHECK (status <> 'confirmed'
               OR (confirmed_at IS NOT NULL AND license_id <> ''))
);

-- The buyer's poller reads their own latest request; the console's list reads
-- by status. Both are "recent first", so the index carries the sort.
CREATE INDEX activation_requests_account_idx
    ON activation_requests (account_id, created_at DESC);
CREATE INDEX activation_requests_status_idx
    ON activation_requests (status, created_at DESC);

-- One live registration per machine. This is the registration idempotency in
-- schema form: a second "buy" tap for a machine that already has a pending
-- request cannot queue a second line on the seller's console, and the upsert
-- in activation.go keys on exactly this predicate. Partial because a machine
-- that was confirmed, rejected or reissued is free to register again — the
-- next purchase is a new row, not a conflict.
CREATE UNIQUE INDEX activation_requests_live_install_idx
    ON activation_requests (install_id)
    WHERE status = 'pending';

-- ---------------------------------------------------------------------------
-- licence_bindings — which machines a licence is pinned to.
--
-- The order_id foreign key is gone with the orders table; what ties a binding
-- to its purchase now is request_id, and what ties it to a person is
-- account_id. The two partial unique indexes are the machine-change policy in
-- schema form: one live licence per machine, one live licence per account.
-- ---------------------------------------------------------------------------
CREATE TABLE licence_bindings (
    license_id  text        PRIMARY KEY,
    install_id  text        NOT NULL,
    account_id  text        NOT NULL REFERENCES accounts (id) ON DELETE CASCADE,
    request_id  text,
    issued_at   TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    revoked_at  TIMESTAMP,

    -- A revocation dated before the licence existed is a bug in whatever
    -- wrote it, not something to store.
    CONSTRAINT licence_bindings_revoked_is_dated
        CHECK (revoked_at IS NULL OR revoked_at >= issued_at)
);

-- One live binding per machine. A reissue revokes the old row and inserts the
-- new one in the same transaction, so this index never sees two live rows for
-- one install id — and a confirm that would create the second one is the
-- machine-change case the seller must answer with a reissue, not a second
-- licence.
CREATE UNIQUE INDEX licence_bindings_live_idx
    ON licence_bindings (install_id)
    WHERE revoked_at IS NULL;

-- One live binding per account: paying twice is a new purchase, and a second
-- machine for one buyer is a reissue. Both are seller decisions, which means
-- neither is something the schema should allow to happen silently.
CREATE UNIQUE INDEX licence_bindings_live_account_idx
    ON licence_bindings (account_id)
    WHERE revoked_at IS NULL;

-- The ledger page reads "everything this account holds, newest first".
CREATE INDEX licence_bindings_account_idx
    ON licence_bindings (account_id, issued_at DESC);
