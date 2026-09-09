using System.Globalization;

namespace TradingDemo.App.Domain;

// Persistence row models.
//
// These are deliberately separate from the API contracts in Contracts.cs, and they are
// mutable classes rather than records, for one concrete reason: SQLite has four storage
// classes, so Dapper sees every INTEGER column as long and every REAL column as double.
// Record materialisation needs a constructor whose parameter types match the reader exactly,
// which would force the domain to model an account balance as a double.
//
// Property-based materialisation lets Dapper widen/narrow on the way in, so the row model can
// use int and decimal - and decimal is the correct type for money. The mapping methods below
// are the single place where storage types become domain types.

public sealed class UserRow
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

public sealed class AccountRow
{
    public int Id { get; set; }
    public string AccountNumber { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public decimal Balance { get; set; }
    public string AccountType { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;

    public AccountResponse ToResponse() =>
        new(Id, AccountNumber, Currency, Balance, AccountType, Username);
}

public sealed class InstrumentRow
{
    public int Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string AssetClass { get; set; } = string.Empty;
    public decimal Bid { get; set; }
    public decimal Ask { get; set; }
    public decimal MinQuantity { get; set; }
    public decimal MaxQuantity { get; set; }
    public bool IsTradable { get; set; }

    public InstrumentResponse ToResponse() =>
        new(Symbol, DisplayName, AssetClass, Bid, Ask, Ask - Bid, MinQuantity, MaxQuantity, IsTradable);
}

public sealed class OrderRow
{
    public string OrderReference { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public string OrderType { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal? LimitPrice { get; set; }
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Read as text, not DateTime. SQLite has no date type, so the column holds
    /// 'yyyy-MM-dd HH:mm:ss' written by datetime('now') - which is UTC. Parsing it explicitly
    /// as UTC here prevents the value silently acquiring the agent's local offset, a classic
    /// source of tests that pass in one timezone and fail in another.
    /// </summary>
    public string CreatedAt { get; set; } = string.Empty;

    public OrderResponse ToResponse() =>
        new(OrderReference, Symbol, Side, OrderType, Quantity, LimitPrice, Status, ParseUtc(CreatedAt));

    private static DateTime ParseUtc(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed)
            ? parsed
            : DateTime.UnixEpoch;
}
