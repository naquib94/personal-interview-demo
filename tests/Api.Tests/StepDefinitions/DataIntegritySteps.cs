using QaFramework.Core.Database;
using QaFramework.Core.Logging;

namespace Api.Tests.StepDefinitions;

/// <summary>
/// Steps for the backend data-integrity feature.
/// </summary>
/// <remarks>
/// <para>Each invariant is written so that a returned row <i>is</i> the defect. That has a
/// useful consequence: the assertion is always "no rows", so there are no expected values to
/// keep up to date. An integrity suite that needed maintaining every time the seed data changed
/// would be abandoned within a month.</para>
///
/// <para>The queries live in <see cref="TradingQueries"/> alongside their row types, and are
/// documented in readable form in <c>database/validation/qa-validation-queries.sql</c> for
/// anyone who wants to run them by hand.</para>
/// </remarks>
[Binding]
public sealed class DataIntegritySteps(DatabaseVerifier database)
{
    private string? sql;
    private string? invariant;
    private IReadOnlyList<AccountActivity>? profiledAccounts;

    [When("the fill-reconciliation invariant is checked")]
    public void WhenFillReconciliationIsChecked() => Select(
        TradingQueries.OrdersWithMismatchedFills,
        "an order marked Filled must have fills that sum to its ordered quantity");

    [When("the orphaned-records invariant is checked")]
    public void WhenOrphanedRecordsAreChecked() => Select(
        TradingQueries.OrphanedOrders,
        "every order must reference an account and an instrument that exist");

    [When("the duplicate-reference invariant is checked")]
    public void WhenDuplicateReferencesAreChecked() => Select(
        TradingQueries.DuplicateOrderReferences,
        "an order reference is customer-facing and must be unique");

    [When("the limit-price consistency invariant is checked")]
    public void WhenLimitPriceConsistencyIsChecked() => Select(
        TradingQueries.OrdersWithInconsistentLimitPrice,
        "a limit order must have a limit price and a market order must not");

    [When("the quantity-range invariant is checked")]
    public void WhenQuantityRangeIsChecked() => Select(
        TradingQueries.OrdersOutsideInstrumentQuantityRange,
        "every stored quantity must lie within its instrument's tradable range");

    private void Select(string query, string description)
    {
        sql = query;
        invariant = description;
    }

    /// <summary>
    /// Runs the selected invariant and fails with the offending rows.
    /// </summary>
    /// <remarks>
    /// <see cref="DatabaseVerifier.AssertNoRows{T}"/> includes the violating rows in the
    /// failure message. "3 rows violate this invariant" is not actionable; naming
    /// <c>ORD-20240403-0006</c> and showing that it is Filled with zero fills is.
    /// </remarks>
    [Then("no rows violate the invariant")]
    public void ThenNoRowsViolateTheInvariant()
    {
        if (sql is null || invariant is null)
            throw TestLog.Failure(
                "No invariant was selected. This Then step requires a When step naming which " +
                "invariant to check.");

        // FillMismatch is the widest row shape in the set, and Dapper populates only the
        // columns a given query returns. Using one type keeps this step generic; the trade-off
        // is that unmapped columns are silently ignored, which is acceptable here because the
        // assertion is about row COUNT and the rows are only ever printed for diagnosis.
        database.AssertNoRows<FillMismatch>(sql, invariant);
    }

    [When("accounts with at least {int} orders are profiled")]
    public void WhenAccountsAreProfiled(int minimumOrders) =>
        profiledAccounts = database.Query<AccountActivity>(
            TradingQueries.AccountActivitySummary, new { minimumOrders });

    [Then("at least {int} account is reported")]
    public void ThenAtLeastNAccountsAreReported(int minimum) =>
        profiledAccounts.Should().NotBeNull().And.HaveCountGreaterThanOrEqualTo(minimum,
            "the seeded data contains an account with six orders, so the profiling query " +
            "should find it");

    [Then("every profiled account reports the instruments it has traded")]
    public void ThenEveryProfiledAccountReportsInstruments()
    {
        using AssertionScope scope = new();

        foreach (AccountActivity account in profiledAccounts!)
        {
            account.OrderCount.Should().BeGreaterThan(0);

            // COUNT(DISTINCT ...) must never exceed COUNT(*). Asserting the relationship rather
            // than a fixed number keeps the test valid as the seed data grows, which is what
            // stops it from becoming a maintenance liability.
            account.DistinctInstrumentsTraded.Should().BeGreaterThan(0)
                .And.BeLessThanOrEqualTo(account.OrderCount,
                    "an account cannot have traded more distinct instruments than it has orders");
        }
    }
}
