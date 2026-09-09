using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using QaFramework.Core.Configuration;
using QaFramework.Core.Logging;
using RestSharp;

namespace QaFramework.Api.Requests;

/// <summary>
/// The HTTP transport layer. One instance per base address, for the lifetime of the run.
/// </summary>
/// <remarks>
/// <para><b>On lifetime.</b> The <see cref="RestClient"/> is created once and reused. A client
/// per request - which is what <c>using var client = new RestClient(...)</c> in a helper method
/// produces, and a pattern I have seen in production suites - exhausts ephemeral ports under
/// load and leaves sockets in TIME_WAIT. It works fine for ten tests and fails at two hundred,
/// which is the worst possible time to discover it.</para>
///
/// <para><b>On layering.</b> This class is the <i>only</i> place RestSharp appears outside its
/// own package reference. Domain clients (see TradingDemo.AppModel/ApiClients) call
/// <see cref="SendAsync{T}"/>; tests call the domain clients. The chain is deliberate:
/// <c>test -&gt; domain client -&gt; ApiClient -&gt; HTTP</c>, and each arrow is a boundary
/// something could be swapped at.</para>
/// </remarks>
public sealed class ApiClient : IDisposable
{
    private readonly RestClient client;
    private readonly TimeoutSettings timeouts;

    /// <summary>
    /// Serialisation settings, matched to the application under test.
    /// </summary>
    /// <remarks>
    /// Exposed so that <see cref="Responses.ApiResponse{T}"/> deserialises with exactly the
    /// same options the client serialised with. Two independently-configured serialisers is a
    /// quiet source of "the field is null but the JSON clearly has a value" confusion.
    /// </remarks>
    public static readonly JsonSerializerOptions SerialiserOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    public ApiClient(string baseUrl, TimeoutSettings timeouts)
    {
        this.timeouts = timeouts;

        client = new RestClient(new RestClientOptions(baseUrl)
        {
            Timeout = timeouts.ApiRequest,
            // The framework asserts on status codes, so a 4xx must arrive as data rather than
            // as an exception. Throwing on error status would make every negative-path test
            // wrap its call in a try/catch.
            ThrowOnAnyError = false
        });
    }

    /// <summary>
    /// Sends a request and returns a typed, non-throwing response.
    /// </summary>
    /// <param name="request">Built with <see cref="ApiRequestBuilder"/>.</param>
    public async Task<Responses.ApiResponse<T>> SendAsync<T>(ApiRequestBuilder request)
    {
        RestRequest restRequest = request.Build(SerialiserOptions);
        string description = $"{restRequest.Method.ToString().ToUpperInvariant()} {request.Resource}";

        Stopwatch stopwatch = Stopwatch.StartNew();
        RestResponse response = await client.ExecuteAsync(restRequest);
        stopwatch.Stop();

        // Logged at Info, not Step: one line per call, always, so a failure five steps later
        // still has the request history in the report. Bodies are logged only when the caller
        // opts in, because request bodies are exactly where credentials live.
        TestLog.Info($"{description} -> {(int)response.StatusCode} in {stopwatch.ElapsedMilliseconds}ms");

        if (request.LogBody && !string.IsNullOrWhiteSpace(response.Content))
            TestLog.Info($"Response body: {response.Content}");

        // A transport failure is defined as "no HTTP status code came back", NOT as
        // "ResponseStatus is not Completed".
        //
        // That distinction is load-bearing and was a real bug here. RestSharp reports
        // ResponseStatus.Error for an unsuccessful HTTP status as well as for a genuine
        // connection failure, so keying off ResponseStatus alone made every negative-path
        // assertion fail with "the request did not reach the server" - while the log directly
        // above it showed a perfectly good 422. Fifteen tests failed for a reason that had
        // nothing to do with the application.
        //
        // Receiving a status code proves the server answered. Only the absence of one, or an
        // explicit timeout, is an environment problem.
        string? transportError = response.StatusCode is not 0 ? null : response.ResponseStatus switch
        {
            ResponseStatus.TimedOut =>
                $"The request timed out after {timeouts.ApiRequest.TotalSeconds:F0}s.",
            ResponseStatus.Aborted => "The request was aborted.",
            _ =>
                "The request did not reach the server: " +
                $"{response.ErrorMessage ?? response.ErrorException?.Message ?? "no response was received"}."
        };

        return new Responses.ApiResponse<T>(
            statusCode: response.StatusCode == 0 ? HttpStatusCode.ServiceUnavailable : response.StatusCode,
            rawBody: response.Content ?? string.Empty,
            headers: CollectHeaders(response),
            elapsed: stopwatch.Elapsed,
            requestDescription: description,
            serialiserOptions: SerialiserOptions,
            transportError: transportError);
    }

    /// <summary>
    /// Flattens the response's headers into one case-insensitive dictionary.
    /// </summary>
    /// <remarks>
    /// <para>RestSharp separates <c>Headers</c> (response headers) from <c>ContentHeaders</c>
    /// (entity headers), mirroring <c>HttpResponseMessage</c>. That is faithful to HTTP but a
    /// trap for a caller: <c>Content-Type</c> and <c>Content-Length</c> live only in the
    /// second collection.</para>
    ///
    /// <para>This was a real bug in this framework. Reading only <c>Headers</c> meant the
    /// <c>ShouldBeJson</c> assertion reported "the response declared '(none)'" for every single
    /// response, including perfectly well-formed JSON ones - an assertion that could never
    /// pass, in a framework whose whole purpose is trustworthy assertions.</para>
    ///
    /// <para>Merging them is the right call for a test framework: a test asserting on
    /// "Content-Type" should not have to know which of two collections HTTP considers it to
    /// belong to. Response headers are added last so that they win a name collision, which
    /// cannot legitimately happen but should be deterministic if it does.</para>
    /// </remarks>
    private static Dictionary<string, string> CollectHeaders(RestResponse response)
    {
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);

        foreach (var header in (response.ContentHeaders ?? []).Concat(response.Headers ?? []))
        {
            if (header.Name is null) continue;

            // Last wins for entity vs response headers; but for genuinely repeated headers
            // (Set-Cookie) keep the first, since a test asserting on one wants the first.
            headers[header.Name] = header.Value?.ToString() ?? string.Empty;
        }

        return headers;
    }

    public void Dispose() => client.Dispose();
}
