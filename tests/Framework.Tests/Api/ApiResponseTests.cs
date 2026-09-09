using System.Net;
using QaFramework.Api.Requests;
using QaFramework.Api.Responses;
using QaFramework.Core.Logging;

namespace Framework.Tests.Api;

/// <summary>
/// Tests for the typed response wrapper.
/// </summary>
/// <remarks>
/// The response is constructed directly - its constructor is internal, and this assembly is
/// granted visibility from QaFramework.Api.csproj, where the reasoning is recorded. The
/// behaviour under test is deserialisation policy, not transport, so putting a socket in the
/// middle of it would only add a way for the test to fail for unrelated reasons.
/// <para>
/// <see cref="ApiClient.SerialiserOptions"/> is used rather than a locally-defined set, because
/// half of what these tests assert is that a response deserialises with exactly the options the
/// client serialised with. A private copy here would keep passing after the real options changed.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ApiResponseTests
{
    private sealed record Order(string Reference, string Status, decimal Quantity);

    private sealed record ProblemDetails(string Title, int Status);

    [Test]
    public void Data_WithAMatchingBody_Deserialises()
    {
        ApiResponse<Order> response = Response<Order>(
            HttpStatusCode.OK,
            """{ "reference": "QA-0001", "status": "Filled", "quantity": 12.5 }""");

        response.Data.Should().Be(new Order("QA-0001", "Filled", 12.5m));
    }

    [Test]
    public void Deserialisation_IsLazyAndNonThrowingOnAMismatchedBody()
    {
        // The decision every negative-path test depends on. A test asserting on a 400 must still
        // be able to read the status code and the raw body, even though the payload will never
        // deserialise into the success type. Eager deserialisation in the constructor would make
        // the framework throw before the test got to make its assertion - so the test would have
        // to wrap the call in a try/catch, and the suite would fight itself on every error path.
        ApiResponse<Order> response = Response<Order>(
            HttpStatusCode.BadRequest,
            """{ "title": "Insufficient funds", "status": 400 }""");

        using (new AssertionScope())
        {
            response.StatusCodeValue.Should().Be(400);
            response.RawBody.Should().Contain("Insufficient funds");
            response.Data.Should().BeNull();
        }
    }

    [Test]
    public void Deserialisation_OfAStructurallyInvalidBody_DoesNotThrow()
    {
        // A 502 from a load balancer returns an HTML error page. Reading Data must report "no
        // payload" rather than surfacing a JsonException from inside the framework, which reads
        // as a framework defect and hides the actual 502.
        ApiResponse<Order> response = Response<Order>(
            HttpStatusCode.BadGateway,
            "<html><body>502 Bad Gateway</body></html>");

        Func<Order?> read = () => response.Data;

        read.Should().NotThrow().Which.Should().BeNull();
    }

    [Test]
    public void Data_WithAnEmptyBody_IsNull()
    {
        // 204 No Content is a success. Treating an empty body as a deserialisation failure would
        // make every successful delete look broken.
        Response<Order>(HttpStatusCode.NoContent, string.Empty).Data.Should().BeNull();
    }

    [Test]
    public void Data_IsOnlyDeserialisedOnce()
    {
        // Lazy, but memoised. A response read in five assertions must not pay for five
        // deserialisations, and - more importantly - must not be able to return two different
        // object instances to two assertions in the same test.
        ApiResponse<Order> response = Response<Order>(
            HttpStatusCode.OK,
            """{ "reference": "QA-0001", "status": "Filled", "quantity": 12.5 }""");

        response.Data.Should().BeSameAs(response.Data);
    }

    [Test]
    public void RequireData_WhenTheBodyDoesNotMatch_IncludesTheRawBodyInTheFailure()
    {
        // The entire value of this method over a null check. A bare "object reference not set"
        // tells the reader nothing; the actual response nearly always explains the problem on
        // sight - a 502 HTML page, or a field that changed type in the last deployment.
        ApiResponse<Order> response = Response<Order>(
            HttpStatusCode.BadRequest,
            """{ "title": "Insufficient funds", "status": 400 }""",
            requestDescription: "POST /api/orders");

        Action require = () => response.RequireData();

        require.Should().Throw<AutomationFailureException>()
            .WithMessage("*POST /api/orders returned 400*")
            .WithMessage("*could not be deserialised into Order*")
            .WithMessage("*Insufficient funds*");
    }

    [Test]
    public void RequireData_WithAnEmptyBody_SaysTheBodyWasEmpty()
    {
        // "Raw body:" followed by nothing looks like a truncated log. Saying "(empty)"
        // distinguishes "the server sent nothing" from "the log lost the body", which are very
        // different investigations.
        ApiResponse<Order> response = Response<Order>(HttpStatusCode.OK, "   ");

        Action require = () => response.RequireData();

        require.Should().Throw<AutomationFailureException>().WithMessage("*(empty)*");
    }

    [Test]
    public void RequireData_WithAMatchingBody_ReturnsThePayload()
    {
        ApiResponse<Order> response = Response<Order>(
            HttpStatusCode.Created,
            """{ "reference": "QA-0001", "status": "Pending", "quantity": 3 }""");

        response.RequireData().Reference.Should().Be("QA-0001");
    }

    [Test]
    public void As_ReadsTheBodyAsTheErrorEnvelope()
    {
        // The error path needs a second view of the same bytes: the success type is wrong, but
        // the problem-details payload is exactly what the test wants to assert on. Without this,
        // an error-envelope assertion has to parse RawBody by hand in every test.
        ApiResponse<Order> response = Response<Order>(
            HttpStatusCode.BadRequest,
            """{ "title": "Insufficient funds", "status": 400 }""");

        response.As<ProblemDetails>().Should().Be(new ProblemDetails("Insufficient funds", 400));
    }

    [Test]
    public void As_WithABodyThatDoesNotMatch_ReturnsTheDefaultRatherThanThrowing()
    {
        // A test asserting "the error envelope is absent" is legitimate - some gateways return a
        // bare status with an HTML body - and it must not have to be written as a caught
        // exception.
        ApiResponse<Order> response = Response<Order>(HttpStatusCode.BadGateway, "<html>502</html>");

        response.As<ProblemDetails>().Should().BeNull();
    }

    [Test]
    public void As_WithAnEmptyBody_ReturnsTheDefault()
    {
        Response<Order>(HttpStatusCode.NoContent, string.Empty).As<ProblemDetails>().Should().BeNull();
    }

    [TestCase(199, false)]
    [TestCase(200, true)]
    [TestCase(201, true)]
    [TestCase(299, true)]
    [TestCase(300, false)]
    [TestCase(404, false)]
    [TestCase(500, false)]
    public void IsSuccess_IsTrueOnlyForTheTwoHundredRange(int statusCode, bool expected)
    {
        // Boundaries asserted individually because an off-by-one here is a false pass or a false
        // failure on every single API test, and "2xx" is written as a range comparison rather
        // than a set - so 199 and 300 are the two values that tell you it was written correctly.
        // 300 in particular is a redirect, not a success, and treating it as one would mask a
        // misconfigured route that answers with a 302 to a login page.
        Response<Order>((HttpStatusCode)statusCode, string.Empty).IsSuccess.Should().Be(expected);
    }

    [Test]
    public void TransportError_IsSeparateFromAnHttpErrorResponse()
    {
        // Two failures that require completely different responses from a human: an unreachable
        // server is an environment problem, a 500 is a finding about the product. Collapsing
        // them is how a suite reports "the application is broken" during a network outage.
        ApiResponse<Order> unreachable = Response<Order>(
            HttpStatusCode.ServiceUnavailable,
            string.Empty,
            transportError: "The request did not reach the server: connection refused.");

        ApiResponse<Order> serverError = Response<Order>(HttpStatusCode.InternalServerError, "{}");

        unreachable.TransportError.Should().Contain("did not reach the server");
        serverError.TransportError.Should().BeNull();
    }

    [Test]
    public void ToString_IsOneLineFitForALog()
    {
        // Used verbatim in log output and failure messages, so it must stay a single line: a
        // multi-line ToString turns a request history into an unreadable block.
        ApiResponse<Order> response = Response<Order>(
            HttpStatusCode.Created,
            "{}",
            requestDescription: "POST /api/orders",
            elapsed: TimeSpan.FromMilliseconds(37));

        response.ToString().Should().Be("POST /api/orders -> 201 in 37ms");
    }

    private static ApiResponse<T> Response<T>(
        HttpStatusCode statusCode,
        string rawBody,
        string requestDescription = "GET /api/orders",
        TimeSpan? elapsed = null,
        string? transportError = null) =>
        new(
            statusCode,
            rawBody,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            elapsed ?? TimeSpan.FromMilliseconds(12),
            requestDescription,
            ApiClient.SerialiserOptions,
            transportError);
}
