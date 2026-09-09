using QaFramework.Web.Components.DataEntry;
using QaFramework.Web.Components.Grid;
using QaFramework.Web.Components.Visual;
using QaFramework.Web.Setup;

namespace TradingDemo.AppModel.Pages;

/// <summary>The order history page.</summary>
public sealed class OrderHistoryPage(WebTestContext context) : ApplicationPage(context, "orders.html")
{
    public TextElement Heading { get; } = new(context, "order-history-heading", "the Order history heading");
    public Dropdown StatusFilter { get; } = new(context, "order-history-status-select", "the Status filter");
    public TextInput SearchBox { get; } = new(context, "order-history-search-input", "the reference search box");
    public TextElement EmptyState { get; } = new(context, "order-history-grid-empty", "the no-orders message");

    /// <summary>
    /// The order grid, keyed by order reference.
    /// </summary>
    /// <remarks>
    /// Keying on the reference is what makes this suite parallel-safe. A scenario asserts on
    /// <i>its own</i> order by reference, so it is unaffected by orders another scenario creates
    /// concurrently. Asserting "the grid has 7 rows" would make every scenario depend on every
    /// other, and is the usual reason a suite has to be run single-threaded.
    /// </remarks>
    public DataGrid Orders { get; } = new(
        context,
        bodyTestId: "order-history-grid-body",
        rowTestId: "order-history-grid-row",
        rowKeyAttribute: "data-reference",
        description: "the order history grid");

    /// <summary>
    /// The displayed and total counts.
    /// </summary>
    /// <remarks>
    /// Modelled because they are a real requirement - a user needs to know a filter has hidden
    /// rows - and because the pair catches a specific defect: a filter applied client-side that
    /// forgets to update the total, or a paginated total that counts only the current page.
    /// </remarks>
    public TextElement DisplayedCount { get; } = new(context, "order-history-row-count-text", "the displayed order count");
    public TextElement TotalCount { get; } = new(context, "order-history-total-count-text", "the total order count");

    public override Task ShouldBeDisplayedAsync() => Heading.ShouldHaveTextAsync("Order history");
}
