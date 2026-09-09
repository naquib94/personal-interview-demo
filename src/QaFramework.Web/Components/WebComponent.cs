using Microsoft.Playwright;
using QaFramework.Core.Configuration;
using QaFramework.Web.Setup;

namespace QaFramework.Web.Components;

/// <summary>
/// Base class for every UI component.
/// </summary>
/// <remarks>
/// <para><b>What a component is, and why it is not a page object.</b> A page object models a
/// screen: which controls exist and where. A component models a <i>control archetype</i> - a
/// button, a text input, a data grid row - and owns the behaviour of that archetype: how to
/// wait for it, how to operate it, how to assert on it, and how to describe itself when it
/// fails.</para>
///
/// <para>The consequence is that behaviour is written once per <i>kind</i> of control rather
/// than once per screen. Pages become declarative manifests with no logic in them at all, and
/// a change to how the application's buttons behave is a change to one file. This is the single
/// most effective structural idea I have seen in a large UI suite, and it is the reason a suite
/// can grow to hundreds of screens without the maintenance cost growing with it.</para>
///
/// <para><b>Why <see cref="Locator"/> is a property, not a field.</b> It rebuilds on every
/// access. A field captured at construction goes stale the moment the page navigates or
/// re-renders, producing intermittent <c>ElementHandle is detached</c> failures that are
/// invariably blamed on the application. Rebuilding is effectively free - Playwright locators
/// are lazy descriptions, not element handles.</para>
///
/// <para><b>On <see cref="Description"/>.</b> Every component names itself in human terms
/// ("the Place order button"), so failures read as
/// <c>Failed to click the Place order button</c> rather than
/// <c>Timeout 5000ms exceeded waiting for locator("[data-testid='new-order-submit-button']")</c>.
/// The second message requires the reader to know the codebase; the first does not.</para>
/// </remarks>
public abstract class WebComponent
{
    protected WebComponent(WebTestContext context, string selector, string description)
    {
        Context = context;
        Selector = selector;
        Description = description;
    }

    protected WebTestContext Context { get; }

    protected string Selector { get; }

    protected TimeoutSettings Timeouts => Context.Timeouts;

    /// <summary>Human-readable name, used in every log line and failure message.</summary>
    public string Description { get; }

    /// <summary>
    /// Rebuilt on every access, and resolved from <c>Context.App</c> rather than the raw page -
    /// see <see cref="WebTestContext.App"/> for why that indirection exists.
    /// </summary>
    public ILocator Locator => Context.App.Locator(Selector);

    /// <summary>Asserts the component is visible.</summary>
    public Task ShouldBeVisibleAsync() =>
        Assertions.Expect(Locator).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = Timeouts.ElementMs });

    /// <summary>
    /// Asserts the component is not visible.
    /// </summary>
    /// <remarks>
    /// Uses the deliberately shorter <see cref="TimeoutSettings.Absence"/> budget. Proving
    /// absence always costs the full timeout when it passes, so a five-second absence check run
    /// twenty times adds a hundred seconds to a suite for no diagnostic value. Where the
    /// application offers a positive signal instead - an "empty state" message - asserting on
    /// that is both instant and a stronger assertion.
    /// </remarks>
    public Task ShouldNotBeVisibleAsync() =>
        Assertions.Expect(Locator).ToBeHiddenAsync(
            new LocatorAssertionsToBeHiddenOptions { Timeout = Timeouts.AbsenceMs });

    /// <summary>
    /// Whether the component is currently present. Returns a bool rather than asserting, for
    /// the cases where a scenario legitimately branches on application state.
    /// </summary>
    public async Task<bool> IsVisibleAsync()
    {
        try { return await Locator.IsVisibleAsync(); }
        // A locator that resolves to nothing is not an error here - it is the answer.
        catch (PlaywrightException) { return false; }
    }

    /// <summary>
    /// Wraps an interaction so that a Playwright failure is re-thrown naming the component.
    /// </summary>
    /// <remarks>
    /// The original exception is kept as the inner exception, so the stack trace and
    /// Playwright's own detail survive. Discarding it - re-throwing only a formatted string, a
    /// pattern I have had to debug more than once - loses exactly the information needed when
    /// the cause is not the obvious one.
    /// </remarks>
    protected async Task PerformAsync(string action, Func<Task> interaction)
    {
        try
        {
            await interaction();
            Core.Logging.TestLog.Step($"{action} {Description}.");
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            throw new Core.Logging.AutomationFailureException(
                $"Failed to {action.ToLowerInvariant()} {Description} " +
                $"(selector '{Selector}'){Environment.NewLine}" +
                $"Current URL: {Context.ActivePage.Url}{Environment.NewLine}" +
                $"Underlying error: {ex.Message}", ex);
        }
    }
}
