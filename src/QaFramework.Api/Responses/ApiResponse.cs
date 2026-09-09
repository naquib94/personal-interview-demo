using System.Net;
using System.Text.Json;

namespace QaFramework.Api.Responses;

/// <summary>
/// The result of an HTTP call, in the shape a test wants to talk about.
/// </summary>
/// <remarks>
/// This type exists so that no test ever touches <c>RestResponse</c>. That is the difference
/// between an API suite that can change HTTP library and one that cannot: the reference
/// frameworks pass the library's own response object into every test, so RestSharp appears in
/// hundreds of files and replacing it becomes a project rather than a change.
/// <para>
/// Deserialisation is lazy and, critically, <b>non-throwing</b>. A test asserting on a 400
/// response must still be able to read the status code and the raw body even though the payload
/// will not deserialise into the success type. Eager deserialisation in the constructor makes
/// every negative-path test fight the framework.
/// </para>
/// </remarks>
public sealed class ApiResponse<T>
{
    private readonly JsonSerializerOptions serialiserOptions;
    private readonly Lazy<T?> data;

    internal ApiResponse(
        HttpStatusCode statusCode,
        string rawBody,
        IReadOnlyDictionary<string, string> headers,
        TimeSpan elapsed,
        string requestDescription,
        JsonSerializerOptions serialiserOptions,
        string? transportError = null)
    {
        StatusCode = statusCode;
        RawBody = rawBody;
        Headers = headers;
        Elapsed = elapsed;
        RequestDescription = requestDescription;
        TransportError = transportError;
        this.serialiserOptions = serialiserOptions;

        data = new Lazy<T?>(Deserialise);
    }

    public HttpStatusCode StatusCode { get; }

    public int StatusCodeValue => (int)StatusCode;

    public string RawBody { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>Wall-clock duration of the call, for response-time assertions.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>e.g. "POST /api/orders". Used in every failure message.</summary>
    public string RequestDescription { get; }

    /// <summary>
    /// Set when the request never reached the server (DNS, connection refused, timeout).
    /// Distinguished from an HTTP error response because the two require completely different
    /// responses from a human: one is an environment problem, the other is a finding.
    /// </summary>
    public string? TransportError { get; }

    public bool IsSuccess => StatusCodeValue is >= 200 and < 300;

    /// <summary>The deserialised payload, or null when the body is empty or does not match.</summary>
    public T? Data => data.Value;

    /// <summary>
    /// The payload, failing with the raw body when it cannot be deserialised.
    /// </summary>
    /// <remarks>
    /// Including the raw body in the failure is the whole value of this method. A bare
    /// "object reference not set" tells the reader nothing; the actual response usually
    /// explains the problem immediately - a 502 HTML error page, or a field that changed type.
    /// </remarks>
    public T RequireData()
    {
        if (Data is not null) return Data;

        throw Core.Logging.TestLog.Failure(
            $"{RequestDescription} returned {StatusCodeValue} but the body could not be " +
            $"deserialised into {typeof(T).Name}.",
            $"Raw body:{Environment.NewLine}{(string.IsNullOrWhiteSpace(RawBody) ? "(empty)" : RawBody)}");
    }

    /// <summary>
    /// Reads the body as a different type - normally the error envelope on a failure path.
    /// </summary>
    public TOther? As<TOther>()
    {
        if (string.IsNullOrWhiteSpace(RawBody)) return default;
        try { return JsonSerializer.Deserialize<TOther>(RawBody, serialiserOptions); }
        catch (JsonException) { return default; }
    }

    /// <summary>A compact one-line summary for logs and failure messages.</summary>
    public override string ToString() =>
        $"{RequestDescription} -> {StatusCodeValue} in {Elapsed.TotalMilliseconds:F0}ms";

    private T? Deserialise()
    {
        if (string.IsNullOrWhiteSpace(RawBody)) return default;
        try { return JsonSerializer.Deserialize<T>(RawBody, serialiserOptions); }
        // Swallowed deliberately and narrowly: a body that does not match T is an expected
        // situation on error paths. RequireData() is the method that turns it into a failure,
        // at the point where the caller has said the payload is required.
        catch (JsonException) { return default; }
    }
}
