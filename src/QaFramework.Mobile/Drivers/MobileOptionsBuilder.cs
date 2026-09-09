using OpenQA.Selenium.Appium;
using QaFramework.Mobile.Capabilities;

namespace QaFramework.Mobile.Drivers;

/// <summary>
/// The capability translation both platform factories share.
/// </summary>
/// <remarks>
/// Extracted rather than duplicated because the platform-independent half of a capability set is
/// most of it, and the two copies would drift - which in this area means one platform quietly
/// stops honouring a timeout or a passthrough capability.
/// <para>
/// The vendor prefix handling deserves a note: Appium's W3C protocol requires non-standard
/// capabilities to carry an <c>appium:</c> prefix, and omitting it produces a rejection whose
/// message names the protocol rather than the offending capability. Applying the prefix in one
/// place makes that impossible to get wrong, and keys that already carry a prefix are left alone.
/// </para>
/// </remarks>
internal static class MobileOptionsBuilder
{
    /// <summary>Capabilities that are standard W3C and must not be prefixed.</summary>
    private static readonly HashSet<string> StandardCapabilities =
        new(StringComparer.OrdinalIgnoreCase) { "platformName", "browserName", "browserVersion" };

    internal static AppiumOptions BuildCommon(MobileRunSettings settings)
    {
        DeviceCapabilities device = settings.Device;

        AppiumOptions options = new()
        {
            PlatformName = device.PlatformName,
            AutomationName = device.AutomationName
        };

        // deviceName, platformVersion and app are assigned through the client's own properties
        // rather than as additional options. The client rejects the latter with "there is already
        // an option for the appium:deviceName capability", which is a good guard and worth
        // honouring rather than working around.
        //
        // Empty values are skipped rather than sent as empty strings. An empty platformVersion
        // means "any attached device will do"; sending "" instead means "match a device whose OS
        // version is the empty string", which matches nothing and fails obscurely.
        if (!string.IsNullOrWhiteSpace(device.DeviceName))
            options.DeviceName = device.DeviceName;

        if (!string.IsNullOrWhiteSpace(device.PlatformVersion))
            options.PlatformVersion = device.PlatformVersion;

        if (!string.IsNullOrWhiteSpace(device.App))
            options.App = device.App;

        options.AddAdditionalAppiumOption("appium:noReset", device.NoReset);
        options.AddAdditionalAppiumOption("appium:fullReset", device.FullReset);
        options.AddAdditionalAppiumOption("appium:newCommandTimeout", device.NewCommandTimeoutSeconds);

        foreach (KeyValuePair<string, string> capability in device.Additional)
            AddIfPresent(options, Qualify(capability.Key), capability.Value);

        // Grid session metadata last, so a grid's own labelling requirements can override a
        // generic default rather than being overridden by one.
        if (settings.Target == RunTarget.CloudGrid && settings.CloudGrid is not null)
        {
            foreach (KeyValuePair<string, string> entry in settings.CloudGrid.SessionMetadata)
                AddIfPresent(options, Qualify(entry.Key), entry.Value);
        }

        return options;
    }

    /// <summary>
    /// Adds the <c>appium:</c> prefix where the protocol requires one.
    /// </summary>
    internal static string Qualify(string key) =>
        key.Contains(':', StringComparison.Ordinal) || StandardCapabilities.Contains(key)
            ? key
            : $"appium:{key}";

    private static void AddIfPresent(AppiumOptions options, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            options.AddAdditionalAppiumOption(key, value);
    }
}
