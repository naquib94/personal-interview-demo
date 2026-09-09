using QaFramework.Core.Logging;
using QaFramework.Mobile.Drivers;
using QaFramework.Mobile.Elements;

namespace QaFramework.Mobile.Screens;

/// <summary>
/// Swipe, scroll and long-press, for screens that need them.
/// </summary>
/// <remarks>
/// A separate small collaborator rather than more methods on <see cref="BaseScreen"/>, because
/// most screens never gesture and inherited methods that are never called are how a base class
/// becomes a dumping ground.
/// <para>
/// On a driver that does not implement <see cref="ISupportsGestures"/> - which is every simulated
/// run - each method logs a warning and returns. Two alternatives were considered and rejected:
/// </para>
/// <list type="bullet">
/// <item><b>Throw.</b> Correct in principle, and it would mean no scenario involving a list could
/// run in CI at all, including the ones whose scrolling is incidental rather than the point.</item>
/// <item><b>Silently do nothing.</b> Cheap, and it hides the fact that a step which claims to
/// scroll did not. A scenario whose outcome genuinely depends on scrolling would then pass in CI
/// for a reason nobody could see.</item>
/// </list>
/// <para>
/// Warning and continuing keeps the skip in the run log, where a reader of the report can see
/// exactly which gestures did not happen. That is the honest middle: the scenario still exercises
/// the framework, and nobody is misled about what was tested.
/// </para>
/// </remarks>
public sealed class Gestures(IMobileDriver driver)
{
    /// <summary>Whether gestures are actually performed on this session.</summary>
    public bool IsSupported => driver is ISupportsGestures;

    /// <summary>Swipes across the screen.</summary>
    public Task SwipeAsync(SwipeDirection direction, double distanceFraction = 0.6) =>
        driver is ISupportsGestures gestures
            ? gestures.SwipeAsync(direction, distanceFraction)
            : Skip($"swipe {direction}");

    /// <summary>Scrolls until the element is on screen.</summary>
    public Task ScrollToAsync(MobileLocator locator) =>
        driver is ISupportsGestures gestures
            ? gestures.ScrollToAsync(locator)
            : Skip($"scroll to {locator}");

    /// <summary>
    /// Presses and holds an element.
    /// </summary>
    /// <param name="duration">
    /// Defaults to a second, which comfortably clears the platform long-press thresholds on both
    /// Android and iOS. Shorter values are the usual cause of a long press registering as a tap.
    /// </param>
    public Task LongPressAsync(MobileLocator locator, TimeSpan? duration = null) =>
        driver is ISupportsGestures gestures
            ? gestures.LongPressAsync(locator, duration ?? TimeSpan.FromSeconds(1))
            : Skip($"long press {locator}");

    private Task Skip(string gesture)
    {
        TestLog.Warning(
            $"Gesture skipped: {gesture}. The current session ({driver.Target}) does not support " +
            "gestures, so this step exercised the framework's call path but moved nothing.");

        return Task.CompletedTask;
    }
}
