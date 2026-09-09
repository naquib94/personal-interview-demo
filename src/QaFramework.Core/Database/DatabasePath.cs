namespace QaFramework.Core.Database;

/// <summary>
/// Resolves a configured database path to an absolute one.
/// </summary>
/// <remarks>
/// <para>Shared by the component that <i>tells the application under test where to put its
/// database</i> and the component that <i>reads that database to verify it</i>. Both must agree
/// exactly, and a single resolver is the only way to guarantee that.</para>
///
/// <para>Before this existed, the configured path was a guess at where the application would
/// happen to create its file - a relative walk up into the application's own build output. That
/// coupled the test configuration to another project's directory layout, and it broke silently:
/// SQLite creates a missing database rather than complaining, so verification queries ran
/// happily against an empty file and every assertion passed.</para>
///
/// <para>Inverting the relationship removes the guess. The suite decides the path, passes it to
/// the application on startup, and reads the same file back. The path has one meaning.</para>
///
/// <para>Relative paths resolve against the test assembly's directory rather than the process
/// working directory, because a test host's working directory differs between
/// <c>dotnet test</c>, an IDE runner and a CI agent - and is therefore not something a test
/// author can rely on.</para>
/// </remarks>
public static class DatabasePath
{
    public static string Resolve(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new InvalidOperationException(
                "No database path is configured for this environment. Set Target:DatabasePath in " +
                "the environment settings file, or remove the database assertions from scenarios " +
                "that run in this environment. Failing here is deliberate: a blank path would let " +
                "SQLite silently create an empty database, so skipping the verification would " +
                "produce a green test that checked nothing.");

        return Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath));
    }
}
