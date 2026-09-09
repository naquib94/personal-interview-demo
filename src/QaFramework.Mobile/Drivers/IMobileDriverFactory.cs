using OpenQA.Selenium.Appium;
using QaFramework.Mobile.Capabilities;

namespace QaFramework.Mobile.Drivers;

/// <summary>
/// Creates a session for one platform.
/// </summary>
/// <remarks>
/// The split between <see cref="BuildOptions"/> and <see cref="CreateSessionAsync"/> is the most
/// important testability decision in this assembly, so it is worth being explicit about it.
/// <para>
/// Capability assembly is where mobile configuration bugs actually live: a missing
/// <c>automationName</c>, an <c>app</c> path that was never substituted by the pipeline, a
/// vendor prefix dropped from a passthrough capability. Every one of those is a pure function of
/// configuration - and every one of them is normally only discoverable by starting a session,
/// which needs a device.
/// </para>
/// <para>
/// Separating the two means <see cref="BuildOptions"/> can be asserted on in an ordinary unit
/// test, with no server, no device and no network, while <see cref="CreateSessionAsync"/> keeps
/// the part that genuinely cannot be tested that way and does nothing else. The alternative -
/// one <c>CreateDriver()</c> method that builds options and opens a session together - is the
/// common shape, and it is the reason capability regressions in most mobile suites are found by
/// a broken pipeline rather than by a test.
/// </para>
/// </remarks>
public interface IMobileDriverFactory
{
    /// <summary>The platform this factory serves. Used by <see cref="MobileDriverFactoryResolver"/>.</summary>
    Platform Platform { get; }

    /// <summary>
    /// Translates configuration into Appium capabilities. Pure: no I/O, no session, no device.
    /// </summary>
    AppiumOptions BuildOptions(MobileRunSettings settings);

    /// <summary>
    /// Opens a real session against a real endpoint. Everything that needs hardware is here and
    /// only here.
    /// </summary>
    Task<IMobileDriver> CreateSessionAsync(MobileRunSettings settings);
}
