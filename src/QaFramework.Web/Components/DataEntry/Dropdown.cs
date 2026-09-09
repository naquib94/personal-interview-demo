using Microsoft.Playwright;
using QaFramework.Web.Setup;
using QaFramework.Web.Synchronisation;

namespace QaFramework.Web.Components.DataEntry;

/// <summary>
/// A native HTML select.
/// </summary>
/// <remarks>
/// Selection is by <b>value</b> rather than by index. Index-based selection is one of the most
/// reliable ways to build a suite that breaks for no reason: adding an option, or sorting the
/// list differently, silently changes what every test selects, and the tests keep passing while
/// exercising the wrong data. Choosing "EURUSD" says what the test means; choosing index 2 does
/// not.
/// <para>
/// A selection triggers the application's change handler, so this waits for idle afterwards for
/// the same reason <see cref="Actions.Button"/> does.
/// </para>
/// </remarks>
public sealed class Dropdown(WebTestContext context, string testId, string description)
    : WebComponent(context, $"[data-testid='{testId}']", description)
{
    public Task SelectAsync(string value) => PerformAsync($"Select '{value}' in", async () =>
    {
        await Locator.SelectOptionAsync(new SelectOptionValue { Value = value },
            new LocatorSelectOptionOptions { Timeout = Timeouts.ElementMs });

        await Assertions.Expect(Locator).ToHaveValueAsync(value,
            new LocatorAssertionsToHaveValueOptions { Timeout = Timeouts.ElementMs });

        await Context.WaitUntilIdleAsync();
    });

    public Task<string> GetSelectedValueAsync() => Locator.InputValueAsync();

    /// <summary>
    /// Returns every option's value.
    /// </summary>
    /// <remarks>
    /// Useful for asserting that a list is correctly populated from the backend - a check
    /// that catches broken reference-data endpoints, which otherwise only surface as a user
    /// complaining that a dropdown is empty.
    /// </remarks>
    public async Task<IReadOnlyList<string>> GetOptionValuesAsync()
    {
        IReadOnlyList<string> values = await Locator.Locator("option")
            .EvaluateAllAsync<string[]>("options => options.map(o => o.value)");
        return values;
    }

    /// <summary>
    /// Asserts an option exists but cannot be chosen.
    /// </summary>
    /// <remarks>
    /// Present because the demo application disables non-tradable instruments in the order
    /// form. That is a UI guard, and it needs its own test separate from the API's rule - the
    /// API must still reject the order if the guard is bypassed, and testing only one layer
    /// leaves the other unverified.
    /// </remarks>
    public Task ShouldHaveDisabledOptionAsync(string value) =>
        Assertions.Expect(Locator.Locator($"option[value='{value}'][disabled]"))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = Timeouts.ElementMs });
}
