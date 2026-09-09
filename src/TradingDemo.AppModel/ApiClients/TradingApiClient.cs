using System.Net;
using QaFramework.Api.Requests;
using QaFramework.Api.Responses;
using QaFramework.Core.Configuration;
using QaFramework.Core.Logging;
using QaFramework.Core.Synchronisation;
using TradingDemo.AppModel.Contracts;

namespace TradingDemo.AppModel.ApiClients;

/// <summary>
/// Accounts, instruments and orders.
/// </summary>
/// <remarks>
/// The token is passed per call rather than held as state on the client. That makes it trivial
/// to write the authorisation tests that matter - "user A cannot read user B's order", "an
/// expired token is rejected" - which a client that silently reuses one cached session cannot
/// express at all. It is a small amount of extra typing at the call site in exchange for a
/// whole category of security test becoming possible.
/// </remarks>
public sealed class TradingApiClient(ApiClient client, TimeoutSettings timeouts)
{
    // ----------------------------------------------------------------------------------------
    // Account
    // ----------------------------------------------------------------------------------------

    public Task<ApiResponse<AccountResponse>> GetMyAccountAsync(string? token) =>
        client.SendAsync<AccountResponse>(
            ApiRequestBuilder.Get(Endpoints.MyAccount).WithBearerToken(token));

    // ----------------------------------------------------------------------------------------
    // Instruments
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Lists instruments, optionally filtered.
    /// </summary>
    /// <remarks>
    /// <paramref name="assetClass"/> is nullable and the builder drops null query parameters,
    /// so "no filter" and "filter by FX" are the same call. That is what makes the unfiltered
    /// case as easy to test as the filtered one.
    /// </remarks>
    public Task<ApiResponse<List<InstrumentResponse>>> GetInstrumentsAsync(string? assetClass = null) =>
        client.SendAsync<List<InstrumentResponse>>(
            ApiRequestBuilder.Get(Endpoints.Instruments).WithQuery("assetClass", assetClass));

    public Task<ApiResponse<InstrumentResponse>> GetInstrumentAsync(string symbol) =>
        client.SendAsync<InstrumentResponse>(
            ApiRequestBuilder.Get(Endpoints.InstrumentBySymbol).WithPath("symbol", symbol));

    /// <summary>
    /// Fetches an instrument for use as test data, failing loudly if it is unavailable.
    /// </summary>
    /// <remarks>
    /// Used by scenarios that need a valid quantity within an instrument's range. Reading the
    /// bounds from the API rather than hardcoding them means a change to the reference data
    /// does not silently turn a boundary test into a mid-range test - which is the quiet way a
    /// boundary suite stops testing boundaries.
    /// </remarks>
    public async Task<InstrumentResponse> GetInstrumentOrFailAsync(string symbol)
    {
        ApiResponse<InstrumentResponse> response = await GetInstrumentAsync(symbol);

        if (response.StatusCode != HttpStatusCode.OK)
            throw TestLog.Failure(
                $"Test setup needed instrument '{symbol}' but the API returned " +
                $"{response.StatusCodeValue}. Check that the seed data still contains it.",
                response.RawBody);

        return response.RequireData();
    }

    // ----------------------------------------------------------------------------------------
    // Orders
    // ----------------------------------------------------------------------------------------

    public Task<ApiResponse<OrderResponse>> PlaceOrderAsync(string? token, PlaceOrderRequest request) =>
        client.SendAsync<OrderResponse>(
            ApiRequestBuilder.Post(Endpoints.Orders)
                .WithBearerToken(token)
                .WithJsonBody(request)
                // Safe to log: an order payload contains no credentials, and having the exact
                // request in the report is what makes a validation failure diagnosable.
                .WithBodyLogging());

    /// <summary>
    /// Places an order for test setup, failing if it is not accepted.
    /// </summary>
    /// <remarks>
    /// This is the API-first seeding path. A UI scenario that needs an existing order in the
    /// history calls this rather than driving the order form, which turns thirty seconds of
    /// browser interaction into one HTTP call. The saving compounds: it also means a defect in
    /// the order form cannot fail the order-history test, so the failure points at the right
    /// screen.
    /// </remarks>
    public async Task<OrderResponse> PlaceOrderOrFailAsync(string? token, PlaceOrderRequest request)
    {
        ApiResponse<OrderResponse> response = await PlaceOrderAsync(token, request);

        if (response.StatusCode != HttpStatusCode.Created)
            throw TestLog.Failure(
                $"Test setup could not place an order: the API returned " +
                $"{response.StatusCodeValue} {response.StatusCode}.",
                $"Request:{Environment.NewLine}{TestLog.Describe(request)}{Environment.NewLine}" +
                $"Response:{Environment.NewLine}{response.RawBody}");

        return response.RequireData();
    }

    public Task<ApiResponse<PagedResponse<OrderResponse>>> GetOrderHistoryAsync(
        string? token, string? status = null, int? page = null, int? pageSize = null) =>
        client.SendAsync<PagedResponse<OrderResponse>>(
            ApiRequestBuilder.Get(Endpoints.Orders)
                .WithBearerToken(token)
                .WithQuery("status", status)
                .WithQuery("page", page)
                .WithQuery("pageSize", pageSize));

    public Task<ApiResponse<OrderResponse>> GetOrderAsync(string? token, string reference) =>
        client.SendAsync<OrderResponse>(
            ApiRequestBuilder.Get(Endpoints.OrderByReference)
                .WithBearerToken(token)
                .WithPath("reference", reference));

    /// <summary>
    /// Waits until an order reaches an expected status.
    /// </summary>
    /// <remarks>
    /// <para>The demo API fills market orders synchronously, so this is not strictly needed
    /// against it. It is here because <i>real</i> trading systems are asynchronous - an order is
    /// accepted, routed and filled over some period - and this is the correct shape for that
    /// problem. Demonstrating the pattern on a synchronous system is honest as long as the
    /// reason is stated, which is what this comment is for.</para>
    ///
    /// <para>The <c>isTerminal</c> argument is the detail that makes it good rather than merely
    /// present: an order that reaches <c>Rejected</c> or <c>Cancelled</c> will never become
    /// <c>Filled</c>, so the wait abandons immediately and reports the status it actually
    /// reached. Without it, the test spends its full timeout budget and then reports
    /// "timed out" - hiding the far more useful fact that the order was rejected.</para>
    /// </remarks>
    public Task<OrderResponse> WaitForOrderStatusAsync(string? token, string reference, string expectedStatus) =>
        Wait.ForValueAsync(
            probe: async () => (await GetOrderAsync(token, reference)).RequireData(),
            predicate: order => string.Equals(order.Status, expectedStatus, StringComparison.OrdinalIgnoreCase),
            description: $"order '{reference}' to reach status '{expectedStatus}'",
            timeout: timeouts.EventualConsistency,
            pollInterval: timeouts.PollInterval,
            isTerminal: order =>
                !string.Equals(order.Status, expectedStatus, StringComparison.OrdinalIgnoreCase) &&
                order.Status is "Rejected" or "Cancelled");

    // ----------------------------------------------------------------------------------------
    // Test support
    // ----------------------------------------------------------------------------------------

    /// <summary>Restores the seeded database, for scenarios that require pristine data.</summary>
    public Task<ApiResponse<object>> ResetDatabaseAsync() =>
        client.SendAsync<object>(ApiRequestBuilder.Post(Endpoints.ResetDatabase));
}
