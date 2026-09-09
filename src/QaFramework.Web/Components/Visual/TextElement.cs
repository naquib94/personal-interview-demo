using Microsoft.Playwright;
using QaFramework.Web.Setup;

namespace QaFramework.Web.Components.Visual;

/// <summary>
/// A read-only piece of text - a heading, a metric, a status.
/// </summary>
/// <remarks>
/// Small by design. The value is not the four methods; it is that a page declares
/// <c>public TextElement Balance { get; }</c> and therefore carries no selectors, no waits and
/// no assertions of its own. A page object with logic in it is a page object that will
/// eventually contain a business rule, and business rules in page objects are invisible to
/// anyone reading the feature file.
/// </remarks>
public sealed class TextElement(WebTestContext context, string testId, string description)
    : WebComponent(context, $"[data-testid='{testId}']", description)
{
    public Task<string> GetTextAsync() => Locator.InnerTextAsync();

    public Task ShouldHaveTextAsync(string expected) =>
        Assertions.Expect(Locator).ToHaveTextAsync(expected,
            new LocatorAssertionsToHaveTextOptions { Timeout = Timeouts.ElementMs });

    public Task ShouldContainTextAsync(string expected) =>
        Assertions.Expect(Locator).ToContainTextAsync(expected,
            new LocatorAssertionsToContainTextOptions { Timeout = Timeouts.ElementMs });

    /// <summary>
    /// Asserts the text is not the placeholder the application renders before data arrives.
    /// </summary>
    /// <remarks>
    /// The demo shows an em dash until a value loads. Without this, a test asserting "the
    /// balance is displayed" passes against an unloaded page - a false pass that hides a broken
    /// endpoint completely.
    /// </remarks>
    public Task ShouldBePopulatedAsync() =>
        Assertions.Expect(Locator).Not.ToHaveTextAsync("\u2014",
            new LocatorAssertionsToHaveTextOptions { Timeout = Timeouts.PageLoadMs });
}
