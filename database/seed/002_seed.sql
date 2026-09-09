-- =====================================================================================
-- Trading Platform Demo - deterministic seed data
-- =====================================================================================
-- Every value below is synthetic and invented for this demonstration.
--
-- On credentials: the password for every seeded account is the same throwaway string
-- 'Demo!Pass123'. It grants access to nothing except a local SQLite file created on your own
-- machine. It is stored as Base64(SHA-256(salt + ':' + password)), which is how the demo API
-- verifies it - see TradingDemo.App/Domain/PasswordHasher.cs.
--
-- Real projects must not ship credentials at all; a demo application whose entire purpose is
-- to be logged into by an automated test is the narrow exception, and the accounts are inert.
-- See SECURITY_REVIEW.md.
--
-- The seed is deliberately *deterministic*: the same rows, the same ids, every run. Tests that
-- need unpredictable data generate it at runtime (see QaFramework.Core/TestData) rather than
-- relying on a growing seed file.
-- =====================================================================================

DELETE FROM audit_events;
DELETE FROM order_fills;
DELETE FROM orders;
DELETE FROM accounts;
DELETE FROM instruments;
DELETE FROM users;
DELETE FROM sqlite_sequence;

-- ---------------------------------------------------------------------------------------
-- Users. Roles are modelled explicitly so that scenarios can say "an active trader" rather
-- than naming a person, and so negative paths (suspended account) have a home.
-- ---------------------------------------------------------------------------------------
INSERT INTO users (id, username, email, password_hash, password_salt, status, country_code, created_at) VALUES
    (1, 'trader.demo',      'trader.demo@example.invalid',      'zgGE43n1/BtqrNoz4wXpzvvXkuqtOPKOHPP76EgcLko=', 's1a2b3c4d5e6f708',  'Active',    'GB', '2024-01-15 09:00:00'),
    (2, 'trader.active',    'trader.active@example.invalid',    'xq4lQOLefq4AUGQy0gnB5+l0fhacniAPET/1Euq0quA=', 's9f8e7d6c5b4a302',  'Active',    'AE', '2024-02-03 11:30:00'),
    (3, 'trader.suspended', 'trader.suspended@example.invalid', 'uS6iiGEgKEHmVdQ2nXkY6s/ItH5SA/v4LWrJhX7TbR0=', 's0102030405060708', 'Suspended', 'CY', '2024-02-20 14:45:00'),
    (4, 'analyst.readonly', 'analyst.readonly@example.invalid', '1/2BXu9A5eqTLwMCaADmm9roctMNVSFDIVx0a0HHGTU=', 'sa1b2c3d4e5f60718', 'Active',    'GB', '2024-03-01 08:15:00');

-- ---------------------------------------------------------------------------------------
-- Accounts. Note user 4 deliberately has no account: that gives us a real 404 path to test
-- rather than a contrived one.
-- ---------------------------------------------------------------------------------------
INSERT INTO accounts (id, user_id, account_number, currency, balance, account_type, created_at) VALUES
    (1, 1, 'DEMO-1000001', 'USD', 25000.00, 'Demo', '2024-01-15 09:05:00'),
    (2, 2, 'DEMO-1000002', 'USD', 50000.00, 'Demo', '2024-02-03 11:35:00'),
    (3, 3, 'DEMO-1000003', 'EUR',  1200.00, 'Demo', '2024-02-20 14:50:00'),
    (4, 1, 'DEMO-1000004', 'GBP',   150.00, 'Demo', '2024-04-01 10:00:00');

