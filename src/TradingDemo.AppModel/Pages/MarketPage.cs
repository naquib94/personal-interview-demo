using QaFramework.Web.Components.DataEntry;
using QaFramework.Web.Components.Grid;
using QaFramework.Web.Components.Visual;
using QaFramework.Web.Setup;

namespace TradingDemo.AppModel.Pages;

/// <summary>The market prices page.</summary>
public sealed class MarketPage(WebTestContext context) : ApplicationPage(context, "market.html")
{
    public TextElement Heading { get; } = new(context, "market-heading", "the Market heading");
    public Dropdown AssetClassFilter { get; } = new(context, "market-asset-class-select", "the Asset class filter");
    public TextElement EmptyState { get; } = new(context, "market-grid-empty", "the no-instruments message");

    /// <summary>
    /// The instrument grid, keyed by symbol.
    /// </summary>
    /// <remarks>
    /// <c>data-symbol</c> is the business key. A test says
    /// <c>Instruments.CellShouldHaveTextAsync("EURUSD", "assetClass", "FX")</c> - which states
    /// its intent - rather than reaching for row 2, column 3, which does not.
    /// </remarks>
    public DataGrid Instruments { get; } = new(
        context,
        bodyTestId: "market-grid-body",
        rowTestId: "market-grid-row",
        rowKeyAttribute: "data-symbol",
        description: "the instruments grid");

    public override async Task ShouldBeDisplayedAsync()
    {
        await Heading.ShouldHaveTextAsync("Market");
        // Asserting at least one row is what distinguishes "the page rendered" from "the
        // reference-data endpoint worked". A grid that is empty because the API failed looks
        // identical to one that is empty because a filter matched nothing.
        await Instruments.Rows.First.WaitForAsync();
    }
}
