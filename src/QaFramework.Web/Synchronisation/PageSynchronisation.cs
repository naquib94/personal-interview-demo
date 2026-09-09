using Microsoft.Playwright;
using QaFramework.Web.Setup;

namespace QaFramework.Web.Synchronisation;

/// <summary>
/// Application-level readiness checks.
/// </summary>
/// <remarks>
/// <para><b>The problem this solves.</b> Playwright's auto-waiting handles "is this element
/// there and clickable". It cannot know that the application has fired a request and is about
/// to replace the grid you were reading. That gap is the single largest source of flaky UI
/// tests, and the usual response - a sprinkling of <c>Thread.Sleep(500)</c> - makes the suite
/// slower and only slightly less flaky.</para>
///
/// <para><b>The fix is a contract with the application, not a cleverer wait.</b> The demo app
/// exposes exactly one busy indicator: an element with <c>data-testid="app-busy"</c> that
/// carries the <c>active</c> class for the whole duration of any in-flight request (see
/// <c>wwwroot/app.js</c>). Waiting for that one element to settle is deterministic and costs
/// nothing when the app is already idle.</para>
///
/// <para>Negotiating that contract is a QA engineering activity. It is a small ask of a
/// developer - most SPAs already track in-flight requests for a loading spinner - and it
/// removes more flakiness than any amount of retry logic. Where an application refuses to
/// provide one, <see cref="WaitForResponseWhileAsync"/> is the fallback: synchronise on the
/// network instead.</para>
/// </remarks>
public static class PageSynchronisation
{
    private const string BusyIndicator = "[data-testid='app-busy']";

    /// <summary>
    /// Waits until the application reports itself idle.
    /// </summary>
    /// <remarks>
    /// A missing indicator is treated as "idle" rather than as an error. Not every page needs
    /// one, and failing here would make the helper unusable on the pages that do not - which
    /// in turn would push callers back towards sleeps.
    /// </remarks>
    public static async Task WaitUntilIdleAsync(this WebTestContext context)
    {
        ILocator busy = context.App.Locator($"{BusyIndicator}.active");

        try
        {
            await busy.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Detached,
                Timeout = context.Timeouts.PageLoadMs
            });
        }
        catch (PlaywrightException)
        {
            // The class is toggled rather than the element being removed, so "detached" can
            // legitimately never happen. Fall through to the class-based check below, which is
            // the reliable signal.
        }

        await Assertions.Expect(context.App.Locator(BusyIndicator))
            .Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("active"),
                new LocatorAssertionsToHaveClassOptions { Timeout = context.Timeouts.PageLoadMs });
    }

    /// <summary>
    /// Waits for the DOM to be ready and then for the application to be idle.
    /// </summary>
    /// <remarks>
    /// Note the use of <c>DOMContentLoaded</c> rather than <c>NetworkIdle</c>. Playwright
    /// itself discourages <c>NetworkIdle</c>, and for good reason: an application with polling,
    /// analytics or a websocket never reaches network idle, so the wait always runs to its full
    /// timeout and then either fails or masks a genuine problem. The application's own busy
    /// signal is both faster and more accurate.
    /// </remarks>
    public static async Task WaitForPageReadyAsync(this WebTestContext context)
    {
        await context.ActivePage.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await context.WaitUntilIdleAsync();
    }

    /// <summary>
    /// Runs an action and waits for the HTTP response it triggers.
    /// </summary>
    /// <remarks>
    /// <para>Uses <c>RunAndWaitForResponseAsync</c>, which registers the listener <i>before</i>
    /// performing the action. The obvious-looking alternative - click, then wait for the
    /// response - contains a race: a fast API can answer before the listener is attached, and
    /// the wait then times out on a request that already succeeded. That produces an
    /// intermittent failure which is very hard to attribute.</para>
    /// <para>This is the fallback for applications with no busy indicator, and the right tool
    /// when a test needs to assert on the response itself as well as the resulting UI.</para>
    /// </remarks>
    public static Task<IResponse> WaitForResponseWhileAsync(
        this WebTestContext context,
        Func<Task> action,
        string urlPattern) =>
        context.ActivePage.RunAndWaitForResponseAsync(
            action,
            response => response.Url.Contains(urlPattern, StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions { Timeout = context.Timeouts.PageLoadMs });
}
