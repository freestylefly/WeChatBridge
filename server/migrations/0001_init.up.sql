-- 0001_init.up.sql — the whole persistence layer, first cut.
--
-- House rules this file obeys, because breaking any of them is a bug that
-- shows up as money or as a security hole rather than as a stack trace:
--
--   * Every instant is `timestamptz` and every instant is stored in UTC. The
--     connection sets `timezone=UTC` (see internal/store/postgres), so the
--     server, the database and the `yyyy-MM-ddTHH:mm:ssZ` the client parses
--     all agree. A licence that expires "yesterday" because one process was
--     configured in Asia/Shanghai is not a bug anyone wants to explain.
--
--   * Nothing that must happen exactly once is implemented as
--     "SELECT then INSERT". Every such rule is a conditional UPDATE that
--     reports how many rows it touched, or a unique index. The application
--     reads the row count; it never reads the row and then decides.
--
--   * Tokens, redeem codes and licences are stored as *hashes*. The plaintext
--     never reaches this database, so a dump of it does not become a session,
--     a licence, or a customer list.
--
--   * Case-insensitive lookups are done with an index on `lower(col)` rather
--     than the `citext` extension, deliberately: CREATE EXTENSION needs a
--     superuser on most managed PostgreSQL, and a migration that fails at
--     deploy time is worse than an extra index. The one column whose case
--     genuinely varies — the redeem code hash, which arrives through email
--     and support chat — is stored verbatim and compared case-insensitively.
--
--   * CHECK constraints encode the invariants that are expensive to get wrong
--     under concurrency. Each one says in a comment why it exists, because a
--     constraint nobody understands is a constraint somebody eventually
--     removes.

BEGIN;

-- ---------------------------------------------------------------------------
-- accounts — one row per WeChat identity.
-- ---------------------------------------------------------------------------
CREATE TABLE accounts (
    id            text        PRIMARY KEY DEFAULT gen_random_uuid()::text,
    open_id       text        NOT NULL,
    union_id      text,
    nickname      text        NOT NULL DEFAULT '',
    avatar_url    text        NOT NULL DEFAULT '',
    plan          text        NOT NULL DEFAULT 'free',
    install_id    text,
    wechat_bound  boolean     NOT NULL DEFAULT false,
    created_at    timestamptz NOT NULL DEFAULT now(),
    last_login_at timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT accounts_plan_check
        CHECK (plan IN ('free', 'buyout', 'subscription'))
);

-- open_id is the identity WeChat knows the person by. It is not portable
-- across WeChat surfaces, but within one website app it is stable and unique,
-- and it is the conflict target of the sign-in upsert.
CREATE UNIQUE INDEX accounts_open_id_key ON accounts (open_id);

-- union_id ties the same human across the website app, the mini programme and
-- the official account. It is NULL more often than not (only accounts bound to
-- an open platform account have one) and empty strings mean "unknown" too, so
-- a partial index is the only thing that does not collapse every unbound
-- account onto one value. Empty is normalised to NULL by the application.
CREATE UNIQUE INDEX accounts_union_id_key
    ON accounts (union_id)
    WHERE union_id IS NOT NULL AND union_id <> '';

CREATE INDEX accounts_install_id_idx ON accounts (install_id) WHERE install_id IS NOT NULL;

-- ---------------------------------------------------------------------------
-- auth_sessions — one QR sign-in in flight.
-- ---------------------------------------------------------------------------
CREATE TABLE auth_sessions (
    ticket        text        PRIMARY KEY,
    state         text        NOT NULL DEFAULT 'pending',
    install_id    text        NOT NULL,
    authorize_url text        NOT NULL DEFAULT '',
    account_id    text        REFERENCES accounts (id) ON DELETE SET NULL,
    expires_at    timestamptz NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),

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
-- sessions + refresh_tokens — one signed-in device, and its token family.
-- ---------------------------------------------------------------------------
CREATE TABLE sessions (
    id                text        PRIMARY KEY DEFAULT gen_random_uuid()::text,
    account_id        text        NOT NULL REFERENCES accounts (id) ON DELETE CASCADE,
    device_id         text        NOT NULL DEFAULT '',
    chain_id          text        NOT NULL,
    access_token_hash text        NOT NULL,
    access_expires_at timestamptz NOT NULL,
    revoked_at        timestamptz,
    created_at        timestamptz NOT NULL DEFAULT now(),

    -- An access token that resolves to two sessions would let one device read
    -- as another; the hash is the only thing that looks the token up.
    CONSTRAINT sessions_access_token_hash_key UNIQUE (access_token_hash),

    -- A row with no chain cannot be revoked as a family, which is the one
    -- operation that reuse detection depends on.
    CONSTRAINT sessions_chain_id_check CHECK (chain_id <> '')
);

