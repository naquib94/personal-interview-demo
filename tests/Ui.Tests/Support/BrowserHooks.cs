using QaFramework.Core.Configuration;
using QaFramework.Core.Logging;
using QaFramework.Web.Setup;
using Reqnroll.BoDi;
using TradingDemo.AppModel.Setup;

namespace Ui.Tests.Support;

/// <summary>
/// Browser lifecycle and failure evidence for the UI suite.
/// </summary>
/// <remarks>
/// <para>Only the browser concerns live here. Configuration loading, starting the application
/// under test, dependency registration and data cleanup are all in
/// <c>TradingDemo.AppModel.Hooks.SharedHooks</c>, which this suite inherits through
/// <c>reqnroll.json</c>. The split means the API and mobile suites do not carry browser code
/// they never use, and the shared lifecycle cannot drift between suites.</para>
///
/// <para><b>Hook ordering is explicit and load-bearing:</b></para>
/// <list type="bullet">
/// <item><c>BeforeScenario(0)</c> - SharedHooks registers configuration and API clients.</item>
/// <item><c>BeforeScenario(10)</c> - this class registers the browser context and launches it,
/// because it needs the configuration registered above.</item>
/// <item><c>AfterScenario(100)</c> - this class captures evidence <i>while the browser is still
/// alive</i>, then closes it.</item>
/// <item><c>AfterScenario(200)</c> - SharedHooks cleans up test data, after the evidence has
/// been captured. Cleaning up first would destroy the state that caused the failure.</item>
/// </list>
/// <para>Ordering in a BDD framework is implicit and easy to get wrong, so it is stated in both
/// hook classes rather than left to declaration order.</para>
/// </remarks>
[Binding]
public sealed class BrowserHooks(
    IObjectContainer container,
    ScenarioContext scenarioContext)
{
    [BeforeScenario(Order = 10)]
    public async Task StartBrowser()
    {
        TestConfiguration configuration = container.Resolve<TestConfiguration>();

        WebTestContext context = new(configuration);
        container.RegisterInstanceAs(context);
        container.RegisterInstanceAs(new EvidenceCollector(configuration.Run.Evidence));

        await context.StartBrowserAsync();
    }

    /// <summary>
    /// Captures evidence on failure, then closes the browser.
    /// </summary>
    /// <remarks>
    /// <para>The order inside this method matters as much as the hook order. Evidence is
    /// captured first, because a closed page cannot be screenshotted.</para>
    ///
    /// <para>The browser is closed in a <c>finally</c> block. Without it, an exception during
    /// evidence capture would leak a browser process for every failing scenario, and a suite
    /// that fails badly would exhaust the agent - turning one product defect into a broken
    /// build agent.</para>
    /// </remarks>
    [AfterScenario(Order = 100)]
    public async Task CaptureEvidenceAndStopBrowser()
    {
        WebTestContext context = container.Resolve<WebTestContext>();

        try
        {
            if (scenarioContext.TestError is not null && context.IsStarted)
            {
                EvidenceCollector evidence = container.Resolve<EvidenceCollector>();
                await evidence.CaptureFailureAsync(context.ActivePage, scenarioContext.ScenarioInfo.Title);
            }
        }
        catch (Exception ex)
        {
            // Evidence collection already swallows its own errors; this is a second line of
            // defence. A teardown failure must never replace the real failure message, which is
            // the single most frustrating thing a framework can do to someone triaging a build.
            TestLog.Warning($"Failure evidence could not be captured: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            await context.StopBrowserAsync();
        }
    }
}
