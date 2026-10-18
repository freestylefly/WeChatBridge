-- 0001_init.down.sql — reverse of 0001_init.up.sql, in dependency order.
--
-- This drops data. It exists so a deployment can be rolled back to an empty
-- schema; it is not a backup plan, and nothing that has been paid for should
-- ever be recovered this way.

BEGIN;

DROP TABLE IF EXISTS qr_payloads;
DROP TABLE IF EXISTS install_records;
DROP TABLE IF EXISTS licence_bindings;
DROP TABLE IF EXISTS orders;
DROP TABLE IF EXISTS redemption_requests;
DROP TABLE IF EXISTS redeem_codes;
DROP TABLE IF EXISTS quotas;
DROP TABLE IF EXISTS refresh_tokens;
DROP TABLE IF EXISTS sessions;
DROP TABLE IF EXISTS auth_sessions;
DROP TABLE IF EXISTS accounts;

COMMIT;
