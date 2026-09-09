namespace QaFramework.Mobile.Drivers;

/// <summary>
/// Ambient access to the current session, for the few places that cannot take a constructor
/// argument.
/// </summary>
/// <remarks>
/// The primary mechanism in this framework is constructor injection: hooks resolve a driver and
/// register it in Reqnroll's container, and screen objects and step classes receive it. That is
/// the right default, because it makes the dependency visible and lets scenarios run in parallel
/// without thinking about it.
/// <para>
/// This class exists for the residue - a reporter attachment, a diagnostic helper invoked from
/// somewhere that has no container. It is backed by <see cref="AsyncLocal{T}"/>, and the choice
/// matters:
/// </para>
/// <list type="bullet">
/// <item><b>A plain static field</b> is shared by every scenario in the process, so two parallel
/// scenarios overwrite each other's driver. Whichever one finishes second tears down a session
/// the other is still using.</item>
/// <item><b><c>[ThreadStatic]</c></b> looks like the fix and is not. An async test does not stay
/// on one thread: the continuation after <c>await</c> resumes on whichever thread-pool thread is
/// free, and thread-static state does not travel with it. The value written before the first
/// <c>await</c> is simply missing afterwards - intermittently, depending on whether the awaited
/// operation completed synchronously, which is the worst possible failure signature. Modern test
/// runners are async from the entry point down, so this is not a corner case.</item>
/// <item><b><see cref="AsyncLocal{T}"/></b> flows with the execution context, so it survives
/// awaits and thread hops, and each scenario's async flow gets its own value.</item>
/// </list>
/// <para>
/// Even so: prefer the constructor. Ambient state that is easy to reach is ambient state that
/// gets reached for, and the result is a framework whose dependencies are invisible.
/// </para>
/// </remarks>
public static class MobileDriverContext
{
    private static readonly AsyncLocal<IMobileDriver?> CurrentDriver = new();

    /// <summary>The session for the current async flow, or null when none is active.</summary>
    public static IMobileDriver? Current => CurrentDriver.Value;

    /// <summary>Associates a session with the current async flow. Called by the suite's hooks.</summary>
    public static void Set(IMobileDriver? driver) => CurrentDriver.Value = driver;

    /// <summary>
    /// The current session, or a failure that explains the likely cause.
    /// </summary>
    public static IMobileDriver Require() =>
        Current ?? throw new InvalidOperationException(
            "No mobile session is active for this execution context. Either the scenario's " +
            "[BeforeScenario] hook did not run, or the caller is on an execution context that " +
            "did not inherit the value - a fire-and-forget task started before the session was " +
            "set, for instance. Prefer taking IMobileDriver as a constructor argument.");
}
