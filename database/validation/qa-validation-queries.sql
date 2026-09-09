-- =====================================================================================
-- QA data-validation query set
-- =====================================================================================
-- These are the queries a QA engineer actually reaches for. They fall into three groups,
-- and the distinction matters more than the SQL itself:
--
--   A. POST-OPERATION VERIFICATION  - "the API returned 201; did the right thing happen?"
--                                     Parameterised, run from a test, one row expected.
--   B. DATA-INTEGRITY / INVARIANT   - "is the data self-consistent?" Should return ZERO rows.
--                                     These make excellent nightly guards and CI gates.
--   C. EXPLORATORY / RISK PROFILING - "where should I aim my testing?" Run by a human.
--
-- Queries in group A are executed by the automated suite via QaFramework.Core.Database
-- (parameterised, read-only). Groups B and C are here to be run by hand or by a scheduled job.
-- Every query below runs against the synthetic schema in database/schema.
--
-- Naming convention: :param marks a bound parameter. Never string-concatenate values in.
-- =====================================================================================


-- =====================================================================================
-- GROUP A - POST-OPERATION VERIFICATION
-- =====================================================================================

-- A1. The single most valuable query in the set.
--     After POST /api/orders returns 201 with an order reference, the API response only proves
--     what the API *said*. This proves what was actually persisted, and joins across four
--     tables so that a broken foreign key or a mis-resolved symbol is caught rather than
--     assumed. Used by the "order is persisted correctly" step in the API and UI suites.
SELECT
    o.order_reference,
    o.side,
    o.order_type,
    o.quantity,
    o.status,
    i.symbol,
    a.account_number,
    a.currency,
    u.username
FROM orders o
    INNER JOIN instruments i ON i.id = o.instrument_id
    INNER JOIN accounts    a ON a.id = o.account_id
    INNER JOIN users       u ON u.id = a.user_id
WHERE o.order_reference = :orderReference;


-- A2. Verifies a side effect rather than the primary record. A successful login must leave an
--     audit trail; a test that only asserts the 200 response would never notice if audit
--     logging silently broke. Counting is deliberate - we assert exactly one new event.
SELECT COUNT(*) AS event_count
FROM audit_events ae
    INNER JOIN users u ON u.id = ae.user_id
WHERE u.username    = :username
  AND ae.event_type = :eventType
  AND ae.created_at >= :since;


-- A3. Negative verification, which is the half people forget. When the API rejects an order
--     with a 422, no order row may exist. Asserting "0" here is what distinguishes a genuine
--     rejection from a request that was accepted and then hidden by the UI.
SELECT COUNT(*) AS order_count
FROM orders
WHERE order_reference = :orderReference;


-- =====================================================================================
-- GROUP B - DATA-INTEGRITY INVARIANTS  (each must return ZERO rows on healthy data)
-- =====================================================================================

-- B1. Fill reconciliation. An order marked Filled must have fills summing to its quantity.
--     GROUP BY + HAVING is the natural expression of "aggregate does not match expectation".
--     Against the committed seed data this query returns ORD-20240403-0006 - order 6 is
--     Filled but has no fill rows at all. That is a deliberately planted defect, and it is the
--     example used in DEFECT_REPORT.md. A query that finds a real bug is far more convincing
--     in an interview than a query that returns nothing.
SELECT
    o.order_reference,
    o.status,
    o.quantity                          AS ordered_quantity,
    COALESCE(SUM(f.fill_quantity), 0)   AS filled_quantity,
    COUNT(f.id)                         AS fill_count
FROM orders o
    LEFT JOIN order_fills f ON f.order_id = o.id
WHERE o.status = 'Filled'
GROUP BY o.id, o.order_reference, o.status, o.quantity
HAVING COALESCE(SUM(f.fill_quantity), 0) <> o.quantity
ORDER BY o.order_reference;


-- B2. Referential orphans. LEFT JOIN ... WHERE right side IS NULL is the standard idiom, and
--     it catches the class of bug that foreign keys miss when they are not enforced (SQLite
--     only enforces them when PRAGMA foreign_keys is ON, which is itself worth a test).
SELECT
    o.id,
    o.order_reference,
    o.account_id,
    o.instrument_id
