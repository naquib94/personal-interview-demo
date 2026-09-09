using Microsoft.Playwright;
using QaFramework.Web.Setup;

namespace QaFramework.Web.Components.Visual;

/// <summary>The kind of message an alert conveys.</summary>
public enum AlertKind { Error, Success, Warning }

/// <summary>
/// The shared alert region used for success, error and validation messages.
/// </summary>
/// <remarks>
/// The application renders one alert element with a constant <c>data-testid</c> and carries the
/// <i>kind</i> in a separate <c>data-alert-kind</c> attribute. That split is what lets a test
/// assert "an error was shown" independently of "the message said X".
/// <para>
/// Both assertions matter, and for different reasons. Asserting only the text means a change of
/// wording breaks the test for no functional reason. Asserting only the kind means a success
/// message reading "Order rejected" would pass. Asserting the kind always, and the text only
/// where the wording is itself the requirement, is the balance that survives contact with a
/// product team that edits copy.
/// </para>
/// </remarks>
public sealed class AlertMessage(WebTestContext context)
    : WebComponent(context, "[data-testid='alert-message']", "the alert message")
{
    private ILocator Text => Context.App.Locator("[data-testid='alert-text']");

    private ILocator DetailItems => Context.App.Locator("[data-testid='alert-detail-item']");

    /// <summary>Asserts an alert of the given kind is shown.</summary>
    public Task ShouldShowAsync(AlertKind kind) =>
        Assertions.Expect(Locator).ToHaveAttributeAsync(
            "data-alert-kind", kind.ToString().ToLowerInvariant(),
            new LocatorAssertionsToHaveAttributeOptions { Timeout = Timeouts.SubmitMs });

    /// <summary>
    /// Asserts the message text exactly.
    /// </summary>
    /// <remarks>
    /// Use where the wording is the requirement - a regulated disclosure, or an error a support
    /// team scripts against. For everything else prefer
    /// <see cref="ShouldShowAsync"/> plus an assertion on the resulting state, which tests the
    /// behaviour rather than the copywriting.
    /// </remarks>
    public Task ShouldHaveTextAsync(string expected) =>
        Assertions.Expect(Text).ToHaveTextAsync(expected,
            new LocatorAssertionsToHaveTextOptions { Timeout = Timeouts.SubmitMs });

    public Task ShouldContainTextAsync(string expected) =>
        Assertions.Expect(Text).ToContainTextAsync(expected,
            new LocatorAssertionsToContainTextOptions { Timeout = Timeouts.SubmitMs });

    public Task<string> GetTextAsync() => Text.InnerTextAsync();

    /// <summary>
    /// Returns the per-field validation details.
    /// </summary>
    /// <remarks>
    /// The demo API returns every field error at once rather than stopping at the first, and
    /// the UI renders them all. Asserting on the full set is what verifies that behaviour -
    /// checking only that "an error appeared" would pass even if the API regressed to
    /// reporting one error at a time, which is a real usability regression.
    /// </remarks>
    public async Task<IReadOnlyList<string>> GetDetailsAsync()
    {
        string[] details = await DetailItems.EvaluateAllAsync<string[]>(
            "items => items.map(i => i.innerText.trim())");
        return details;
    }

    public Task ShouldHaveDetailCountAsync(int expected) =>
        Assertions.Expect(DetailItems).ToHaveCountAsync(expected,
            new LocatorAssertionsToHaveCountOptions { Timeout = Timeouts.SubmitMs });
}
