using OpenQA.Selenium;
using OpenQA.Selenium.Appium;
using QaFramework.Mobile.Elements;

namespace QaFramework.Mobile.Drivers;

/// <summary>
/// Turns a <see cref="MobileLocator"/> into an Appium <see cref="By"/>.
/// </summary>
/// <remarks>
/// This translation lives in <c>Drivers</c> rather than on <see cref="MobileLocator"/> itself,
/// and that is deliberate. A locator is a description of an element; a <see cref="By"/> is an
/// Appium implementation detail. Keeping the conversion here means <c>Elements</c> and
/// <c>Screens</c> - the parts an engineer writing tests actually touches - carry no reference to
/// Appium at all, which is what lets <see cref="Simulation.SimulatedMobileDriver"/> exist without
/// pretending to produce WebDriver elements.
/// </remarks>
internal static class LocatorTranslator
{
    internal static By ToBy(MobileLocator locator, Platform platform)
    {
        string selector = locator.For(platform);

        // The two native-query strategies are rejected on the wrong platform rather than
        // attempted. Appium's own error for a UiAutomator query sent to XCUITest names the
        // protocol, not the locator, and would send the reader looking in the wrong place.
        return locator.Strategy switch
        {
            LocatorStrategy.AccessibilityId => MobileBy.AccessibilityId(selector),

            // Plain Selenium locators for id and XPath. The Appium client offers wrappers, but
            // these two map to strategies the server implements natively for both platforms, and
            // using the Selenium types keeps this file honest about which strategies are
            // genuinely Appium-specific.
            LocatorStrategy.Id => By.Id(selector),
            LocatorStrategy.XPath => By.XPath(selector),

            LocatorStrategy.AndroidUiAutomator when platform == Platform.Android =>
                MobileBy.AndroidUIAutomator(selector),
            LocatorStrategy.IosClassChain when platform == Platform.IOS =>
                MobileBy.IosClassChain(selector),
            LocatorStrategy.AndroidUiAutomator or LocatorStrategy.IosClassChain =>
                throw new InvalidOperationException(
                    $"The locator '{locator.Description}' uses the platform-specific strategy " +
                    $"'{locator.Strategy}', which cannot be evaluated on {platform}. Give the " +
                    "element a platform-appropriate locator, or restrict the scenario to the " +
                    "platform that supports it."),
            _ => throw new InvalidOperationException(
                $"Unhandled locator strategy '{locator.Strategy}'.")
        };
    }
}
