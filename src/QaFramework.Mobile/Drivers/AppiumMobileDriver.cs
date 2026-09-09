using System.Drawing;
using System.Globalization;
using OpenQA.Selenium;
using OpenQA.Selenium.Appium;
using QaFramework.Core.Logging;
using QaFramework.Mobile.Elements;

namespace QaFramework.Mobile.Drivers;

/// <summary>
/// The real <see cref="IMobileDriver"/>: a thin adapter over Appium's <see cref="AppiumDriver"/>.
/// </summary>
/// <remarks>
/// <b>This type is not exercised by any test in this repository, and cannot be.</b> Every method
/// below requires a live Appium session, which requires a running server and an attached device
/// or emulator - none of which exist on the CI agent, by design. The suite runs against
/// <see cref="Simulation.SimulatedMobileDriver"/> instead. Saying so plainly here is deliberate:
/// a reader deserves to know which parts of a framework have been executed and which have only
/// been written.
/// <para>
/// The mitigation is to keep this class as thin as it is. It contains no waiting, no retry, no
/// assertion and no state machine - those all live in <see cref="Screens.BaseScreen"/> and
/// <see cref="Core.Synchronisation.Wait"/>, which <i>are</i> covered. What is left here is one
/// Appium call per method, so the untested surface is as small as it can be made without
/// pretending.
/// </para>
/// <para>
/// State is held in a readonly field passed to the constructor - not in a static, and not in a
/// <c>[ThreadStatic]</c>. See <see cref="MobileDriverContext"/> for why the thread-static shortcut
/// is actively wrong in an async test runner.
/// </para>
/// </remarks>
public sealed class AppiumMobileDriver(AppiumDriver driver, Platform platform, RunTarget target)
    : IMobileDriver, ISupportsGestures
{
    private bool disposed;

    /// <inheritdoc />
    public Platform Platform { get; } = platform;

    /// <inheritdoc />
    public RunTarget Target { get; } = target;

    /// <inheritdoc />
    public Task<string> GetTextAsync(MobileLocator locator) =>
        Task.FromResult(Find(locator).Text);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetTextsAsync(MobileLocator locator)
    {
        IReadOnlyCollection<AppiumElement> elements =
            driver.FindElements(LocatorTranslator.ToBy(locator, Platform));

        // No exception when nothing matches: an empty list is a legitimate answer to "what is in
        // this list", and it is what lets a caller assert on emptiness without a try/catch.
        return Task.FromResult<IReadOnlyList<string>>(
            elements.Select(element => element.Text).ToList());
    }

    /// <inheritdoc />
    public Task TapAsync(MobileLocator locator)
    {
        Find(locator).Click();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task EnterTextAsync(MobileLocator locator, string text)
    {
        IWebElement element = Find(locator);

        // Clear before typing. Appium's SendKeys appends, so a step that runs twice - a retried
        // scenario, a background that re-enters a field - would otherwise submit the previous
        // attempt's value concatenated with this one.
        element.Clear();
        element.SendKeys(text);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> IsDisplayedAsync(MobileLocator locator)
    {
        try
        {
            return Task.FromResult(Find(locator).Displayed);
        }
        catch (Exception ex) when (ex is NoSuchElementException or KeyNotFoundException
                                      or StaleElementReferenceException)
        {
            // Absent and stale both mean "not displayed" to a caller. Stale is included because
            // an element that vanished between the find and the query is, for the purposes of
            // this question, gone.
            return Task.FromResult(false);
        }
    }

    /// <inheritdoc />
    public Task<byte[]> TakeScreenshotAsync() =>
        Task.FromResult(driver.GetScreenshot().AsByteArray);

    /// <inheritdoc />
    public Task<string> GetPageSourceAsync() => Task.FromResult(driver.PageSource);

    /// <inheritdoc />
    public Task SwipeAsync(SwipeDirection direction, double distanceFraction = 0.6)
    {
        // Driven through the platform's own gesture endpoint rather than through a hand-rolled
        // W3C action sequence. The endpoints are markedly more reliable across OS versions, and
        // a suite that maintains its own action sequences ends up maintaining a fork of the
        // driver's gesture logic.
        Size size = driver.Manage().Window.Size;
        int width = size.Width;
        int height = size.Height;

        Dictionary<string, object> arguments = new()
        {
            ["left"] = (int)(width * 0.1),
            ["top"] = (int)(height * 0.1),
            ["width"] = (int)(width * 0.8),
            ["height"] = (int)(height * 0.8),
            ["direction"] = direction.ToString().ToLower(CultureInfo.InvariantCulture),
            ["percent"] = Math.Clamp(distanceFraction, 0.1, 1.0)
        };

        driver.ExecuteScript(
            Platform == Platform.Android ? "mobile: swipeGesture" : "mobile: swipe", arguments);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task ScrollToAsync(MobileLocator locator)
    {
        // Android has a native scroll-into-view query, and it is dramatically faster than
        // repeated swipes because the search runs on the device instead of round-tripping the
        // page source for every attempt. iOS has no equivalent, so it falls back to swiping.
        if (Platform == Platform.Android && locator.Strategy == LocatorStrategy.AccessibilityId)
        {
            string query =
                "new UiScrollable(new UiSelector().scrollable(true))" +
                $".scrollIntoView(new UiSelector().description(\"{locator.For(Platform)}\"))";

            driver.FindElement(MobileBy.AndroidUIAutomator(query));
            return;
        }

        for (int attempt = 0; attempt < 10 && !await IsDisplayedAsync(locator); attempt++)
            await SwipeAsync(SwipeDirection.Up);
    }

    /// <inheritdoc />
    public Task LongPressAsync(MobileLocator locator, TimeSpan duration)
    {
        IWebElement element = Find(locator);

        // Addressed by coordinates rather than by element reference. Both platforms' gesture
        // endpoints accept either, but the element-reference form needs the driver's internal
        // element id, and reaching for that couples this adapter to a WebDriver implementation
        // detail that has moved between Selenium versions. The centre of the element's rectangle
        // is stable, obvious and good enough.
        Point origin = element.Location;
        Size bounds = element.Size;

        Dictionary<string, object> arguments = new()
        {
            ["x"] = origin.X + (bounds.Width / 2),
            ["y"] = origin.Y + (bounds.Height / 2),
            ["duration"] = (int)duration.TotalMilliseconds
        };

        driver.ExecuteScript(
            Platform == Platform.Android ? "mobile: longClickGesture" : "mobile: touchAndHold",
            arguments);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DisposeSessionAsync()
    {
        // Idempotent, because both the hook and an await using may reach it and a double Quit
        // throws. A teardown path that can throw is a teardown path that masks the real failure.
        if (disposed)
            return Task.CompletedTask;

        disposed = true;

        try
        {
            driver.Quit();
        }
        catch (WebDriverException ex)
        {
            // Logged, never rethrown. If the session has already died, the scenario's own result
            // is the interesting news; a teardown exception on top of it only obscures it.
            TestLog.Warning($"Ignoring an error while ending the Appium session: {ex.Message}");
        }
        finally
        {
            driver.Dispose();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await DisposeSessionAsync();

    /// <summary>
    /// Finds one element, translating Appium's absence exception into the framework's.
    /// </summary>
    /// <remarks>
    /// The translation is the point. <c>NoSuchElementException</c> is a WebDriver type, and
    /// <see cref="Core.Synchronisation.Wait"/> deliberately knows nothing about WebDriver -
    /// it must serve the API and database layers too. Mapping absence onto
    /// <see cref="KeyNotFoundException"/>, which <c>Wait</c> treats as transient, is what lets
    /// one polling helper serve every layer of the framework.
    /// </remarks>
    private IWebElement Find(MobileLocator locator)
    {
        try
        {
            return driver.FindElement(LocatorTranslator.ToBy(locator, Platform));
        }
        catch (NoSuchElementException ex)
        {
            throw new KeyNotFoundException(
                $"The element '{locator.Description}' was not found on {Platform} using " +
                $"{locator.Strategy} selector '{locator.For(Platform)}'.", ex);
        }
    }
}
