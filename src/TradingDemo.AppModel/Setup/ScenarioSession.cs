using QaFramework.Core.Configuration;
using QaFramework.Core.Logging;
using QaFramework.Core.TestData;
using TradingDemo.AppModel.ApiClients;
using TradingDemo.AppModel.Contracts;

namespace TradingDemo.AppModel.Setup;

/// <summary>
/// Per-scenario state: who is signed in, what has been created, what needs cleaning up.
/// </summary>
/// <remarks>
/// <para>Resolved from the BDD container, so each scenario gets its own instance and scenarios
/// cannot see each other's state. This is what allows the suite to run in parallel.</para>
///
/// <para><b>Why this exists rather than passing values through <c>ScenarioContext</c>.</b>
/// A string-keyed bag (<c>ScenarioContext["orderRef"]</c>) has no compile-time safety, no
/// discoverability, and a typo produces a runtime KeyNotFoundException in an unrelated step.
/// A typed session object gives IntelliSense, refactoring support, and a single place to
/// document what a scenario is allowed to remember.</para>
/// </remarks>
public sealed class ScenarioSession(
    TestConfiguration configuration,
    AuthApiClient authClient,
    TradingApiClient tradingClient,
    DataGenerator data,
    ResourceTracker resources)
{
    public TestConfiguration Configuration { get; } = configuration;

    public AuthApiClient Auth { get; } = authClient;

    public TradingApiClient Trading { get; } = tradingClient;

    public DataGenerator Data { get; } = data;

    /// <summary>
    /// Resources created by this scenario, removed in <c>[AfterScenario]</c>.
    /// </summary>
    /// <remarks>
    /// The demo has no order-deletion endpoint, so orders are reverted by restoring the seed
    /// (see <see cref="TrackOrderForCleanupAsync"/>). That is an honest limitation of the demo
    /// application rather than of the pattern, and it is called out here because the tracker
    /// is the right mechanism either way.
    /// </remarks>
    public ResourceTracker Resources { get; } = resources;

    /// <summary>The role currently signed in, or null.</summary>
    public TestUser? CurrentUser { get; private set; }

    /// <summary>Bearer token for the current session.</summary>
    public string? Token { get; private set; }

    /// <summary>
    /// The most recent API response of interest, so a Then step can assert on what a When step
    /// did.
    /// </summary>
    /// <remarks>
    /// Typed as <see cref="object"/> because a scenario's steps deal in different payload types.
    /// <see cref="LastResponseAs{T}"/> is the accessor, and it fails with a clear message rather
    /// than an InvalidCastException - which in a step definition is otherwise very hard to
    /// attribute.
    /// </remarks>
    public object? LastResponse { get; set; }

    /// <summary>The order this scenario placed, for the Then steps to assert against.</summary>
    public OrderResponse? PlacedOrder { get; set; }

    public T LastResponseAs<T>() => LastResponse is T typed
        ? typed
        : throw TestLog.Failure(
            $"This step expected the previous step to have produced a {typeof(T).Name}, but " +
            $"ScenarioSession.LastResponse holds {LastResponse?.GetType().Name ?? "nothing"}. " +
            "The Given/When steps for this scenario are probably in the wrong order, or a When " +
            "step did not record its response.");

    /// <summary>
    /// Signs in through the API and remembers the token.
    /// </summary>
    /// <remarks>
    /// Signing in via the API even for UI scenarios: the token is needed for API-based setup
    /// and for database verification, and obtaining it costs one HTTP call rather than a
    /// browser round trip.
    /// </remarks>
    public async Task<string> SignInAsync(string role)
    {
        TestUser user = Configuration.User(role);
        Token = await Auth.GetTokenOrFailAsync(user);
        CurrentUser = user;
        return Token;
    }

    public string RequireToken() => Token
        ?? throw TestLog.Failure(
            "This step requires an authenticated session, but no Given step has signed a user in. " +
            "Add a 'Given an active trader is signed in' step to the scenario.");

    /// <summary>
    /// Registers an order for cleanup.
    /// </summary>
    /// <remarks>
    /// The demo API has no delete endpoint, so cleanup restores the seeded state. That is a
    /// blunt instrument - it removes every scenario's data, not just this one's - and is
    /// therefore only safe because this suite owns its own application instance. Against a
    /// shared environment the correct implementation is a targeted delete, and the tracker's
    /// interface is unchanged either way, which is the point of expressing cleanup as an
    /// arbitrary undo action rather than as a list of ids.
    /// </remarks>
    public Task TrackOrderForCleanupAsync(string orderReference)
    {
        Resources.Track($"order {orderReference}", async () =>
        {
            if (Configuration.Target.StartApplicationUnderTest) return;
            await Trading.ResetDatabaseAsync();
        });

        return Task.CompletedTask;
    }
}
