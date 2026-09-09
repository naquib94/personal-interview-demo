using QaFramework.Core.Configuration;
using QaFramework.Core.Logging;
using QaFramework.Mobile.Capabilities;
using QaFramework.Mobile.Drivers;
using QaFramework.Mobile.Screens.Trading;
using QaFramework.Mobile.Simulation;
using Reqnroll;
using Reqnroll.BoDi;

namespace Mobile.Tests.Support;

/// <summary>
/// Scenario setup and teardown.
/// </summary>
/// <remarks>
/// Everything a step definition needs is resolved here and registered in Reqnroll's container,
/// so step classes declare their dependencies as constructor parameters and nothing in this suite
/// reaches for ambient state. There is no static field in this project - not even a cached
/// configuration - and that is a deliberate constraint: static mutable state is what makes a
/// suite unable to run its scenarios in parallel later, and "later" always arrives.
/// <para>
/// A note on the road not taken. <see cref="MobileDriverContext"/> exists for callers that cannot
/// accept a constructor argument, and it would have been tempting to set it here. It is not set,
/// because an <see cref="AsyncLocal{T}"/> written inside an <c>async</c> hook does not survive the
/// hook returning: the runtime copies the execution context at the async boundary, so the write
/// is visible inside the hook and gone by the time a step runs. That is a subtle enough trap to
/// be worth writing down rather than rediscovering - and it is one more argument for constructor
/// injection being the primary mechanism rather than a nicety.
/// </para>
/// </remarks>
[Binding]
public sealed class MobileHooks(IObjectContainer container, ScenarioContext scenario)
{
    /// <summary>
    /// Resolves configuration, opens a session and registers everything a step may need.
    /// </summary>
    /// <remarks>
    /// Configuration is loaded per scenario rather than once per run. It costs a few file reads,
    /// which is nothing next to a session start, and it buys the absence of a static cache -
    /// see the class remarks. If a suite ever grows to the point where this matters, the fix is a
    /// run-scoped container registration, not a static field.
    /// </remarks>
    [BeforeScenario]
    public async Task StartSessionAsync()
    {
        string configurationDirectory = AppContext.BaseDirectory;

        TestConfiguration configuration = ConfigurationLoader.Load(configurationDirectory);
        MobileRunSettings mobile = MobileSettingsLoader.Load(configurationDirectory);

        TestLog.Step(
            $"Starting scenario '{scenario.ScenarioInfo.Title}' on {mobile.Platform} " +
            $"({mobile.Target}).");

        MobileDriverProvider provider = new(MobileDriverFactoryResolver.Default());

        // The expected credentials are handed to the simulated application as named tokens rather
        // than being written into the committed fixture. That keeps credentials out of fixture
        // files and, usefully, means the simulated run exercises the real configuration chain
        // instead of bypassing it.
        TestUser trader = configuration.User("ActiveTrader");

        IMobileDriver driver = await provider.CreateAsync(
            mobile,
            configurationDirectory,
            SimulationTokens.ForUser(trader.Username, trader.Password));

        container.RegisterInstanceAs(configuration);
        container.RegisterInstanceAs(configuration.Timeouts);
        container.RegisterInstanceAs(mobile);
        container.RegisterInstanceAs(driver);
        container.RegisterInstanceAs(new EvidenceRecorder(configuration.Run.Evidence));

        // Screens are registered as instances rather than resolved by the container, because they
        // need the driver and the timeout catalogue and the container would otherwise have to be
        // taught how to build each one. Four lines here is cheaper than a registration convention.
        container.RegisterInstanceAs(new LoginScreen(driver, configuration.Timeouts));
        container.RegisterInstanceAs(new AccountScreen(driver, configuration.Timeouts));
        container.RegisterInstanceAs(new NewOrderScreen(driver, configuration.Timeouts));
        container.RegisterInstanceAs(new OrderHistoryScreen(driver, configuration.Timeouts));
    }

    /// <summary>
    /// Captures evidence when the scenario failed, then ends the session.
    /// </summary>
    /// <remarks>
    /// The order matters and it is the reverse of what reads naturally: evidence first, teardown
    /// second. Ending the session invalidates the very thing the evidence is captured from, so a
    /// teardown that quits first can only ever produce an empty artefact folder.
    /// <para>
    /// <c>ScenarioContext.TestError</c> is the only reliable signal here. Inspecting the scenario
    /// status enum instead is the common alternative and it misses failures thrown from a
    /// <c>Before</c> hook.
    /// </para>
    /// </remarks>
    [AfterScenario]
    public async Task EndSessionAsync()
    {
        // Resolved rather than injected: if StartSessionAsync threw before registering the
        // driver, there is nothing to tear down and nothing to capture, and an injected
        // constructor parameter would fail the teardown itself.
        if (!container.IsRegistered<IMobileDriver>())
        {
            TestLog.Warning(
                "No mobile session was registered for this scenario, so there is nothing to " +
                "tear down. The failure will be in the [BeforeScenario] hook.");
            return;
        }

        IMobileDriver driver = container.Resolve<IMobileDriver>();

        try
        {
            if (scenario.TestError is not null)
            {
                EvidenceRecorder recorder = container.Resolve<EvidenceRecorder>();

                await recorder.CaptureAsync(
                    driver, scenario.ScenarioInfo.Title, scenario.TestError);
            }
        }
        finally
        {
            // In a finally block, so a capture that somehow throws still cannot leak a session.
            // On a device grid a leaked session is a device the next run cannot acquire.
            await driver.DisposeSessionAsync();
        }
    }
}
