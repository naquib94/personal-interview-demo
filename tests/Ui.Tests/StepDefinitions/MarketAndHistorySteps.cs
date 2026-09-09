using TradingDemo.AppModel.Pages;

namespace Ui.Tests.StepDefinitions;

[Binding]
public sealed class MarketAndHistorySteps(MarketPage marketPage, OrderHistoryPage orderHistoryPage)
{
    // -------------------------------------------------------------------------------------
    // Market
    // -------------------------------------------------------------------------------------

    [Given("the market page is open")]
    public Task GivenTheMarketPageIsOpen() => marketPage.OpenAsync();

    [When("the asset class filter is set to {string}")]
    public Task WhenTheAssetClassFilterIsSetTo(string assetClass) =>
        marketPage.AssetClassFilter.SelectAsync(assetClass);

    [Then("the market grid contains the instrument {string}")]
    public Task ThenTheMarketGridContains(string symbol) =>
        marketPage.Instruments.ShouldContainRowAsync(symbol);

    [Then("the market grid does not contain the instrument {string}")]
    public Task ThenTheMarketGridDoesNotContain(string symbol) =>
        marketPage.Instruments.ShouldNotContainRowAsync(symbol);

    [Then("the instrument {string} is shown in the asset class {string}")]
    public Task ThenTheInstrumentIsShownInAssetClass(string symbol, string assetClass) =>
        marketPage.Instruments.CellShouldHaveTextAsync(symbol, "assetClass", assetClass);

    [Then("the instrument {string} is shown as not tradable")]
    public Task ThenTheInstrumentIsShownAsNotTradable(string symbol) =>
        marketPage.Instruments.CellShouldHaveTextAsync(symbol, "isTradable", "No");

    // -------------------------------------------------------------------------------------
    // Order history
    // -------------------------------------------------------------------------------------

    [Given("the order history page is open")]
    public Task GivenTheOrderHistoryPageIsOpen() => orderHistoryPage.OpenAsync();

    [When("the status filter is set to {string}")]
    public Task WhenTheStatusFilterIsSetTo(string status) =>
        orderHistoryPage.StatusFilter.SelectAsync(status);

    [When("the order reference search is set to {string}")]
    public Task WhenTheSearchIsSetTo(string search) => orderHistoryPage.SearchBox.EnterAsync(search);

    [Then("the order history grid contains the order {string}")]
    public Task ThenTheHistoryContains(string reference) =>
        orderHistoryPage.Orders.ShouldContainRowAsync(reference);

    [Then("the order history grid does not contain the order {string}")]
    public Task ThenTheHistoryDoesNotContain(string reference) =>
        orderHistoryPage.Orders.ShouldNotContainRowAsync(reference);

    [Then("the order {string} is shown as {string}")]
    public Task ThenTheOrderIsShownAs(string reference, string status) =>
        orderHistoryPage.Orders.CellShouldHaveTextAsync(reference, "status", status);

    [Then("the order {string} is shown with the symbol {string}")]
    public Task ThenTheOrderIsShownWithSymbol(string reference, string symbol) =>
        orderHistoryPage.Orders.CellShouldHaveTextAsync(reference, "symbol", symbol);

    [Then("the no-orders message is shown")]
    public Task ThenTheNoOrdersMessageIsShown() => orderHistoryPage.EmptyState.ShouldBeVisibleAsync();

    /// <summary>
    /// Cross-checks the reported count against the rendered rows.
    /// </summary>
    /// <remarks>
    /// Comparing two independent views of the same fact rather than either against a hardcoded
    /// number. That keeps the assertion valid as the seed data or another scenario's orders
    /// change, and it still catches the defect it is aimed at: a count computed from the wrong
    /// collection.
    /// </remarks>
    [Then("the displayed order count matches the number of rows in the grid")]
    public async Task ThenTheDisplayedCountMatchesTheRows()
    {
        int actualRows = await orderHistoryPage.Orders.RowCountAsync();
        string reported = (await orderHistoryPage.DisplayedCount.GetTextAsync()).Trim();

        reported.Should().Be(actualRows.ToString(),
            "the count shown to the user must match the rows actually rendered");
    }
}
