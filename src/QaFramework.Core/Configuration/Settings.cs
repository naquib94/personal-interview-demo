namespace QaFramework.Core.Configuration;

// Strongly-typed configuration.
//
// The split below is the important design decision, and it is worth stating explicitly because
// it is the thing that stops configuration files rotting:
//
//   RunSettings         = HOW we test.  Browser, headless, timeouts, evidence, retries.
//                                       Identical in every environment.
//   EnvironmentSettings = WHERE we test. URLs, database location, which users exist.
//                                       Different per environment, selected at runtime.
//
// Mixing the two is what produces files where nobody can tell which values are safe to change.
//
// `required` members are used deliberately: a malformed settings file fails at bind time with
// a precise message, rather than surfacing 40 minutes into a run as a null reference.

public sealed class RunSettings
{
    public required BrowserSettings Browser { get; init; }
    public required TimeoutSettings Timeouts { get; init; }
    public required EvidenceSettings Evidence { get; init; }
}

public sealed class BrowserSettings
{
    /// <summary>chromium, firefox or webkit.</summary>
    public string Name { get; init; } = "chromium";

    /// <summary>
    /// Optional browser channel - "chrome", "msedge", "chrome-beta" and so on.
    /// </summary>
    /// <remarks>
    /// Empty by default, which uses Playwright's own bundled browser. That is the right default
    /// because a pinned browser build makes runs reproducible.
    /// <para>
    /// Setting a channel drives a browser already installed on the machine instead. Two real
    /// reasons to want that: a locked-down corporate network where Playwright's browser
    /// download is blocked by TLS interception, and the occasional need to reproduce a defect
    /// against the exact Chrome build a customer has. Both are situations where "just download
    /// the bundled browser" is not an available answer, so the setting is worth its two lines.
    /// </para>
    /// </remarks>
    public string Channel { get; init; } = string.Empty;

    public bool Headless { get; init; } = true;

    public int ViewportWidth { get; init; } = 1920;

    public int ViewportHeight { get; init; } = 1080;

    /// <summary>
    /// Artificial delay between Playwright operations, in milliseconds. Zero in CI; useful
    /// locally when demonstrating a scenario to a human, which is exactly what an interview is.
    /// </summary>
    public int SlowMotionMs { get; init; }
}

public sealed class EvidenceSettings
{
    /// <summary>
    /// Capture a screenshot on failure. Note "on failure", not "on every step": a screenshot
    /// per step produces a filmstrip that is pleasant to look at and slows a suite down
    /// measurably for evidence nobody reads. Failures are what need evidence.
    /// </summary>
    public bool CaptureScreenshotOnFailure { get; init; } = true;

    /// <summary>Capture the DOM alongside the screenshot. Cheap, and often more diagnostic.</summary>
    public bool CapturePageSourceOnFailure { get; init; } = true;

    public bool RecordVideo { get; init; }

    public string OutputDirectory { get; init; } = "evidence";
}

public sealed class EnvironmentSettings
{
    /// <summary>Base address of the application under test, UI and API.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>
    /// Whether the suite is responsible for starting the application under test. True locally
    /// and in CI (the suite owns the whole world); false when pointing at a deployment.
    /// </summary>
    public bool StartApplicationUnderTest { get; init; } = true;

    /// <summary>
    /// Path to the SQLite file the application uses, for read-only verification.
    /// Empty means "database verification is unavailable in this environment", which the
    /// database layer reports as an explicit, actionable error rather than a crash.
    /// </summary>
    public string DatabasePath { get; init; } = string.Empty;

    public required Dictionary<string, TestUser> Users { get; init; }
}

/// <summary>
/// A test user, addressed by role.
/// </summary>
/// <remarks>
/// Scenarios say "Given an active trader is signed in", never "Given trader.demo signs in with
/// Demo!Pass123". Two benefits: the Gherkin reads like a business rule, and the credential
/// lives in exactly one place per environment - so rotating it is a config edit, not a
/// find-and-replace across feature files.
/// </remarks>
public sealed class TestUser
{
    public required string Username { get; init; }

    /// <summary>
    /// Resolved at load time from configuration, which layers environment variables over JSON.
    /// See <see cref="ConfigurationLoader"/> for why a missing password is a hard failure
    /// rather than an empty string.
    /// </summary>
    public required string Password { get; init; }

    /// <summary>Human-readable description of what makes this user interesting to a test.</summary>
    public string Description { get; init; } = string.Empty;

    public override string ToString() => Username;
}
