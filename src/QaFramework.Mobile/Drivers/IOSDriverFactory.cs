using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Appium.iOS;
using QaFramework.Mobile.Capabilities;
using QaFramework.Core.Logging;

namespace QaFramework.Mobile.Drivers;

/// <summary>
/// Builds iOS capabilities, and - separately - opens iOS sessions.
/// </summary>
/// <remarks>
/// The iOS twin of <see cref="AndroidDriverFactory"/>. It exists as a separate type rather than
/// as a branch inside one factory because the platform-specific rules genuinely differ - iOS has
/// no activity concept, its identifiers are bundle identifiers, and its session failures have
/// different causes - and a single factory with two <c>if</c> chains is where those differences
/// get blurred.
/// </remarks>
public sealed class IOSDriverFactory : IMobileDriverFactory
{
    /// <inheritdoc />
    public Platform Platform => Platform.IOS;

    /// <inheritdoc />
    public AppiumOptions BuildOptions(MobileRunSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Platform != Platform.IOS)
            throw new InvalidOperationException(
                $"{nameof(IOSDriverFactory)} was given settings for platform " +
                $"'{settings.Platform}'. This almost always means the factory was resolved by " +
                "hand instead of through MobileDriverFactoryResolver.");

        AppiumOptions options = MobileOptionsBuilder.BuildCommon(settings);
        DeviceCapabilities device = settings.Device;

        if (!string.IsNullOrWhiteSpace(device.BundleId))
            options.AddAdditionalAppiumOption("appium:bundleId", device.BundleId);

        if (string.IsNullOrWhiteSpace(device.App) && string.IsNullOrWhiteSpace(device.BundleId))
            throw new InvalidOperationException(
                "iOS capabilities specify neither 'app' (a path to an .app or .ipa) nor " +
                "'bundleId' (an already-installed build). Failing here keeps the diagnosis in " +
                "configuration, where the fault is, rather than in an XCUITest log.");

        // deviceName is advisory on Android but selects the simulator on iOS, so its absence is
        // an error here and merely a shrug there. This asymmetry is exactly why the two factories
        // are separate types.
        if (string.IsNullOrWhiteSpace(device.DeviceName))
            throw new InvalidOperationException(
                "iOS capabilities must specify 'deviceName'; it selects the simulator or device. " +
                "Without it XCUITest reports a failure that reads like an Xcode problem.");

        return options;
    }

    /// <inheritdoc />
    public Task<IMobileDriver> CreateSessionAsync(MobileRunSettings settings)
    {
        AppiumOptions options = BuildOptions(settings);
        Uri endpoint = MobileEndpoint.Resolve(settings);

        // See the equivalent comment in AndroidDriverFactory: the description is logged, never
        // the resolved URI, because on a grid the URI contains the access key.
        TestLog.Info(
            $"Opening an iOS session against {MobileEndpoint.Describe(settings)} " +
            $"(target: {settings.Target}, device: '{settings.Device.DeviceName}').");

        IOSDriver driver = new(endpoint, options, settings.SessionStartup);

        return Task.FromResult<IMobileDriver>(
            new AppiumMobileDriver(driver, Platform.IOS, settings.Target));
    }
}
