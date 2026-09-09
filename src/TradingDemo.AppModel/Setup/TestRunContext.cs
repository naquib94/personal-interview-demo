using QaFramework.Api.Requests;
using QaFramework.Core.Configuration;
using QaFramework.Core.Logging;
using QaFramework.Core.TestData;

namespace TradingDemo.AppModel.Setup;

/// <summary>
/// State that is created once per test run and shared by every scenario.
/// </summary>
/// <remarks>
/// <para><b>On the use of static state.</b> Statics in a test framework are usually a mistake,
/// and elsewhere in this project they are avoided - see the comment on
/// <see cref="QaFramework.Web.Setup.WebTestContext"/>. Three specific properties make it
/// acceptable here, and all three have to hold:</para>
/// <list type="number">
/// <item>It is written exactly once, in <c>[BeforeTestRun]</c>, before any scenario starts.</item>
/// <item>Everything it holds is either immutable (<see cref="Configuration"/>) or genuinely
/// run-scoped and thread-safe (one HTTP client, one application process).</item>
/// <item>No scenario ever mutates it.</item>
/// </list>
/// <para>Write-once-then-read-only is a different thing from shared mutable state, and the
/// distinction is worth making explicitly rather than applying "no statics" as a rule without
/// understanding it. The moment a scenario needed to change any of this, it would have to move
/// into the per-scenario container.</para>
///
/// <para><see cref="ApiClient"/> in particular is deliberately run-scoped: it owns an HTTP
/// connection pool, and creating one per scenario would leak sockets - the failure mode
/// described in <see cref="QaFramework.Api.Requests.ApiClient"/>.</para>
/// </remarks>
public static class TestRunContext
{
    private static TestConfiguration? configuration;
    private static ApiClient? apiClient;
    private static ApplicationUnderTest? applicationUnderTest;

    public static TestConfiguration Configuration => configuration
        ?? throw new InvalidOperationException(
            "The test run has not been initialised. TestRunContext.InitialiseAsync must be called " +
            "from a [BeforeTestRun] hook. If this is a new test project, check that its " +
            "reqnroll.json lists TradingDemo.AppModel in bindingAssemblies - otherwise the shared " +
            "hooks are never discovered.");

    public static ApiClient ApiClient => apiClient
        ?? throw new InvalidOperationException("The test run has not been initialised.");

    /// <summary>
    /// The seed used for generated test data this run.
    /// </summary>
    /// <remarks>
    /// Logged at the start of every run. Reproducing a data-dependent failure then means
    /// setting <c>QA_DATA_SEED</c> to this value - which is the difference between a bug that
    /// can be investigated and one that can only be waited for.
    /// </remarks>
    public static int DataSeed { get; private set; }

    public static async Task InitialiseAsync()
    {
        if (configuration is not null) return;

        configuration = ConfigurationLoader.Load(AppContext.BaseDirectory);

        DataSeed = new DataGenerator().Seed;

        TestLog.Info(
            $"Test run starting. Environment: {configuration.Environment}. " +
            $"Target: {configuration.Target.BaseUrl}. " +
            $"Browser: {configuration.Run.Browser.Name} " +
            $"({(configuration.Run.Browser.Headless ? "headless" : "headed")}). " +
            $"Test-data seed: {DataSeed} (set QA_DATA_SEED to this value to reproduce this run's data).");

        applicationUnderTest = new ApplicationUnderTest(configuration.Target, configuration.Timeouts);
        await applicationUnderTest.StartAsync();

        apiClient = new ApiClient(configuration.Target.BaseUrl, configuration.Timeouts);
    }

    public static async Task ShutDownAsync()
    {
        apiClient?.Dispose();
        apiClient = null;

        if (applicationUnderTest is not null)
        {
            await applicationUnderTest.DisposeAsync();
            applicationUnderTest = null;
        }

        configuration = null;
    }
}
