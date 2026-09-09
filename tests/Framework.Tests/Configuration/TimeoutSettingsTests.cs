using QaFramework.Core.Configuration;

namespace Framework.Tests.Configuration;

/// <summary>
/// Tests for the timeout catalogue's validation and its TimeSpan projections.
/// </summary>
/// <remarks>
/// A zero or negative timeout does not fail loudly; it changes how the suite waits. A zero
/// element timeout turns every wait into a single attempt, which produces failures that look
/// exactly like a slow application. Validation is what turns that into a startup error, and
/// these tests are what keep validation honest.
/// </remarks>
[TestFixture]
public sealed class TimeoutSettingsTests
{
    [Test]
    public void Validate_WithTheDefaultValues_Succeeds()
    {
        // The defaults are what a suite runs with when runsettings.json omits the section, so
        // they have to be self-consistent - in particular PollIntervalMs < EventualConsistencyMs.
        Action validate = () => new TimeoutSettings().Validate();

        validate.Should().NotThrow();
    }

    [TestCaseSource(nameof(ZeroValueCases))]
    public void Validate_WithAZeroValue_NamesTheOffendingProperty(
        TimeoutSettings settings,
        string expectedPropertyName)
    {
        // One case per property on purpose. The guard is eight near-identical calls of the form
        // Check(ElementMs, nameof(ElementMs)), and the bug this catches is the copy/paste slip
        // where the value and the name come from different properties - which reports
        // "Timeouts.PageLoadMs must be greater than zero" for a bad ElementMs and sends the
        // reader to edit a value that was already correct.
        Action validate = () => settings.Validate();

        validate.Should().Throw<InvalidOperationException>()
            .WithMessage($"*Timeouts.{expectedPropertyName} must be greater than zero but was 0*")
            .WithMessage("*runsettings.json*");
    }

    [TestCaseSource(nameof(NegativeValueCases))]
    public void Validate_WithANegativeValue_ReportsTheActualValue(
        TimeoutSettings settings,
        string expectedPropertyName,
        int expectedValue)
    {
        // Negative values arrive from a placeholder such as -1 meaning "no timeout" in another
        // library's convention. Echoing the offending value back is what tells the reader the
        // file was read at all, rather than a default being validated.
        Action validate = () => settings.Validate();

        validate.Should().Throw<InvalidOperationException>()
            .WithMessage($"*Timeouts.{expectedPropertyName} must be greater than zero but was {expectedValue}*");
    }

    [TestCase(20_000, 20_000, TestName = "Validate_WhenPollIntervalEqualsTheBudget_Throws")]
    [TestCase(20_000, 25_000, TestName = "Validate_WhenPollIntervalExceedsTheBudget_Throws")]
    public void Validate_WhenPollIntervalIsNotSmallerThanTheBudget_ExplainsTheConsequence(
        int eventualConsistencyMs,
        int pollIntervalMs)
    {
        // Individually both values are legal, which is why this needs its own rule: the defect
        // is in their relationship. A poll interval at or above the total budget makes exactly
        // one attempt, so "wait up to 20 seconds for the order to settle" becomes "check once",
        // and the eventual-consistency waits stop waiting without anything reporting a problem.
        TimeoutSettings settings = Settings(
            eventualConsistencyMs: eventualConsistencyMs,
            pollIntervalMs: pollIntervalMs);

        Action validate = () => settings.Validate();

        validate.Should().Throw<InvalidOperationException>()
            .WithMessage($"*PollIntervalMs ({pollIntervalMs})*")
            .WithMessage($"*EventualConsistencyMs ({eventualConsistencyMs})*")
            .WithMessage("*single attempt*");
    }

    [Test]
    public void Validate_WhenPollIntervalIsJustBelowTheBudget_Succeeds()
    {
        // The boundary in the other direction. The rule is ">=", and an off-by-one that made it
        // ">" or "<=" would either reject a legal configuration or admit the broken one above.
        Action validate = () => Settings(eventualConsistencyMs: 251, pollIntervalMs: 250).Validate();

        validate.Should().NotThrow();
    }

