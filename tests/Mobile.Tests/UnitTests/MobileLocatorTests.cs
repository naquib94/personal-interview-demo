using AwesomeAssertions;
using NUnit.Framework;
using QaFramework.Mobile.Drivers;
using QaFramework.Mobile.Elements;

namespace Mobile.Tests.UnitTests;

/// <summary>
/// Tests for the platform pair.
/// </summary>
/// <remarks>
/// Small tests for a small type, but they cover the failure this abstraction exists to prevent:
/// an element that has no selector on the platform being run. On an Android-only suite that
/// mistake is invisible until the day iOS is added, at which point it appears everywhere at once.
/// </remarks>
[TestFixture]
public sealed class MobileLocatorTests
{
    [Test]
    public void A_shared_locator_serves_both_platforms()
    {
        MobileLocator locator = MobileLocator.Shared("the sign-in button", "login-submit-button");

        locator.For(Platform.Android).Should().Be("login-submit-button");
        locator.For(Platform.IOS).Should().Be("login-submit-button");
    }

    [Test]
    public void A_paired_locator_resolves_per_platform()
    {
        MobileLocator locator = new("the account heading", "account-heading", "accountHeading");

        locator.For(Platform.Android).Should().Be("account-heading");
        locator.For(Platform.IOS).Should().Be("accountHeading");
    }

    [Test]
    public void A_locator_with_no_selector_for_the_platform_fails_with_an_explanation()
    {
        MobileLocator androidOnly = new("the Android-only widget", "widget", string.Empty);

        androidOnly.SupportsPlatform(Platform.IOS).Should().BeFalse();

        Action resolve = () => androidOnly.For(Platform.IOS);

        // The message matters more than the exception type. An empty selector sent to a driver
        // produces an "invalid selector" error from the far end, which sends the reader looking
        // at the application rather than at the locator.
        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*no IOS selector*");
    }

    [Test]
    public void A_locator_describes_itself_in_prose()
    {
        MobileLocator locator = MobileLocator.Shared("the order history rows", "order-history-grid-row");

        // Used verbatim in timeout messages, which is why it is prose and not a selector:
        // "Timed out waiting for the order history rows to be displayed" is diagnosable from a
        // CI log without opening the code.
        locator.ToString().Should().Be("the order history rows");
    }

    [Test]
    public void Locators_with_the_same_definition_are_equal()
    {
        // A record rather than a class, so that a locator can be compared and used as a
        // dictionary key without anybody writing an Equals override by hand.
        MobileLocator first = MobileLocator.Shared("the heading", "login-heading");
        MobileLocator second = MobileLocator.Shared("the heading", "login-heading");

        first.Should().Be(second);
    }
}
