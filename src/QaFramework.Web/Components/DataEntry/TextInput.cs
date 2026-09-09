using Microsoft.Playwright;
using QaFramework.Web.Setup;

namespace QaFramework.Web.Components.DataEntry;

/// <summary>
/// A single-line text input.
/// </summary>
/// <remarks>
/// <see cref="EnterAsync"/> writes the value and then <b>reads it back</b>. That
/// self-verification catches a specific and genuinely common class of defect: an input with a
/// mask, a trim, a max-length or a debounced re-render that silently changes what was typed.
/// Without the read-back the test proceeds with the wrong value and fails three steps later on
/// an assertion that has nothing to do with the actual cause.
/// <para>
/// The cost is one extra assertion per field, which Playwright resolves from its own snapshot
/// without a network round trip. The benefit is that a failure points at the field that
/// misbehaved. That trade is worth taking every time.
/// </para>
/// </remarks>
public sealed class TextInput(WebTestContext context, string testId, string description)
    : WebComponent(context, $"[data-testid='{testId}']", description)
{
    public Task EnterAsync(string value) => PerformAsync($"Enter '{value}' into", async () =>
    {
        // FillAsync clears first and dispatches the input event the way a real user's typing
        // does. TypeAsync is slower and only needed when the application listens for
        // individual key events.
        await Locator.FillAsync(value, new LocatorFillOptions { Timeout = Timeouts.ElementMs });

        await Assertions.Expect(Locator).ToHaveValueAsync(value,
            new LocatorAssertionsToHaveValueOptions { Timeout = Timeouts.ElementMs });
    });

    /// <summary>
    /// Clears the field.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="EnterAsync"/> because "submit an empty field" is a distinct
    /// test case, and expressing it as <c>EnterAsync("")</c> reads like an accident.
    /// </remarks>
    public Task ClearAsync() => PerformAsync("Clear", () =>
        Locator.FillAsync(string.Empty, new LocatorFillOptions { Timeout = Timeouts.ElementMs }));

    public Task<string> GetValueAsync() => Locator.InputValueAsync();

    public Task ShouldHaveValueAsync(string expected) =>
        Assertions.Expect(Locator).ToHaveValueAsync(expected,
            new LocatorAssertionsToHaveValueOptions { Timeout = Timeouts.ElementMs });
}
