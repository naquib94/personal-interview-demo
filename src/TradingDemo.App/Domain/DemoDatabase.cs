using Microsoft.Data.Sqlite;

namespace TradingDemo.App.Domain;

/// <summary>
/// Owns the lifecycle of the demo SQLite database: create, apply schema, apply seed.
/// </summary>
/// <remarks>
/// Two things here are worth pointing out, because both exist to serve the tests rather than
/// the application:
/// <list type="number">
/// <item>The schema and seed are applied from the committed .sql files. There is no
/// code-first model, so the SQL a reviewer reads in <c>database/</c> is the SQL that runs.</item>
/// <item><see cref="Reset"/> re-applies the seed on demand. That is what makes the suite
/// repeatable: a scenario that needs pristine data asks for it, instead of hoping the previous
/// scenario cleaned up after itself.</item>
/// </list>
/// </remarks>
public sealed class DemoDatabase
{
    private readonly string scriptRoot;

    public string ConnectionString { get; }

    public DemoDatabase(string databasePath, string scriptRoot)
    {
        this.scriptRoot = scriptRoot;
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            // Pooling keeps the file handle alive between connections, which matters for
            // in-memory-like performance during a test run.
            Pooling = true,
            ForeignKeys = true
        }.ToString();
    }

    public SqliteConnection OpenConnection()
    {
        SqliteConnection connection = new(ConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>Applies schema then seed. Safe to call repeatedly.</summary>
    public void Initialise()
    {
        ExecuteScriptsIn(Path.Combine(scriptRoot, "Schema"));
        Reset();
    }

    /// <summary>Restores the database to the committed seed state.</summary>
    public void Reset() => ExecuteScriptsIn(Path.Combine(scriptRoot, "Seed"));

    private void ExecuteScriptsIn(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(
                $"SQL script directory '{directory}' was not found. The schema and seed scripts are " +
                "linked into the build output by TradingDemo.App.csproj; a missing directory usually " +
                "means the project was not rebuilt after the scripts moved.");

        using SqliteConnection connection = OpenConnection();
        // Ordering by file name is what makes the numeric prefixes (001_, 002_) meaningful.
        foreach (string file in Directory.GetFiles(directory, "*.sql").OrderBy(f => f, StringComparer.Ordinal))
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = File.ReadAllText(file);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }
}
