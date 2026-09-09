using Microsoft.Playwright;
using QaFramework.Web.Setup;
using TradingDemo.AppModel.Setup;

namespace Ui.Tests.StepDefinitions;

/// <summary>
/// Establishes a browser session without driving the sign-in form.
/// </summary>
/// <remarks>
/// <para><b>This is the most consequential step definition in the UI suite.</b> It obtains a
/// token over HTTP and injects it into the browser's session storage, so a scenario about order
/// placement starts already authenticated.</para>
///
/// <para>Three reasons this is the right trade, not a shortcut:</para>
/// <list type="number">
/// <item><b>Speed.</b> One HTTP call instead of a page load, two field entries, a form
/// submission and a navigation. Across a suite that is the difference between a run people wait
/// for and a run people skip.</item>
/// <item><b>Failure attribution.</b> A defect in the sign-in page cannot fail an
/// order-placement scenario, so a red test points at the screen that is actually broken.</item>
/// <item><b>It removes duplicated coverage.</b> The sign-in form is tested once, properly, in
/// Login.feature. Re-testing it as the preamble to thirty other scenarios adds no
/// information.</item>
/// </list>
///
/// <para><b>The honest cost.</b> It couples the test suite to an implementation detail: the
/// storage key the application uses for its token. If that key changes, this step breaks and
/// the failure will not be obvious. That is a real and accepted trade-off, mitigated by the
/// assertion at the end of <see cref="GivenTheActiveTraderIsSignedInThroughTheApi"/>, which
/// verifies the injected session actually works rather than assuming it. Without that check
/// this step would fail silently and every scenario would fail on its first assertion for a
/// reason unrelated to the test.</para>
/// </remarks>
[Binding]
public sealed class SessionSteps(ScenarioSession session, WebTestContext context)
{
    /// <summary>
    /// Must match the key used by <c>wwwroot/app.js</c>. See the class remarks on the coupling
    /// this introduces and why it is accepted.
    /// </summary>
    private const string TokenStorageKey = "demo.token";
    private const string UsernameStorageKey = "demo.username";

    [Given("the active trader is signed in through the API")]
    public async Task GivenTheActiveTraderIsSignedInThroughTheApi()
    {
        string token = await session.SignInAsync("ActiveTrader");

        // The page must be on the application's origin before session storage can be written -
        // storage is origin-scoped, and writing it on about:blank silently does nothing.
        await context.NavigateToAsync("index.html");

        await context.ActivePage.EvaluateAsync(
            """
            ([key, token, userKey, username]) => {
                sessionStorage.setItem(key, token);
                sessionStorage.setItem(userKey, username);
            }
            """,
            new[] { TokenStorageKey, token, UsernameStorageKey, session.CurrentUser!.Username });

        context.RecordSignIn(session.CurrentUser!, token);

        // Verifying the injected session works. This is what turns a silent coupling into a
        // loud one: if the storage key ever changes, this assertion fails here with a clear
        // message instead of every downstream scenario failing for an unrelated-looking reason.
        await context.NavigateToAsync("account.html");
        await Assertions.Expect(context.App.Locator("[data-testid='account-number-text']"))
            .Not.ToHaveTextAsync("\u2014", new LocatorAssertionsToHaveTextOptions
            {
                Timeout = context.Timeouts.PageLoadMs
            });
    }
}
