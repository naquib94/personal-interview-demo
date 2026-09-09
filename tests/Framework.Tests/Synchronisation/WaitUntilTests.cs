using System.Diagnostics;
using QaFramework.Core.Synchronisation;

namespace Framework.Tests.Synchronisation;

/// <summary>
/// Tests for <see cref="Wait.UntilAsync"/>.
/// </summary>
/// <remarks>
/// Timeouts here are deliberately short - a few hundred milliseconds - so the fixture costs
/// under a second. Elapsed time is asserted as a <i>bound</i> rather than a value: "it returned
/// well before the timeout" is the property that matters and it holds on a loaded agent, whereas
/// "it returned in 12ms" is a promise about the machine, not about the code.
/// </remarks>
[TestFixture]
public sealed class WaitUntilTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    [Test]
    public async Task UntilAsync_WhenTheConditionIsAlreadyTrue_ReturnsWithoutWaiting()
    {
        // The reason this helper replaces Thread.Sleep. A sleep pays its full cost on every
        // run; a poll pays nothing when the condition already holds. If a refactor moved the
        // delay to the top of the loop, every wait in the suite would silently acquire a
        // guaranteed minimum cost - invisible in a single test, minutes across a suite.
        Stopwatch elapsed = Stopwatch.StartNew();

        await Wait.UntilAsync(() => Task.FromResult(true), "an already-true condition", Timeout, PollInterval);

        elapsed.Stop();
        elapsed.Elapsed.Should().BeLessThan(Timeout / 2);
    }

    [Test]
    public async Task UntilAsync_WhenTheConditionBecomesTrue_StopsPolling()
    {
        // Proves the loop exits on success rather than running to the deadline and reporting
        // success afterwards - which would pass a naive "did it throw?" test while making every
        // wait cost its full timeout.
        int attempts = 0;

        await Wait.UntilAsync(
            () => Task.FromResult(++attempts >= 3),
            "a condition that becomes true on the third attempt",
            Timeout,
            PollInterval);

        attempts.Should().Be(3);
    }

    [Test]
    public async Task UntilAsync_WhenTheConditionIsNeverTrue_ExplainsWhatItWasWaitingFor()
    {
        // The description is a required parameter precisely so this message can exist. "Timed
        // out waiting for condition" in a CI log costs the reader a code search; the phrase
        // below identifies the wait immediately.
        Func<Task> wait = () => Wait.UntilAsync(
            () => Task.FromResult(false),
            "the order status to reach 'Filled'",
            Timeout,
            PollInterval);

        (await wait.Should().ThrowAsync<TimeoutException>())
            .WithMessage("*Timed out waiting for the order status to reach 'Filled'*")
            .WithMessage("*attempt(s)*");
    }

    [Test]
    public async Task UntilAsync_WhenItTimesOut_ReportsMoreThanOneAttempt()
    {
        // The attempt count is what distinguishes "polled 20 times and the value never changed"
        // from "polled once because the interval was misconfigured". The second is a framework
        // bug wearing the costume of a product bug, and without the count in the message there
        // is nothing to tell them apart.
        int attempts = 0;

        Func<Task> wait = () => Wait.UntilAsync(
            () => { attempts++; return Task.FromResult(false); },
            "a condition that never holds",
            Timeout,
            PollInterval);

        (await wait.Should().ThrowAsync<TimeoutException>())
            .WithMessage($"*and {attempts} attempt(s)*");

        attempts.Should().BeGreaterThan(1);
    }

    [Test]
    public async Task UntilAsync_WithATransientProbeFailure_RetriesAndCanStillSucceed()
    {
        // A probe that calls an API which is still starting up throws HttpRequestException for
        // the first second or so. Treating that as a hard failure would make the suite depend on
        // the application being warm, which is the classic "passes locally, fails in CI" split.
        int attempts = 0;

        await Wait.UntilAsync(
            () => ++attempts < 3
                ? throw new HttpRequestException("connection refused")
                : Task.FromResult(true),
            "an endpoint that is still starting up",
            Timeout,
            PollInterval);

        attempts.Should().Be(3);
    }

    [Test]
    public async Task UntilAsync_WithAPersistentTransientFailure_ReportsTheUnderlyingCause()
    {
        // A permanently broken probe must not degrade to a bare timeout. Without the final
        // exception in the message, "timed out waiting for the service to report healthy" hides
        // the fact that the URL was wrong and nothing was ever reachable.
        Func<Task> wait = () => Wait.UntilAsync(
            () => throw new IOException("the pipe is broken"),
            "the service to report healthy",
            Timeout,
            PollInterval);

        (await wait.Should().ThrowAsync<TimeoutException>())
            .WithMessage("*The final attempt failed with: IOException: the pipe is broken*");
    }

    [Test]
    public async Task UntilAsync_WhenATransientFailureIsFollowedByACleanFalse_ForgetsTheStaleException()
    {
        // Reporting an exception from attempt 2 alongside a timeout at attempt 40 would be
        // actively misleading: the reader would chase a transient error that resolved itself
        // thirty-eight attempts ago.
        int attempts = 0;

        Func<Task> wait = () => Wait.UntilAsync(
            () => ++attempts == 1
                ? throw new IOException("a one-off blip")
                : Task.FromResult(false),
            "a condition that never holds",
            Timeout,
            PollInterval);

        (await wait.Should().ThrowAsync<TimeoutException>())
            .WithMessage("*Timed out waiting for a condition that never holds*")
            .Which.Message.Should().NotContain("a one-off blip");
    }

    [TestCase(typeof(NullReferenceException))]
    [TestCase(typeof(ArgumentException))]
    [TestCase(typeof(ArgumentNullException))]
    [TestCase(typeof(IndexOutOfRangeException))]
    public async Task UntilAsync_WithANonTransientProbeFailure_PropagatesItImmediately(Type exceptionType)
    {
        // The most important design decision in this class, and the one most frameworks get
        // wrong by catching Exception. A NullReferenceException in the probe is a bug in the
        // test code; swallowing it converts a one-second stack trace pointing at the exact line
        // into a full-timeout failure that reports "the condition was never true" and discards
        // the only useful evidence.
        //
        // Asserted as "propagates" and "immediately": one attempt, not a retried loop.
        int attempts = 0;
        Stopwatch elapsed = Stopwatch.StartNew();

        Func<Task> wait = () => Wait.UntilAsync(
            () =>
            {
                attempts++;
                throw (Exception)Activator.CreateInstance(exceptionType)!;
            },
            "a condition whose probe is broken",
            Timeout,
            PollInterval);

        (await wait.Should().ThrowAsync<Exception>()).Which.Should().BeOfType(exceptionType);

        elapsed.Stop();
        attempts.Should().Be(1);
        elapsed.Elapsed.Should().BeLessThan(Timeout);
    }

    [Test]
    public async Task UntilAsync_ReportsEveryAttemptToTheCallback()
    {
        // The callback is how a long wait produces progress output instead of appearing hung.
        // Numbering from one and never repeating is the contract a reporter relies on.
        List<int> reported = [];

        Func<Task> wait = () => Wait.UntilAsync(
            () => Task.FromResult(false),
            "a condition that never holds",
            TimeSpan.FromMilliseconds(150),
            PollInterval,
            onAttempt: (attempt, _) => reported.Add(attempt));

        await wait.Should().ThrowAsync<TimeoutException>();

        reported.Should().HaveCountGreaterThan(1);
        reported.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        reported[0].Should().Be(1);
    }

    [Test]
    public async Task UntilAsync_WithAnAlreadyElapsedTimeout_StillReportsATimeout()
    {
        // A zero timeout can only reach the suite through a misconfiguration, and validation
        // rejects it - but the helper must not respond by looping forever or by returning
        // success without evaluating anything. Failing with a timeout naming the wait is the
        // only defensible behaviour left.
        int attempts = 0;

        Func<Task> wait = () => Wait.UntilAsync(
            () => { attempts++; return Task.FromResult(true); },
            "a wait with no budget",
            TimeSpan.Zero,
            PollInterval);

        (await wait.Should().ThrowAsync<TimeoutException>())
            .WithMessage("*a wait with no budget*");

        attempts.Should().Be(0);
    }
}
