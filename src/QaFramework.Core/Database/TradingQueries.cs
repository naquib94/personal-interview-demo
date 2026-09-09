namespace QaFramework.Core.Database;

/// <summary>
/// The SQL the automated suite executes, in one place.
/// </summary>
/// <remarks>
/// Named constants rather than inline strings in step definitions. The reason is maintenance:
/// when a column is renamed, there is one file to change and a compiler-visible list of
/// everything affected. Inline SQL spreads the same query across a dozen step files, each
/// slightly different, and one of them is always missed.
/// <para>
/// These mirror the queries documented in <c>database/validation/qa-validation-queries.sql</c>.
/// That file is the explained, readable version for a human; this one is the executable version
/// for the suite.
/// </para>
/// </remarks>
public static class TradingQueries
{
    /// <summary>
    /// Post-operation verification: the order was persisted, joined to the right instrument,
    /// account and user.
    /// </summary>
    public const string OrderByReference =
        """
        SELECT o.order_reference AS OrderReference,
               o.side            AS Side,
               o.order_type      AS OrderType,
               o.quantity        AS Quantity,
               o.limit_price     AS LimitPrice,
               o.status          AS Status,
               i.symbol          AS Symbol,
               a.account_number  AS AccountNumber,
               a.currency        AS Currency,
               u.username        AS Username
        FROM orders o
            INNER JOIN instruments i ON i.id = o.instrument_id
            INNER JOIN accounts    a ON a.id = o.account_id
            INNER JOIN users       u ON u.id = a.user_id
        WHERE o.order_reference = @orderReference
        """;

    /// <summary>Negative verification: a rejected order must leave no row behind.</summary>
    public const string CountOrdersByReference =
        "SELECT COUNT(*) FROM orders WHERE order_reference = @orderReference";

    /// <summary>Side-effect verification: an action wrote the expected audit event.</summary>
    public const string CountAuditEventsForUser =
        """
        SELECT COUNT(*)
        FROM audit_events ae
            INNER JOIN users u ON u.id = ae.user_id
        WHERE u.username    = @username
          AND ae.event_type = @eventType
        """;

    /// <summary>
    /// Fill reconciliation invariant. Any row returned is a defect: an order marked Filled
    /// whose fills do not sum to its quantity.
    /// </summary>
    public const string OrdersWithMismatchedFills =
        """
        SELECT o.order_reference                    AS OrderReference,
               o.status                             AS Status,
               o.quantity                           AS OrderedQuantity,
               COALESCE(SUM(f.fill_quantity), 0)    AS FilledQuantity,
               COUNT(f.id)                          AS FillCount
        FROM orders o
            LEFT JOIN order_fills f ON f.order_id = o.id
        WHERE o.status = 'Filled'
        GROUP BY o.id, o.order_reference, o.status, o.quantity
        HAVING COALESCE(SUM(f.fill_quantity), 0) <> o.quantity
        ORDER BY o.order_reference
        """;

    /// <summary>Referential integrity invariant. Any row returned is an orphaned order.</summary>
    public const string OrphanedOrders =
        """
        SELECT o.id              AS Id,
               o.order_reference AS OrderReference
        FROM orders o
            LEFT JOIN accounts    a ON a.id = o.account_id
            LEFT JOIN instruments i ON i.id = o.instrument_id
        WHERE a.id IS NULL OR i.id IS NULL
        """;

    /// <summary>Uniqueness invariant. Any row returned is a duplicated customer-facing reference.</summary>
    public const string DuplicateOrderReferences =
        """
        SELECT order_reference AS OrderReference,
               COUNT(*)        AS Occurrences
        FROM orders
        GROUP BY order_reference
        HAVING COUNT(*) > 1
        """;

    /// <summary>
    /// Business-rule invariant. A Limit order needs a limit price; a Market order must not have
    /// one. Checked across all stored data, not only the paths the functional tests exercise.
    /// </summary>
    public const string OrdersWithInconsistentLimitPrice =
        """
        SELECT order_reference AS OrderReference,
               order_type      AS OrderType,
               limit_price     AS LimitPrice
        FROM orders
        WHERE (order_type = 'Limit'  AND limit_price IS NULL)
           OR (order_type = 'Market' AND limit_price IS NOT NULL)
        """;

    /// <summary>Boundary invariant: stored quantities must respect the instrument's limits.</summary>
    public const string OrdersOutsideInstrumentQuantityRange =
        """
        SELECT o.order_reference AS OrderReference,
               i.symbol          AS Symbol,
               o.quantity        AS Quantity,
               i.min_quantity    AS MinQuantity,
               i.max_quantity    AS MaxQuantity
        FROM orders o
            INNER JOIN instruments i ON i.id = o.instrument_id
        WHERE o.quantity < i.min_quantity
           OR o.quantity > i.max_quantity
        """;

    /// <summary>
    /// Risk profiling: order volume and instrument diversity per account. Feeds the
    /// prioritisation reasoning in TEST_STRATEGY.md.
    /// </summary>
    public const string AccountActivitySummary =
        """
        SELECT a.account_number                 AS AccountNumber,
               u.username                       AS Username,
               COUNT(o.id)                      AS OrderCount,
               COUNT(DISTINCT o.instrument_id)  AS DistinctInstrumentsTraded
        FROM accounts a
            INNER JOIN users  u ON u.id = a.user_id
            LEFT  JOIN orders o ON o.account_id = a.id
        GROUP BY a.id, a.account_number, u.username
        HAVING COUNT(o.id) >= @minimumOrders
        ORDER BY OrderCount DESC
        """;
}

// Row shapes for the queries above. Kept alongside the SQL so a change to one is an obvious
// prompt to change the other.

public sealed class PersistedOrder
{
    public string OrderReference { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public string OrderType { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal? LimitPrice { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;

    public override string ToString() =>
        $"{OrderReference} {Side} {Quantity} {Symbol} [{Status}] on {AccountNumber} ({Username})";
}

public sealed class FillMismatch
{
    public string OrderReference { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal OrderedQuantity { get; set; }
    public decimal FilledQuantity { get; set; }
    public int FillCount { get; set; }

    public override string ToString() =>
        $"{OrderReference}: status {Status}, ordered {OrderedQuantity}, " +
        $"filled {FilledQuantity} across {FillCount} fill(s)";
}

public sealed class AccountActivity
{
    public string AccountNumber { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public int OrderCount { get; set; }
    public int DistinctInstrumentsTraded { get; set; }

    public override string ToString() =>
        $"{AccountNumber} ({Username}): {OrderCount} order(s) across " +
        $"{DistinctInstrumentsTraded} instrument(s)";
}
