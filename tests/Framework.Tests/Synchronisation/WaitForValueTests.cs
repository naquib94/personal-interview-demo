using System.Diagnostics;
using QaFramework.Core.Synchronisation;

namespace Framework.Tests.Synchronisation;

/// <summary>
/// Tests for <see cref="Wait.ForValueAsync{T}(Func{Task{T}}, Func{T, bool}, string, TimeSpan, TimeSpan?, Func{T, bool}?)"/>.
/// </summary>
/// <remarks>
/// The reason this helper exists rather than a bare <c>UntilAsync</c> is its failure message: it
/// reports the last value it actually saw. Several tests here assert on that text, because a
/// refactor that dropped it would leave every test passing and every timeout undiagnosable -
/// exactly the class of regression that is invisible to application-level tests.
/// </remarks>
[TestFixture]
public sealed class WaitForValueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    [Test]
    public async Task ForValueAsync_WhenTheFirstValueMatches_ReturnsItWithoutWaiting()
    {
        Stopwatch elapsed = Stopwatch.StartNew();

        string status = await Wait.ForValueAsync(
            () => Task.FromResult("Filled"),
            value => value == "Filled",
            "the order status to reach 'Filled'",
            Timeout,
            PollInterval);

        elapsed.Stop();
        status.Should().Be("Filled");
        elapsed.Elapsed.Should().BeLessThan(Timeout / 2);
    }

    [Test]
    public async Task ForValueAsync_WhenTheValueMatchesLater_ReturnsTheMatchingValue()
    {
        // Returning the value rather than void is what removes the "wait, then read it again"
        // pattern from call sites - and that second read is a genuine race: the state can move
        // on between the wait succeeding and the caller re-reading it.
        Queue<string> statuses = new(["Pending", "Working", "Filled"]);

        string status = await Wait.ForValueAsync(
            () => Task.FromResult(statuses.Dequeue()),
            value => value == "Filled",
            "the order status to reach 'Filled'",
            Timeout,
            PollInterval);

        status.Should().Be("Filled");
    }

    [Test]
    public async Task ForValueAsync_WhenTheValueNeverMatches_ReportsTheLastObservedValue()
    {
        // The whole diagnostic value of this helper. "Expected 'Filled' but the last observed
        // value was 'Pending'" identifies the defect from a CI log alone; "timed out waiting for
        // condition" requires re-running the suite with a debugger attached.
        Func<Task<string>> probe = () => Task.FromResult("Pending");

        Func<Task> wait = () => Wait.ForValueAsync(
            probe,
            value => value == "Filled",
            "the order status to reach 'Filled'",
            Timeout,
            PollInterval);

        (await wait.Should().ThrowAsync<TimeoutException>())
            .WithMessage("*Timed out waiting for the order status to reach 'Filled'*")
            .WithMessage("*last observed value*")
            .WithMessage("*was 'Pending'*")
            .WithMessage("*attempt(s)*");
    }

    [Test]
    public async Task ForValueAsync_WhenTheLastObservedValueIsNull_SaysSoRatherThanCrashing()
    {
        // A diagnostic helper must never be the cause of the failure it is describing. A null
        // reference dereferenced while building a timeout message replaces a useful timeout with
        // a NullReferenceException from inside the framework.
        Func<Task> wait = () => Wait.ForValueAsync<string?>(
            () => Task.FromResult<string?>(null),
            value => value == "Filled",
            "the order status to reach 'Filled'",
            Timeout,
            PollInterval);

        (await wait.Should().ThrowAsync<TimeoutException>()).WithMessage("*was 'null'*");
    }

    [Test]
    public async Task ForValueAsync_WhenTheProbeNeverSucceeds_DistinguishesThatFromAnUnmatchedValue()
    {
        // "Never successfully read" and "read, but always 'Pending'" call for completely
        // different responses from a human: one is an environment or test-code problem, the
        // other is a finding about the product. Collapsing them into one message wastes the
        // reader's time on every occurrence.
        // Typed explicitly rather than inlined: a lambda whose body is only a throw is
        // convertible to both ForValueAsync overloads, so the call would be ambiguous.
        Func<Task<string>> probe = () => throw new HttpRequestException("connection refused");

        Func<Task> wait = () => Wait.ForValueAsync(
            probe,
            value => value == "Filled",
            "the order status to reach 'Filled'",
            Timeout,
            PollInterval);

        (await wait.Should().ThrowAsync<TimeoutException>())
            .WithMessage("*never successfully read*")
            .WithMessage("*HttpRequestException: connection refused*");
    }

    [Test]
    public async Task ForValueAsync_WithATerminalValue_AbandonsTheWaitLongBeforeTheTimeout()
    {
        // An order that reaches 'Rejected' will never reach 'Filled'. Without the short-circuit
        // the test spends its entire budget watching a settled value and then reports a timeout,
        // which describes the framework's patience rather than the product's behaviour.
        //
        // The elapsed-time bound is the point of the test: a terminal check that ran but did not
        // exit the loop would still produce the right exception type at the deadline.
        TimeSpan generousTimeout = TimeSpan.FromSeconds(5);
        int attempts = 0;
        Stopwatch elapsed = Stopwatch.StartNew();

        Func<Task> wait = () => Wait.ForValueAsync(
            () => { attempts++; return Task.FromResult("Rejected"); },
            value => value == "Filled",
            "the order status to reach 'Filled'",
            generousTimeout,
            PollInterval,
            isTerminal: value => value is "Rejected" or "Cancelled");

        (await wait.Should().ThrowAsync<WaitAbandonedException>())
            .WithMessage("*Stopped waiting for the order status to reach 'Filled'*")
            .WithMessage("*'Rejected' is a terminal state*")
            .WithMessage("*rather than waiting for the full 5s timeout*");

        elapsed.Stop();
        attempts.Should().Be(1);
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task ForValueAsync_WhenAbandoned_DoesNotReportATimeout()
    {
        // A distinct exception type, asserted explicitly, because the two failures mean
        // different things: "it never arrived" versus "it went somewhere it cannot come back
        // from". Reporting the second as a timeout invites the reader to raise the timeout.
        Func<Task> wait = () => Wait.ForValueAsync(
            () => Task.FromResult("Rejected"),
            value => value == "Filled",
            "the order status to reach 'Filled'",
            Timeout,
            PollInterval,
            isTerminal: value => value == "Rejected");

        Exception thrown = (await wait.Should().ThrowAsync<WaitAbandonedException>()).Which;

        thrown.Should().NotBeAssignableTo<TimeoutException>();
    }

    [Test]
    public async Task ForValueAsync_WhenAbandoned_IsNotSwallowedByItsOwnRetryLoop()
    {
        // WaitAbandonedException is thrown from inside the try block that catches transient
        // failures, so it has to be explicitly excluded from the transient set. If it were not,
        // the short-circuit would be caught, recorded as "not ready yet", and the wait would run
        // to its full timeout - the exact behaviour the short-circuit exists to prevent, with a
        // misleading timeout message on top.
        //
        // The attempt count is what proves it: a swallowed abandon would poll repeatedly.
        int attempts = 0;

        Func<Task> wait = () => Wait.ForValueAsync(
            () => { attempts++; return Task.FromResult("Rejected"); },
            value => value == "Filled",
            "the order status to reach 'Filled'",
            Timeout,
            PollInterval,
            isTerminal: _ => true);

        await wait.Should().ThrowAsync<WaitAbandonedException>();

        attempts.Should().Be(1);
    }

    [Test]
    public async Task ForValueAsync_WhenAValueIsBothMatchingAndTerminal_PrefersTheMatch()
    {
        // Ordering matters and is easy to get backwards. 'Filled' is itself a terminal state, so
        // a terminal check placed before the predicate would abandon the wait at the moment of
        // success and fail every happy-path test in the suite.
        string status = await Wait.ForValueAsync(
            () => Task.FromResult("Filled"),
            value => value == "Filled",
            "the order status to reach 'Filled'",
            Timeout,
            PollInterval,
            isTerminal: value => value is "Filled" or "Rejected");

        status.Should().Be("Filled");
    }

    [TestCase(typeof(NullReferenceException))]
    [TestCase(typeof(ArgumentException))]
    public async Task ForValueAsync_WithANonTransientProbeFailure_PropagatesItImmediately(Type exceptionType)
    {
        // Same deliberate decision as UntilAsync: a bug in the probe must arrive as itself, with
        // its stack trace, on the first attempt. Swallowing it would trade a precise pointer to
        // the broken line for a timeout that blames the application.
        int attempts = 0;

        Func<Task<string>> probe = () =>
        {
            attempts++;
            throw (Exception)Activator.CreateInstance(exceptionType)!;
        };

        Func<Task> wait = () => Wait.ForValueAsync(
            probe,
            value => value == "Filled",
            "the order status to reach 'Filled'",
            Timeout,
            PollInterval);

        (await wait.Should().ThrowAsync<Exception>()).Which.Should().BeOfType(exceptionType);

        attempts.Should().Be(1);
    }

    [Test]
    public async Task ForValueAsync_WithASynchronousProbe_BehavesIdenticallyToTheAsyncOverload()
    {
        // The synchronous overload exists so a database or in-memory probe does not have to be
        // wrapped in Task.FromResult at every call site. It must not acquire its own copy of the
        // polling logic - two implementations of the same wait is how the two drift apart.
        int attempts = 0;

        int value = await Wait.ForValueAsync(
            () => ++attempts,
            observed => observed >= 3,
            "a synchronous counter to reach three",
            Timeout,
            PollInterval);

        value.Should().Be(3);
        attempts.Should().Be(3);
    }

    [Test]
    public async Task ForValueAsync_WithASynchronousProbe_ProducesTheSameDiagnosticMessage()
    {
        // Confirms the overload delegates rather than reimplementing: the last-observed-value
        // text is the property most likely to be lost in a duplicated implementation.
        Func<Task> wait = () => Wait.ForValueAsync(
            () => "Pending",
            value => value == "Filled",
            "the order status to reach 'Filled'",
            Timeout,
            PollInterval);

        (await wait.Should().ThrowAsync<TimeoutException>()).WithMessage("*was 'Pending'*");
    }
}
