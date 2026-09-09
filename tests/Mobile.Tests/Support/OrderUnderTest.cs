namespace Mobile.Tests.Support;

/// <summary>
/// What the current scenario has just done, carried between its steps.
/// </summary>
/// <remarks>
/// Reqnroll's container creates one of these per scenario and injects the same instance into
/// every step class that asks for it, which is what makes state sharing possible without a static
/// field. The distinction is not academic: a static <c>string LastOrderReference</c> works
/// perfectly until two scenarios run at once, at which point one scenario asserts against the
/// other's order and the failure is unreproducible.
/// <para>
/// Chosen over stashing values in <c>ScenarioContext</c> by string key. Both are scenario-scoped;
/// this one is typed, so a renamed property is a compiler error rather than a null at run time,
/// and the set of things a scenario carries is documented by the class instead of being scattered
/// across step files as magic strings.
/// </para>
/// </remarks>
public sealed class OrderUnderTest
{
    /// <summary>The reference the application issued, or null if none was.</summary>
    public string? Reference { get; set; }

    /// <summary>The instrument the scenario asked to trade.</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>The side the scenario asked for.</summary>
    public string Side { get; set; } = string.Empty;

    /// <summary>The quantity as the scenario expressed it, unparsed.</summary>
    public string Quantity { get; set; } = string.Empty;

    /// <summary>
    /// The reference, or a failure explaining which step should have set it.
    /// </summary>
    /// <remarks>
    /// A guarded accessor rather than a nullable read at each use site. The message names the step
    /// that populates it, which turns "object reference not set" into a one-line diagnosis.
    /// </remarks>
    public string RequireReference() =>
        Reference ?? throw new InvalidOperationException(
            "No order reference has been recorded for this scenario. The step that places an " +
            "order records it; a step asserting on it must run after that one.");
}