CREATE INDEX sessions_chain_id_idx  ON sessions (chain_id);
CREATE INDEX sessions_account_id_idx ON sessions (account_id);

CREATE TABLE refresh_tokens (
    id          text        PRIMARY KEY DEFAULT gen_random_uuid()::text,
    session_id  text        NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
    account_id  text        NOT NULL REFERENCES accounts (id) ON DELETE CASCADE,
    chain_id    text        NOT NULL,
    token_hash  text        NOT NULL,
    device_id   text        NOT NULL DEFAULT '',
    issued_at   timestamptz NOT NULL DEFAULT now(),
    expires_at  timestamptz NOT NULL,
    consumed_at timestamptz,
    rotated_to  text        REFERENCES refresh_tokens (id) ON DELETE SET NULL,
    revoked_at  timestamptz,

    -- Rotation writes consumed_at and rotated_to together, so "consumed but we
    -- do not know what replaced it" is not a state this table can be in. That
    -- matters for the audit that reuse detection is: the chain has to be
    -- walkable forwards from any consumed token.
    CONSTRAINT refresh_tokens_consumption_is_complete
        CHECK (consumed_at IS NULL OR rotated_to IS NOT NULL),

    -- Rotation carries the old expiry forward instead of issuing a fresh one.
    -- Without that rule a client that refreshes every hour holds a chain alive
    -- forever, which turns "30 days to sign in again" into "never".
    CONSTRAINT refresh_tokens_expires_after_issue
        CHECK (expires_at >= issued_at)
);

CREATE UNIQUE INDEX refresh_tokens_token_hash_key ON refresh_tokens (token_hash);
CREATE INDEX refresh_tokens_chain_id_idx ON refresh_tokens (chain_id);
CREATE INDEX refresh_tokens_live_idx
    ON refresh_tokens (chain_id)
    WHERE consumed_at IS NULL AND revoked_at IS NULL;

-- ---------------------------------------------------------------------------
-- quotas — the server-side meter that keeps LLM costs bounded.
-- ---------------------------------------------------------------------------
CREATE TABLE quotas (
    account_id      text        PRIMARY KEY REFERENCES accounts (id) ON DELETE CASCADE,
    used            bigint      NOT NULL DEFAULT 0,
    quota_limit     bigint      NOT NULL,
    reset_at        timestamptz NOT NULL,

    -- The length of one metering window. It lives in the table rather than in
    -- configuration because ConsumeQuota is the only thing that rolls a
    -- window over, and a window that cannot describe its own length cannot be
    -- rolled over: leaving reset_at in the past would make every subsequent
    -- request start from zero, which is unlimited free use.
    window_seconds  bigint      NOT NULL DEFAULT 2592000,

    CONSTRAINT quotas_used_check  CHECK (used >= 0),
    CONSTRAINT quotas_limit_check CHECK (quota_limit >= 0),
    CONSTRAINT quotas_window_check CHECK (window_seconds > 0),

    -- The hard stop. ConsumeQuota only ever succeeds with
    -- `used + units <= quota_limit` in the same UPDATE, so this constraint is
    -- what makes "the database cannot oversell" a fact about the data rather
    -- than a fact about a particular code path. Raising a plan's allowance is
    -- a matter of raising quota_limit above the current used count.
    CONSTRAINT quotas_within_limit CHECK (used <= quota_limit)
);