    [Test]
    public void TimeSpanProjections_MatchTheirMillisecondValues()
    {
        // Every call site reads Timeouts.Element rather than the raw milliseconds, so a
        // projection wired to the wrong field would silently apply the wrong timeout everywhere
        // while validation still passed. Distinct values per property is what makes a
        // transposition detectable at all.
        TimeoutSettings settings = new()
        {
            ElementMs = 1_100,
            PageLoadMs = 1_200,
            SubmitMs = 1_300,
            AbsenceMs = 1_400,
            ApiRequestMs = 1_500,
            EventualConsistencyMs = 1_600,
            PollIntervalMs = 1_700,
            ApplicationStartupMs = 1_800
        };

        using (new AssertionScope())
        {
            settings.Element.Should().Be(TimeSpan.FromMilliseconds(1_100));
            settings.PageLoad.Should().Be(TimeSpan.FromMilliseconds(1_200));
            settings.Submit.Should().Be(TimeSpan.FromMilliseconds(1_300));
            settings.Absence.Should().Be(TimeSpan.FromMilliseconds(1_400));
            settings.ApiRequest.Should().Be(TimeSpan.FromMilliseconds(1_500));
            settings.EventualConsistency.Should().Be(TimeSpan.FromMilliseconds(1_600));
            settings.PollInterval.Should().Be(TimeSpan.FromMilliseconds(1_700));
            settings.ApplicationStartup.Should().Be(TimeSpan.FromMilliseconds(1_800));
        }
    }

    private static IEnumerable<TestCaseData> ZeroValueCases()
    {
        yield return Case(Settings(elementMs: 0), nameof(TimeoutSettings.ElementMs));
        yield return Case(Settings(pageLoadMs: 0), nameof(TimeoutSettings.PageLoadMs));
        yield return Case(Settings(submitMs: 0), nameof(TimeoutSettings.SubmitMs));
        yield return Case(Settings(absenceMs: 0), nameof(TimeoutSettings.AbsenceMs));
        yield return Case(Settings(apiRequestMs: 0), nameof(TimeoutSettings.ApiRequestMs));
        yield return Case(Settings(eventualConsistencyMs: 0), nameof(TimeoutSettings.EventualConsistencyMs));
        yield return Case(Settings(pollIntervalMs: 0), nameof(TimeoutSettings.PollIntervalMs));
        yield return Case(Settings(applicationStartupMs: 0), nameof(TimeoutSettings.ApplicationStartupMs));

        static TestCaseData Case(TimeoutSettings settings, string propertyName) =>
            new TestCaseData(settings, propertyName).SetName($"Validate_WhenZero_Names_{propertyName}");
    }

    private static IEnumerable<TestCaseData> NegativeValueCases()
    {
        yield return new TestCaseData(Settings(elementMs: -1), nameof(TimeoutSettings.ElementMs), -1)
            .SetName("Validate_WhenNegative_Names_ElementMs");

        yield return new TestCaseData(Settings(pollIntervalMs: -250), nameof(TimeoutSettings.PollIntervalMs), -250)
            .SetName("Validate_WhenNegative_Names_PollIntervalMs");

        yield return new TestCaseData(
                Settings(applicationStartupMs: int.MinValue),
                nameof(TimeoutSettings.ApplicationStartupMs),
                int.MinValue)
            .SetName("Validate_WhenNegative_Names_ApplicationStartupMs");
    }

    /// <summary>
    /// A valid catalogue with one value overridden. Named parameters at the call site keep each
    /// case readable as "the same sane configuration, except this".
    /// </summary>
    private static TimeoutSettings Settings(
        int elementMs = 5_000,
        int pageLoadMs = 15_000,
        int submitMs = 10_000,
        int absenceMs = 2_000,
        int apiRequestMs = 30_000,
        int eventualConsistencyMs = 20_000,
        int pollIntervalMs = 250,
        int applicationStartupMs = 60_000) =>
        new()
        {
            ElementMs = elementMs,
            PageLoadMs = pageLoadMs,
            SubmitMs = submitMs,
            AbsenceMs = absenceMs,
            ApiRequestMs = apiRequestMs,
            EventualConsistencyMs = eventualConsistencyMs,
            PollIntervalMs = pollIntervalMs,
            ApplicationStartupMs = applicationStartupMs
        };
}
