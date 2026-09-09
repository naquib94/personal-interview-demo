namespace QaFramework.Core.Configuration;

/// <summary>
/// A named catalogue of timeouts.
/// </summary>
/// <remarks>
/// This class exists to eliminate magic numbers. In most suites, timeouts accumulate as literals
/// scattered through page objects - <c>WaitFor(..., 30000)</c>, <c>WaitFor(..., 5000)</c> - and
/// nobody can answer two basic questions: why is this one 30 seconds, and where do I change
/// them all for a slow CI agent?
/// <para>
/// Naming a timeout after its <i>purpose</i> answers both. Each value is documented with the
/// reason it has that magnitude, every value is overridable from configuration, and a suite
/// running on a slow agent is tuned by one settings file rather than by a hunt through code.
/// </para>
/// </remarks>
public sealed class TimeoutSettings
{
    /// <summary>
    /// An element that should already be there. Short on purpose: if a button is not present
    /// within two seconds of the page settling, waiting thirty more will not help, it will only
    /// make the failure take thirty seconds to report.
    /// </summary>
    public int ElementMs { get; init; } = 5_000;

    /// <summary>
    /// A network round trip plus render - a grid loading, a page navigating. Sized for the
    /// slowest reasonable CI agent rather than for a developer laptop.
    /// </summary>
    public int PageLoadMs { get; init; } = 15_000;

    /// <summary>
    /// A user-initiated write that must be confirmed on screen (order placed, alert shown).
    /// </summary>
    public int SubmitMs { get; init; } = 10_000;

    /// <summary>
    /// Proving something is <i>absent</i>. This is the one timeout that is a straight cost:
    /// the wait always runs to completion when the assertion passes. Kept deliberately short,
    /// and used sparingly - preferring "the empty-state message is visible" over "no rows
    /// exist" turns a two-second wait into an instant assertion.
    /// </summary>
    public int AbsenceMs { get; init; } = 2_000;

    /// <summary>
    /// An HTTP request/response cycle.
    /// </summary>
    public int ApiRequestMs { get; init; } = 30_000;

    /// <summary>
    /// Total budget for a poll waiting on eventual consistency. See
    /// <see cref="Synchronisation.Wait"/>.
    /// </summary>
    public int EventualConsistencyMs { get; init; } = 20_000;

    /// <summary>Gap between poll attempts. Short enough to be responsive, long enough not to hammer.</summary>
    public int PollIntervalMs { get; init; } = 250;

    /// <summary>
    /// How long the suite waits for the application under test to report healthy on startup.
    /// Generous, because a cold .NET start on a shared CI agent is genuinely slow and a
    /// too-tight value here produces the most confusing possible failure: everything red.
    /// </summary>
    public int ApplicationStartupMs { get; init; } = 60_000;

    // Convenience projections. Call sites read Timeouts.Element rather than
    // TimeSpan.FromMilliseconds(Timeouts.ElementMs), which keeps the noise out of page objects.
    public TimeSpan Element => TimeSpan.FromMilliseconds(ElementMs);
    public TimeSpan PageLoad => TimeSpan.FromMilliseconds(PageLoadMs);
    public TimeSpan Submit => TimeSpan.FromMilliseconds(SubmitMs);
    public TimeSpan Absence => TimeSpan.FromMilliseconds(AbsenceMs);
    public TimeSpan ApiRequest => TimeSpan.FromMilliseconds(ApiRequestMs);
    public TimeSpan EventualConsistency => TimeSpan.FromMilliseconds(EventualConsistencyMs);
    public TimeSpan PollInterval => TimeSpan.FromMilliseconds(PollIntervalMs);
    public TimeSpan ApplicationStartup => TimeSpan.FromMilliseconds(ApplicationStartupMs);

    /// <summary>
    /// Rejects values that could only be mistakes. A zero or negative timeout does not mean
    /// "wait forever" or "do not wait" - it means someone left a placeholder in a settings
    /// file, and it is far cheaper to say so at startup than to debug the resulting behaviour.
    /// </summary>
    public void Validate()
    {
        Check(ElementMs, nameof(ElementMs));
        Check(PageLoadMs, nameof(PageLoadMs));
        Check(SubmitMs, nameof(SubmitMs));
        Check(AbsenceMs, nameof(AbsenceMs));
        Check(ApiRequestMs, nameof(ApiRequestMs));
        Check(EventualConsistencyMs, nameof(EventualConsistencyMs));
        Check(PollIntervalMs, nameof(PollIntervalMs));
        Check(ApplicationStartupMs, nameof(ApplicationStartupMs));

        if (PollIntervalMs >= EventualConsistencyMs)
            throw new InvalidOperationException(
                $"Timeouts.PollIntervalMs ({PollIntervalMs}) must be smaller than " +
                $"Timeouts.EventualConsistencyMs ({EventualConsistencyMs}), otherwise a poll " +
                "can only ever make a single attempt.");

        static void Check(int value, string name)
        {
            if (value <= 0)
                throw new InvalidOperationException(
                    $"Timeouts.{name} must be greater than zero but was {value}. Check the " +
                    "runsettings.json file or the corresponding environment variable override.");
        }
    }
}
