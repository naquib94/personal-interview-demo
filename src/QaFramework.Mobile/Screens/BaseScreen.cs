using QaFramework.Core.Configuration;
using QaFramework.Core.Logging;
using QaFramework.Core.Synchronisation;
using QaFramework.Mobile.Drivers;
using QaFramework.Mobile.Elements;

namespace QaFramework.Mobile.Screens;

/// <summary>
/// Base class for screen objects: the interaction vocabulary, plus the waiting.
/// </summary>
/// <remarks>
/// Every interaction here goes through <see cref="Wait"/>. There is no <c>Thread.Sleep</c> in this
/// assembly, and that is not a stylistic preference: a sleep is simultaneously too long on a
/// developer's machine, wasting time on every run, and too short on a loaded CI agent or a cold
/// emulator, producing a failure that is indistinguishable from a defect. Mobile makes this worse
/// than web, because emulator timings vary by an order of magnitude.
/// <para>
/// Dependencies arrive through the constructor - a driver and a timeout catalogue - rather than
/// from ambient static state. That is what allows a screen object to be constructed against
/// <see cref="Simulation.SimulatedMobileDriver"/> in one test and a real session in another,
/// without either knowing about the other.
/// </para>
/// <para>
/// Deliberately not included: assertions. A screen object that asserts cannot be reused by a
/// scenario that expects the opposite outcome, and the failure message ends up in the wrong
/// layer. Screens expose state; steps assert on it.
/// </para>
/// </remarks>
public abstract class BaseScreen(IMobileDriver driver, TimeoutSettings timeouts)
{
    /// <summary>The session this screen drives.</summary>
    protected IMobileDriver Driver { get; } = driver;

    /// <summary>The named timeout catalogue. No magic numbers in a screen object.</summary>
    protected TimeoutSettings Timeouts { get; } = timeouts;

    /// <summary>Swipes, scrolls and long presses. No-op on a driver that cannot gesture.</summary>
    protected Gestures Gestures { get; } = new(driver);

    /// <summary>
    /// The element that proves this screen is the one on display.
    /// </summary>
    /// <remarks>
    /// Required of every screen, because "the screen has loaded" needs a definition, and a
    /// heading is a far better one than a spinner's absence. Choosing it per screen also means
    /// <see cref="WaitUntilLoadedAsync"/> is written once here.
    /// </remarks>
    protected abstract MobileLocator Anchor { get; }

    /// <summary>Human-readable screen name, used in log lines.</summary>
    protected virtual string ScreenName => GetType().Name;

    /// <summary>
    /// Waits for the screen to be on display.
    /// </summary>
    /// <remarks>
    /// Uses the page-load timeout rather than the element timeout: arriving at a screen involves
    /// a transition and often a network call, whereas finding a control on a screen that is
    /// already there should be nearly instant. Using one timeout for both forces a choice between
    /// slow failures and flaky ones.
    /// </remarks>
    public async Task WaitUntilLoadedAsync()
    {
        await WaitUntilDisplayedAsync(Anchor, Timeouts.PageLoad);
        TestLog.Info($"{ScreenName} is displayed.");
    }

    /// <summary>Whether this screen is currently on display. Does not wait.</summary>
    public Task<bool> IsDisplayedAsync() => Driver.IsDisplayedAsync(Anchor);

    /// <summary>Waits until an element is visible.</summary>
    protected Task WaitUntilDisplayedAsync(MobileLocator locator, TimeSpan? timeout = null) =>
        Wait.UntilAsync(
            () => Driver.IsDisplayedAsync(locator),
            $"{locator} to be displayed on {ScreenName}",
            timeout ?? Timeouts.Element,
            Timeouts.PollInterval);

    /// <summary>
    /// Waits until an element is gone.
    /// </summary>
    /// <remarks>
    /// Uses the absence timeout, which is deliberately short. Proving something is absent always
    /// costs the full wait when the assertion passes, so it is the one wait worth being stingy
    /// with - see the note on <see cref="TimeoutSettings.AbsenceMs"/>.
    /// </remarks>
    protected Task WaitUntilGoneAsync(MobileLocator locator) =>
        Wait.UntilAsync(
            async () => !await Driver.IsDisplayedAsync(locator),
            $"{locator} to disappear from {ScreenName}",
            Timeouts.Absence,
            Timeouts.PollInterval);

    /// <summary>Waits for an element, then taps it.</summary>
    protected async Task TapAsync(MobileLocator locator)
    {
        await WaitUntilDisplayedAsync(locator);
        TestLog.Step($"Tap {locator} on {ScreenName}.");
        await Driver.TapAsync(locator);
    }

    /// <summary>Waits for a field, then replaces its contents.</summary>
    protected async Task EnterTextAsync(MobileLocator locator, string text)
    {
        await WaitUntilDisplayedAsync(locator);

        // The value is not logged. Fields carry passwords, and a framework that logs whatever is
        // typed will eventually publish one into a CI log that is retained for a year.
        TestLog.Step($"Enter text into {locator} on {ScreenName}.");
        await Driver.EnterTextAsync(locator, text);
    }

    /// <summary>Waits for an element, then reads its text.</summary>
    protected async Task<string> TextAsync(MobileLocator locator)
    {
        await WaitUntilDisplayedAsync(locator);
        return await Driver.GetTextAsync(locator);
    }

    /// <summary>Reads the text of every row matching the locator.</summary>
    protected Task<IReadOnlyList<string>> TextsAsync(MobileLocator locator) =>
        Driver.GetTextsAsync(locator);

    /// <summary>Whether an element is visible right now. Does not wait.</summary>
    protected Task<bool> IsDisplayedAsync(MobileLocator locator) =>
        Driver.IsDisplayedAsync(locator);

    /// <summary>
    /// Waits for an element and returns whether it appeared, rather than throwing.
    /// </summary>
    /// <remarks>
    /// Exists for the case where a step needs to assert on an outcome that may legitimately not
    /// happen - an error banner, say. Without it, step definitions grow try/catch blocks around
    /// waits, and a caught <see cref="TimeoutException"/> discards the diagnostic message the
    /// wait had gone to the trouble of composing.
    /// </remarks>
    protected async Task<bool> AppearsAsync(MobileLocator locator, TimeSpan? timeout = null)
    {
        try
        {
            await WaitUntilDisplayedAsync(locator, timeout ?? Timeouts.Element);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
