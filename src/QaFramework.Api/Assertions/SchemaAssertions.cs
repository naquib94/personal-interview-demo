using System.Text.Json;
using Json.Schema;
using QaFramework.Api.Responses;
using QaFramework.Core.Logging;

namespace QaFramework.Api.Assertions;

/// <summary>
/// JSON Schema validation of response bodies.
/// </summary>
/// <remarks>
/// <para><b>Why this is worth having.</b> Field-by-field assertions verify the values a test
/// happens to care about. They do not notice that <c>quantity</c> changed from a number to a
/// string, that a required field disappeared, or that a new null crept into a field the mobile
/// client dereferences. Those are contract regressions, and they are the ones that break
/// consumers rather than tests.</para>
///
/// <para>Schema validation catches a whole class of defect with one assertion, and it does so
/// on <i>every</i> response rather than only the fields under test. It is the cheapest form of
/// contract testing available without introducing a broker such as Pact.</para>
///
/// <para><b>What it is not.</b> Schema validation says nothing about whether the values are
/// <i>correct</i>. An order with the wrong quantity passes schema validation cleanly. The two
/// kinds of assertion are complements, not alternatives, and using one as an excuse to skip the
/// other is a mistake I would push back on in review.</para>
/// </remarks>
public static class SchemaAssertions
{
    /// <summary>
    /// Validates the response body against a JSON Schema.
    /// </summary>
    /// <param name="schemaJson">
    /// The schema as a string. Kept as source rather than loaded from disk so the contract
    /// sits next to the tests that depend on it and moves with them in code review.
    /// </param>
    public static ApiResponse<T> ShouldMatchSchema<T>(this ApiResponse<T> response, string schemaJson)
    {
        if (string.IsNullOrWhiteSpace(response.RawBody))
            throw TestLog.Failure(
                $"{response.RequestDescription}: cannot validate against a schema because the " +
                "response body was empty.");

        JsonSchema schema;
        try
        {
            schema = JsonSchema.FromText(schemaJson);
        }
        catch (JsonException ex)
        {
            // Distinguished from a validation failure on purpose: a broken schema is a defect
            // in the test, and reporting it as a product failure wastes someone's afternoon.
            throw TestLog.Failure(
                "The JSON Schema itself is not valid JSON, so the response could not be " +
                $"validated. This is a fault in the test, not the application. {ex.Message}");
        }

        // JsonSchema.Net 9 evaluates a JsonElement rather than a JsonNode - it reads the
        // original buffer through spans instead of allocating a node per value. The document
        // must therefore stay alive for the duration of the evaluation, hence the using.
        using JsonDocument document = ParseOrFail(response);

        // OutputFormat.Hierarchical is required, and this is worth explaining because leaving
        // it out produced a genuinely misleading bug.
        //
        // JsonSchema.Net's default output format is Flag: it reports only a boolean and
        // populates neither Errors nor Details. DescribeFailures below then walked an empty
        // tree and fell through to its "no specific errors, probably a malformed schema"
        // fallback - so a perfectly valid schema catching a real contract regression reported
        // that the schema itself was broken, and named no field at all.
        //
        // Hierarchical output carries the instance location per violation, which is what turns
        // "the response does not match its schema" into "at /items/1/quantity: value is a
        // string but should be a number". Naming the field is the entire value of the
        // assertion; without it this method detects regressions but helps nobody fix them.
        //
        // Found by a unit test of the framework itself (Framework.Tests/Api). It would not have
        // been found by any test of the application, because the assertion still went red - it
        // just went red unhelpfully.
        EvaluationResults results = schema.Evaluate(document.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.Hierarchical
        });

        if (results.IsValid)
        {
            TestLog.Step($"{response.RequestDescription} matches its response schema.");
            return response;
        }

        throw TestLog.Failure(
            $"{response.RequestDescription}: the response does not match its expected schema.",
            $"Schema violations:{Environment.NewLine}{DescribeFailures(results)}" +
            $"{Environment.NewLine}Raw body:{Environment.NewLine}{response.RawBody}");
    }

    /// <summary>
    /// Flattens the evaluation tree into readable lines.
    /// </summary>
    /// <remarks>
    /// The library's own tree is thorough and unreadable in a CI log. Reducing it to
    /// "at /items/2/quantity: value is not a number" per violation is what makes the assertion
    /// usable by someone who did not write it.
    /// </remarks>
    private static string DescribeFailures(EvaluationResults results)
    {
        List<string> lines = [];
        Collect(results, lines);

        return lines.Count == 0
            ? "(the schema reported the response as invalid but produced no specific errors; " +
              "this usually means a malformed schema)"
            : string.Join(Environment.NewLine, lines.Distinct().Select(l => $"  - {l}"));

        // Version 9 removed HasErrors; a null Errors dictionary is now the signal.
        static void Collect(EvaluationResults node, List<string> lines)
        {
            if (!node.IsValid && node.Errors is { Count: > 0 } errors)
            {
                string location = node.InstanceLocation.ToString() is { Length: > 0 } path
                    ? path
                    : "(root)";

                foreach (KeyValuePair<string, string> error in errors)
                    lines.Add($"at {location}: {error.Value}");
            }

            // Details is nullable on a leaf node, so a null check is required rather than
            // relying on it being an empty list.
            foreach (EvaluationResults child in node.Details ?? [])
                Collect(child, lines);
        }
    }

    private static JsonDocument ParseOrFail<T>(ApiResponse<T> response)
    {
        try
        {
            return JsonDocument.Parse(response.RawBody);
        }
        catch (JsonException ex)
        {
            throw TestLog.Failure(
                $"{response.RequestDescription}: the response body is not valid JSON. {ex.Message}",
                $"Raw body:{Environment.NewLine}{response.RawBody}");
        }
    }
}
