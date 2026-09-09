namespace TradingDemo.AppModel.ApiClients;

/// <summary>
/// Every route the suite calls, as constants.
/// </summary>
/// <remarks>
/// Centralised so that a route change is one edit with a compiler-verified list of call sites.
/// Inline route strings spread the same path across a dozen step files with subtle differences
/// - a leading slash here, a trailing one there - and one of them is always missed.
/// <para>
/// <c>{placeholder}</c> segments are substituted by <c>ApiRequestBuilder.WithPath</c>, which
/// URL-encodes the value. Building a path with string interpolation instead breaks on an
/// identifier containing a slash or a space, which generated test data eventually produces.
/// </para>
/// </remarks>
public static class Endpoints
{
    public const string Login = "/api/auth/login";
    public const string MyAccount = "/api/accounts/me";
    public const string Instruments = "/api/instruments";
    public const string InstrumentBySymbol = "/api/instruments/{symbol}";
    public const string Orders = "/api/orders";
    public const string OrderByReference = "/api/orders/{reference}";

    /// <summary>Liveness probe, used when waiting for the application to start.</summary>
    public const string Health = "/health";

    /// <summary>
    /// Restores the seeded database. A documented test hook, not a product endpoint - see the
    /// comment in TradingDemo.App/Program.cs for the trade-off.
    /// </summary>
    public const string ResetDatabase = "/test-support/reset";
}
