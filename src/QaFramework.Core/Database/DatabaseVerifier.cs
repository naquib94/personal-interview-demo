using Dapper;
using Microsoft.Data.Sqlite;
using QaFramework.Core.Logging;

namespace QaFramework.Core.Database;

/// <summary>
/// Read-only database access for verifying backend state.
/// </summary>
/// <remarks>
/// <para><b>Why a test suite needs this at all.</b> An API returning <c>201 Created</c> proves
/// the API said so. It does not prove a row was written, that it was written to the right
/// account, or that the side effects fired. Verifying at the boundary the API controls is
/// circular; verifying against the database closes the loop. The bugs this finds are the
/// expensive ones - orders attributed to the wrong account, silent truncation, a status
/// transition that updates one table and not another.</para>
///
/// <para><b>Why read-only.</b> Every method here executes SELECT statements, and
/// <see cref="Guard"/> rejects anything else. This is a design constraint, not a limitation. A
/// test suite that can write directly to the database will eventually be used to set up state
/// that the application itself cannot produce, and at that point the tests are verifying a
/// world that cannot exist. Setup goes through the application's own API; the database is for
/// observation only.</para>
///
/// <para><b>Why parameters only.</b> Every query is parameterised. Test code is a legitimate
/// source of SQL injection - test data routinely contains apostrophes and semicolons, and a
/// concatenated query fails confusingly on a name like O'Brien. Beyond correctness, a QA
/// engineer who writes concatenated SQL in tests is unlikely to spot it in a review of
/// production code.</para>
/// </remarks>
public sealed class DatabaseVerifier
{
    private readonly string connectionString;

    /// <summary>The absolute path actually opened. Surfaced for diagnostics and unit tests.</summary>
    public string ResolvedPath { get; }

    public DatabaseVerifier(string databasePath)
    {
        // Resolved through the shared resolver, so the file read here is by construction the
        // same file the application under test was told to write. See DatabasePath.
        ResolvedPath = DatabasePath.Resolve(databasePath);

        if (!File.Exists(ResolvedPath))
            throw new FileNotFoundException(
                $"No database file exists at '{ResolvedPath}' (configured as '{databasePath}'). " +
                "The demo application creates its database on first start, so this usually means " +
                "the application has not been built or run yet. Failing here is deliberate: " +
                "SQLite would otherwise happily create an empty database and every verification " +
                "would pass against no data at all.", ResolvedPath);

        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = ResolvedPath,
            // The application under test owns this file. Opening it read-only removes any
            // possibility of the suite corrupting or locking the data it is inspecting, and
            // enforces the read-only contract at the driver rather than relying on discipline.
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private
        }.ToString();
    }

    /// <summary>Returns a single row, or null when nothing matches.</summary>
    public T? QuerySingleOrDefault<T>(string sql, object? parameters = null)
    {
        Guard(sql);
        using SqliteConnection connection = Open();
        return connection.QuerySingleOrDefault<T>(sql, parameters);
    }

    /// <summary>
    /// Returns a single row, failing with the query and its parameters when there is none.
    /// </summary>
    /// <remarks>
    /// Use this when the row's absence <i>is</i> the defect. The failure message includes the
    /// SQL and the bound parameters, which is the difference between a five-second diagnosis
    /// and re-running the suite with a debugger attached.
    /// </remarks>
    public T QuerySingle<T>(string sql, object? parameters = null, string? because = null)
    {
        T? result = QuerySingleOrDefault<T>(sql, parameters);

        if (result is null)
            throw TestLog.Failure(
                because ?? "Expected exactly one row in the database but found none.",
                $"SQL:{Environment.NewLine}{sql}{Environment.NewLine}" +
                $"Parameters: {TestLog.Describe(parameters)}");

        return result;
    }

    public IReadOnlyList<T> Query<T>(string sql, object? parameters = null)
    {
        Guard(sql);
        using SqliteConnection connection = Open();
        return connection.Query<T>(sql, parameters).ToList();
    }

    public long Count(string sql, object? parameters = null)
    {
        Guard(sql);
        using SqliteConnection connection = Open();
        return connection.ExecuteScalar<long>(sql, parameters);
    }

    /// <summary>
    /// Asserts that an integrity query returns no rows.
    /// </summary>
    /// <remarks>
    /// The natural shape for the invariant queries in database/validation: each is written so
    /// that a returned row <i>is</i> a defect. Offending rows are included in the failure
    /// message, because "3 rows violate this invariant" without saying which is not actionable.
    /// </remarks>
    public void AssertNoRows<T>(string sql, string invariantDescription, object? parameters = null)
    {
        IReadOnlyList<T> offending = Query<T>(sql, parameters);

        if (offending.Count > 0)
            throw TestLog.Failure(
                $"Data integrity violation: {invariantDescription}. Found {offending.Count} " +
                "offending row(s).",
                $"Offending rows:{Environment.NewLine}{TestLog.Describe(offending)}" +
                $"{Environment.NewLine}SQL:{Environment.NewLine}{sql}");
    }

    private SqliteConnection Open()
    {
        SqliteConnection connection = new(connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Rejects any statement that is not a SELECT.
    /// </summary>
    /// <remarks>
    /// Belt and braces: the connection is already opened read-only, so a write would fail at
    /// the driver. This check exists because it fails <i>earlier</i> and with a message that
    /// explains the architectural rule rather than a driver-level "attempt to write a readonly
    /// database". The intent is to stop the pattern from spreading, not just to stop the write.
    /// </remarks>
    private static void Guard(string sql)
    {
        string trimmed = sql.TrimStart();

        // Skip leading SQL comments so a documented query is not rejected for its header.
        while (trimmed.StartsWith("--", StringComparison.Ordinal))
        {
            int newline = trimmed.IndexOf('\n');
            if (newline < 0) { trimmed = string.Empty; break; }
            trimmed = trimmed[(newline + 1)..].TrimStart();
        }

        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "DatabaseVerifier executes read-only queries only; the statement must begin with " +
                "SELECT or WITH. Test data must be created through the application's own API so " +
                "that the suite never verifies a state the application itself cannot produce. " +
                $"Rejected statement began: '{Truncate(trimmed, 60)}'.");
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length] + "...";
}
