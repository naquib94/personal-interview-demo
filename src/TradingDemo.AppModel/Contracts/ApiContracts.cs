namespace TradingDemo.AppModel.Contracts;

// The API contracts, as the tests understand them.
//
// These are deliberately *duplicates* of the types in TradingDemo.App/Domain/Contracts.cs, and
// the duplication is the point. If the test suite referenced the application's own DTOs, then
// renaming a field would update both sides simultaneously and every test would keep passing -
// while every real client of the API broke.
//
// A test suite must assert against the contract it *expects*, not against whatever the
// application currently emits. This is the same reason a consumer-driven contract test defines
// its own expectations rather than importing the provider's model.
//
// The cost is that a legitimate contract change requires editing two files. That cost is the
// mechanism: it forces the change to be noticed and consciously accepted.

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

public sealed record OrderResponse(
    string OrderReference,
    string Symbol,
    string Side,
    string OrderType,
    decimal Quantity,
    decimal? LimitPrice,
    string Status,
    DateTime CreatedAtUtc);

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount, int TotalPages);

/// <summary>
/// The API's single error envelope.
/// </summary>
/// <remarks>
/// One shape for every non-success response is a property worth testing for in its own right.
/// An API that returns a different error structure per endpoint forces every client - and every
/// test - to special-case, and the special cases are where handling gets forgotten.
/// </remarks>
public sealed record ValidationProblem(string Message, IReadOnlyList<ValidationDetail> Errors)
{
    /// <summary>
    /// The reason for a specific field, or null if that field is not mentioned.
    /// </summary>
    /// <remarks>
    /// Returning null rather than throwing lets a test assert on the absence of an error for a
    /// field, which is how you verify that fixing one input does not start reporting problems
    /// with another.
    /// </remarks>
    public string? ReasonFor(string field) => Errors
        .FirstOrDefault(e => string.Equals(e.Field, field, StringComparison.OrdinalIgnoreCase))
        ?.Reason;

    public IReadOnlyList<string> Fields => Errors.Select(e => e.Field).ToList();
}

public sealed record ValidationDetail(string Field, string Reason);

// Request payloads. Modelled with nullable members so that a test can send a deliberately
// incomplete request - which is exactly what a validation test needs to do, and what a model
// with non-nullable required members makes impossible to express.

public sealed record LoginRequest(string? Username, string? Password);

public sealed record PlaceOrderRequest(
    string? Symbol,
    string? Side,
    string? OrderType,
    decimal? Quantity,
    decimal? LimitPrice);
