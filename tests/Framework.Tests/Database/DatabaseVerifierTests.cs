using Framework.Tests.Support;
using Microsoft.Data.Sqlite;
using QaFramework.Core.Database;
using QaFramework.Core.Logging;

namespace Framework.Tests.Database;

/// <summary>
/// Tests for the read-only guard and the failure messages of <see cref="DatabaseVerifier"/>.
/// </summary>
/// <remarks>
/// The guard is a safety property rather than a feature, which is what makes it worth testing.
/// If it regressed, nothing would fail: the suite would simply gain the ability to write to the
/// database it is supposed to be observing, and someone would eventually use that to set up
/// state the application itself cannot produce. From that point the tests verify a world that
/// cannot exist, and they do it in green.
/// <para>
/// A real SQLite file is created once for the fixture rather than mocked. The read-only
/// enforcement lives partly in the connection string, so a fake connection would test the guard
/// clause and skip the half of the mechanism that actually protects the data.
/// </para>
/// </remarks>
[TestFixture]
public sealed class DatabaseVerifierTests
{
    private TemporaryDirectory directory = null!;
    private string databasePath = null!;
    private DatabaseVerifier verifier = null!;

    [OneTimeSetUp]
    public void CreateDatabase()
    {
        directory = new TemporaryDirectory();
        databasePath = directory.PathTo("verifier-tests.db");

        using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());

        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE orders (
                id        INTEGER PRIMARY KEY,
                reference TEXT    NOT NULL,
                status    TEXT    NOT NULL,
                quantity  REAL    NOT NULL
            );
            INSERT INTO orders (reference, status, quantity) VALUES ('QA-0001', 'Filled', 12.5);
            INSERT INTO orders (reference, status, quantity) VALUES ('O''Brien', 'Pending', 3.0);
            INSERT INTO orders (reference, status, quantity) VALUES ('QA-0003', 'Rejected', -1.0);
            """;
        command.ExecuteNonQuery();

        verifier = new DatabaseVerifier(databasePath);
    }

    [OneTimeTearDown]
    public void DeleteDatabase()
    {
        // Once per fixture rather than per test: the file is opened read-only by every test, so
        // there is nothing for one test to leave behind that could affect another, and creating
        // it eleven times would be pure cost.
        SqliteConnection.ClearAllPools();
        directory.Dispose();
    }

    [TestCase("")]
    [TestCase("   ")]
    public void Constructor_WithNoConfiguredPath_SaysVerificationIsUnavailable(string path)
    {
        // An environment with no database is a legitimate configuration - a deployment the suite
        // cannot reach the storage of. The one thing that must not happen is quietly skipping
        // the verification, because that produces a green test that checked nothing.
        Action construct = () => _ = new DatabaseVerifier(path);

        construct.Should().Throw<InvalidOperationException>()
            .WithMessage("*no database path is configured for this environment*")
            .WithMessage("*Target:DatabasePath*")
            .WithMessage("*green test that checked nothing*");
    }

    [Test]
    public void Constructor_WithAMissingFile_FailsRatherThanCreatingAnEmptyDatabase()
    {
        // SQLite's default behaviour is to create the file, so a typo in DatabasePath would
        // yield an empty database in which every "no orphaned rows" invariant passes perfectly.
        // That is the single most dangerous default in this layer, hence the explicit check.
        string missing = directory.PathTo("does-not-exist.db");

        Action construct = () => _ = new DatabaseVerifier(missing);

        construct.Should().Throw<FileNotFoundException>()
            .WithMessage($"*{missing}*")
            .WithMessage("*every verification would pass against no data at all*")
            .Which.FileName.Should().Be(missing);
    }

    [Test]
    public void Constructor_WithARootedPath_ReportsItUnchanged()
    {
        // ResolvedPath is surfaced so a failure can say which file was actually read. A relative
        // path is resolved against the assembly directory rather than the process working
        // directory, which differs between `dotnet test`, an IDE runner and a CI agent - and a
        // configured path that means three different things is not a configured path.
        verifier.ResolvedPath.Should().Be(Path.GetFullPath(databasePath));
    }

    [TestCase("INSERT INTO orders (reference) VALUES ('x')")]
    [TestCase("UPDATE orders SET status = 'Filled'")]
    [TestCase("DELETE FROM orders")]
    [TestCase("DROP TABLE orders")]
    [TestCase("ALTER TABLE orders ADD COLUMN note TEXT")]
    [TestCase("CREATE TABLE scratch (id INTEGER)")]
    [TestCase("PRAGMA journal_mode = WAL")]
    [TestCase("  \r\n   REPLACE INTO orders (id, reference) VALUES (1, 'x')")]
    public void Query_WithAStatementThatIsNotARead_ExplainsTheArchitecturalRule(string sql)
    {
        // The message deliberately explains the rule rather than just refusing. The intent is to
        // stop the pattern spreading - somebody blocked by a terse "not allowed" writes a raw
        // SqliteConnection next to it, and the constraint is gone without a review noticing.
        Action query = () => verifier.Query<string>(sql);

        query.Should().Throw<InvalidOperationException>()
            .WithMessage("*read-only queries only*")
            .WithMessage("*must begin with SELECT or WITH*")
            .WithMessage("*through the application's own API*")
            .WithMessage("*Rejected statement began:*");
    }

    [Test]
    public void Query_WhenRejecting_QuotesTheOffendingStatement()
    {
        // Echoing the statement back matters when the SQL was built by a helper: without it the
        // reader has to work out which of several queries the framework objected to.
        Action query = () => verifier.Query<string>("DELETE FROM orders WHERE reference = 'QA-0001'");

        query.Should().Throw<InvalidOperationException>()
            .WithMessage("*'DELETE FROM orders WHERE reference = 'QA-0001''*");
    }

    [Test]
    public void Query_WithAStatementThatIsNothingButAComment_IsRejected()
    {
        // The comment-skipping loop consumes the whole statement here. Falling through to
        // "accepted" would let an empty command reach the driver and fail with something far
        // less informative than the guard's own message.
        Action query = () => verifier.Query<string>("-- a query somebody deleted the body of");

        query.Should().Throw<InvalidOperationException>().WithMessage("*read-only queries only*");
    }

    [TestCase("SELECT reference FROM orders ORDER BY id")]
    [TestCase("select reference from orders order by id")]
    [TestCase("\r\n\t  SELECT reference FROM orders ORDER BY id")]
    [TestCase("-- Every order, oldest first.\nSELECT reference FROM orders ORDER BY id")]
    [TestCase("-- First line.\n-- Second line.\n   SELECT reference FROM orders ORDER BY id")]
    [TestCase("WITH recent AS (SELECT reference, id FROM orders) SELECT reference FROM recent ORDER BY id")]
    public void Query_WithAReadStatement_IsAccepted(string sql)
    {
        // The accepted forms are enumerated because the guard is a string match, and a string
        // match that is too strict is just as damaging as one that is too loose: rejecting a
        // documented query for its own comment header, or a CTE for starting with WITH, pushes
        // the author towards a raw connection.
        IReadOnlyList<string> references = verifier.Query<string>(sql);

        references.Should().HaveCount(3);
    }

    [Test]
    public void Query_WithParameters_HandlesTestDataContainingAnApostrophe()
    {
        // Test code is a legitimate source of SQL injection: generated names routinely contain
        // apostrophes, and a concatenated query fails confusingly on O'Brien. Beyond
        // correctness, a QA engineer who writes concatenated SQL in tests is unlikely to object
        // to it in a review of production code.
        IReadOnlyList<string> statuses = verifier.Query<string>(
            "SELECT status FROM orders WHERE reference = @reference",
            new { reference = "O'Brien" });

        statuses.Should().Equal("Pending");
    }

    [Test]
    public void Count_ReturnsTheScalarResult()
    {
        verifier.Count("SELECT COUNT(*) FROM orders WHERE status <> @status", new { status = "Rejected" })
            .Should().Be(2);
    }

    [Test]
    public void QuerySingle_WithNoMatchingRow_IncludesTheSqlAndTheParameters()
    {
        // Use this method when the row's absence is the defect. The SQL and the bound parameters
        // in the message are the difference between a five-second diagnosis and re-running the
        // suite with a debugger attached - and the parameters are the half usually omitted,
        // which is unfortunate because a wrong parameter is the more common cause.
        Action query = () => verifier.QuerySingle<string>(
            "SELECT status FROM orders WHERE reference = @reference",
            new { reference = "QA-DOES-NOT-EXIST" },
            because: "the order placed by the scenario should have been persisted");

        query.Should().Throw<AutomationFailureException>()
            .WithMessage("*the order placed by the scenario should have been persisted*")
            .WithMessage("*SELECT status FROM orders WHERE reference = @reference*")
            .WithMessage("*QA-DOES-NOT-EXIST*");
    }

    [Test]
    public void QuerySingle_WithAMatchingRow_ReturnsIt()
    {
        verifier.QuerySingle<string>(
                "SELECT status FROM orders WHERE reference = @reference",
                new { reference = "QA-0001" })
            .Should().Be("Filled");
    }

    [Test]
    public void QuerySingleOrDefault_WithNoMatchingRow_ReturnsNullWithoutFailing()
    {
        // The negative-path counterpart. Asserting that something is absent is a legitimate
        // verification, and it must not have to be expressed as a caught exception.
        verifier.QuerySingleOrDefault<string>(
                "SELECT status FROM orders WHERE reference = @reference",
                new { reference = "QA-DOES-NOT-EXIST" })
            .Should().BeNull();
    }

    [Test]
    public void AssertNoRows_WhenTheInvariantHolds_Passes()
    {
        Action assert = () => verifier.AssertNoRows<string>(
            "SELECT reference FROM orders WHERE quantity IS NULL",
            "every order has a quantity");

        assert.Should().NotThrow();
    }

    [Test]
    public void AssertNoRows_WhenTheInvariantIsViolated_NamesItAndListsTheOffendingRows()
    {
        // "3 rows violate this invariant" is not actionable; the invariant queries in
        // database/validation are written so that a returned row *is* the defect, so the rows
        // themselves are the finding and have to appear in the message.
        Action assert = () => verifier.AssertNoRows<string>(
            "SELECT reference FROM orders WHERE quantity <= 0",
            "no order has a non-positive quantity");

        assert.Should().Throw<AutomationFailureException>()
            .WithMessage("*Data integrity violation: no order has a non-positive quantity*")
            .WithMessage("*Found 1 offending row(s)*")
            .WithMessage("*QA-0003*");
    }

    [Test]
    public void AWriteThatSlipsPastTheGuard_StillFailsAtTheDriver()
    {
        // Belt and braces, and the reason the guard is not the only protection. SQLite accepts a
        // WITH clause before an INSERT, so this statement passes the "must begin with SELECT or
        // WITH" check - and is then refused by the connection, which is opened read-only.
        //
        // Worth an explicit test because the two mechanisms are independent and each looks
        // redundant in the presence of the other, which is exactly how one of them gets removed.
        Action write = () => verifier.Count(
            """
            WITH incoming (reference, status, quantity) AS (SELECT 'QA-9999', 'Filled', 1.0)
            INSERT INTO orders (reference, status, quantity)
            SELECT reference, status, quantity FROM incoming
            """);

        write.Should().Throw<SqliteException>().WithMessage("*readonly database*");
    }
}
