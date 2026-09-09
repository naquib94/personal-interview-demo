using System.Diagnostics;

namespace QaFramework.Core.Synchronisation;

/// <summary>
/// Deadline-based polling for conditions that become true rather than being true.
/// </summary>
/// <remarks>
/// This type replaces <c>Thread.Sleep</c>. The distinction matters: a sleep is a guess that is
/// simultaneously too long (on a fast machine, wasting time on every run) and too short (on a
/// loaded CI agent, producing a flaky failure). A poll returns as soon as the condition holds
/// and fails with a precise message when it never does.
/// <para>
/// Three details make the difference between a helper that works and one that hides problems:
/// </para>
/// <list type="number">
/// <item><b>Deadline, not attempt count.</b> "Give up after 20 seconds" is a statement about
/// the system's contract. "Give up after 40 attempts" depends on how fast the probe happens to
/// run, so the effective timeout changes with machine load - which is precisely when you need
/// it to be stable.</item>
/// <item><b>The last observed value appears in the failure message.</b> "Expected status
/// 'Filled' but the last observed value after 20.0s over 79 attempts was 'Pending'" is
/// diagnosable. "Timed out waiting for condition" is not, and a team that sees the second
/// message enough times stops trusting the suite.</item>
/// <item><b>Terminal states short-circuit.</b> See the <c>isTerminal</c> parameter on
/// <see cref="ForValue{T}"/>. If an order reaches 'Rejected' it will never become 'Filled', so
/// burning the remaining 19 seconds tells you nothing and makes the suite slower to fail.</item>
/// </list>
/// </remarks>
public static class Wait
{
    /// <summary>
    /// Polls until <paramref name="condition"/> returns true.
    /// </summary>
    /// <param name="description">
    /// What is being waited for, phrased so it reads correctly after "Timed out waiting for".
    /// Required, not optional - an unnamed wait is an undiagnosable wait.
    /// </param>
    public static async Task UntilAsync(
        Func<Task<bool>> condition,
        string description,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        Action<int, TimeSpan>? onAttempt = null)
    {
        TimeSpan interval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        Stopwatch elapsed = Stopwatch.StartNew();
        int attempts = 0;
        Exception? lastException = null;

        while (elapsed.Elapsed < timeout)
        {
            attempts++;
            onAttempt?.Invoke(attempts, elapsed.Elapsed);

            try
            {
                if (await condition())
                    return;

                lastException = null;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                // A probe that throws is treated as "not yet true", but the exception is kept
                // so that a timeout caused by a persistently broken probe reports the real
                // cause instead of a bare timeout.
                lastException = ex;
            }

            await Task.Delay(interval);
        }

        string detail = lastException is null
            ? string.Empty
            : $" The final attempt failed with: {lastException.GetType().Name}: {lastException.Message}";

        throw new TimeoutException(
            $"Timed out waiting for {description}. Gave up after {elapsed.Elapsed.TotalSeconds:F1}s " +
            $"and {attempts} attempt(s).{detail}");
    }

    /// <summary>
    /// Polls until <paramref name="probe"/> produces a value that satisfies
    /// <paramref name="predicate"/>, then returns it.
    /// </summary>
    /// <param name="isTerminal">
    /// Optional early-exit. Returns true when the observed value is in a state it can never
    /// leave, so continuing to poll is pointless. Without this, a test asserting that an order
    /// reaches 'Filled' spends its entire timeout budget watching an order that was rejected in
    /// the first 50ms, and then reports a timeout instead of the actual outcome.
    /// </param>
    public static async Task<T> ForValueAsync<T>(
        Func<Task<T>> probe,
        Func<T, bool> predicate,
        string description,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        Func<T, bool>? isTerminal = null)
    {
        TimeSpan interval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        Stopwatch elapsed = Stopwatch.StartNew();
        int attempts = 0;
        T? lastObserved = default;
        bool observed = false;
        Exception? lastException = null;

        while (elapsed.Elapsed < timeout)
        {
            attempts++;

            try
            {
                T value = await probe();
                lastObserved = value;
                observed = true;
                lastException = null;

                if (predicate(value))
                    return value;

                if (isTerminal is not null && isTerminal(value))
                    throw new WaitAbandonedException(
                        $"Stopped waiting for {description} after {elapsed.Elapsed.TotalSeconds:F1}s: " +
                        $"the observed value '{Describe(value)}' is a terminal state, so the expected " +
                        "condition can no longer become true. Failing now rather than waiting for the " +
                        $"full {timeout.TotalSeconds:F0}s timeout.");
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                lastException = ex;
            }

            await Task.Delay(interval);
        }

        string lastValue = observed ? $"'{Describe(lastObserved)}'" : "never successfully read";
        string detail = lastException is null
            ? string.Empty
            : $" The final attempt failed with: {lastException.GetType().Name}: {lastException.Message}";

        throw new TimeoutException(
            $"Timed out waiting for {description}. The last observed value after " +
            $"{elapsed.Elapsed.TotalSeconds:F1}s and {attempts} attempt(s) was {lastValue}.{detail}");
    }

    /// <summary>Synchronous convenience overload for non-async probes.</summary>
    public static Task<T> ForValueAsync<T>(
        Func<T> probe,
        Func<T, bool> predicate,
        string description,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        Func<T, bool>? isTerminal = null) =>
        ForValueAsync(() => Task.FromResult(probe()), predicate, description, timeout, pollInterval, isTerminal);

    /// <summary>
    /// Determines which exceptions from a probe mean "not ready yet" rather than "this is
    /// broken".
    /// </summary>
    /// <remarks>
    /// Kept narrow on purpose. Swallowing every exception is the standard mistake here: it
    /// turns a NullReferenceException in the probe itself into a 20-second timeout, discarding
    /// the stack trace that would have identified the bug in one second.
    /// <para>
    /// <see cref="WaitAbandonedException"/> is explicitly excluded so the terminal-state
    /// short-circuit cannot be caught by its own retry loop.
    /// </para>
    /// </remarks>
    private static bool IsTransient(Exception ex) => ex switch
    {
        WaitAbandonedException => false,
        TimeoutException => true,
        HttpRequestException => true,
        TaskCanceledException => true,
        IOException => true,
        InvalidOperationException => true,
        KeyNotFoundException => true,
        _ => false
    };

    private static string Describe<T>(T? value) => value?.ToString() ?? "null";
}

/// <summary>
/// Thrown when a wait is abandoned early because the condition became unreachable.
/// </summary>
/// <remarks>
/// A distinct type rather than a TimeoutException, because the two mean different things and a
/// reader of the failure deserves to know which happened: "it never arrived" versus "it went
/// somewhere it cannot come back from".
/// </remarks>
public sealed class WaitAbandonedException(string message) : Exception(message);