FROM orders o
    LEFT JOIN accounts    a ON a.id = o.account_id
    LEFT JOIN instruments i ON i.id = o.instrument_id
WHERE a.id IS NULL
   OR i.id IS NULL
ORDER BY o.id;


-- B3. Duplicate business keys. An order reference is the customer-facing identifier; two orders
--     sharing one is a support incident. The UNIQUE constraint should prevent it, so this query
--     is really testing that the constraint exists and was never dropped by a migration.
SELECT
    order_reference,
    COUNT(*) AS occurrences
FROM orders
GROUP BY order_reference
HAVING COUNT(*) > 1
ORDER BY occurrences DESC;


-- B4. Business-rule invariant expressed in SQL: a Limit order requires a limit price, and a
--     Market order must not have one. Encoding the rule here means it is checked across all
--     historical data, not only on the paths the functional tests happen to exercise.
SELECT
    order_reference,
    order_type,
    limit_price
FROM orders
WHERE (order_type = 'Limit'  AND limit_price IS NULL)
   OR (order_type = 'Market' AND limit_price IS NOT NULL)
ORDER BY order_reference;


-- B5. Boundary invariant. Quantities must sit inside the instrument's tradable range. This is
--     exactly the rule the API validates on the way in; running it over stored data verifies
--     that the rule was always enforced, including by any back-office or import path.
SELECT
    o.order_reference,
    i.symbol,
    o.quantity,
    i.min_quantity,
    i.max_quantity
FROM orders o
    INNER JOIN instruments i ON i.id = o.instrument_id
WHERE o.quantity < i.min_quantity
   OR o.quantity > i.max_quantity
ORDER BY i.symbol, o.order_reference;


-- =====================================================================================
-- GROUP C - EXPLORATORY / RISK PROFILING
-- =====================================================================================

-- C1. Which accounts are heavy users? Heavy usage is one of the inputs to risk-based test
--     prioritisation (see TEST_STRATEGY.md): the busiest paths deserve the most regression
--     coverage. This is the canonical GROUP BY / HAVING / COUNT shape.
SELECT
    a.account_number,
    u.username,
    COUNT(o.id)                 AS order_count,
    COUNT(DISTINCT o.instrument_id) AS distinct_instruments_traded
FROM accounts a
    INNER JOIN users  u ON u.id = a.user_id
    LEFT  JOIN orders o ON o.account_id = a.id
GROUP BY a.id, a.account_number, u.username
HAVING COUNT(o.id) > 5
ORDER BY order_count DESC;


-- C2. Order outcome distribution per instrument. A sudden shift in the Rejected ratio for one
--     symbol is a stronger signal than any individual test failure, which is why this kind of
--     query belongs in a QA toolkit and not only in a BI dashboard.
SELECT
    i.symbol,
    i.asset_class,
    COUNT(o.id)                                                AS total_orders,
    SUM(CASE WHEN o.status = 'Filled'    THEN 1 ELSE 0 END)     AS filled,
    SUM(CASE WHEN o.status = 'Rejected'  THEN 1 ELSE 0 END)     AS rejected,
    SUM(CASE WHEN o.status = 'Cancelled' THEN 1 ELSE 0 END)     AS cancelled,
    SUM(CASE WHEN o.status = 'Pending'   THEN 1 ELSE 0 END)     AS pending
FROM instruments i
    LEFT JOIN orders o ON o.instrument_id = i.id
GROUP BY i.id, i.symbol, i.asset_class
ORDER BY total_orders DESC, i.symbol;


-- C3. Coverage gap finder. Any tradable instrument with no orders is an untested path in the
--     data. Turning "what have we not covered?" into a query is cheap and repeatable.
SELECT
    i.symbol,
    i.asset_class,
    i.is_tradable
FROM instruments i
    LEFT JOIN orders o ON o.instrument_id = i.id
WHERE o.id IS NULL
  AND i.is_tradable = 1
ORDER BY i.symbol;


-- C4. Users who can never log in successfully but hold a funded account - the sort of
--     cross-entity inconsistency that produces support tickets rather than test failures.
SELECT DISTINCT
    u.username,
    u.status,
    a.account_number,
    a.balance
FROM users u
    INNER JOIN accounts a ON a.user_id = u.id
WHERE u.status <> 'Active'
  AND a.balance > 0
ORDER BY a.balance DESC;
