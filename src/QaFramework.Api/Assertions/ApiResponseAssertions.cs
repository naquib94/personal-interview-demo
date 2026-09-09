using System.Net;
using QaFramework.Api.Responses;
using QaFramework.Core.Logging;

namespace QaFramework.Api.Assertions;

/// <summary>
/// Assertions on an <see cref="ApiResponse{T}"/>, written so that a failure explains itself.
/// </summary>
/// <remarks>
/// <para>Extension methods returning the response, so assertions chain:</para>
/// <code>
/// response.ShouldHaveStatus(HttpStatusCode.Created)
///         .ShouldRespondWithin(TimeSpan.FromSeconds(2))
///         .ShouldMatchSchema(Schemas.Order);
/// </code>
/// <para><b>The design decision worth defending here</b> is that every failure message
/// includes the request description, the status code and the raw body. A message reading
/// "Expected 201 but got 422" requires the reader to re-run the test to find out why. A message
/// reading "POST /api/orders: expected 201 Created but the API returned 422 UnprocessableEntity"
/// followed by the response body normally makes the cause obvious without running anything.
/// Over a suite's lifetime that difference dominates the cost of maintaining it.</para>
/// </remarks>
public static class ApiResponseAssertions
{
    public static ApiResponse<T> ShouldHaveStatus<T>(
        this ApiResponse<T> response, HttpStatusCode expected)
    {
        response.GuardTransport();

        if (response.StatusCode != expected)
            throw TestLog.Failure(
                $"{response.RequestDescription}: expected {(int)expected} {expected} but the API " +
                $"returned {response.StatusCodeValue} {response.StatusCode}.",
                FormatBody(response));

        TestLog.Step($"{response.RequestDescription} returned the expected {(int)expected} {expected}.");
        return response;
    }

    /// <summary>Asserts any 2xx, for cases where the exact success code is not the point.</summary>
    public static ApiResponse<T> ShouldSucceed<T>(this ApiResponse<T> response)
    {
        response.GuardTransport();

        if (!response.IsSuccess)
            throw TestLog.Failure(
                $"{response.RequestDescription}: expected a successful (2xx) response but the API " +
                $"returned {response.StatusCodeValue} {response.StatusCode}.",
                FormatBody(response));

        return response;
    }

    /// <summary>
    /// Asserts a response-time budget.
    /// </summary>
    /// <remarks>
    /// Used sparingly and only where slowness is a functional defect - an order submission that
    /// takes eight seconds has failed regardless of its status code. A blanket response-time
    /// assertion on every call turns a shared CI agent's noisy neighbour into a red suite, which
    /// trains the team to ignore failures. Load and latency profiling belong in a performance
    /// tool, not in a functional assertion.
    /// </remarks>
    public static ApiResponse<T> ShouldRespondWithin<T>(this ApiResponse<T> response, TimeSpan budget)
    {
        if (response.Elapsed > budget)
            throw TestLog.Failure(
                $"{response.RequestDescription}: responded in " +
                $"{response.Elapsed.TotalMilliseconds:F0}ms, exceeding the " +
                $"{budget.TotalMilliseconds:F0}ms budget for this operation.",
                FormatBody(response));

        return response;
    }

    /// <summary>Asserts the response is JSON, which is easy to assume and occasionally false.</summary>
    public static ApiResponse<T> ShouldBeJson<T>(this ApiResponse<T> response)
    {
        string contentType = response.Headers.TryGetValue("Content-Type", out string? value)
            ? value
            : "(none)";

        if (!contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            throw TestLog.Failure(
                $"{response.RequestDescription}: expected a JSON content type but the response " +
                $"declared '{contentType}'. A reverse proxy returning an HTML error page is the " +
                "usual cause, and it produces confusing deserialisation failures downstream.",
                FormatBody(response));

        return response;
    }

    /// <summary>Asserts the body is empty - the correct assertion for 204 No Content.</summary>
    public static ApiResponse<T> ShouldHaveEmptyBody<T>(this ApiResponse<T> response)
    {
        if (!string.IsNullOrWhiteSpace(response.RawBody))
            throw TestLog.Failure(
                $"{response.RequestDescription}: expected an empty body but the response contained " +
                $"{response.RawBody.Length} character(s).",
                FormatBody(response));

        return response;
    }

    /// <summary>
    /// Turns a transport failure into a message that names the real problem.
    /// </summary>
    /// <remarks>
    /// Called at the start of every status assertion. Without it, a connection-refused error
    /// surfaces as "expected 200 but got 503" and the reader spends time looking for a product
    /// defect. Naming the environment failure explicitly is the single highest-value
    /// diagnostic in this file.
    /// </remarks>
    private static void GuardTransport<T>(this ApiResponse<T> response)
    {
        if (response.TransportError is null) return;

        throw TestLog.Failure(
            $"{response.RequestDescription} could not be completed, so no assertion about the " +
            $"application's behaviour is possible. {response.TransportError} This is an " +
            "environment or connectivity problem rather than a product defect - check that the " +
            "application under test is running and reachable at the configured base URL.");
    }

    private static string FormatBody<T>(ApiResponse<T> response) =>
        string.IsNullOrWhiteSpace(response.RawBody)
            ? "Response body: (empty)"
            : $"Response body:{Environment.NewLine}{response.RawBody}";
}