-- ---------------------------------------------------------------------------------------
-- Instruments. GOLD-SPOT is intentionally non-tradable, and BTCUSD has a low max quantity,
-- so boundary and business-rule tests have something real to assert against.
-- ---------------------------------------------------------------------------------------
INSERT INTO instruments (id, symbol, display_name, asset_class, bid, ask, min_quantity, max_quantity, is_tradable) VALUES
    (1, 'EURUSD',     'Euro / US Dollar',        'FX',     1.08420, 1.08435, 0.01,  50.00, 1),
    (2, 'GBPUSD',     'British Pound / US Dollar','FX',    1.26510, 1.26529, 0.01,  50.00, 1),
    (3, 'USDJPY',     'US Dollar / Japanese Yen','FX',   157.20500, 157.21200, 0.01, 50.00, 1),
    (4, 'XAUUSD',     'Gold / US Dollar',        'Metal', 2318.40000, 2318.75000, 0.01, 20.00, 1),
    (5, 'BTCUSD',     'Bitcoin / US Dollar',     'Crypto', 61250.00, 61285.00, 0.01,  2.00, 1),
    (6, 'US500',      'US 500 Index',            'Index',  5238.20, 5238.90, 0.10,  25.00, 1),
    (7, 'GOLD-SPOT',  'Gold Spot (delisted)',    'Metal', 2318.40000, 2318.75000, 0.01, 20.00, 0);

-- ---------------------------------------------------------------------------------------
-- Orders. Shaped to make the validation queries in database/validation meaningful:
--   * account 1 has 6 orders  -> exercises HAVING COUNT(*) > 5
--   * order 3 is partially filled across two fills -> exercises SUM() reconciliation
--   * order 6 is Filled but has NO fill rows       -> a genuine data-integrity defect to find
-- ---------------------------------------------------------------------------------------
INSERT INTO orders (id, order_reference, account_id, instrument_id, side, order_type, quantity, limit_price, status, created_at) VALUES
    (1, 'ORD-20240401-0001', 1, 1, 'Buy',  'Market', 1.00,  NULL,    'Filled',    '2024-04-01 09:10:00'),
    (2, 'ORD-20240401-0002', 1, 2, 'Sell', 'Limit',  2.00,  1.27000, 'Pending',   '2024-04-01 09:20:00'),
    (3, 'ORD-20240402-0003', 1, 4, 'Buy',  'Market', 5.00,  NULL,    'Filled',    '2024-04-02 10:05:00'),
    (4, 'ORD-20240402-0004', 1, 5, 'Buy',  'Limit',  0.50,  60000.00,'Cancelled', '2024-04-02 11:00:00'),
    (5, 'ORD-20240403-0005', 1, 1, 'Sell', 'Market', 1.50,  NULL,    'Filled',    '2024-04-03 08:45:00'),
    (6, 'ORD-20240403-0006', 1, 6, 'Buy',  'Market', 3.00,  NULL,    'Filled',    '2024-04-03 09:30:00'),
    (7, 'ORD-20240404-0007', 2, 1, 'Buy',  'Market', 10.00, NULL,    'Filled',    '2024-04-04 09:00:00'),
    (8, 'ORD-20240404-0008', 2, 3, 'Sell', 'Limit',  4.00,  158.00000,'Pending',  '2024-04-04 09:15:00'),
    (9, 'ORD-20240405-0009', 3, 1, 'Buy',  'Market', 1.00,  NULL,    'Rejected',  '2024-04-05 12:00:00');

INSERT INTO order_fills (id, order_id, fill_quantity, fill_price, filled_at) VALUES
    (1, 1, 1.00, 1.08435, '2024-04-01 09:10:02'),
    -- Order 3 filled in two tranches; SUM(fill_quantity) must equal orders.quantity.
    (2, 3, 2.00, 2318.75000, '2024-04-02 10:05:01'),
    (3, 3, 3.00, 2318.80000, '2024-04-02 10:05:04'),
    (4, 5, 1.50, 1.08420, '2024-04-03 08:45:01'),
    (5, 7, 10.00, 1.08435, '2024-04-04 09:00:03');

INSERT INTO audit_events (user_id, event_type, detail, created_at) VALUES
    (1, 'LoginSucceeded', 'trader.demo',      '2024-04-01 09:00:00'),
    (1, 'OrderPlaced',    'ORD-20240401-0001','2024-04-01 09:10:00'),
    (3, 'LoginBlocked',   'account suspended','2024-04-05 11:59:00'),
    (2, 'LoginSucceeded', 'trader.active',    '2024-04-04 08:59:00');
