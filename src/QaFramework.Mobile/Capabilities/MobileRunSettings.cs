using QaFramework.Mobile.Drivers;

namespace QaFramework.Mobile.Capabilities;

// Mobile configuration follows the same split as QaFramework.Core/Configuration/Settings.cs:
//
//   MobileRunSettings  = HOW we drive the app. Platform, target, server address, fixture.
//   DeviceCapabilities = WHAT we drive it on.  Device name, OS version, app identifiers.
//
// The reason for keeping them apart is practical rather than aesthetic. A capability set belongs
// to a device and is edited whenever the device pool changes; the run settings belong to the
// suite and are edited when the way we test changes. Merged into one file, nobody can tell which
// half is safe to touch, and the file rots. Kept apart, capability files can be committed per
// platform (capabilities.android.json, capabilities.ios.json) and selected at runtime.
//
// `required` members are used for the values that have no defensible default. A wrong default in
// this area is expensive: an automationName of the wrong flavour produces a session that starts
// and then fails every locator, which reads like an application defect for the first hour.

/// <summary>
/// How a mobile run is driven: which platform, against which target, with which capabilities.
/// </summary>
/// <remarks>
/// Composed by <see cref="MobileSettingsLoader"/> from two sources - the <c>Mobile</c> section of
/// <c>runsettings.json</c> and the platform's capability file - rather than bound from a single
/// document, so that neither file has to know about the other.
/// </remarks>
public sealed class MobileRunSettings
{
    /// <summary>The platform to drive. Selects which capability file is read.</summary>
    public required Platform Platform { get; init; }

    /// <summary>
    /// Where the run executes. <see cref="RunTarget.Simulated"/> in the committed configuration,
    /// which is why the suite is green in a pipeline with no Android SDK installed.
    /// </summary>
    public required RunTarget Target { get; init; }

    /// <summary>The device and application identifiers for this platform.</summary>
    public required DeviceCapabilities Device { get; init; }

    /// <summary>
    /// Address of the Appium server for <see cref="RunTarget.LocalAppium"/> and
    /// <see cref="RunTarget.Emulator"/>. A localhost default is safe: it cannot leak anything and
    /// it is where an engineer's server actually is.
    /// </summary>
    public string AppiumServerUrl { get; init; } = "http://127.0.0.1:4723/";

    /// <summary>
    /// Grid settings for <see cref="RunTarget.CloudGrid"/>. Null unless configured, and every
    /// value inside it is resolved from environment variables. See <see cref="CloudGridSettings"/>.
    /// </summary>
    public CloudGridSettings? CloudGrid { get; init; }

    /// <summary>
    /// Path, relative to the test output directory, of the JSON fixture the simulated driver
    /// loads. Configurable so a team can point a scenario at a different application shape
    /// without recompiling the framework.
    /// </summary>
    public string SimulationFixture { get; init; } = "Simulation/Fixtures/trading-app.json";

    /// <summary>
    /// How long a new session may take to become usable. Sessions are the slowest thing in mobile
    /// automation by an order of magnitude - a cold emulator boot plus an app install is
    /// routinely a minute - so this deliberately does not reuse the element or page-load timeouts
    /// from <see cref="Core.Configuration.TimeoutSettings"/>. Reusing them would either make
    /// session start flaky or make every element wait absurdly long.
    /// </summary>
    public int SessionStartupMs { get; init; } = 120_000;

    /// <summary>Convenience projection, matching the style of <c>TimeoutSettings</c>.</summary>
    public TimeSpan SessionStartup => TimeSpan.FromMilliseconds(SessionStartupMs);

