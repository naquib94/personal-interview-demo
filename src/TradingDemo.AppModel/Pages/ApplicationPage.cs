using QaFramework.Web.Components.Actions;
using QaFramework.Web.Components.Visual;
using QaFramework.Web.Setup;
using QaFramework.Web.Synchronisation;

namespace TradingDemo.AppModel.Pages;

/// <summary>
/// Base for every page inside the signed-in application.
/// </summary>
/// <remarks>
/// <para>Holds only what genuinely appears on every signed-in screen: the navigation bar, the
/// shared alert region, and the route to navigate to.</para>
///
/// <para><b>Why composition over a deep inheritance chain.</b> The natural-looking alternative
/// is <c>NavigationPage -&gt; AlertPage -&gt; AccountPage</c>, adding a layer per shared
/// concern. It works until two screens need overlapping-but-different subsets, and then the
/// hierarchy has to be reshaped and every page moves. One shallow base holding components, with
/// concrete pages adding their own, does not have that failure mode. This is a specific lesson
/// from reading a suite that had gone three levels deep and could not add a fourth.</para>
///
/// <para>Note there is not a single assertion or piece of logic in the page classes below.
/// Everything a page can do lives in the components it declares.</para>
/// </remarks>
public abstract class ApplicationPage(WebTestContext context, string route)
{
    protected WebTestContext Context { get; } = context;

    /// <summary>The path this page lives at, relative to the base URL.</summary>
    public string Route { get; } = route;

    /// <summary>The shared alert region. Present on every screen; used by most of them.</summary>
    public AlertMessage Alert { get; } = new(context);

    public Button AccountNavLink { get; } = new(context, "nav-account-link", "the Account navigation link");
    public Button MarketNavLink { get; } = new(context, "nav-market-link", "the Market navigation link");
    public Button NewOrderNavLink { get; } = new(context, "nav-new-order-link", "the New Order navigation link");
    public Button OrderHistoryNavLink { get; } = new(context, "nav-order-history-link", "the Order History navigation link");
    public Button SignOutNavLink { get; } = new(context, "nav-sign-out-link", "the Sign out link");

    /// <summary>
    /// Navigates directly to this page and waits for it to settle.
    /// </summary>
    /// <remarks>
    /// Direct navigation rather than clicking through the menu. Both are legitimate, and the
    /// choice is not arbitrary: a test about order history should not fail because the
    /// navigation bar is broken. Menu navigation deserves its own test, once, rather than being
    /// re-tested implicitly as the preamble to every other scenario.
    /// </remarks>
    public async Task OpenAsync()
    {
        await Context.NavigateToAsync(Route);
        await Context.WaitForPageReadyAsync();
    }

    /// <summary>Asserts the page has finished loading and is showing its own content.</summary>
    public abstract Task ShouldBeDisplayedAsync();
}
