namespace QaFramework.Mobile.Elements;

/// <summary>
/// How a <see cref="MobileLocator"/> should be resolved on the device.
/// </summary>
/// <remarks>
/// The order of the members is the recommended order of preference, and it is worth stating why
/// because the wrong choice here is the single largest source of flake in a mobile suite:
/// <list type="number">
/// <item><b><see cref="AccessibilityId"/> first.</b> It maps to <c>content-desc</c> on Android
/// and to the accessibility identifier on iOS, so one string serves both platforms. It is set
/// deliberately by developers, it is stable across layout changes, and asking for it improves
/// the product's accessibility as a side effect - the rare case where the testability argument
/// and the user-facing argument point the same way.</item>
/// <item><b><see cref="Id"/></b> when an accessibility identifier is genuinely unavailable.
/// Stable, but Android resource ids and iOS identifiers rarely match, so it costs a platform
/// pair.</item>
/// <item><b><see cref="AndroidUiAutomator"/> and <see cref="IosClassChain"/></b> for the cases
/// native queries handle far better than anything else, principally scrolling a long list into
/// view. They are fast because the query runs on the device instead of round-tripping every
/// candidate over HTTP, but they are platform-specific by construction.</item>
/// <item><b><see cref="XPath"/> last, and reluctantly.</b> It is the slowest strategy on both
/// platforms because the driver serialises the whole page source to evaluate it, and it couples
/// the test to the view hierarchy, so an innocuous re-layout breaks locators that were never
/// about layout. It stays in the enum because sometimes there is no alternative, and pretending
/// otherwise just means someone writes an XPath in a string literal somewhere worse.</item>
/// </list>
/// </remarks>
public enum LocatorStrategy
{
    /// <summary>Android <c>content-desc</c> / iOS accessibility identifier. Preferred.</summary>
    AccessibilityId,

    /// <summary>Android resource id / iOS element identifier.</summary>
    Id,

    /// <summary>An XPath over the native view hierarchy. Slow and brittle; last resort.</summary>
    XPath,

    /// <summary>An Android UiAutomator query, evaluated on the device. Android only.</summary>
    AndroidUiAutomator,

    /// <summary>An iOS class chain query, evaluated on the device. iOS only.</summary>
    IosClassChain
}
