using System.Text.Json;
using QaFramework.Api.Requests;
using QaFramework.Api.Responses;
using QaFramework.Core.Logging;
using TradingDemo.AppModel.Contracts;

namespace Api.Tests.Support;

/// <summary>
/// Reads status and body from an <see cref="ApiResponse{T}"/> without knowing its type argument.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> Several assertions are genuinely generic - "the request was
/// refused as unauthorised", "the error mentions field X" - and apply to every endpoint. But
/// <c>ApiResponse&lt;T&gt;</c> closes over a different <c>T</c> in every scenario, so a single
/// shared step definition cannot name the type it is asserting on.</para>
///
/// <para><b>Why reflection, and what was rejected.</b> Three alternatives were considered:</para>
/// <list type="number">
/// <item>Adding a non-generic interface to <c>ApiResponse&lt;T&gt;</c>. Cleaner at the call
/// site, but it would exist purely to serve a test-suite convenience, and shaping framework
/// code around one consumer's convenience is how frameworks acquire dead abstractions.</item>
/// <item>Repeating each assertion in every step class with the right closed type. That is what
/// produced the duplicate-binding failure this class fixes, and it duplicates the assertion
/// logic four ways.</item>
/// <item>Scoping the duplicated bindings per feature with <c>[Scope]</c>. Legitimate, and
/// common in the wild, but it hides four copies of one assertion behind a scoping attribute
/// rather than removing the duplication.</item>
/// </list>
///
/// <para>Reflection confined to one small, well-named class with explicit failure messages is
/// the better trade. The failure messages matter: if <c>ApiResponse&lt;T&gt;</c> is ever
/// renamed, this fails with an instruction naming the file to update, rather than with a
/// <c>NullReferenceException</c> inside a step definition.</para>
/// </remarks>
public static class ResponseInspector
{
    public static int StatusCode(object response) =>
        (int)(Read(response, nameof(ApiResponse<object>.StatusCodeValue))
            ?? throw TestLog.Failure("The recorded response reported a null status code."));

    public static string RawBody(object response) =>
        Read(response, nameof(ApiResponse<object>.RawBody)) as string ?? string.Empty;

    public static string Describe(object response) => response.ToString() ?? response.GetType().Name;

    /// <summary>
    /// Deserialises the body into the API's standard error envelope.
    /// </summary>
    /// <remarks>
    /// Fails rather than returning null when the body is not an error envelope. A single
    /// predictable error shape is itself a requirement of this API, so a body that does not
    /// match one is a finding, not a reason for a test to skip its assertion.
    /// </remarks>
    public static ValidationProblem RequireProblem(object response)
    {
        string body = RawBody(response);

        ValidationProblem? problem = null;
        try
        {
            problem = JsonSerializer.Deserialize<ValidationProblem>(body, ApiClient.SerialiserOptions);
        }
        catch (JsonException)
        {
            // Handled by the null check below, which produces a far more useful message than a
            // raw JsonException about a character position.
        }

        return problem ?? throw TestLog.Failure(
            "Expected the API's standard error envelope but the response body could not be " +
            "deserialised into one. Every non-success response is required to use the same " +
            "shape, so a body that does not is a defect rather than a test problem.",
            $"Call: {Describe(response)}{Environment.NewLine}" +
            $"Raw body:{Environment.NewLine}{(string.IsNullOrWhiteSpace(body) ? "(empty)" : body)}");
    }

    private static object? Read(object response, string propertyName)
    {
        System.Reflection.PropertyInfo property = response.GetType().GetProperty(propertyName)
            ?? throw TestLog.Failure(
                $"Expected the recorded response ({response.GetType().Name}) to expose a " +
                $"'{propertyName}' property. Shared step definitions read it reflectively " +
                "because the response type is generic. If ApiResponse<T> has been renamed or " +
                $"reshaped, update {nameof(ResponseInspector)}.");

        return property.GetValue(response);
    }
}