-- ---------------------------------------------------------------------------
-- redeem_codes — the buy-out product's only online path.
-- ---------------------------------------------------------------------------
CREATE TABLE redeem_codes (
    id                  text        PRIMARY KEY DEFAULT gen_random_uuid()::text,
    code_hash           text        NOT NULL,
    product_id          text        NOT NULL DEFAULT '',
    plan                text        NOT NULL,
    lifetime_seconds    bigint      NOT NULL DEFAULT 0,
    issued_at           timestamptz NOT NULL DEFAULT now(),
    -- A perpetual buy-out licence has no expiry; such a code carries the
    -- maximum timestamp (9999-12-31T00:00:00Z) rather than NULL, because the
    -- field is a plain time.Time in the domain type and a NULL here would
    -- have to mean two different things to two different readers.
    expires_at          timestamptz NOT NULL,
    redeemed_at         timestamptz,
    redeemed_install_id text,
    license_id          text,
    revoked_at          timestamptz,

    CONSTRAINT redeem_codes_plan_check
        CHECK (plan IN ('free', 'buyout', 'subscription')),
    CONSTRAINT redeem_codes_lifetime_check CHECK (lifetime_seconds >= 0),
    CONSTRAINT redeem_codes_hash_not_blank CHECK (code_hash <> ''),

    -- The reason BurnRedeemCode is a transaction. A code that is burnt without
    -- naming the install id cannot be attributed to anybody, and a code that is
    -- burnt without a licence id has destroyed the customer's entitlement: the
    -- row says "spent" and nobody can say what they bought. The flip and both
    -- payloads are written together, so this constraint states that they are
    -- one fact, not three.
    CONSTRAINT redeem_codes_burn_is_complete
        CHECK (redeemed_at IS NULL
               OR (redeemed_install_id IS NOT NULL AND license_id IS NOT NULL)),

    -- Support answers "was my code cancelled?" with this column, not with a
    -- guess — and a cancellation dated before the code existed is a bug in
    -- whatever wrote it, not something to store.
    CONSTRAINT redeem_codes_revocation_is_dated
        CHECK (revoked_at IS NULL OR revoked_at >= issued_at)
);

-- Case-insensitive on purpose. The hash is a hex digest, and a code that
-- arrives by mail, by chat and by hand-typed support ticket is normalised
-- differently every time; matching on lower() means the case nobody agreed on
-- cannot turn a valid code into "not found".
CREATE UNIQUE INDEX redeem_codes_code_hash_key ON redeem_codes (lower(code_hash));

-- ---------------------------------------------------------------------------
-- redemption_requests — one user gesture, one code, one answer.
-- ---------------------------------------------------------------------------
CREATE TABLE redemption_requests (
    request_id text        PRIMARY KEY,
    code_hash  text        NOT NULL,
    license_id text        NOT NULL,

    -- bytea, not jsonb, and that is the whole point: jsonb reorders keys,
    -- drops duplicates and normalises numbers, so storing the envelope as
    -- jsonb would make "the bytes we returned the first time" a different
    -- value every time it was read back. The signature is the product.
    envelope_json bytea     NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now()
);

-- No foreign key to redeem_codes.code_hash on purpose: that key is indexed as
-- lower(code_hash), which cannot carry a referential constraint, and a
-- redemption request must outlive any cleanup of burnt codes. The licence id
-- here is the durable link.
CREATE INDEX redemption_requests_code_hash_idx ON redemption_requests (lower(code_hash));
CREATE INDEX redemption_requests_created_at_idx ON redemption_requests (created_at);

-- ---------------------------------------------------------------------------
-- orders — one purchase attempt.
-- ---------------------------------------------------------------------------
CREATE TABLE orders (
    id               text        PRIMARY KEY DEFAULT gen_random_uuid()::text,
    -- Ours, not WeChat's, and generated by us in upper case. The unique index
    -- on it is what stops a retry from creating a second order for one
    -- checkout and sending the customer two QR codes for one price.
    out_trade_no     text        NOT NULL,
    product_id       text        NOT NULL DEFAULT '',
    plan             text        NOT NULL,
    amount_fen       bigint      NOT NULL,
    currency         text        NOT NULL DEFAULT 'CNY',
    status           text        NOT NULL DEFAULT 'pending',
    account_id       text        NOT NULL REFERENCES accounts (id) ON DELETE RESTRICT,
    code_url         text        NOT NULL DEFAULT '',
    created_at       timestamptz NOT NULL DEFAULT now(),
    expires_at       timestamptz NOT NULL,
    paid_at          timestamptz,
    transaction_id   text        NOT NULL DEFAULT '',
    license_id       text        NOT NULL DEFAULT '',
    license_key      text        NOT NULL DEFAULT '',
    license_envelope bytea,

    CONSTRAINT orders_plan_check   CHECK (plan IN ('free', 'buyout', 'subscription')),
    CONSTRAINT orders_status_check CHECK (status IN ('pending', 'paid', 'closed', 'refunded')),
    -- Money is in fen, integer minor units, never a float. A negative amount is
    -- a refund path that does not exist in this table.
    CONSTRAINT orders_amount_check  CHECK (amount_fen >= 0),
    -- "paid" with no payment reference is the exact row a chargeback cannot be
    -- settled against, and a callback that sets it without a transaction id is
    -- a forgery rather than bookkeeping. MarkPaid is conditional on
    -- status='pending', and this constraint is the backstop that says a paid
    -- order is always evidenced.
    CONSTRAINT orders_paid_is_evidenced
        CHECK (status <> 'paid' OR (paid_at IS NOT NULL AND transaction_id <> '')),

    -- IssueOrderLicense writes all three licence columns or none, so "this
    -- order has an envelope" and "this order has a licence id" cannot
    -- disagree.
    CONSTRAINT orders_license_is_all_or_nothing
        CHECK ((license_id = '') = (license_envelope IS NULL))
);

