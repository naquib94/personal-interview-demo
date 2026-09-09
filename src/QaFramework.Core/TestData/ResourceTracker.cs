using QaFramework.Core.Logging;

namespace QaFramework.Core.TestData;

/// <summary>
/// Records everything a scenario creates and removes it afterwards.
/// </summary>
/// <remarks>
/// This addresses the single most common omission in real automation suites: no cleanup at all.
/// The consequences build slowly and then all at once - a shared environment fills with
/// hundreds of thousands of orphaned records, grids get slower, "should return 3 results"
/// assertions start failing because they now return 4,000, and the suite quietly becomes
/// order-dependent.
/// <para>
/// The design is a stack of undo actions rather than a list of ids, which matters for two
/// reasons. First, reverse order respects foreign keys without the tracker needing to know
/// anything about the schema. Second, the caller supplies the removal logic, so the tracker
/// stays generic - it cleans up API resources, database rows and uploaded files identically.
/// </para>
/// <para>
/// Cleanup failures are collected and reported, never thrown. A scenario that passed must not
/// be reported as failed because teardown could not delete something; and equally, a teardown
/// problem must not be silent, or the mess accumulates invisibly. Reporting an aggregated
/// warning is the honest middle ground.
/// </para>
/// </remarks>
public sealed class ResourceTracker
{
    private readonly Stack<TrackedResource> resources = new();

    public int Count => resources.Count;

    /// <summary>
    /// Registers a resource and how to remove it.
    /// </summary>
    /// <param name="description">
    /// Human-readable, e.g. "order QA-4F2A9C-0001". Used in cleanup logs and in the warning
    /// raised if removal fails, so it must identify the resource well enough to delete it by
    /// hand.
    /// </param>
    public void Track(string description, Func<Task> remove) =>
        resources.Push(new TrackedResource(description, remove));

    /// <summary>
    /// Removes every tracked resource, most recent first.
    /// </summary>
    /// <returns>
    /// The descriptions of resources that could not be removed. Empty means a clean teardown.
    /// Returned rather than thrown so the caller - normally an AfterScenario hook - decides
    /// how loudly to complain.
    /// </returns>
    public async Task<IReadOnlyList<string>> CleanUpAsync()
    {
        List<string> failures = [];

        while (resources.Count > 0)
        {
            TrackedResource resource = resources.Pop();

            try
            {
                await resource.Remove();
                TestLog.Info($"Cleaned up {resource.Description}.");
            }
            catch (Exception ex)
            {
                // Deliberately broad. One resource that refuses to delete must not prevent the
                // remaining twelve from being cleaned up.
                failures.Add($"{resource.Description} ({ex.GetType().Name}: {ex.Message})");
            }
        }

        if (failures.Count > 0)
            TestLog.Warning(
                $"{failures.Count} resource(s) could not be cleaned up and may need removing " +
                $"manually: {string.Join("; ", failures)}");

        return failures;
    }

    private sealed record TrackedResource(string Description, Func<Task> Remove);
}
