-- =====================================================================================
-- Trading Platform Demo - schema
-- =====================================================================================
-- Entirely synthetic. This schema was designed for this demonstration project; it models a
-- deliberately simplified retail trading platform and is not derived from any real system.
--
-- The shape is chosen so that meaningful QA data-validation queries are possible:
--   users 1--* accounts 1--* orders *--1 instruments
--   orders 1--* order_fills          (lets us verify partial fills aggregate correctly)
--   audit_events                     (lets us verify side effects of an API/UI action)
--
-- Dialect: SQLite. Chosen so the whole project runs with zero external infrastructure -
-- see docs/design-decisions.md for the trade-off discussion.
-- =====================================================================================

PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS users (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    username        TEXT    NOT NULL UNIQUE,
    email           TEXT    NOT NULL,
    -- Synthetic, non-reversible. See seed script: these are salted SHA-256 hashes of
    -- throwaway demo passwords that exist only inside this repository.
    password_hash   TEXT    NOT NULL,
    password_salt   TEXT    NOT NULL,
    status          TEXT    NOT NULL CHECK (status IN ('Active', 'Suspended', 'Closed')),
    country_code    TEXT    NOT NULL,
    created_at      TEXT    NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS accounts (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id         INTEGER NOT NULL REFERENCES users(id),
    account_number  TEXT    NOT NULL UNIQUE,
    currency        TEXT    NOT NULL CHECK (length(currency) = 3),
    balance         REAL    NOT NULL DEFAULT 0,
    account_type    TEXT    NOT NULL CHECK (account_type IN ('Demo', 'Live')),
    created_at      TEXT    NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS instruments (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    symbol          TEXT    NOT NULL UNIQUE,
    display_name    TEXT    NOT NULL,
    asset_class     TEXT    NOT NULL CHECK (asset_class IN ('FX', 'Metal', 'Index', 'Crypto')),
    bid             REAL    NOT NULL,
    ask             REAL    NOT NULL,
    min_quantity    REAL    NOT NULL DEFAULT 0.01,
    max_quantity    REAL    NOT NULL DEFAULT 100,
    is_tradable     INTEGER NOT NULL DEFAULT 1 CHECK (is_tradable IN (0, 1))
);

CREATE TABLE IF NOT EXISTS orders (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    order_reference TEXT    NOT NULL UNIQUE,
    account_id      INTEGER NOT NULL REFERENCES accounts(id),
    instrument_id   INTEGER NOT NULL REFERENCES instruments(id),
    side            TEXT    NOT NULL CHECK (side IN ('Buy', 'Sell')),
    order_type      TEXT    NOT NULL CHECK (order_type IN ('Market', 'Limit')),
    quantity        REAL    NOT NULL CHECK (quantity > 0),
    limit_price     REAL    NULL,
    status          TEXT    NOT NULL CHECK (status IN ('Pending', 'Filled', 'Rejected', 'Cancelled')),
    created_at      TEXT    NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS order_fills (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    order_id        INTEGER NOT NULL REFERENCES orders(id),
    fill_quantity   REAL    NOT NULL CHECK (fill_quantity > 0),
    fill_price      REAL    NOT NULL,
    filled_at       TEXT    NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS audit_events (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id         INTEGER NULL REFERENCES users(id),
    event_type      TEXT    NOT NULL,
    detail          TEXT    NULL,
    created_at      TEXT    NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS ix_accounts_user        ON accounts (user_id);
CREATE INDEX IF NOT EXISTS ix_orders_account       ON orders (account_id);
CREATE INDEX IF NOT EXISTS ix_orders_instrument    ON orders (instrument_id);
CREATE INDEX IF NOT EXISTS ix_orders_reference     ON orders (order_reference);
CREATE INDEX IF NOT EXISTS ix_order_fills_order    ON order_fills (order_id);
CREATE INDEX IF NOT EXISTS ix_audit_events_user    ON audit_events (user_id, event_type);
