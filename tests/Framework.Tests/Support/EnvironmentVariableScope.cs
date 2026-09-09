namespace Framework.Tests.Support;

/// <summary>
/// Sets environment variables for the duration of a test and restores them afterwards.
/// </summary>
/// <remarks>
/// Environment variables are process-global, so a test that sets one and forgets to unset it
/// changes the behaviour of every test that runs after it - producing a suite whose result
/// depends on execution order. That is the exact failure this whole repository argues against,
/// so it is worth a small helper rather than a hand-rolled try/finally per test.
/// <para>
/// Restoring the <i>original</i> value rather than deleting the variable matters: a developer
/// or an agent may legitimately have QA_ENVIRONMENT set in their shell, and a test run must not
/// silently change their session.
/// </para>
/// </remarks>
internal sealed class EnvironmentVariableScope : IDisposable
{
    private readonly List<(string Name, string? Original)> saved = [];

    /// <summary>
    /// Sets a variable, or clears it when <paramref name="value"/> is null.
    /// </summary>
    public EnvironmentVariableScope Set(string name, string? value)
    {
        // Recorded before the first mutation, and only once per name, so repeated Set calls for
        // the same variable still restore the value the test started with.
        if (saved.All(entry => entry.Name != name))
            saved.Add((name, Environment.GetEnvironmentVariable(name)));

        Environment.SetEnvironmentVariable(name, value);
        return this;
    }

    /// <summary>Clears a variable that the host machine may or may not have set.</summary>
    public EnvironmentVariableScope Clear(string name) => Set(name, null);

    public void Dispose()
    {
        // Reverse order for symmetry with the sets. Irrelevant while each name is recorded once,
        // but free, and it keeps the helper correct if that ever changes.
        for (int index = saved.Count - 1; index >= 0; index--)
            Environment.SetEnvironmentVariable(saved[index].Name, saved[index].Original);

        saved.Clear();
    }
}
