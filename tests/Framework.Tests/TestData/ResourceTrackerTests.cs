using QaFramework.Core.Logging;
using QaFramework.Core.TestData;

namespace Framework.Tests.TestData;

/// <summary>
/// Tests for the cleanup contract of <see cref="ResourceTracker"/>.
/// </summary>
/// <remarks>
/// Cleanup failures are invisible by design - they are reported, never thrown - which is the
/// right behaviour and also the reason this class needs tests of its own. If reverse ordering
/// regressed, or one failing removal started aborting the rest, nothing in the suite would turn
/// red. The environment would simply fill up with orphaned rows over a few weeks until
/// "should return 3 results" started returning four thousand.
/// <para>
/// The fixture is <see cref="NonParallelizableAttribute"/> because one test replaces the
/// process-global <see cref="TestLog.Sink"/>.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class ResourceTrackerTests
{
    [Test]
    public async Task CleanUpAsync_RemovesResourcesInReverseRegistrationOrder()
    {
        // This is the whole reason the tracker is a stack rather than a list. A scenario creates
        // an account, then an order against it; removing them in registration order fails on a
        // foreign key. Reverse order is correct without the tracker knowing anything about the
        // schema - and it is exactly the kind of detail a well-meaning refactor to a List<T>
        // with a foreach would quietly reverse.
        List<string> removed = [];
        ResourceTracker tracker = new();

        tracker.Track("the account", () => Record(removed, "the account"));
        tracker.Track("the order", () => Record(removed, "the order"));
        tracker.Track("the order amendment", () => Record(removed, "the order amendment"));

        await tracker.CleanUpAsync();

        removed.Should().Equal("the order amendment", "the order", "the account");
    }

    [Test]
    public async Task CleanUpAsync_WithNothingTracked_ReportsACleanTeardown()
    {
        // Every scenario runs this, including the ones that created nothing. An empty tracker
        // that reported a failure, or threw, would make teardown noisy for the majority case.
        ResourceTracker tracker = new();

        IReadOnlyList<string> failures = await tracker.CleanUpAsync();

        failures.Should().BeEmpty();
        tracker.Count.Should().Be(0);
    }

    [Test]
    public async Task CleanUpAsync_EmptiesTheTracker()
    {
        // Count is how a hook decides whether to log anything. A tracker that cleaned up but
        // retained its entries would make a second teardown delete the same resources twice -
        // and the second attempt would fail, reporting phantom cleanup problems.
        ResourceTracker tracker = new();
        tracker.Track("the order", () => Task.CompletedTask);
        tracker.Count.Should().Be(1);

        await tracker.CleanUpAsync();

        tracker.Count.Should().Be(0);
    }

    [Test]
    public async Task CleanUpAsync_CalledTwice_DoesNothingTheSecondTime()
    {
        // Teardown hooks get called more than once more often than anyone expects - a nested
        // hook, a retry, a failure inside another hook. Idempotence is what stops that turning
        // into duplicate deletions and spurious failure reports.
        int removals = 0;
        ResourceTracker tracker = new();
        tracker.Track("the order", () => { removals++; return Task.CompletedTask; });

        await tracker.CleanUpAsync();
        IReadOnlyList<string> secondPass = await tracker.CleanUpAsync();

        removals.Should().Be(1);
        secondPass.Should().BeEmpty();
    }

    [Test]
    public async Task CleanUpAsync_WhenOneRemovalFails_StillRemovesTheOthers()
    {
        // The behaviour that keeps a shared environment usable. One resource that refuses to
        // delete - because a downstream system locked it, or it was already gone - must not
        // strand the other twelve. Without this, a single stubborn row causes unbounded
        // accumulation from that point on.
        List<string> removed = [];
        ResourceTracker tracker = new();

        tracker.Track("the account", () => Record(removed, "the account"));
        tracker.Track("the locked order", () => throw new InvalidOperationException("the order is locked"));
        tracker.Track("the amendment", () => Record(removed, "the amendment"));

        await tracker.CleanUpAsync();

        removed.Should().Equal("the amendment", "the account");
    }

    [Test]
    public async Task CleanUpAsync_ReturnsFailuresRatherThanThrowingThem()
    {
        // A scenario that passed must not be reported as failed because teardown could not
        // delete something: that is a false negative, and false negatives are what teach a team
        // to ignore the suite. Returning the failures leaves the decision with the hook.
        ResourceTracker tracker = new();
        tracker.Track("the locked order", () => throw new InvalidOperationException("the order is locked"));

        Func<Task> cleanUp = () => tracker.CleanUpAsync();

        await cleanUp.Should().NotThrowAsync();
    }

    [Test]
    public async Task CleanUpAsync_NamesEachFailedResourceAndItsCause()
    {
        // The returned descriptions are what somebody uses to delete the leftovers by hand, so
        // "1 resource could not be removed" is useless and the resource's own description is
        // the minimum. The exception type and message are included because "already deleted" and
        // "permission denied" call for completely different follow-up.
        ResourceTracker tracker = new();
        tracker.Track("order QA-4F2A9C-0001", () => throw new InvalidOperationException("the order is locked"));
        tracker.Track("account QA-4F2A9C-0002", () => throw new TimeoutException("the delete endpoint timed out"));

        IReadOnlyList<string> failures = await tracker.CleanUpAsync();

        using (new AssertionScope())
        {
            failures.Should().HaveCount(2);
            failures.Should().Contain("order QA-4F2A9C-0001 (InvalidOperationException: the order is locked)");
            failures.Should().Contain("account QA-4F2A9C-0002 (TimeoutException: the delete endpoint timed out)");
        }
    }

    [Test]
    public async Task CleanUpAsync_WithAFailure_RaisesOneAggregatedWarning()
    {
        // The other half of "never thrown": a teardown problem must not be silent either, or the
        // mess accumulates invisibly, which is the failure mode this class exists to prevent.
        // One aggregated warning rather than one per resource, so a scenario that leaked a dozen
        // rows produces a line somebody reads instead of a wall somebody scrolls past.
        Action<LogEntry> originalSink = TestLog.Sink;
        List<LogEntry> captured = [];

        try
        {
            TestLog.Sink = captured.Add;

            ResourceTracker tracker = new();
            tracker.Track("order QA-0001", () => throw new InvalidOperationException("locked"));
            tracker.Track("order QA-0002", () => Task.CompletedTask);

            await tracker.CleanUpAsync();
        }
        finally
        {
            // Restored in a finally: leaving a captured sink installed would swallow every log
            // line for the rest of the run, and a framework test that breaks the framework's
            // logging is a poor trade.
            TestLog.Sink = originalSink;
        }

        LogEntry warning = captured.Should().ContainSingle(entry => entry.Level == LogLevel.Warning).Which;

        warning.Message.Should().Contain("1 resource(s) could not be cleaned up");
        warning.Message.Should().Contain("order QA-0001");
        warning.Message.Should().Contain("may need removing");
    }

    [Test]
    public async Task CleanUpAsync_WithNoFailures_RaisesNoWarning()
    {
        // The common case has to stay quiet. A warning on every clean teardown trains readers to
        // filter warnings out, which is how the genuine one above gets missed.
        Action<LogEntry> originalSink = TestLog.Sink;
        List<LogEntry> captured = [];

        try
        {
            TestLog.Sink = captured.Add;

            ResourceTracker tracker = new();
            tracker.Track("order QA-0001", () => Task.CompletedTask);

            await tracker.CleanUpAsync();
        }
        finally
        {
            TestLog.Sink = originalSink;
        }

        captured.Should().NotContain(entry => entry.Level == LogLevel.Warning);
        captured.Should().Contain(entry => entry.Message.Contains("Cleaned up order QA-0001"));
    }

    private static Task Record(List<string> removed, string description)
    {
        removed.Add(description);
        return Task.CompletedTask;
    }
}
