using QaFramework.Mobile.Elements;

namespace QaFramework.Mobile.Drivers;

/// <summary>
/// Everything the mobile framework is allowed to ask of a device.
/// </summary>
/// <remarks>
/// This interface is the seam that makes device-free execution possible, and its size is the
/// whole design decision. It exposes intentions - tap this, read that - rather than an element
/// object model, because the moment an interface hands back a driver-native element, every
/// caller is coupled to Appium and no alternative implementation is viable.
/// <para>
/// Kept deliberately small. Each additional member is another method
/// <see cref="Simulation.SimulatedMobileDriver"/> has to emulate credibly, and a simulation that
/// lags behind the interface is worse than no simulation, because it fails for reasons that have
/// nothing to do with the test. Anything genuinely device-specific - gestures, app lifecycle -
/// lives behind <see cref="ISupportsGestures"/> so the small surface stays honest rather than
/// growing methods that the simulated driver pretends to implement.
/// </para>
/// <para>
/// All members are asynchronous even though the Appium .NET client is synchronous. The reason is
/// the call sites, not the transport: waits in <see cref="Core.Synchronisation.Wait"/> are async,
/// Reqnroll steps are async, and a synchronous interface in the middle would force
/// <c>.GetAwaiter().GetResult()</c> somewhere - the classic route to a deadlock and to stack
/// traces nobody can read. The Appium adapter simply returns completed tasks and says so.
/// </para>
/// </remarks>
public interface IMobileDriver : IAsyncDisposable
{
    /// <summary>
    /// The platform this session is driving. Read by the driver itself to pick the correct half
    /// of a <see cref="MobileLocator"/>, and by evidence writers to label artefacts.
    /// </summary>
    Platform Platform { get; }

    /// <summary>Which transport this session is using. Recorded in evidence so a report can
    /// never imply that a simulated run touched hardware.</summary>
    RunTarget Target { get; }

    /// <summary>Reads the visible text of an element.</summary>
    /// <exception cref="KeyNotFoundException">
    /// Thrown when the element is not present. A specific exception type matters here:
    /// <see cref="Core.Synchronisation.Wait"/> treats it as "not ready yet" and keeps polling,
    /// whereas a programming error such as a null reference is rethrown immediately.
    /// </exception>
    Task<string> GetTextAsync(MobileLocator locator);

    /// <summary>Reads the text of every element matching the locator, in document order.</summary>
    /// <remarks>
    /// Present so that assertions about lists - order history rows, market instruments - do not
    /// have to index locators by position, which is how a suite ends up with
    /// <c>Row(1)</c>, <c>Row(2)</c> and no way to assert "exactly one row matches".
    /// </remarks>
    Task<IReadOnlyList<string>> GetTextsAsync(MobileLocator locator);

    /// <summary>Taps an element.</summary>
    Task TapAsync(MobileLocator locator);

    /// <summary>
    /// Replaces the contents of a text field.
    /// </summary>
    /// <remarks>
    /// Replaces rather than appends. Appending is the source of the classic mobile flake where a
    /// retried step types into a field that already holds the previous attempt's value.
    /// </remarks>
    Task EnterTextAsync(MobileLocator locator, string text);

    /// <summary>
    /// Whether the element is present and visible. Returns false rather than throwing, because
    /// callers use it as a wait condition and as a negative assertion.
    /// </summary>
    Task<bool> IsDisplayedAsync(MobileLocator locator);

    /// <summary>A PNG screenshot, for failure evidence.</summary>
    Task<byte[]> TakeScreenshotAsync();

    /// <summary>
    /// The native view hierarchy as XML. Cheap to capture and frequently more diagnostic than a
    /// screenshot, because it shows elements a screenshot only implies.
    /// </summary>
    Task<string> GetPageSourceAsync();

    /// <summary>
    /// Ends the session and releases the device.
    /// </summary>
    /// <remarks>
    /// Named explicitly rather than relying only on <see cref="IAsyncDisposable"/> because on a
    /// device grid this call is what stops the meter running, and a leaked session is a leaked
    /// device that the next run then cannot acquire. <c>DisposeAsync</c> delegates to it, so
    /// either route is safe, and both are idempotent.
    /// </remarks>
    Task DisposeSessionAsync();
}

/// <summary>
/// Gestures, separated from <see cref="IMobileDriver"/> on purpose.
/// </summary>
/// <remarks>
/// A swipe has no meaningful equivalent in an in-memory element tree. Two options existed:
/// put gestures on <see cref="IMobileDriver"/> and have the simulated driver silently do
/// nothing, or make gesture support an explicit capability that callers can query. The second
/// was chosen because a silent no-op on the core interface would let a scenario that genuinely
/// depends on scrolling pass in CI for the wrong reason.
/// <para>
/// <see cref="Screens.Gestures"/> checks for this interface and logs a skip when it is absent,
/// which keeps the skip visible in the run log rather than invisible in the driver.
/// </para>
/// </remarks>
public interface ISupportsGestures
{
    /// <summary>Swipes across the screen in the given direction.</summary>
    Task SwipeAsync(SwipeDirection direction, double distanceFraction = 0.6);

    /// <summary>Scrolls until the element is on screen, or gives up at the platform's limit.</summary>
    Task ScrollToAsync(MobileLocator locator);

    /// <summary>Presses and holds an element.</summary>
    Task LongPressAsync(MobileLocator locator, TimeSpan duration);
}

/// <summary>Direction of a swipe, expressed as the direction the content moves.</summary>
public enum SwipeDirection
{
    /// <summary>Content moves up, revealing what is below it.</summary>
    Up,

    /// <summary>Content moves down, revealing what is above it.</summary>
    Down,

    /// <summary>Content moves left.</summary>
    Left,

    /// <summary>Content moves right.</summary>
    Right
}
