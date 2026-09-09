namespace QaFramework.Mobile.Drivers;

/// <summary>
/// Where a mobile run actually executes.
/// </summary>
/// <remarks>
/// This enum is the honest centre of the mobile layer. Separating "the automation" from "the
/// thing it talks to" makes the framework's claim checkable: the same screen objects, waits and
/// step bindings run against every target, and only the transport changes.
/// </remarks>
public enum RunTarget
{
    /// <summary>
    /// An Appium server on the engineer's own machine, talking to whatever physical device is
    /// attached. This is the normal development loop.
    /// </summary>
    LocalAppium,

    /// <summary>
    /// An emulator or simulator, still reached through a local Appium server. Distinguished from
    /// <see cref="LocalAppium"/> because emulators need noticeably more generous timeouts and a
    /// different device name, and because a run's evidence should record which of the two it was.
    /// </summary>
    Emulator,

    /// <summary>
    /// A hosted device grid. The grid URL and credentials come from environment variables only;
    /// see <see cref="Capabilities.CloudGridSettings"/>, which fails fast rather than defaulting.
    /// No specific provider is named or required anywhere in this repository.
    /// </summary>
    CloudGrid,

    /// <summary>
    /// The committed default, and the only target CI uses. No device, no emulator and no Appium
    /// server are involved: <see cref="Simulation.SimulatedMobileDriver"/> serves an in-memory
    /// element tree loaded from a committed JSON fixture.
    /// <para>
    /// What this proves: the framework's wiring - configuration binding, locator resolution,
    /// screen objects, waits, hooks, evidence capture, step bindings and reporting - works end
    /// to end. What it does not prove: that any real application behaves correctly. Those are
    /// different claims and conflating them would be dishonest.
    /// </para>
    /// </summary>
    Simulated
}
