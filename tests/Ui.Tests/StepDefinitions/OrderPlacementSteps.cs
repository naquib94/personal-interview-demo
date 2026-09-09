using QaFramework.Core.Database;
using QaFramework.Web.Components.Visual;
using TradingDemo.AppModel.Contracts;
using TradingDemo.AppModel.Pages;
using TradingDemo.AppModel.Setup;

namespace Ui.Tests.StepDefinitions;

[Binding]
public sealed class OrderPlacementSteps(
    ScenarioSession session,
    NewOrderPage newOrderPage,
    OrderHistoryPage orderHistoryPage,
    DatabaseVerifier database)
{
    private string? placedReference;

    [Given("the new order page is open")]
    public Task GivenTheNewOrderPageIsOpen() => newOrderPage.OpenAsync();

    [When("the trader places a market order to buy {decimal} of {string} through the browser")]
    public Task WhenTheTraderPlacesAMarketOrder(decimal quantity, string symbol) =>
        newOrderPage.PlaceOrderAsync(new PlaceOrderRequest(symbol, "Buy", "Market", quantity, null));

    [When("the trader selects the order type {string}")]
    public Task WhenTheTraderSelectsTheOrderType(string orderType) =>
        newOrderPage.OrderType.SelectAsync(orderType);

    [When("the trader completes a limit order to sell {decimal} of {string} at {decimal}")]
    public Task WhenTheTraderCompletesALimitOrder(decimal quantity, string symbol, decimal limitPrice) =>
        newOrderPage.PlaceOrderAsync(new PlaceOrderRequest(symbol, "Sell", "Limit", quantity, limitPrice));

    /// <summary>
    /// Submits the order form leaving quantity empty.
    /// </summary>
    /// <remarks>
    /// Deliberately does not use <c>PlaceOrderAsync</c>: that method fills every field, and
    /// this scenario is specifically about a field being left blank. Reaching past the
    /// convenience method for a negative case is correct - bending the convenience method to
    /// accept "sometimes skip this field" would make it harder to read for the twelve scenarios
    /// that do not need that.
    /// </remarks>
    [When("the trader submits the order form with no quantity")]
    public async Task WhenTheTraderSubmitsWithNoQuantity()
    {
        await newOrderPage.Symbol.SelectAsync("EURUSD");
        await newOrderPage.Side.SelectAsync("Buy");
        await newOrderPage.OrderType.SelectAsync("Market");
        await newOrderPage.Quantity.ClearAsync();
        await newOrderPage.SubmitButton.ClickAsync();
    }

    [Then("a success message confirms the order was placed")]
    public async Task ThenASuccessMessageConfirmsTheOrder()
    {
        await newOrderPage.Alert.ShouldShowAsync(AlertKind.Success);
        await newOrderPage.Alert.ShouldContainTextAsync("placed successfully");

        // Read from the dedicated element rather than parsed out of the success prose. The
        // application publishes the reference specifically so tests do not have to scrape it,
        // which keeps this step working when the wording changes.
        placedReference = (await newOrderPage.OrderReference.GetTextAsync()).Trim();

        placedReference.Should().StartWith("ORD-",
            "the application should publish the new order's reference for the test to read");

        await session.TrackOrderForCleanupAsync(placedReference);
    }

    [Then("the order appears in the order history page")]
    public async Task ThenTheOrderAppearsInTheOrderHistoryPage()
    {
        await orderHistoryPage.OpenAsync();

        // Addressed by business key. An index-based assertion would break as soon as another
        // scenario places an order concurrently, which is exactly what happens in this suite.
        await orderHistoryPage.Orders.ShouldContainRowAsync(RequireReference());
    }

    /// <summary>
    /// Verifies the order reached the database, not just the screen.
    /// </summary>
    /// <remarks>
    /// The final link in the chain. The grid showing a row proves the browser received a
    /// response; this proves a row exists, attributed to the right account and instrument. A
    /// UI-only assertion cannot distinguish a persisted order from one rendered optimistically
    /// on the client.
    /// </remarks>
    [Then("the order is persisted against the trader's own account")]
    public void ThenTheOrderIsPersisted()
    {
        PersistedOrder stored = database.QuerySingle<PersistedOrder>(
            TradingQueries.OrderByReference,
            new { orderReference = RequireReference() },
            because: $"the browser reported order '{RequireReference()}' as placed, so a row " +
                     "must exist in the database");

        using (new AssertionScope())
        {
            stored.Username.Should().Be(session.CurrentUser!.Username,
                "the order must be attributed to the signed-in trader");
            stored.Status.Should().BeOneOf("Filled", "Pending");
        }
    }

    [Then("an error is shown on the new order page")]
    public Task ThenAnErrorIsShown() => newOrderPage.Alert.ShouldShowAsync(AlertKind.Error);

    /// <summary>
    /// Asserts on a per-field reason rather than the summary message.
    /// </summary>
    /// <remarks>
    /// A deliberately separate step from "the error message mentions ...". The platform's error
    /// envelope has two levels - a general message and a list of per-field reasons - and only
    /// the second tells the user what to change. Asserting on the summary alone would pass
    /// against a page that renders "The order was rejected." and silently drops the detail,
    /// which is a real usability regression and exactly what this scenario is for.
    /// </remarks>
    [Then("a rejection detail mentions {string}")]
    public async Task ThenARejectionDetailMentions(string fragment)
    {
        IReadOnlyList<string> details = await newOrderPage.Alert.GetDetailsAsync();

        details.Should().NotBeEmpty(
            "the platform returned a per-field reason, so the page must render it rather than " +
            "showing only the general message");

        details.Should().ContainMatch($"*{fragment}*",
            "the reason shown to the user should explain what to change. Details rendered: {0}",
            string.Join(" | ", details));
    }

    [Then("the limit price field is shown")]
    public Task ThenTheLimitPriceFieldIsShown() => newOrderPage.LimitPriceField.ShouldBeVisibleAsync();

    [Then("the limit price field is not shown")]
    public Task ThenTheLimitPriceFieldIsNotShown() => newOrderPage.LimitPriceField.ShouldNotBeVisibleAsync();

    [Then("the instrument {string} cannot be selected")]
    public Task ThenTheInstrumentCannotBeSelected(string symbol) =>
        newOrderPage.Symbol.ShouldHaveDisabledOptionAsync(symbol);

    private string RequireReference() => placedReference
        ?? throw new InvalidOperationException(
            "No order reference was captured. The 'a success message confirms the order was " +
            "placed' step must run before this one.");
}
