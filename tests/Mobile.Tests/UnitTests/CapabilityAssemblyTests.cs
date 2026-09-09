using AwesomeAssertions;
using NUnit.Framework;
using OpenQA.Selenium.Appium;
using QaFramework.Mobile.Capabilities;
using QaFramework.Mobile.Drivers;

namespace Mobile.Tests.UnitTests;

/// <summary>
/// Tests for the half of the driver factories that does not need a device.
/// </summary>
/// <remarks>
/// These are the tests that justify the split described on <see cref="IMobileDriverFactory"/>.
/// Capability assembly is where mobile configuration defects actually live - a missing
/// automation name, an app path the pipeline never substituted, a passthrough capability that
/// lost its vendor prefix - and in most suites every one of those is discoverable only by
/// starting a session. Here they are ordinary assertions that run in a second on any machine.
/// </remarks>
[TestFixture]
public sealed class CapabilityAssemblyTests
{
    private static MobileRunSettings AndroidSettings(
        RunTarget target = RunTarget.LocalAppium,
        Dictionary<string, string>? additional = null) =>
        new()
        {
            Platform = Platform.Android,
            Target = target,
            Device = new DeviceCapabilities
            {
                PlatformName = "Android",
                AutomationName = "UiAutomator2",
                DeviceName = "Android Device",
                AppPackage = "com.example.tradingdemo",
                AppActivity = "com.example.tradingdemo.MainActivity",
                NewCommandTimeoutSeconds = 120,
                Additional = additional ?? []
            }
        };

    private static MobileRunSettings IosSettings() =>
        new()
        {
            Platform = Platform.IOS,
            Target = RunTarget.LocalAppium,
            Device = new DeviceCapabilities
            {
                PlatformName = "iOS",
                AutomationName = "XCUITest",
                DeviceName = "iPhone 15",
                BundleId = "com.example.tradingdemo"
            }
        };

    [Test]
    public void Android_options_carry_the_platform_and_automation_name()
    {
        AppiumOptions options = new AndroidDriverFactory().BuildOptions(AndroidSettings());

        options.PlatformName.Should().Be("Android");
        options.AutomationName.Should().Be("UiAutomator2");
    }

    [Test]
    public void Android_options_include_the_activity_for_an_installed_build()
    {
        AppiumOptions options = new AndroidDriverFactory().BuildOptions(AndroidSettings());

        options.ToDictionary().Should()
            .Contain("appium:appPackage", "com.example.tradingdemo").And
            .ContainKey("appium:appActivity");
    }

    [Test]
    public void Passthrough_capabilities_gain_the_vendor_prefix()
    {
        // The prefix is added in code because ':' is the configuration key separator, so the
        // committed JSON cannot carry it. This test is what stops that arrangement silently
        // breaking - without the prefix, Appium rejects the session with a message about the
        // W3C protocol rather than about the capability.
        AppiumOptions options = new AndroidDriverFactory()
            .BuildOptions(AndroidSettings(additional: new Dictionary<string, string>
            {
                ["autoGrantPermissions"] = "true"
            }));

        options.ToDictionary().Should().ContainKey("appium:autoGrantPermissions");
    }

    [Test]
    public void An_already_prefixed_capability_is_not_prefixed_twice()
    {
        AppiumOptions options = new AndroidDriverFactory()
            .BuildOptions(AndroidSettings(additional: new Dictionary<string, string>
            {
                ["appium:settings[waitForIdleTimeout]"] = "100"
            }));

        options.ToDictionary().Should()
            .ContainKey("appium:settings[waitForIdleTimeout]").And
            .NotContainKey("appium:appium:settings[waitForIdleTimeout]");
    }

    [Test]
    public void Android_capabilities_with_neither_app_nor_package_are_rejected()
    {
        MobileRunSettings settings = new()
        {
            Platform = Platform.Android,
            Target = RunTarget.LocalAppium,
            Device = new DeviceCapabilities
            {
                PlatformName = "Android",
                AutomationName = "UiAutomator2"
            }
        };

        Action build = () => new AndroidDriverFactory().BuildOptions(settings);

        // Failing during capability assembly is the whole point: the alternative is a session
        // that starts, costs a minute, and then has nothing to drive.
        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*neither 'app'*nor 'appPackage'*");
    }

    [Test]
    public void iOS_capabilities_require_a_device_name()
    {
        MobileRunSettings settings = new()
        {
            Platform = Platform.IOS,
            Target = RunTarget.LocalAppium,
            Device = new DeviceCapabilities
            {
                PlatformName = "iOS",
                AutomationName = "XCUITest",
                BundleId = "com.example.tradingdemo"
            }
        };

        Action build = () => new IOSDriverFactory().BuildOptions(settings);

        build.Should().Throw<InvalidOperationException>().WithMessage("*deviceName*");
    }

    [Test]
    public void iOS_options_carry_the_bundle_identifier()
    {
        AppiumOptions options = new IOSDriverFactory().BuildOptions(IosSettings());

        options.ToDictionary().Should()
            .Contain("appium:bundleId", "com.example.tradingdemo");
    }

    [Test]
    public void A_factory_rejects_settings_for_the_other_platform()
    {
        // A mistake that would otherwise produce a confusing session failure on a real device:
        // Android capabilities sent to XCUITest.
        Action build = () => new IOSDriverFactory().BuildOptions(AndroidSettings());

        build.Should().Throw<InvalidOperationException>().WithMessage("*platform 'Android'*");
    }

    [Test]
    public void Grid_session_metadata_reaches_the_capabilities()
    {
        MobileRunSettings settings = new()
        {
            Platform = Platform.Android,
            Target = RunTarget.CloudGrid,
            Device = new DeviceCapabilities
            {
                PlatformName = "Android",
                AutomationName = "UiAutomator2",
                AppPackage = "com.example.tradingdemo"
            },
            CloudGrid = new CloudGridSettings
            {
                SessionMetadata = new Dictionary<string, string> { ["buildName"] = "nightly" }
            }
        };

        AppiumOptions options = new AndroidDriverFactory().BuildOptions(settings);

        // Note that no credential is involved. Metadata is descriptive labelling; the endpoint
        // and credentials are resolved separately, from the environment only.
        options.ToDictionary().Should().Contain("appium:buildName", "nightly");
    }
}
