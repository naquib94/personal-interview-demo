using System.Net;
using Api.Tests.Support;
using QaFramework.Core.Logging;
using TradingDemo.AppModel.Contracts;
using TradingDemo.AppModel.Setup;

namespace Api.Tests.StepDefinitions;

/// <summary>
/// Steps that apply to every feature: signing in, and the generic outcome assertions.
/// </summary>
/// <remarks>
/// <para>Every step here is defined <b>exactly once for the whole suite</b>. Reqnroll matches
/// step text globally across all binding assemblies, so the same sentence declared in two
/// classes is an ambiguous-binding error at runtime rather than a compile error - which is how
/// it is normally discovered.</para>
///
/// <para>That constraint is a feature rather than an annoyance: it forces a single definition
/// of what "the request is refused as unauthorised" means, so the assertion cannot quietly
/// differ between features.</para>
/// </remarks>
[Binding]
public sealed class CommonSteps(ScenarioSession session)
{
    // -------------------------------------------------------------------------------------
    // Given - authentication
    // -------------------------------------------------------------------------------------

    [Given("the active trader is signed in")]
    public Task GivenTheActiveTraderIsSignedIn() => session.SignInAsync("ActiveTrader");

    [Given("the user without an account is signed in")]
    public Task GivenTheUserWithoutAnAccountIsSignedIn() => session.SignInAsync("UserWithoutAccount");

    // -------------------------------------------------------------------------------------
    // Then - HTTP outcomes
    // -------------------------------------------------------------------------------------

    [Then("the request is refused as unauthorised")]
    public void ThenRefusedAsUnauthorised() => AssertStatus(HttpStatusCode.Unauthorized);

    [Then("the request is refused as not found")]
    public void ThenRefusedAsNotFound() => AssertStatus(HttpStatusCode.NotFound);

    // -------------------------------------------------------------------------------------
    // Then - the error envelope
    // -------------------------------------------------------------------------------------

    [Then("the validation error mentions the field {string}")]
    public void ThenTheValidationErrorMentionsField(string field)
    {
        ValidationProblem problem = ResponseInspector.RequireProblem(RequireResponse());

        problem.ReasonFor(field).Should().NotBeNull(
            "'{0}' should have been reported as a problem, but the response mentioned: {1}",
            field, problem.Fields.Count == 0 ? "no fields at all" : string.Join(", ", problem.Fields));
    }

    /// <summary>
    /// Asserts the complete set of reported fields.
    /// </summary>
    /// <remarks>
    /// Deliberately an exact set rather than a containment check. The requirement is that
    /// <i>every</i> invalid field is reported in one response; a containment check would still
    /// pass if the API regressed to reporting one field at a time, which is the exact
    /// regression this scenario exists to catch.
    /// </remarks>
    [Then("the validation errors mention the fields {string}")]
    public void ThenTheValidationErrorsMentionFields(string commaSeparatedFields)
    {
        string[] expected = commaSeparatedFields.Split(',', StringSplitOptions.TrimEntries);
        ValidationProblem problem = ResponseInspector.RequireProblem(RequireResponse());

        // Order-insensitive: the API is not required to report fields in a fixed sequence, and
        // asserting on order would fail for a change that harms nobody.
        problem.Fields.Should().BeEquivalentTo(expected,
            "every invalid field should be reported in a single response rather than one at a time");
    }

    // -------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------

    private object RequireResponse() => session.LastResponse
        ?? throw TestLog.Failure(
            "This step asserts on the previous step's response, but no When step recorded one. " +
            "Check that the scenario's When step assigns to ScenarioSession.LastResponse.");

    private void AssertStatus(HttpStatusCode expected)
    {
        object response = RequireResponse();

        ResponseInspector.StatusCode(response).Should().Be((int)expected,
            "the call should have been refused with {0} {1}. Actual call: {2}. Body: {3}",
            (int)expected, expected,
            ResponseInspector.Describe(response),
            ResponseInspector.RawBody(response));
    }
}
