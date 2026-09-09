using QaFramework.Mobile.Drivers;

namespace QaFramework.Mobile.Elements;

/// <summary>
/// A single element, described once for both platforms.
/// </summary>
/// <remarks>
/// The platform pair is the point of this type. An Android-only suite can get away with a bare
/// selector string, and that is exactly the shortcut that makes adding iOS later feel like a
/// rewrite: every locator turns into an <c>if (platform == ...)</c> at the call site, or worse,
/// into a duplicate screen object per platform that then drifts.
/// <para>
/// Carrying both selectors in one immutable value means a screen object is written once and the
/// platform is resolved at the last possible moment, inside the driver. Two selectors are
/// usually identical anyway when <see cref="LocatorStrategy.AccessibilityId"/> is used, hence
/// <see cref="Shared"/>, which keeps the common case free of noise.
/// </para>
/// <para>
/// Alternative considered and rejected: a dictionary keyed by platform. More flexible, but it
/// loses compile-time knowledge of which platforms exist, and every read site has to handle a
/// missing key. A record with two fields makes "this element has no iOS equivalent" an explicit,
/// documented state instead of an absent dictionary entry.
/// </para>
/// <para>
/// <see cref="Description"/> is required rather than optional because it is what appears in a
/// timeout message. "Timed out waiting for the order history grid" is diagnosable from a CI log;
/// "Timed out waiting for //*[@resource-id='...']" sends someone to read the code first.
/// </para>
/// </remarks>
/// <param name="Description">Human-readable name, phrased to read well after "waiting for".</param>
/// <param name="AndroidSelector">The Android selector, or an empty string if unsupported there.</param>
/// <param name="IOSSelector">The iOS selector, or an empty string if unsupported there.</param>
/// <param name="Strategy">How to resolve the selector. See <see cref="LocatorStrategy"/>.</param>
public sealed record MobileLocator(
    string Description,
    string AndroidSelector,
    string IOSSelector,
    LocatorStrategy Strategy = LocatorStrategy.AccessibilityId)
{
    /// <summary>
    /// A locator whose selector is the same on both platforms.
    /// </summary>
    /// <remarks>
    /// The normal case when the application under test exposes accessibility identifiers, which
    /// is what this framework asks of the teams it works with. The demo application uses
    /// <c>data-testid</c> values such as <c>login-submit-button</c>; a native build of the same
    /// product would expose the identical strings as accessibility identifiers, so the locators
    /// below intentionally reuse them and the naming convention stays consistent across the web,
    /// API and mobile suites.
    /// </remarks>
    public static MobileLocator Shared(
        string description,
        string selector,
        LocatorStrategy strategy = LocatorStrategy.AccessibilityId) =>
        new(description, selector, selector, strategy);

    /// <summary>
    /// The selector to use on <paramref name="platform"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the element has no selector for that platform. Failing loudly here beats
    /// sending an empty selector to the driver, which produces an unrelated "invalid selector"
    /// error from the far end and a wasted debugging session.
    /// </exception>
    public string For(Platform platform)
    {
        string selector = platform switch
        {
            Platform.Android => AndroidSelector,
            Platform.IOS => IOSSelector,
            _ => throw new InvalidOperationException($"Unhandled platform '{platform}'.")
        };

        if (string.IsNullOrWhiteSpace(selector))
            throw new InvalidOperationException(
                $"The locator '{Description}' has no {platform} selector. Either the element does " +
                "not exist on that platform - in which case the scenario should not be running " +
                "there - or the locator definition is incomplete.");

        return selector;
    }

    /// <summary>Whether this locator can be resolved on the given platform at all.</summary>
    public bool SupportsPlatform(Platform platform) => platform switch
    {
        Platform.Android => !string.IsNullOrWhiteSpace(AndroidSelector),
        Platform.IOS => !string.IsNullOrWhiteSpace(IOSSelector),
        _ => false
    };

    /// <summary>Used verbatim in wait and failure messages, so it stays short and readable.</summary>
    public override string ToString() => Description;
}
