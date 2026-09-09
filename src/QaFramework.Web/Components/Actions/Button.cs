using QaFramework.Web.Setup;
using QaFramework.Web.Synchronisation;

namespace QaFramework.Web.Components.Actions;

/// <summary>
/// A button or link.
/// </summary>
/// <remarks>
/// <see cref="ClickAsync"/> does three things, and the second and third are the reason this
/// class exists rather than callers writing <c>page.Locator(...).ClickAsync()</c>:
/// <list type="number">
/// <item>Waits for the button to be visible and enabled.</item>
/// <item>Clicks it.</item>
/// <item><b>Waits for the application to become idle again.</b></item>
/// </list>
/// Step 3 is what removes the sleeps. A test that clicks and immediately asserts is racing the
/// application; a test that clicks through this component cannot, because the wait is part of
/// the click. Putting it here rather than asking every caller to remember it is the difference
/// between a convention and a guarantee.
/// </remarks>
public sealed class Button(WebTestContext context, string testId, string description)
    : WebComponent(context, $"[data-testid='{testId}']", description)
{
    public Task ClickAsync() => PerformAsync("Click", async () =>
    {
        await Locator.ClickAsync(new Microsoft.Playwright.LocatorClickOptions
        {
            Timeout = Timeouts.ElementMs
        });

        await Context.WaitUntilIdleAsync();
    });

    /// <summary>
    /// Clicks and waits for the API call it triggers, returning that response.
    /// </summary>
    /// <remarks>
    /// For the cases where the UI outcome is not the whole assertion - a test that needs to
    /// confirm the browser actually sent a request, or to read the order reference from the
    /// response rather than scraping it off the screen. Scraping identifiers out of prose is a
    /// common source of brittleness.
    /// </remarks>
    public Task<Microsoft.Playwright.IResponse> ClickAndWaitForResponseAsync(string urlPattern) =>
        Context.WaitForResponseWhileAsync(() => Locator.ClickAsync(), urlPattern);

    public Task ShouldBeEnabledAsync() =>
        Microsoft.Playwright.Assertions.Expect(Locator).ToBeEnabledAsync(
            new Microsoft.Playwright.LocatorAssertionsToBeEnabledOptions { Timeout = Timeouts.ElementMs });

    public Task ShouldBeDisabledAsync() =>
        Microsoft.Playwright.Assertions.Expect(Locator).ToBeDisabledAsync(
            new Microsoft.Playwright.LocatorAssertionsToBeDisabledOptions { Timeout = Timeouts.ElementMs });
}
