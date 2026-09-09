namespace QaFramework.Mobile.Drivers;

/// <summary>
/// The mobile platforms this framework can drive.
/// </summary>
/// <remarks>
/// An enum, for the same reason <see cref="Core.Configuration.TestEnvironment"/> is an enum:
/// the set is knowable at compile time, so a typo in a settings file fails at bind time with
/// the list of valid values rather than half way through a run.
/// </remarks>
public enum Platform
{
    /// <summary>Android, driven through UiAutomator2.</summary>
    Android,

    /// <summary>iOS, driven through XCUITest.</summary>
    IOS
}
