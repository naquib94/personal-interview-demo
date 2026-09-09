namespace TradingDemo.App.Domain;

// Request and response contracts for the demo API.
//
// These live in the application, not the test framework. The test framework deliberately
// declares its *own* copies (TradingDemo.AppModel/Contracts) rather than referencing these:
// if the tests share the application's DTOs, a breaking contract change silently updates both
// sides and the test cannot detect it. Duplication is the point.

public sealed record LoginRequest(string? Username, string? Password);

public sealed record LoginResponse(string Token, int UserId, string Username, DateTime ExpiresAtUtc);

public sealed record AccountResponse(
    int Id,
    string AccountNumber,
    string Currency,
    decimal Balance,
    string AccountType,
    string Username);

public sealed record InstrumentResponse(
    string Symbol,
    string DisplayName,
    string AssetClass,
    decimal Bid,
    decimal Ask,
    decimal Spread,
    decimal MinQuantity,
    decimal MaxQuantity,
    bool IsTradable);

public sealed record PlaceOrderRequest(
    string? Symbol,
    string? Side,
    string? OrderType,
    decimal? Quantity,
    decimal? LimitPrice);

public sealed record OrderResponse(
    string OrderReference,
    string Symbol,
    string Side,
    string OrderType,
    decimal Quantity,
    decimal? LimitPrice,
    string Status,
    DateTime CreatedAtUtc);

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

/// <summary>
/// A single, consistent error envelope for every non-success response.
/// One predictable shape means the test framework needs one deserialiser and one assertion
/// helper, instead of a special case per endpoint.
/// </summary>
public sealed record ValidationProblem(string Message, IReadOnlyList<ValidationDetail> Errors)
{
    public static ValidationProblem Single(string message, string field, string reason) =>
        new(message, [new ValidationDetail(field, reason)]);

    public static ValidationProblem ForMessage(string message) => new(message, []);
}

public sealed record ValidationDetail(string Field, string Reason);
