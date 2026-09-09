using System.Net;
using QaFramework.Api.Assertions;
using QaFramework.Api.Responses;
using QaFramework.Core.Configuration;
using QaFramework.Core.Database;
using TradingDemo.AppModel.Contracts;
using TradingDemo.AppModel.Setup;

namespace Api.Tests.StepDefinitions;

/// <summary>
/// Steps for the authentication feature.
/// </summary>
/// <remarks>
/// <para>Dependencies arrive by constructor injection from Reqnroll's container. No static
/// state, no service locator, no <c>ScenarioContext["key"]</c> lookups - which is what allows
/// scenarios to run in parallel and what makes a missing dependency a compile error rather
/// than a runtime surprise.</para>
///
/// <para>Steps stay thin: they translate a business sentence into a call on the application
/// model and one assertion. Logic that grows beyond that belongs in the API client or the page
/// object, because logic inside a step definition is invisible to anyone reading the feature
/// file.</para>
/// </remarks>
[Binding]
public sealed class AuthenticationSteps(ScenarioSession session, DatabaseVerifier database)
{
    private ApiResponse<LoginResponse> Response => session.LastResponseAs<ApiResponse<LoginResponse>>();

    // -------------------------------------------------------------------------------------
    // When
    // -------------------------------------------------------------------------------------

    [When("the active trader signs in")]
    public async Task WhenTheActiveTraderSignsIn()
    {
        TestUser user = session.Configuration.User("ActiveTrader");
        session.LastResponse = await session.Auth.LoginAsync(user);
    }

    [When("the suspended trader signs in")]
    public async Task WhenTheSuspendedTraderSignsIn()
    {
        TestUser user = session.Configuration.User("SuspendedTrader");
        session.LastResponse = await session.Auth.LoginAsync(user);
    }

    [When("someone attempts to sign in as the active trader with the password {string}")]
    public async Task WhenSomeoneAttemptsToSignInWithPassword(string password)
    {
        TestUser user = session.Configuration.User("ActiveTrader");
        session.LastResponse = await session.Auth.LoginAsync(user.Username, password);
    }

    [When("someone attempts to sign in as {string} with the password {string}")]
    public async Task WhenSomeoneAttemptsToSignIn(string username, string password) =>
        session.LastResponse = await session.Auth.LoginAsync(username, password);

    /// <summary>
    /// Handles the Scenario Outline covering empty and absent credentials.
    /// </summary>
    /// <remarks>
    /// The literal <c>null</c> in the Examples table is translated to a real null here. Gherkin
    /// has no null, and the alternatives are worse: a separate scenario for the absent case
    /// duplicates the assertions, and treating an empty string as null would stop the suite
    /// distinguishing "sent as empty" from "not sent at all" - which are different requests and
    /// can legitimately behave differently.
    /// </remarks>
    [When("someone attempts to sign in with username {string} and password {string}")]
    public async Task WhenSomeoneAttemptsToSignInWithFields(string username, string password) =>
        session.LastResponse = await session.Auth.LoginAsync(AsNullable(username), AsNullable(password));

    // -------------------------------------------------------------------------------------
    // Then
    // -------------------------------------------------------------------------------------

    [Then("the sign-in succeeds")]
    public void ThenTheSignInSucceeds() => Response.ShouldHaveStatus(HttpStatusCode.OK).ShouldBeJson();

    [Then("a session token is issued")]
    public void ThenASessionTokenIsIssued()
    {
        LoginResponse login = Response.RequireData();

        login.Token.Should().NotBeNullOrWhiteSpace("a session cannot be established without a token");

        // An expiry in the past would be issued successfully and then rejected on the very next
        // call - a defect that a test asserting only "a token was returned" would not notice.
        login.ExpiresAtUtc.Should().BeAfter(DateTime.UtcNow,
            "a token whose expiry has already passed is unusable");
    }

    /// <summary>
    /// Verifies the side effect rather than the response.
    /// </summary>
    /// <remarks>
    /// A test that only asserts the 200 would never notice audit logging breaking, and audit
    /// logging is a compliance requirement in this domain. Verifying it needs the database,
    /// because the API does not expose the audit trail - which is exactly the class of
    /// requirement that makes a database verification layer worth building.
    /// </remarks>
    [Then("the sign-in is recorded in the audit trail")]
    public void ThenTheSignInIsRecorded()
    {
        string username = session.Configuration.User("ActiveTrader").Username;

        long events = database.Count(TradingQueries.CountAuditEventsForUser,
            new { username, eventType = "LoginSucceeded" });

        events.Should().BeGreaterThan(0,
            "a successful sign-in must leave an audit event for '{0}'", username);
    }

    [Then("the sign-in attempt is refused as unauthorised")]
    public void ThenRefusedAsUnauthorised() => Response.ShouldHaveStatus(HttpStatusCode.Unauthorized);

    [Then("the sign-in attempt is refused as forbidden")]
    public void ThenRefusedAsForbidden() => Response.ShouldHaveStatus(HttpStatusCode.Forbidden);

    [Then("the sign-in attempt is rejected as a bad request")]
    public void ThenRejectedAsBadRequest() => Response.ShouldHaveStatus(HttpStatusCode.BadRequest);

    /// <summary>
    /// Asserts the failure message does not disclose whether the username exists.
    /// </summary>
    /// <remarks>
    /// A real security requirement, and one that is easy to regress: a well-meaning developer
    /// improving the error message to "no such user" reintroduces username enumeration. Both
    /// the wrong-password and unknown-username scenarios assert the same message here, which is
    /// what makes the property testable at all - the two responses must be indistinguishable.
    /// </remarks>
    [Then("the failure message does not reveal whether the username exists")]
    public void ThenTheMessageDoesNotRevealUserExistence()
    {
        ValidationProblem? problem = Response.As<ValidationProblem>();

        problem.Should().NotBeNull("the API must return its standard error envelope");
        problem!.Message.Should().Be("Invalid username or password.",
            "the message must be identical for a wrong password and an unknown username, " +
            "otherwise it can be used to enumerate valid usernames");
    }

    [Then("the failure message explains that the account is suspended")]
    public void ThenTheMessageExplainsSuspension()
    {
        ValidationProblem? problem = Response.As<ValidationProblem>();

        problem.Should().NotBeNull();
        problem!.Message.Should().Contain("suspended",
            "a user whose credentials are correct needs to know why they cannot proceed");
    }

    // "the validation error mentions the field {string}" is deliberately NOT defined here.
    // It applies to several features, so it lives once in CommonSteps - see the note there on
    // why Reqnroll's global step matching makes a single definition mandatory rather than
    // merely tidy.

    [Then("the sign-in response matches the login schema")]
    public void ThenTheResponseMatchesTheLoginSchema() =>
        Response.ShouldHaveStatus(HttpStatusCode.OK).ShouldMatchSchema(ResponseSchemas.Login);

    /// <summary>
    /// Translates the Gherkin literals <c>null</c> and <c>""</c> into real values.
    /// </summary>
    private static string? AsNullable(string value) =>
        string.Equals(value, "null", StringComparison.OrdinalIgnoreCase) ? null : value;
}
