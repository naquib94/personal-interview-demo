using Microsoft.Extensions.Configuration;
using QaFramework.Mobile.Drivers;

namespace QaFramework.Mobile.Capabilities;

/// <summary>
/// Binds the mobile settings from the same output directory
/// <see cref="Core.Configuration.ConfigurationLoader"/> reads.
/// </summary>
/// <remarks>
/// A separate loader rather than an extra property on <c>RunSettings</c>. The reason is that
/// <c>QaFramework.Core</c> must not learn about Appium, platforms or devices: it is referenced by
/// the API and web suites, which have no interest in any of that. Keeping the mobile schema in
/// the mobile assembly means the web suite's configuration cannot fail to bind because of a
/// mobile capability it never uses.
/// <para>
/// The precedence chain is inherited deliberately, not reinvented - the same
/// <c>QA_</c>-prefixed environment variables override the same JSON, so
/// <c>QA_Mobile__Target=LocalAppium</c> switches a run without editing a file. Two different
/// override mechanisms in one repository would be one too many.
/// </para>
/// </remarks>
public static class MobileSettingsLoader
{
    /// <summary>Section of <c>runsettings.json</c> that carries mobile settings.</summary>
    public const string SectionName = "Mobile";

    /// <summary>
    /// Loads the mobile settings and the capability file for the selected platform.
    /// </summary>
    /// <param name="configurationDirectory">
    /// Normally the test assembly's output directory, which is where the build copies both
    /// <c>runsettings.json</c> and the capability files.
    /// </param>
    public static MobileRunSettings Load(string configurationDirectory)
    {
        string runSettingsPath = Path.Combine(configurationDirectory, "runsettings.json");

        if (!File.Exists(runSettingsPath))
            throw new FileNotFoundException(
                $"Could not find 'runsettings.json' at '{runSettingsPath}'. The mobile settings " +
                "live in its 'Mobile' section.", runSettingsPath);

        IConfigurationRoot root = new ConfigurationBuilder()
            .AddJsonFile(runSettingsPath, optional: false)
            .AddEnvironmentVariables(Core.Configuration.ConfigurationLoader.EnvironmentVariablePrefix)
            .Build();

        IConfigurationSection section = root.GetSection(SectionName);

        if (!section.Exists())
            throw new InvalidOperationException(
                $"'{runSettingsPath}' has no '{SectionName}' section. The mobile suite needs one; " +
                "at minimum it must state Platform and Target.");

        MobileSelection selection = section.Get<MobileSelection>()
            ?? throw new InvalidOperationException(
                $"The '{SectionName}' section of '{runSettingsPath}' could not be bound.");

        DeviceCapabilities device = LoadCapabilities(
            configurationDirectory, selection.CapabilitiesDirectory, selection.Platform);

        MobileRunSettings settings = new()
        {
            Platform = selection.Platform,
            Target = selection.Target,
            Device = device,
            AppiumServerUrl = selection.AppiumServerUrl,
            CloudGrid = selection.CloudGrid,
            SimulationFixture = selection.SimulationFixture,
            SessionStartupMs = selection.SessionStartupMs
        };

        settings.Validate();

        // Resolved here, before any session is attempted, purely so that a misconfigured grid run
        // fails during setup with a message about environment variables rather than during the
        // first step with a message about HTTP 401.
        if (settings.Target == RunTarget.CloudGrid)
            _ = settings.CloudGrid!.Resolve();

        return settings;
    }

    /// <summary>
    /// Reads <c>capabilities.android.json</c> or <c>capabilities.ios.json</c>.
    /// </summary>
    /// <remarks>
    /// Public and parameterised so that a unit test can point it at a fixture directory. The
    /// alternative - a private method reachable only through <see cref="Load"/> - would make
    /// capability binding testable only by writing a whole runsettings file.
    /// </remarks>
    public static DeviceCapabilities LoadCapabilities(
        string configurationDirectory,
        string capabilitiesDirectory,
        Platform platform)
    {
        string fileName = $"capabilities.{FileSuffix(platform)}.json";
        string path = Path.Combine(configurationDirectory, capabilitiesDirectory, fileName);

        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Platform '{platform}' was selected but its capability file '{path}' does not " +
                "exist. Capability files are copied to the output directory by QaFramework.Mobile; " +
                "a missing file usually means the project was not rebuilt after one was added.",
                path);

        IConfigurationRoot root = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false)
            .AddEnvironmentVariables(Core.Configuration.ConfigurationLoader.EnvironmentVariablePrefix)
            .Build();

        try
        {
            return root.Get<DeviceCapabilities>()
                ?? throw new InvalidOperationException("the file bound to null");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to bind {nameof(DeviceCapabilities)} from '{path}'. {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Maps a platform to its capability file suffix.
    /// </summary>
    /// <remarks>
    /// Not <c>platform.ToString().ToLower()</c>: that yields "ios" today only because the enum
    /// member happens to be spelled <c>IOS</c>. An explicit map means renaming the enum member
    /// for readability cannot silently rename a committed file.
    /// </remarks>
    private static string FileSuffix(Platform platform) => platform switch
    {
        Platform.Android => "android",
        Platform.IOS => "ios",
        _ => throw new InvalidOperationException($"Unhandled platform '{platform}'.")
    };

    /// <summary>
    /// The shape of the <c>Mobile</c> section: everything except the capabilities, which come
    /// from their own per-platform file.
    /// </summary>
    private sealed class MobileSelection
    {
        public Platform Platform { get; init; } = Platform.Android;

        // Simulated is the default in code as well as in the committed JSON. If someone deletes
        // the setting, the safe outcome is a device-free run, not an attempt to reach a server
        // that is not there.
        public RunTarget Target { get; init; } = RunTarget.Simulated;

        public string AppiumServerUrl { get; init; } = "http://127.0.0.1:4723/";

        public string CapabilitiesDirectory { get; init; } = "Capabilities";

        public string SimulationFixture { get; init; } = "Simulation/Fixtures/trading-app.json";

        public int SessionStartupMs { get; init; } = 120_000;

        public CloudGridSettings? CloudGrid { get; init; }
    }
}
