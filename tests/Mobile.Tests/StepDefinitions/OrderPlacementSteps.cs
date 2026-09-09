using System.Globalization;
using AwesomeAssertions;
using Mobile.Tests.Support;
using QaFramework.Mobile.Screens.Trading;
using Reqnroll;

namespace Mobile.Tests.StepDefinitions;

/// <summary>
/// Steps about placing an order and finding it again.
/// </summary>
/// <remarks>
/// <see cref="OrderUnderTest"/> carries the reference between the "when" and the "then", so the
/// feature file can say "the order" without knowing what it is called. Reqnroll gives every
/// scenario its own instance, which is why no static field is needed and why two scenarios could
/// run concurrently without interfering.
/// </remarks>
[Binding]
public sealed class OrderPlacementSteps(
    AccountScreen accountScreen,
    NewOrderScreen newOrderScreen,
    OrderHistoryScreen orderHistoryScreen,
    OrderUnderTest order)
{
    /// <summary>
    /// Places a market order.
    /// </summary>
    /// <remarks>
    /// Navigation to the order screen happens here rather than in the Gherkin. "When they place
    /// an order" is the business intent; "when they tap New order" is a description of a menu,
    /// and putting the second in a feature file means the feature file has to change when the
    /// menu does.
    /// <para>
    /// The quantity is captured as a string because that is what a user types, and because the
    /// interesting order-entry cases are precisely the ones a decimal cannot express.
    /// </para>
    /// </remarks>
    [When("they place a market order to buy {int} of {string}")]
    public async Task TheyPlaceAMarketOrderAsync(int quantity, string symbol)
    {
        // The Gherkin says "buy 2 of EURUSD" - an unquoted number, because that is how a person
        // writes it - and the step converts. The screen object still takes a string, so the
        // scenarios that need "abc" or an empty field remain expressible; they simply belong in
        // a scenario outline rather than in this sentence.
        order.Symbol = symbol;
        order.Side = "Buy";
        order.Quantity = quantity.ToString(CultureInfo.InvariantCulture);

        await accountScreen.GoToNewOrderAsync();
        await newOrderScreen.WaitUntilLoadedAsync();
        await newOrderScreen.PlaceMarketOrderAsync(symbol, "Buy", order.Quantity);
    }

    [When("they place an order without saying how much to trade")]
    public async Task TheyPlaceAnOrderWithoutAQuantityAsync()
    {
        order.Symbol = "EURUSD";

        await accountScreen.GoToNewOrderAsync();
        await newOrderScreen.WaitUntilLoadedAsync();

        // The instrument is chosen and the quantity left untouched, which is what a distracted
        // user does. Submitting a completely blank form would also fail, but for several reasons
        // at once, and a negative test that could pass for the wrong reason is not worth much.
        await newOrderScreen.ChooseInstrumentAsync("EURUSD");
        await newOrderScreen.SubmitAsync();
    }

    [Then("the order is acknowledged with a reference")]
    public async Task TheOrderIsAcknowledgedAsync()
    {
        (await newOrderScreen.ConfirmationAppearsAsync())
            .Should().BeTrue("a placed order must be acknowledged on screen");

        string reference = await newOrderScreen.OrderReferenceAsync();

        reference.Should().NotBeNullOrWhiteSpace(
            "an acknowledgement without a reference gives the user nothing to quote");

        order.Reference = reference;
    }

    [Then("the order appears in their order history")]
    public async Task TheOrderAppearsInTheirOrderHistoryAsync()
    {
        await newOrderScreen.GoToOrderHistoryAsync();
        await orderHistoryScreen.WaitUntilLoadedAsync();

        // A poll, not a read. An order reaching a history list is eventually consistent in any
        // real trading system - the write is acknowledged before the read model catches up - so
        // this is the assertion that would flake first if it were written as a single read.
        string row = await orderHistoryScreen.WaitForOrderAsync(order.RequireReference());

        row.Should().Contain(
            order.Symbol, "the history row must be about the instrument that was traded");

        row.Should().Contain(
            order.Side, "the history row must show which way the trade went");
    }

    [Then("the order is rejected with the message {string}")]
    public async Task TheOrderIsRejectedWithTheMessageAsync(string expected)
    {
        (await newOrderScreen.RejectionAppearsAsync())
            .Should().BeTrue("a rejected order must say why it was rejected");

        (await newOrderScreen.RejectionMessageAsync()).Should().Be(expected);
    }

    [Then("no reference is issued")]
    public async Task NoReferenceIsIssuedAsync()
    {
        // The important half of the rejection. A message on screen next to a silently accepted
        // order would be the worst outcome available, and only this assertion would catch it.
        //
        // The non-waiting check is used deliberately: the preceding step has already waited for
        // the rejection message, so the screen has settled, and the waiting variant would spend
        // the whole submit timeout proving a negative on every run of this scenario.
        (await newOrderScreen.IsConfirmationDisplayedAsync())
            .Should().BeFalse("a rejected order must not also be acknowledged");

        order.Reference.Should().BeNull();
    }
}
