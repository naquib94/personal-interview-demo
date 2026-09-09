using QaFramework.Core.Logging;
using QaFramework.Mobile.Capabilities;
using QaFramework.Mobile.Simulation;

namespace QaFramework.Mobile.Drivers;

/// <summary>
/// The single entry point a test suite uses to obtain a session.
/// </summary>
/// <remarks>
/// One decision lives here and nowhere else: whether this run talks to a device or to the
/// simulator. Putting it in the provider rather than inside each platform factory keeps the
/// factories about capabilities and sessions, and keeps the hooks free of an <c>if</c> that
/// would otherwise be duplicated in every suite that wants a mobile driver.
/// <para>
/// The provider takes a <see cref="MobileDriverFactoryResolver"/> rather than constructing one,
/// so a test can substitute a stub factory and prove that a <c>LocalAppium</c> configuration
/// would route to the right place - without a server being present to route to.
/// </para>
/// </remarks>
public sealed class MobileDriverProvider(MobileDriverFactoryResolver resolver)
{
    /// <summary>
    /// Opens a session for the given settings.
    /// </summary>
    /// <param name="settings">Resolved mobile settings, normally from <see cref="MobileSettingsLoader"/>.</param>
    /// <param name="fixtureDirectory">
    /// Directory that <see cref="MobileRunSettings.SimulationFixture"/> is relative to; ignored
    /// unless the target is <see cref="RunTarget.Simulated"/>. Defaults to the test output
    /// directory, which is where the build puts the fixtures.
    /// </param>
    /// <param name="simulationTokens">
    /// Values the fixture refers to by name - see <see cref="SimulationTokens"/>. This is how the
    /// expected credentials reach the simulated application without being written into a
    /// committed fixture file.
    /// </param>
    public Task<IMobileDriver> CreateAsync(
        MobileRunSettings settings,
        string? fixtureDirectory = null,
        IReadOnlyDictionary<string, string>? simulationTokens = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Target == RunTarget.Simulated)
        {
            string path = Path.Combine(
                fixtureDirectory ?? AppContext.BaseDirectory, settings.SimulationFixture);

            // Logged at the start of every simulated run, and worded so that no report reader can
            // mistake the result for evidence about a real device.
            TestLog.Info(
                $"Mobile run target is 'Simulated': no device, emulator or Appium server is " +
                $"involved. Driving the in-memory fixture at '{settings.SimulationFixture}' on " +
                $"platform {settings.Platform}. This exercises the framework, not the product.");

            SimulatedApp app = SimulatedApp.LoadFromFile(path, simulationTokens);

            return Task.FromResult<IMobileDriver>(
                new SimulatedMobileDriver(app, settings.Platform));
        }

        IMobileDriverFactory factory = resolver.Resolve(settings.Platform);

        return factory.CreateSessionAsync(settings);
    }
}
