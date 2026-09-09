using QaFramework.Web.Components.Actions;
using QaFramework.Web.Components.DataEntry;
using QaFramework.Web.Components.Visual;
using QaFramework.Web.Setup;
using TradingDemo.AppModel.Contracts;

namespace TradingDemo.AppModel.Pages;

/// <summary>The order entry page.</summary>
public sealed class NewOrderPage(WebTestContext context) : ApplicationPage(context, "order-new.html")
{
    public TextElement Heading { get; } = new(context, "new-order-heading", "the New order heading");
    public Dropdown Symbol { get; } = new(context, "new-order-symbol-select", "the Instrument dropdown");
    public Dropdown Side { get; } = new(context, "new-order-side-select", "the Side dropdown");
    public Dropdown OrderType { get; } = new(context, "new-order-type-select", "the Order type dropdown");
    public TextInput Quantity { get; } = new(context, "new-order-quantity-input", "the Quantity field");
    public TextInput LimitPrice { get; } = new(context, "new-order-limit-price-input", "the Limit price field");
    public Button SubmitButton { get; } = new(context, "new-order-submit-button", "the Place order button");
    public Button ResetButton { get; } = new(context, "new-order-reset-button", "the Reset button");

    /// <summary>
    /// The conditional limit-price field wrapper.
    /// </summary>
    /// <remarks>
    /// Modelled separately from <see cref="LimitPrice"/> because its visibility is itself a
    /// requirement: the field must appear for a limit order and disappear for a market order.
    /// Testing the container's visibility rather than the input's makes the assertion match
    /// what the application actually toggles.
    /// </remarks>
    public TextElement LimitPriceField { get; } = new(context, "new-order-limit-price-field", "the Limit price field container");

    /// <summary>
    /// The placed order's reference, published to a dedicated element by the application.
    /// </summary>
    /// <remarks>
    /// The reference also appears inside the success message. Reading it from a dedicated
    /// element instead of parsing it out of prose is the difference between a test that
    /// survives a copy change and one that does not - and asking the developers for that
    /// element cost one line of JavaScript.
    /// </remarks>
    public TextElement OrderReference { get; } = new(context, "new-order-reference-text", "the placed order reference");

    public override Task ShouldBeDisplayedAsync() => Heading.ShouldHaveTextAsync("New order");

    /// <summary>
    /// Completes and submits the order form.
    /// </summary>
    /// <remarks>
    /// <para>Takes the same <see cref="PlaceOrderRequest"/> the API client takes. That is a
    /// small but deliberate choice: one scenario can then place an order through the UI and
    /// another through the API using an identical description of the order, which is what makes
    /// "the API and the UI enforce the same rules" a testable statement rather than an
    /// assumption.</para>
    ///
    /// <para>Order type is selected <i>before</i> the limit price, because selecting it is what
    /// reveals the field. Sequencing that matters is exactly the knowledge that belongs in a
    /// page object rather than in every step definition that fills this form.</para>
    /// </remarks>
    public async Task PlaceOrderAsync(PlaceOrderRequest request)
    {
        if (request.Symbol is not null) await Symbol.SelectAsync(request.Symbol);
        if (request.Side is not null) await Side.SelectAsync(request.Side);
        if (request.OrderType is not null) await OrderType.SelectAsync(request.OrderType);

        await Quantity.EnterAsync(FormatNumber(request.Quantity));

        if (request.LimitPrice is not null)
            await LimitPrice.EnterAsync(FormatNumber(request.LimitPrice));

        await SubmitButton.ClickAsync();
    }

    /// <summary>
    /// Formats a number for entry into a text field.
    /// </summary>
    /// <remarks>
    /// Invariant culture, explicitly. On an agent with a European locale the default
    /// <c>ToString()</c> renders 1.5 as "1,5", the application parses it as NaN, and the test
    /// fails with a validation error that makes no sense to anyone reading it. This is a real
    /// cross-machine failure and a one-line fix.
    /// </remarks>
    private static string FormatNumber(decimal? value) =>
        value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
}
