using System.Globalization;
using System.Text.Json;
using QaFramework.Api.Requests;
using RestSharp;

namespace Framework.Tests.Api;

/// <summary>
/// Tests for the request the builder assembles.
/// </summary>
/// <remarks>
/// These assert against the <see cref="RestRequest"/> produced by the internal
/// <c>Build</c> method, which tests/Framework.Tests can reach through an
/// <c>InternalsVisibleTo</c> entry in QaFramework.Api.csproj - the reasoning is documented
/// there. The alternative was to drive every case over a real socket, which would exercise
/// RestSharp rather than the null-dropping and formatting decisions under test.
/// </remarks>
[TestFixture]
public sealed class ApiRequestBuilderTests
{
    private static readonly JsonSerializerOptions SerialiserOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Test]
    public void WithQuery_WithANullValue_DropsTheParameterEntirely()
    {
        // The decision that removes a pile of branching from domain clients: an endpoint with
        // six optional filters becomes six unconditional chained calls. The bug this prevents is
        // the alternative - a literal "?status=" or "?status=null" reaching the API, which most
        // servers interpret as a filter for the empty string and quietly return nothing.
        RestRequest request = ApiRequestBuilder.Get("/api/orders")
            .WithQuery("status", "Filled")
            .WithQuery("side", null)
            .WithQuery("symbol", null)
            .Build(SerialiserOptions);

        QueryParameters(request).Should().Equal(("status", "Filled"));
    }

    [Test]
    public void WithHeader_WithANullValue_DropsTheHeader()
    {
        // Same reasoning, and it matters more here: an "Authorization: " header with no value is
        // rejected by some gateways with a 400 rather than the 401 the test was written to
        // assert, which sends the reader looking for a request-shape problem.
        RestRequest request = ApiRequestBuilder.Get("/api/orders")
            .WithHeader("X-Correlation-Id", "abc-123")
            .WithHeader("X-Trace-Id", null)
            .Build(SerialiserOptions);

        Headers(request).Should().Equal(("X-Correlation-Id", "abc-123"));
    }

    [Test]
    public void WithBearerToken_WithNoToken_SendsNoAuthorizationHeader()
    {
        // "Call this endpoint unauthenticated" is a case worth being able to express in one
        // call, because it is the case most likely to be skipped otherwise. A builder that
        // attached "Bearer " would make the unauthenticated test assert against a malformed
        // request instead of an absent credential.
        RestRequest request = ApiRequestBuilder.Get("/api/account")
            .WithBearerToken(null)
            .Build(SerialiserOptions);

        Headers(request).Should().BeEmpty();
    }

    [Test]
    public void WithBearerToken_WithAToken_UsesTheBearerScheme()
    {
        RestRequest request = ApiRequestBuilder.Get("/api/account")
            .WithBearerToken("a-token")
            .Build(SerialiserOptions);

        Headers(request).Should().Equal(("Authorization", "Bearer a-token"));
    }

    [TestCase(true, "true")]
    [TestCase(false, "false")]
    public void WithQuery_RendersBooleansInLowerCase(bool value, string expected)
    {
        // .NET renders a bool as "True". Most APIs, and every JSON-shaped query string, expect
        // "true" - and the ones that do not simply ignore the parameter, which turns
        // "?includeCancelled=True" into a filter that silently did not apply.
        RestRequest request = ApiRequestBuilder.Get("/api/orders")
            .WithQuery("includeCancelled", value)
            .Build(SerialiserOptions);

        QueryParameters(request).Should().Equal(("includeCancelled", expected));
    }

    [Test]
    [NonParallelizable]
    public void WithQuery_RendersNumbersInvariantlyEvenUnderACommaDecimalCulture()
    {
        // A genuine cross-machine bug class, and one that is invisible on any machine configured
        // in English. Under de-DE the default ToString() renders 1234.56 as "1234,56"; the comma
        // is a parameter separator in some query-string conventions and a plain parse failure in
        // the rest, so the request either filters on the wrong value or is rejected outright.
        //
        // NonParallelizable because CultureInfo.CurrentCulture is ambient state, and restored in
        // a finally so a failure here cannot leave the rest of the run in a German locale.
        CultureInfo originalCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            // Confirms the culture actually took effect. Without this the test would still pass
            // on a machine where setting the culture silently failed, and would then be
            // asserting nothing at all.
            1234.56d.ToString(CultureInfo.CurrentCulture).Should().Be("1234,56");

            RestRequest request = ApiRequestBuilder.Get("/api/orders")
                .WithQuery("minQuantity", 1234.56m)
                .WithQuery("maxQuantity", 9876.54d)
                .Build(SerialiserOptions);

            QueryParameters(request).Should().Equal(
                ("minQuantity", "1234.56"),
                ("maxQuantity", "9876.54"));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Test]
    public void WithQuery_RendersDatesAsRoundTrippableUtc()
    {
        // The "O" format is unambiguous about offset and precision. A culture-dependent date
        // format is the same bug class as the numbers above, with the added charm that
        // "03/04/2025" is valid in two locales and means two different days.
        RestRequest request = ApiRequestBuilder.Get("/api/orders")
            .WithQuery("placedAfter", new DateTime(2025, 3, 4, 13, 45, 0, DateTimeKind.Utc))
            .Build(SerialiserOptions);

        QueryParameters(request).Should().Equal(("placedAfter", "2025-03-04T13:45:00.0000000Z"));
    }

    [Test]
    public void WithPath_SubstitutesTheResourceTemplate()
    {
        // Kept distinct from a query parameter, and from string interpolation into the resource:
        // an id containing a slash or a space has to be encoded as a segment, and doing it by
        // hand is how a test ends up requesting a URL that does not exist.
        RestRequest request = ApiRequestBuilder.Get("/api/orders/{reference}")
            .WithPath("reference", "QA-0001")
            .Build(SerialiserOptions);

        Named(request, ParameterType.UrlSegment).Should().Equal(("reference", "QA-0001"));
    }

    [Test]
    public void LogBody_DefaultsToFalse()
    {
        // Opt-in, not opt-out, and the default is the whole point. Login requests contain
        // passwords, and a suite that logs every body writes credentials into CI output that is
        // retained for months and readable by anyone with pipeline access. A default that
        // flipped to true would be a security regression with no visible symptom.
        ApiRequestBuilder.Post("/api/authentication/token").LogBody.Should().BeFalse();
    }

    [Test]
    public void WithBodyLogging_IsWhatTurnsBodyLoggingOn()
    {
        ApiRequestBuilder.Post("/api/orders").WithBodyLogging().LogBody.Should().BeTrue();
    }

    [Test]
    public void WithJsonBody_SerialisesWithTheSuppliedOptions()
    {
        // Serialised with the framework's own options rather than left to RestSharp's default
        // serialiser, so request and response share one contract. A mismatch produces PascalCase
        // field names that the API ignores, and a 400 that is genuinely hard to explain because
        // the body looks correct to a human reading it.
        RestRequest request = ApiRequestBuilder.Post("/api/orders")
            .WithJsonBody(new { OrderReference = "QA-0001", QuantityRequested = 12.5m })
            .Build(SerialiserOptions);

        string body = request.Parameters
            .Single(parameter => parameter.Type == ParameterType.RequestBody)
            .Value?.ToString() ?? string.Empty;

        body.Should().Contain("\"orderReference\":\"QA-0001\"");
        body.Should().Contain("\"quantityRequested\":12.5");
    }

    [Test]
    public void Build_WithNoBody_SendsNoBody()
    {
        // A GET with an empty JSON body is rejected by some servers and, worse, accepted with
        // different semantics by others.
        RestRequest request = ApiRequestBuilder.Get("/api/orders").Build(SerialiserOptions);

        request.Parameters.Should().NotContain(parameter => parameter.Type == ParameterType.RequestBody);
    }

    [TestCase(Method.Get)]
    [TestCase(Method.Post)]
    [TestCase(Method.Put)]
    [TestCase(Method.Delete)]
    public void TheFactoryMethods_SetTheMatchingHttpMethod(Method expected)
    {
        // Four one-line factories are exactly where a copy/paste leaves Delete constructing a
        // Put. The consequence - a test that reports "deletion returned 200" while deleting
        // nothing - is a false pass, so the redundancy is worth it.
        ApiRequestBuilder builder = expected switch
        {
            Method.Get => ApiRequestBuilder.Get("/api/orders"),
            Method.Post => ApiRequestBuilder.Post("/api/orders"),
            Method.Put => ApiRequestBuilder.Put("/api/orders"),
            _ => ApiRequestBuilder.Delete("/api/orders")
        };

        RestRequest request = builder.Build(SerialiserOptions);

        request.Method.Should().Be(expected);
        request.Resource.Should().Be("/api/orders");
    }

    private static IReadOnlyList<(string Name, string Value)> QueryParameters(RestRequest request) =>
        Named(request, ParameterType.QueryString);

    private static IReadOnlyList<(string Name, string Value)> Headers(RestRequest request) =>
        Named(request, ParameterType.HttpHeader);

    private static IReadOnlyList<(string Name, string Value)> Named(RestRequest request, ParameterType type) =>
        request.Parameters
            .Where(parameter => parameter.Type == type)
            .Select(parameter => (parameter.Name ?? string.Empty, parameter.Value?.ToString() ?? string.Empty))
            .ToList();
}