CREATE UNIQUE INDEX orders_out_trade_no_key ON orders (out_trade_no);
CREATE UNIQUE INDEX orders_transaction_id_key
    ON orders (transaction_id)
    WHERE transaction_id <> '';
CREATE INDEX orders_account_status_idx ON orders (account_id, status);
CREATE INDEX orders_pending_expiry_idx ON orders (expires_at) WHERE status = 'pending';

-- ---------------------------------------------------------------------------
-- licence_bindings — which machines a licence is pinned to.
-- ---------------------------------------------------------------------------
CREATE TABLE licence_bindings (
    id           text        PRIMARY KEY DEFAULT gen_random_uuid()::text,
    order_id     text        NOT NULL REFERENCES orders (id) ON DELETE CASCADE,
    install_id   text        NOT NULL,
    plan         text        NOT NULL DEFAULT 'buyout',
    bound_at     timestamptz NOT NULL DEFAULT now(),
    last_seen_at timestamptz NOT NULL DEFAULT now(),
    released_at  timestamptz,

    CONSTRAINT licence_bindings_plan_check
        CHECK (plan IN ('free', 'buyout', 'subscription')),

    -- One row per machine per order, which is what makes "releasing" a state
    -- change on the same row rather than a delete followed by an insert. A
    -- delete-and-insert history cannot answer "when did this machine first get
    -- its licence", and support asks exactly that.
    CONSTRAINT licence_bindings_order_install_key UNIQUE (order_id, install_id),

    -- A live binding is one that has not been released. The partial index is
    -- the slot count's only access path, so BindDevice's count is cheap enough
    -- to run inside the transaction that holds the order lock.
    CONSTRAINT licence_bindings_released_is_dated
        CHECK (released_at IS NULL OR released_at >= bound_at)
);

CREATE INDEX licence_bindings_live_idx
    ON licence_bindings (order_id)
    WHERE released_at IS NULL;
CREATE INDEX licence_bindings_install_id_idx ON licence_bindings (install_id);

-- ---------------------------------------------------------------------------
-- install_records — what the client did before it had an account.
-- ---------------------------------------------------------------------------
-- Anonymous history is the reason CompleteAuthSession has to be a transaction.
-- A person buys a licence on Tuesday and only signs in on Thursday; when they
-- do, every record still carrying the anonymous install id has to move onto
-- the account, in the same transaction that marks the ticket used. Splitting
-- that in two is the "I paid and my history vanished" bug, and it is not
-- recoverable afterwards — the anonymous rows have no other owner.
CREATE TABLE install_records (
    id         text        PRIMARY KEY DEFAULT gen_random_uuid()::text,
    install_id text        NOT NULL,
    account_id text        REFERENCES accounts (id) ON DELETE CASCADE,
    kind       text        NOT NULL,
    payload    jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX install_records_install_id_idx ON install_records (install_id, created_at);
CREATE INDEX install_records_account_id_idx
    ON install_records (account_id)
    WHERE account_id IS NOT NULL;

-- ---------------------------------------------------------------------------
-- qr_payloads — the cache between "render a code" and "the user acts on it".
-- ---------------------------------------------------------------------------
CREATE TABLE qr_payloads (
    kind text        NOT NULL,
    ref  text        NOT NULL,
    body text        NOT NULL,
    at   timestamptz NOT NULL DEFAULT now(),

    -- The composite key is the cache's identity: a "login" ref and a "pay" ref
    -- may hold the same opaque string without colliding.
    CONSTRAINT qr_payloads_pkey PRIMARY KEY (kind, ref),
    CONSTRAINT qr_payloads_kind_check CHECK (kind IN ('login', 'pay'))
);

CREATE INDEX qr_payloads_at_idx ON qr_payloads (at);

COMMIT;
