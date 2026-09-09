using QaFramework.Core.Configuration;
using QaFramework.Core.Database;
using QaFramework.Core.Logging;
using QaFramework.Core.TestData;
using Reqnroll;
using Reqnroll.BoDi;
using TradingDemo.AppModel.ApiClients;
using TradingDemo.AppModel.Setup;

namespace TradingDemo.AppModel.Hooks;

/// <summary>
/// Lifecycle hooks shared by every suite.
/// </summary>
/// <remarks>
/// <para><b>Why these live in the application model rather than in each test project.</b> The
/// API, UI and mobile suites all need the same run initialisation, the same dependency
/// registration and the same cleanup. Reqnroll discovers bindings in any assembly listed under
/// <c>bindingAssemblies</c> in <c>reqnroll.json</c>, so each suite lists this assembly and
/// inherits the lifecycle rather than copying it.</para>
///
/// <para>That is not merely tidier. Copied hooks drift: one suite gains a fix, the others do
/// not, and the resulting difference in behaviour between suites is very hard to reason about.
/// Suite-specific concerns - starting a browser, opening an Appium session - stay in the suite
/// that needs them.</para>
/// </remarks>
[Binding]
public sealed class SharedHooks(IObjectContainer container, ScenarioContext scenarioContext)
{
    [BeforeTestRun(Order = 0)]
    public static Task InitialiseTestRun() => TestRunContext.InitialiseAsync();

    [AfterTestRun(Order = 100)]
    public static Task ShutDownTestRun() => TestRunContext.ShutDownAsync();

    /// <summary>
    /// Registers per-scenario dependencies.
    /// </summary>
    /// <remarks>
    /// <para>Order 0 so it runs before any suite-specific hook that needs these registrations.
    /// Hook ordering in a BDD framework is implicit and easy to get wrong, so it is stated
    /// explicitly here and in the suite hooks rather than being left to declaration order.</para>
    ///
    /// <para>Only the types that cannot be constructed by the container unaided are registered.
    /// Page objects, API clients and <see cref="ScenarioSession"/> resolve automatically from
    /// these, so adding a page requires no registration - which is what keeps the container
    /// configuration from becoming a maintenance burden of its own.</para>
    /// </remarks>
    [BeforeScenario(Order = 0)]
    public void RegisterScenarioDependencies()
    {
        TestConfiguration configuration = TestRunContext.Configuration;

        container.RegisterInstanceAs(configuration);
        container.RegisterInstanceAs(configuration.Timeouts);
        container.RegisterInstanceAs(configuration.Run.Evidence);

        // Run-scoped, registered per scenario. One connection pool for the whole run; see the
        // comment in TestRunContext for why that lifetime is deliberate.
        container.RegisterInstanceAs(TestRunContext.ApiClient);

        // Seeded from the run-wide seed plus the scenario's own identity, so that data is
        // unique per scenario (no collisions under parallel execution) while the run as a whole
        // stays reproducible from a single logged seed.
        container.RegisterInstanceAs(new DataGenerator(
            TestRunContext.DataSeed ^ scenarioContext.ScenarioInfo.Title.GetHashCode(StringComparison.Ordinal)));

        container.RegisterInstanceAs(new ResourceTracker());

        // Registered as a factory rather than an instance: a scenario that never touches the
        // database must not fail because this environment has no database path configured.
        // Construction is deferred to the first step that actually asks for a verifier.
        container.RegisterFactoryAs(_ => new DatabaseVerifier(configuration.Target.DatabasePath));

        TestLog.Step($"Scenario: {scenarioContext.ScenarioInfo.Title}");
    }

    /// <summary>
    /// Cleans up whatever the scenario created.
    /// </summary>
    /// <remarks>
    /// <para>Order 200 so it runs <i>after</i> suite-specific teardown - the browser must still
    /// be alive when the UI suite captures its failure evidence, and evidence capture must
    /// happen before cleanup changes the state that caused the failure.</para>
    ///
    /// <para>Cleanup runs whether the scenario passed or failed. Skipping it on failure is a
    /// tempting shortcut - "leave the data for investigation" - but in practice it means a
    /// flaky test slowly fills the environment, and the data left behind is rarely the thing
    /// anyone looks at. The evidence captured at the point of failure is.</para>
    /// </remarks>
    [AfterScenario(Order = 200)]
    public async Task CleanUpScenario()
    {
        ResourceTracker resources = container.Resolve<ResourceTracker>();

        if (resources.Count > 0)
            await resources.CleanUpAsync();

        string outcome = scenarioContext.TestError is null ? "PASSED" : "FAILED";
        TestLog.Info($"Scenario {outcome}: {scenarioContext.ScenarioInfo.Title}");
    }
}
