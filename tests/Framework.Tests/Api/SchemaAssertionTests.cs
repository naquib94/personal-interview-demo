using System.Net;
using QaFramework.Api.Assertions;
using QaFramework.Api.Requests;
using QaFramework.Api.Responses;
using QaFramework.Core.Logging;

namespace Framework.Tests.Api;

/// <summary>
/// Tests for schema validation of response bodies.
/// </summary>
/// <remarks>
/// Schema validation is the cheapest contract testing available without a broker, and it is the
/// assertion most likely to rot unnoticed: a validator that reported everything as valid would
/// keep every API test green while silently ceasing to check anything at all. The negative cases
/// below are what make the positive one mean something.
/// </remarks>
[TestFixture]
public sealed class SchemaAssertionTests
{
    private const string OrderSchema =
        """
        {
          "type": "object",
          "required": ["reference", "items"],
          "properties": {
            "reference": { "type": "string" },
            "items": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["quantity"],
                "properties": { "quantity": { "type": "number" } }
              }
            }
          }
        }
        """;

    [Test]
    public void ShouldMatchSchema_WithAConformingBody_PassesAndReturnsTheResponseForChaining()
    {
        // Returning the response is what lets an assertion sit in the middle of a chain rather
        // than forcing a statement of its own. Worth asserting because a method that returned a
        // fresh object would break every chained call downstream of it.
        ApiResponse<object> response = Response(
            """{ "reference": "QA-0001", "items": [{ "quantity": 12.5 }] }""");

        ApiResponse<object> returned = response.ShouldMatchSchema(OrderSchema);

        returned.Should().BeSameAs(response);
    }

    [Test]
    public void ShouldMatchSchema_WhenAFieldChangesType_Fails()
    {
        // The regression this assertion exists for: quantity changing from a number to a string.
        // Field-by-field assertions on other properties would not notice, and every consumer
        // that parses it as a number breaks in production.
        ApiResponse<object> response = Response(
            """
            { "reference": "QA-0001", "items": [{ "quantity": 12.5 }, { "quantity": "3.0" }] }
            """);

        Action assert = () => response.ShouldMatchSchema(OrderSchema);

        assert.Should().Throw<AutomationFailureException>()
            .WithMessage("*GET /api/orders/QA-0001*")
            .WithMessage("*does not match its expected schema*");
    }

    [Test]
    public void ShouldMatchSchema_WhenARequiredFieldIsMissing_Fails()
    {
        // A field disappearing is the other half of the contract-regression pair, and it is the
        // one a test suite is least likely to catch by accident: an assertion on the fields a
        // test does care about passes perfectly while the field a mobile client dereferences
        // has gone.
        ApiResponse<object> response = Response("""{ "items": [] }""");

        Action assert = () => response.ShouldMatchSchema(OrderSchema);

        assert.Should().Throw<AutomationFailureException>()
            .WithMessage("*does not match its expected schema*");
    }

    [Test]
    public void ShouldMatchSchema_WhenItFails_NamesTheOffendingJsonPointer()
    {
        // ------------------------------------------------------------------------------------
        // This is the most valuable test in the file, and it exists because of a bug it found.
        //
        // SchemaAssertions.DescribeFailures walks the evaluation tree to build
        // "at /items/1/quantity: value is a string but should be a number" - which is the entire
        // diagnostic point of schema validation. The framework originally called
        // schema.Evaluate with default options, and JsonSchema.Net 9's default OutputFormat is
        // Flag: it reports only IsValid and populates neither Details nor Errors. So the walk
        // always found nothing and the assertion fell through to its "produced no specific
        // errors; this usually means a malformed schema" branch.
        //
        // The result was a failure that said the response was wrong without saying which field,
        // against payloads with dozens of fields, while blaming a schema that was perfectly
        // valid. The assertion still went red on a real contract regression - it just went red
        // unhelpfully, which is why no test of the application would ever have found it.
        //
        // Fixed by requesting OutputFormat.Hierarchical. This test now pins the behaviour that
        // matters: the failure must name the exact location.
        // ------------------------------------------------------------------------------------
        ApiResponse<object> response = Response(
            """
            { "reference": "QA-0001", "items": [{ "quantity": 12.5 }, { "quantity": "3.0" }] }
            """);

        Action assert = () => response.ShouldMatchSchema(OrderSchema);

        string message = assert.Should().Throw<AutomationFailureException>().Which.Message;

        message.Should().Contain("/items/1",
            "the instance location is what makes a schema violation actionable - without it the " +
            "reader has to diff the payload against the schema by hand");

        message.Should().NotContain("produced no specific errors",
            "the fallback branch means the evaluation produced no per-location detail, which is " +
            "the exact regression this test guards against");
    }

