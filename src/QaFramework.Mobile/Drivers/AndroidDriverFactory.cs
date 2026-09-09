using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Appium.Android;
using QaFramework.Mobile.Capabilities;
using QaFramework.Core.Logging;

namespace QaFramework.Mobile.Drivers;

/// <summary>
/// Builds Android capabilities, and - separately - opens Android sessions.
/// </summary>
/// <remarks>
/// See <see cref="IMobileDriverFactory"/> for why those two responsibilities are two methods.
/// <see cref="BuildOptions"/> is exercised by unit tests in CI; <see cref="CreateSessionAsync"/>
/// is not and cannot be, because it needs a server and a device.
/// </remarks>
public sealed class AndroidDriverFactory : IMobileDriverFactory
{
    /// <inheritdoc />
    public Platform Platform => Platform.Android;

    /// <inheritdoc />
    public AppiumOptions BuildOptions(MobileRunSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Platform != Platform.Android)
            throw new InvalidOperationException(
                $"{nameof(AndroidDriverFactory)} was given settings for platform " +
                $"'{settings.Platform}'. This almost always means the factory was resolved by " +
                "hand instead of through MobileDriverFactoryResolver.");

        AppiumOptions options = MobileOptionsBuilder.BuildCommon(settings);
        DeviceCapabilities device = settings.Device;

        // appPackage/appActivity let a session attach to an already-installed build instead of
        // reinstalling. They are only meaningful when no `app` path was supplied - if both are
        // present Appium installs the app and then launches the named activity, which is valid
        // but slower, so they are sent regardless and the choice is left to configuration.
        if (!string.IsNullOrWhiteSpace(device.AppPackage))
            options.AddAdditionalAppiumOption("appium:appPackage", device.AppPackage);

        if (!string.IsNullOrWhiteSpace(device.AppActivity))
            options.AddAdditionalAppiumOption("appium:appActivity", device.AppActivity);

        if (string.IsNullOrWhiteSpace(device.App)
            && string.IsNullOrWhiteSpace(device.AppPackage))
            throw new InvalidOperationException(
                "Android capabilities specify neither 'app' (a path to an .apk) nor 'appPackage' " +
                "(an already-installed build). A session started with neither has nothing to " +
                "drive, so this fails during capability assembly rather than after the session " +
                "has been paid for.");

        return options;
    }

    /// <inheritdoc />
    public Task<IMobileDriver> CreateSessionAsync(MobileRunSettings settings)
    {
        AppiumOptions options = BuildOptions(settings);
        Uri endpoint = MobileEndpoint.Resolve(settings);

        // Note what is logged: the description, not the resolved URI. On a grid the URI carries
        // the access key, and a log line is a place secrets go to be published.
        TestLog.Info(
            $"Opening an Android session against {MobileEndpoint.Describe(settings)} " +
            $"(target: {settings.Target}, device: '{settings.Device.DeviceName}').");

        // The Appium client is synchronous and its constructor performs the session handshake.
        // Wrapped in a completed task rather than Task.Run: moving a blocking call to the thread
        // pool would hide the blocking without removing it, and would make a session failure
        // surface as an AggregateException from an unrelated thread.
        AndroidDriver driver = new(endpoint, options, settings.SessionStartup);

        return Task.FromResult<IMobileDriver>(
            new AppiumMobileDriver(driver, Platform.Android, settings.Target));
    }
}
