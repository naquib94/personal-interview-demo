using System.Text;
using QaFramework.Core.Logging;
using QaFramework.Mobile.Drivers;
using QaFramework.Mobile.Elements;

namespace QaFramework.Mobile.Simulation;

/// <summary>
/// An <see cref="IMobileDriver"/> backed by an in-memory element tree instead of a device.
/// </summary>
/// <remarks>
/// <b>Read this before drawing any conclusion from a green mobile run.</b>
/// <para>
/// This driver exists so that the mobile framework can be executed - genuinely executed, not
/// merely compiled - on a machine with no Android SDK, no Xcode, no emulator and no Appium
/// server. That is the entire situation in a public CI runner, and it is the situation in which
/// most "Appium demonstration" repositories quietly contain code that has never run.
/// </para>
/// <para>
/// <b>What a passing run against this driver proves.</b> That the framework's wiring works end
/// to end: configuration binds, capability files parse, the factory resolver picks the right
/// factory, locators resolve to the correct platform selector, screen objects issue the right
/// calls in the right order, waits poll and time out correctly, Reqnroll bindings and hooks fire,
/// failure evidence is captured and written to the configured directory, and the reporting chain
/// receives what it expects. Those are real defects when they break, and they break often.
/// </para>
/// <para>
/// <b>What it does not prove, at all.</b> That any application behaves correctly. That a real
/// device renders the screen. That the accessibility identifiers exist in a real build. That a
/// gesture works. That a network call succeeds. The fixture answers exactly what it was told to
/// answer, so a scenario passing here says nothing whatsoever about a product.
/// </para>
/// <para>
/// Being explicit about that boundary is the point of building it this way. The alternative -
/// committing an Appium suite that cannot be run and letting a reader assume it has been - is a
/// more impressive-looking artefact and a less honest one. Point the same suite at
/// <see cref="RunTarget.LocalAppium"/> with a device attached and the assertions become claims
/// about the application; until then they are claims about the framework.
/// </para>
/// </remarks>
public sealed class SimulatedMobileDriver : IMobileDriver
{
    /// <summary>
    /// A 1x1 transparent PNG, returned by <see cref="TakeScreenshotAsync"/>.
    /// </summary>
    /// <remarks>
    /// A real PNG rather than an empty array, because the evidence path writes the bytes to a
    /// <c>.png</c> file and an artefact viewer that cannot open it produces a support question
    /// about the framework. It is deliberately blank: a rendered mock-up would be a picture of
    /// something that was never on a screen, which is exactly the impression this driver must not
    /// create.
    /// </remarks>
    private static readonly byte[] PlaceholderPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYGD4DwABBAEAX+d1zAAAAABJRU5ErkJggg==");

    private readonly SimulatedApp app;
    private bool disposed;

    /// <summary>Creates a driver over an already-loaded application.</summary>
    public SimulatedMobileDriver(SimulatedApp app, Platform platform)
    {
        this.app = app;
        Platform = platform;
    }

    /// <inheritdoc />
    public Platform Platform { get; }

    /// <inheritdoc />
    public RunTarget Target => RunTarget.Simulated;

    /// <summary>The screen the simulated application is showing. Useful in diagnostics.</summary>
    public string CurrentScreen => app.CurrentScreen;

    /// <summary>The most recent order reference the fixture generated, if any.</summary>
    public string? LastGeneratedReference => app.LastGeneratedReference;

    /// <inheritdoc />
    public Task<string> GetTextAsync(MobileLocator locator)
    {
        ThrowIfDisposed();
        return Task.FromResult(app.GetText(Selector(locator)));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetTextsAsync(MobileLocator locator)
    {
        ThrowIfDisposed();
        return Task.FromResult(app.GetTexts(Selector(locator)));
    }

    /// <inheritdoc />
    public Task TapAsync(MobileLocator locator)
    {
        ThrowIfDisposed();
        app.Tap(Selector(locator));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task EnterTextAsync(MobileLocator locator, string text)
    {
        ThrowIfDisposed();
        app.EnterText(Selector(locator), text);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> IsDisplayedAsync(MobileLocator locator)
    {
        ThrowIfDisposed();

        // Absence and invisibility are both "false" here, matching the real driver's behaviour.
        // Diverging on this - throwing for absence, say - would mean a negative assertion has to
        // be written differently for the two targets, and a framework whose assertions depend on
        // the target is a framework that cannot be trusted on either.
        SimulatedElement? element = app.FindOnCurrentScreen(Selector(locator));

        return Task.FromResult(element is { Visible: true });
    }

    /// <inheritdoc />
    public Task<byte[]> TakeScreenshotAsync()
    {
        // Not gated on disposal: evidence capture runs during teardown, and a teardown that
        // throws replaces the scenario's real failure with an irrelevant one.
        return Task.FromResult(PlaceholderPng);
    }

    /// <inheritdoc />
    public Task<string> GetPageSourceAsync()
    {
        // Deliberately shaped like a native hierarchy so the evidence path is exercised the same
        // way it would be with a real driver, and prefixed with a warning so that no artefact
        // from a simulated run can be mistaken for a capture from a device.
        StringBuilder xml = new();
        xml.AppendLine("<!-- Simulated page source. No device was involved in producing this. -->");
        xml.AppendLine($"<simulated-app platform=\"{Platform}\" screen=\"{app.CurrentScreen}\">");

        foreach (SimulatedElement element in app.ElementsOnCurrentScreen)
        {
            xml.AppendLine(
                $"  <element selector=\"{element.Selector}\" visible=\"{element.Visible}\" " +
                $"input=\"{element.IsInput}\" list=\"{element.IsList}\">");

            if (element.IsList)
            {
                foreach (string item in element.Items)
                    xml.AppendLine($"    <row>{Escape(item)}</row>");
            }
            else if (!string.IsNullOrEmpty(element.Text))
            {
                xml.AppendLine($"    <text>{Escape(element.Text)}</text>");
            }

            xml.AppendLine("  </element>");
        }

        xml.AppendLine("</simulated-app>");

        return Task.FromResult(xml.ToString());
    }

    /// <inheritdoc />
    public Task DisposeSessionAsync()
    {
        if (disposed)
            return Task.CompletedTask;

        disposed = true;

        TestLog.Info(
            $"Simulated mobile session ended on screen '{app.CurrentScreen}'. No device or " +
            "emulator was used.");

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await DisposeSessionAsync();

    /// <summary>
    /// Resolves the locator's platform selector.
    /// </summary>
    /// <remarks>
    /// This one line is why the simulated target is worth having. Running the suite as iOS
    /// against the same fixture proves that every locator carries a usable iOS selector - a
    /// mistake that, on an Android-only suite, is otherwise discovered on the day someone is
    /// asked to add iOS.
    /// </remarks>
    private string Selector(MobileLocator locator) => locator.For(Platform);

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(
                nameof(SimulatedMobileDriver),
                "The simulated session has already been ended. A step running after teardown " +
                "usually means a fire-and-forget task was not awaited.");
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
             .Replace("<", "&lt;", StringComparison.Ordinal)
             .Replace(">", "&gt;", StringComparison.Ordinal);
}