    [Test]
    public void ShouldMatchSchema_WhenItFails_IncludesTheRawBody()
    {
        // The schema violation says what is wrong; the body says what arrived. Both are needed,
        // because the most common cause of a violation is an entirely different response than
        // the test expected - an error envelope where the payload should be.
        ApiResponse<object> response = Response("""{ "reference": 42, "items": [] }""");

        Action assert = () => response.ShouldMatchSchema(OrderSchema);

        assert.Should().Throw<AutomationFailureException>()
            .WithMessage("*Raw body:*")
            .WithMessage("*\"reference\": 42*");
    }

    [Test]
    public void ShouldMatchSchema_WithAnEmptyBody_SaysThereWasNothingToValidate()
    {
        // Distinguished from a violation. An empty body against a schema is not a contract
        // regression, it is a 204 or a dropped response, and reporting it as "does not match the
        // schema" points the reader at the wrong thing.
        ApiResponse<object> response = Response(string.Empty);

        Action assert = () => response.ShouldMatchSchema(OrderSchema);

        assert.Should().Throw<AutomationFailureException>()
            .WithMessage("*cannot validate against a schema because the response body was empty*");
    }

    [Test]
    public void ShouldMatchSchema_WithABodyThatIsNotJson_SaysSoRatherThanBlamingTheSchema()
    {
        // The 502-HTML-error-page case again. "The response body is not valid JSON" plus the
        // body is a two-second diagnosis; a schema violation report against an HTML document is
        // a confusing one.
        ApiResponse<object> response = Response("<html><body>502 Bad Gateway</body></html>");

        Action assert = () => response.ShouldMatchSchema(OrderSchema);

        assert.Should().Throw<AutomationFailureException>()
            .WithMessage("*the response body is not valid JSON*")
            .WithMessage("*502 Bad Gateway*");
    }

    [Test]
    public void ShouldMatchSchema_WithAMalformedSchema_SaysTheFaultIsInTheTest()
    {
        // The distinction that saves somebody an afternoon. A broken schema is a defect in the
        // test, and reporting it in the same shape as a product failure sends an engineer to
        // investigate an application that is behaving perfectly. Asserting on this exact wording
        // is the point of the test - the behaviour is a message, not a return value.
        ApiResponse<object> response = Response(
            """{ "reference": "QA-0001", "items": [] }""");

        Action assert = () => response.ShouldMatchSchema("{ \"type\": \"object\", ");

        assert.Should().Throw<AutomationFailureException>()
            .WithMessage("*The JSON Schema itself is not valid JSON*")
            .WithMessage("*This is a fault in the test, not the application*");
    }

    /// <remarks>
    /// The constructor is internal; visibility is granted to this assembly from
    /// QaFramework.Api.csproj, where the reasoning is recorded.
    /// </remarks>
    private static ApiResponse<object> Response(string rawBody) =>
        new(
            HttpStatusCode.OK,
            rawBody,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            TimeSpan.FromMilliseconds(12),
            "GET /api/orders/QA-0001",
            ApiClient.SerialiserOptions);
}
