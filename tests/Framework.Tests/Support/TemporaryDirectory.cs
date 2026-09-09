namespace Framework.Tests.Support;

/// <summary>
/// A uniquely-named scratch directory that deletes itself.
/// </summary>
/// <remarks>
/// The configuration and database tests need real files on disk, because the behaviour under
/// test is "what happens when this file is missing, present, or overridden by that other file".
/// Faking the file system would test an abstraction rather than the loader.
/// <para>
/// A GUID-named directory per test rather than one shared folder, so the fixtures remain
/// independent of each other and of anything left behind by a previous run - a stale file in a
/// shared scratch folder is a very effective way to produce a test that passes only on the
/// machine that created it.
/// </para>
/// </remarks>
internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "qa-framework-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Writes a file into the directory and returns its full path.</summary>
    public string WriteFile(string name, string content)
    {
        string fullPath = System.IO.Path.Combine(Path, name);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    public string PathTo(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup of a temp directory must never fail a test that otherwise passed. On
            // Windows a SQLite file can briefly remain locked after the last connection closes,
            // and reporting that as a test failure would be a false negative about the framework.
        }
    }
}