    /// <summary>
    /// Rejects combinations that cannot work, at load time rather than mid-run.
    /// </summary>
    public void Validate()
    {
        if (SessionStartupMs <= 0)
            throw new InvalidOperationException(
                $"Mobile.SessionStartupMs must be greater than zero but was {SessionStartupMs}.");

        if (Target is RunTarget.LocalAppium or RunTarget.Emulator
            && !Uri.TryCreate(AppiumServerUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException(
                $"Mobile.AppiumServerUrl ('{AppiumServerUrl}') is not an absolute URI, and target " +
                $"'{Target}' needs one. Expected something like 'http://127.0.0.1:4723/'.");

        if (Target == RunTarget.CloudGrid && CloudGrid is null)
            throw new InvalidOperationException(
                "Target 'CloudGrid' was requested but no Mobile.CloudGrid section is configured. " +
                "The section names the environment variables that carry the grid URL and " +
                "credentials; it never carries the values themselves.");

        // Deliberately no validation of Device.App here. Whether an application path or bundle
        // identifier is required depends entirely on the target, and the factories are a better
        // place to say so because they know what they are about to send.
    }
}

/// <summary>
/// The device and application identifiers that become Appium capabilities.
/// </summary>
/// <remarks>
/// This is a curated subset, not a mirror of the Appium capability list. Naming the handful of
/// capabilities that a suite actually reasons about - and documenting why each one has the value
/// it has - is worth more than a passthrough dictionary, because it makes an unfamiliar reader's
/// first question ("why is noReset true here?") answerable from the type itself.
/// <para>
/// <see cref="Additional"/> exists as the escape hatch for the long tail, so that needing one
/// unusual capability never requires a framework change. Everything in it is passed through
/// verbatim.
/// </para>
/// </remarks>
public sealed class DeviceCapabilities
{
    /// <summary>
    /// <c>platformName</c>. Required and not derived from <see cref="MobileRunSettings.Platform"/>
    /// on purpose: the capability file is the document an engineer reads when a session will not
    /// start, and a file that does not state its own platform is a file that gets copied to the
    /// wrong place.
    /// </summary>
    public required string PlatformName { get; init; }

    /// <summary>
    /// <c>appium:automationName</c> - <c>UiAutomator2</c> or <c>XCUITest</c>. Required, because
    /// the driver's own default has changed between major Appium versions, and inheriting a
    /// silent default makes an upgrade look like a product regression.
    /// </summary>
    public required string AutomationName { get; init; }

    /// <summary>
    /// <c>appium:deviceName</c>. Advisory on Android, significant on iOS where it selects the
    /// simulator.
    /// </summary>
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>
    /// <c>appium:platformVersion</c>. Left empty in the committed files so that any attached
    /// device matches; a device pool with mixed OS versions would pin it per environment.
    /// </summary>
    public string PlatformVersion { get; init; } = string.Empty;

    /// <summary>
    /// <c>appium:app</c> - path to the .apk or .app under test. Empty in the committed files
    /// because no binary is or should be committed to this repository; a pipeline supplies it
    /// from the build artefact it has just produced.
    /// </summary>
    public string App { get; init; } = string.Empty;

    /// <summary>
    /// <c>appium:appPackage</c>. Android only. Used with <see cref="AppActivity"/> to attach to
    /// an already-installed build, which is much faster than reinstalling and is what a
    /// developer wants in a tight loop.
    /// </summary>
    public string AppPackage { get; init; } = string.Empty;

    /// <summary><c>appium:appActivity</c>. Android only.</summary>
    public string AppActivity { get; init; } = string.Empty;

    /// <summary><c>appium:bundleId</c>. iOS equivalent of <see cref="AppPackage"/>.</summary>
    public string BundleId { get; init; } = string.Empty;

    /// <summary>
    /// <c>appium:noReset</c>. False in the committed files: a scenario that inherits the previous
    /// scenario's logged-in state is a scenario that passes alone and fails in a suite. The
    /// seconds saved by resetting less are not worth the class of failure it introduces.
    /// </summary>
    public bool NoReset { get; init; }

    /// <summary>
    /// <c>appium:fullReset</c>. False: a full reinstall between scenarios is correct in principle
    /// and unaffordable in practice. Per-scenario data isolation is the suite's job - see
    /// <c>QaFramework.Core/TestData</c> - not the installer's.
    /// </summary>
    public bool FullReset { get; init; }

    /// <summary>
    /// <c>appium:newCommandTimeout</c>, in seconds. Must comfortably exceed the longest gap
    /// between two commands, otherwise the server tears down a session that is merely waiting -
    /// a failure that looks random because it depends on how long an assertion took.
    /// </summary>
    public int NewCommandTimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// Any further Appium capability, passed through as-is.
    /// </summary>
    /// <remarks>
    /// Keys are written <i>without</i> the <c>appium:</c> prefix and the factories add it. That
    /// is forced by the configuration stack rather than chosen: <c>':'</c> is the key separator
    /// in <c>Microsoft.Extensions.Configuration</c>, so a JSON property named
    /// <c>appium:autoGrantPermissions</c> binds as a nested section and never arrives as a
    /// dictionary entry. A key that already contains <c>':'</c> is left untouched, so a
    /// non-Appium vendor prefix supplied through an environment variable still works.
    /// </remarks>
    public Dictionary<string, string> Additional { get; init; } = [];
}

/// <summary>
/// Where the grid URL and credentials come from - never what they are.
/// </summary>
/// <remarks>
/// This type holds the <i>names</i> of environment variables, and the committed JSON contains
/// only those names. There is no default URL, no default user and no default key anywhere in the
/// code path, which is a deliberate constraint rather than an oversight: a hardcoded fallback is
/// how a credential ends up in a public repository, and a placeholder URL is how a suite silently
/// points at the wrong grid.
/// <para>
/// <see cref="Resolve"/> therefore throws when a variable is missing, naming every variable it
/// needed. A grid run that cannot authenticate should fail in the first second with an actionable
/// message, not on the first locator.
/// </para>
/// </remarks>
public sealed class CloudGridSettings
{
    /// <summary>Name of the variable holding the grid's WebDriver endpoint.</summary>
    public string UrlVariable { get; init; } = "QA_MOBILE_GRID_URL";

    /// <summary>Name of the variable holding the grid user name.</summary>
    public string UserNameVariable { get; init; } = "QA_MOBILE_GRID_USERNAME";

    /// <summary>Name of the variable holding the grid access key.</summary>
    public string AccessKeyVariable { get; init; } = "QA_MOBILE_GRID_ACCESS_KEY";

    /// <summary>
    /// Extra capabilities a grid needs to label a session - build name, project name. Values
    /// here are descriptive metadata only; a credential must never be routed through it.
    /// </summary>
    public Dictionary<string, string> SessionMetadata { get; init; } = [];

    /// <summary>
    /// Reads the three variables, or fails with a message that says exactly what to set.
    /// </summary>
    /// <param name="readVariable">
    /// Injected so this can be unit-tested without mutating the process environment, which is
    /// global state that leaks between parallel tests.
    /// </param>
    public ResolvedCloudGrid Resolve(Func<string, string?>? readVariable = null)
    {
        Func<string, string?> read = readVariable ?? Environment.GetEnvironmentVariable;

        string? url = read(UrlVariable);
        string? user = read(UserNameVariable);
        string? key = read(AccessKeyVariable);

        List<string> missing = [];
        if (string.IsNullOrWhiteSpace(url)) missing.Add(UrlVariable);
        if (string.IsNullOrWhiteSpace(user)) missing.Add(UserNameVariable);
        if (string.IsNullOrWhiteSpace(key)) missing.Add(AccessKeyVariable);

        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Run target 'CloudGrid' was requested but these environment variables are not " +
                $"set: {string.Join(", ", missing)}. Cloud device grid credentials are supplied " +
                "only through the environment - there is no default and no committed value - so " +
                "the run stops here rather than attempting an unauthenticated session.");

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? endpoint))
            throw new InvalidOperationException(
                $"The value of {UrlVariable} is not an absolute URI. Expected the grid's " +
                "WebDriver endpoint, for example 'https://grid.example.invalid/wd/hub'.");

        return new ResolvedCloudGrid(endpoint, user!, key!);
    }
}

/// <summary>
/// A resolved set of grid credentials.
/// </summary>
/// <remarks>
/// A distinct type from <see cref="CloudGridSettings"/> so that "names of variables" and "secret
/// values" are not the same shape. It is also why <see cref="ToString"/> is overridden: this
/// object can reach a log line through an interpolated string or a serialiser, and the default
/// record formatting would print the access key.
/// </remarks>
public sealed record ResolvedCloudGrid(Uri Endpoint, string UserName, string AccessKey)
{
    /// <summary>Redacted by design. See the remarks on the type.</summary>
    public override string ToString() => $"cloud device grid at {Endpoint.Host} as {UserName} (key redacted)";
}
