using System.Text.Json;
using RestSharp;

namespace QaFramework.Api.Requests;

/// <summary>
/// Fluent builder for an HTTP request.
/// </summary>
/// <remarks>
/// <para><b>Why null-tolerant.</b> Every <c>With...</c> method ignores a null value and
/// returns <c>this</c>. That single decision removes a large amount of branching from domain
/// clients: an endpoint with six optional filters becomes six unconditional chained calls
/// instead of six <c>if (x is not null)</c> blocks. It also means a test can express "search
/// with no filters" by passing nulls, which is the case most likely to be skipped otherwise.</para>
///
/// <para><b>Why bodies are not logged by default.</b> <see cref="LogBody"/> defaults to false
/// and must be opted into. Login requests contain passwords, and a suite that logs every body
/// writes credentials into CI output that is retained for months and visible to anyone with
/// read access to the pipeline. Opt-in is the safe default; a domain client turns it on for the
/// endpoints where it is useful and harmless.</para>
/// </remarks>
public sealed class ApiRequestBuilder(Method method, string resource)
{
    private readonly List<(string Key, string Value)> queryParameters = [];
    private readonly List<(string Key, string Value)> pathParameters = [];
    private readonly List<(string Key, string Value)> headers = [];
    private object? body;

    public string Resource { get; } = resource;

    public bool LogBody { get; private set; }

    public static ApiRequestBuilder Get(string resource) => new(Method.Get, resource);
    public static ApiRequestBuilder Post(string resource) => new(Method.Post, resource);
    public static ApiRequestBuilder Put(string resource) => new(Method.Put, resource);
    public static ApiRequestBuilder Delete(string resource) => new(Method.Delete, resource);

    /// <summary>Adds a query parameter, ignoring it when the value is null.</summary>
    public ApiRequestBuilder WithQuery(string key, object? value)
    {
        if (value is not null) queryParameters.Add((key, Stringify(value)));
        return this;
    }

    /// <summary>Substitutes a <c>{placeholder}</c> segment in the resource template.</summary>
    public ApiRequestBuilder WithPath(string key, object value)
    {
        pathParameters.Add((key, Stringify(value)));
        return this;
    }

    public ApiRequestBuilder WithHeader(string key, string? value)
    {
        if (value is not null) headers.Add((key, value));
        return this;
    }

    /// <summary>
    /// Adds a bearer token.
    /// </summary>
    /// <remarks>
    /// Takes the token rather than a username and password on purpose. Authentication is the
    /// responsibility of a domain client that knows how this product issues tokens; the
    /// transport layer only knows how to attach one. Mixing the two produces a builder that has
    /// to be changed for every new auth scheme.
    /// </remarks>
    public ApiRequestBuilder WithBearerToken(string? token) =>
        WithHeader("Authorization", token is null ? null : $"Bearer {token}");

    public ApiRequestBuilder WithJsonBody(object? payload)
    {
        body = payload;
        return this;
    }

    /// <summary>
    /// Opts this request's response body into the log. Never enable it for anything carrying
    /// a credential.
    /// </summary>
    public ApiRequestBuilder WithBodyLogging()
    {
        LogBody = true;
        return this;
    }

    internal RestRequest Build(JsonSerializerOptions serialiserOptions)
    {
        RestRequest request = new(Resource, method);

        foreach ((string key, string value) in pathParameters)
            request.AddParameter(key, value, ParameterType.UrlSegment);

        foreach ((string key, string value) in queryParameters)
            request.AddQueryParameter(key, value);

        foreach ((string key, string value) in headers)
            request.AddHeader(key, value);

        if (body is not null)
        {
            // Serialised here with the framework's own options rather than left to RestSharp's
            // default serialiser, so that request and response use one identical contract.
            // A mismatch here produces PascalCase field names the API silently ignores, and a
            // 400 that is very hard to explain.
            request.AddStringBody(JsonSerializer.Serialize(body, serialiserOptions), DataFormat.Json);
        }

        return request;
    }

    private static string Stringify(object value) => value switch
    {
        // Booleans must be lower-cased for most APIs; .NET renders them as "True".
        bool flag => flag ? "true" : "false",
        // Invariant formatting: a decimal must not become "1,5" because the agent's locale
        // differs from the developer's. This is a genuine cross-machine failure.
        decimal d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("O"),
        _ => value.ToString() ?? string.Empty
    };
}
