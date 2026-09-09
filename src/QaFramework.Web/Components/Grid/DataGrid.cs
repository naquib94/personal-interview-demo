using Microsoft.Playwright;
using QaFramework.Core.Logging;
using QaFramework.Web.Setup;

namespace QaFramework.Web.Components.Grid;

/// <summary>
/// A tabular data grid.
/// </summary>
/// <remarks>
/// <para><b>The central design decision: rows are addressed by business key, never by index.</b>
/// The demo application stamps each row with a <c>data-*</c> attribute carrying its identity
/// (<c>data-reference="ORD-..."</c>, <c>data-symbol="EURUSD"</c>), and this component locates
/// through that attribute.</para>
///
/// <para>Index addressing - <c>rows.Nth(0)</c> - fails as soon as sort order changes, a
/// default filter is added, or another test creates a row. Worse, it fails <i>silently</i>: the
/// test still finds a row, asserts against the wrong one, and passes or fails for reasons
/// unrelated to what it was written to check. Every UI suite I have worked on has had a version
/// of this bug.</para>
///
/// <para>Cells are addressed by <c>data-field</c> for the same reason: inserting a column must
/// not renumber every assertion in the suite.</para>
///
/// <para>Note also what is <i>not</i> here: no assertion that reads "Showing 3 of 12" out of
/// the page and parses a number from it. Counting DOM rows is robust; parsing prose breaks on a
/// copy change or a translation.</para>
/// </remarks>
public sealed class DataGrid(
    WebTestContext context,
    string bodyTestId,
    string rowTestId,
    string rowKeyAttribute,
    string description)
    : WebComponent(context, $"[data-testid='{bodyTestId}']", description)
{
    /// <summary>Every row currently rendered.</summary>
    public ILocator Rows => Context.App.Locator($"[data-testid='{rowTestId}']");

    /// <summary>The row whose business key matches, e.g. <c>Row("ORD-20240401-0001")</c>.</summary>
    public ILocator Row(string key) =>
        Context.App.Locator($"[data-testid='{rowTestId}'][{rowKeyAttribute}='{key}']");

    /// <summary>A single cell, addressed by row key and column name.</summary>
    public ILocator Cell(string key, string field) => Row(key).Locator($"[data-field='{field}']");

    public Task<int> RowCountAsync() => Rows.CountAsync();

    public Task ShouldHaveRowCountAsync(int expected) =>
        Assertions.Expect(Rows).ToHaveCountAsync(expected,
            new LocatorAssertionsToHaveCountOptions { Timeout = Timeouts.PageLoadMs });

    /// <summary>Asserts a row with the given key is present.</summary>
    public Task ShouldContainRowAsync(string key) =>
        Assertions.Expect(Row(key)).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = Timeouts.PageLoadMs });

    /// <summary>
    /// Asserts a row with the given key is absent.
    /// </summary>
    /// <remarks>
    /// Uses the shorter absence budget, and asserts a count of zero rather than "not visible" -
    /// a hidden row is still a row, and the distinction has caught real filtering defects.
    /// </remarks>
    public Task ShouldNotContainRowAsync(string key) =>
        Assertions.Expect(Row(key)).ToHaveCountAsync(0,
            new LocatorAssertionsToHaveCountOptions { Timeout = Timeouts.AbsenceMs });

    /// <summary>
    /// Asserts a cell's text.
    /// </summary>
    /// <remarks>
    /// Exact text rather than "contains". Substring matching is a quiet source of false
    /// passes: asserting that a status cell contains "Filled" also passes on "Partially
    /// Filled" and on "Unfilled". Where a substring genuinely is the requirement, that should
    /// be explicit at the call site rather than being the default everywhere.
    /// </remarks>
    public Task CellShouldHaveTextAsync(string key, string field, string expected) =>
        Assertions.Expect(Cell(key, field)).ToHaveTextAsync(expected,
            new LocatorAssertionsToHaveTextOptions { Timeout = Timeouts.ElementMs });

    /// <summary>
    /// Returns a whole row as a field-to-text dictionary.
    /// </summary>
    /// <remarks>
    /// Lets a test assert every field of a record in one comparison and report every mismatch
    /// together, instead of failing on the first difference and hiding the other four. When a
    /// mapping bug shifts several columns at once, seeing all of them is what identifies the
    /// cause.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, string>> RowValuesAsync(string key)
    {
        ILocator row = Row(key);

        if (await row.CountAsync() == 0)
            throw TestLog.Failure(
                $"No row with {rowKeyAttribute}='{key}' was found in {Description}.",
                await DescribeVisibleKeysAsync());

        var pairs = await row.Locator("[data-field]").EvaluateAllAsync<Dictionary<string, string>>(
            "cells => Object.fromEntries(cells.map(c => [c.dataset.field, c.innerText.trim()]))");

        return pairs;
    }

    /// <summary>Reads the business key of every visible row.</summary>
    public async Task<IReadOnlyList<string>> VisibleKeysAsync()
    {
        string[] keys = await Rows.EvaluateAllAsync<string[]>(
            $"rows => rows.map(r => r.getAttribute('{rowKeyAttribute}'))");
        return keys;
    }

    /// <summary>
    /// Builds the "what was actually there" half of a failure message.
    /// </summary>
    /// <remarks>
    /// This is deliberately part of the component. "Row X not found" sends the reader to
    /// re-run the test with a debugger; "Row X not found - the grid contained Y and Z" usually
    /// identifies the cause on sight, and frequently reveals that the real defect is a filter
    /// or a sort rather than a missing record.
    /// </remarks>
    private async Task<string> DescribeVisibleKeysAsync()
    {
        try
        {
            IReadOnlyList<string> keys = await VisibleKeysAsync();
            return keys.Count == 0
                ? "The grid is empty."
                : $"The grid contained {keys.Count} row(s): {string.Join(", ", keys)}";
        }
        catch (PlaywrightException ex)
        {
            // A diagnostic helper must never become the failure. If the page is in a state
            // where even reading the rows fails, say so and let the original failure stand.
            return $"(the grid's contents could not be read for diagnostics: {ex.Message})";
        }
    }
}
