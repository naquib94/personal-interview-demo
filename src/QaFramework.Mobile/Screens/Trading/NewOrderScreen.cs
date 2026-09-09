using QaFramework.Core.Configuration;
using QaFramework.Mobile.Drivers;
using QaFramework.Mobile.Elements;

namespace QaFramework.Mobile.Screens.Trading;

/// <summary>
/// The order entry screen.
/// </summary>
public sealed class NewOrderScreen(IMobileDriver driver, TimeoutSettings timeouts)
    : BaseScreen(driver, timeouts)
{
    private static readonly MobileLocator Heading =
        MobileLocator.Shared("the new order heading", "new-order-heading");

    private static readonly MobileLocator Symbol =
        MobileLocator.Shared("the instrument picker", "new-order-symbol-select");

    private static readonly MobileLocator Side =
        MobileLocator.Shared("the side picker", "new-order-side-select");

    private static readonly MobileLocator OrderType =
        MobileLocator.Shared("the order type picker", "new-order-type-select");

    private static readonly MobileLocator Quantity =
        MobileLocator.Shared("the quantity field", "new-order-quantity-input");

    private static readonly MobileLocator LimitPrice =
        MobileLocator.Shared("the limit price field", "new-order-limit-price-input");

    private static readonly MobileLocator SubmitButton =
        MobileLocator.Shared("the place order button", "new-order-submit-button");

    private static readonly MobileLocator Reference =
        MobileLocator.Shared("the order reference", "new-order-reference-text");

    private static readonly MobileLocator ErrorMessage =
        MobileLocator.Shared("the order rejection message", "new-order-error-text");

    private static readonly MobileLocator OrderHistoryLink =
        MobileLocator.Shared("the order history navigation item", "nav-order-history-link");

    /// <inheritdoc />
    protected override MobileLocator Anchor => Heading;

    /// <inheritdoc />
    protected override string ScreenName => "the new order screen";

    /// <summary>
    /// Fills in a market order and submits it.
    /// </summary>
    /// <remarks>
    /// Quantity is a string, not a decimal. That is deliberate: the interesting cases in order
    /// entry are the ones a decimal cannot express - an empty field, "abc", "1.0.0", a value with
    /// the wrong number of decimal places. A typed parameter would make those scenarios
    /// impossible to write through this screen object, which is the layer that ought to be able
    /// to express anything a user can type.
    /// </remarks>
    public async Task PlaceMarketOrderAsync(string symbol, string side, string quantity)
    {
        // Pickers are set by value here. On a real device this is a scroll-and-select interaction
        // and would go through Gestures; the simulated driver models the picker as a field, and
        // that shortcut is recorded in the fixture rather than hidden in the screen object.
        await EnterTextAsync(Symbol, symbol);
        await EnterTextAsync(Side, side);
        await EnterTextAsync(OrderType, "Market");
        await EnterTextAsync(Quantity, quantity);
        await TapAsync(SubmitButton);
    }

    /// <summary>
    /// Submits a limit order.
    /// </summary>
    /// <remarks>
    /// A separate method rather than an optional limit price parameter, because the limit price
    /// field only exists when the type is Limit - the web application hides it, and a native one
    /// would too. An optional parameter would let a caller ask for something the screen cannot do.
    /// </remarks>
    public async Task PlaceLimitOrderAsync(
        string symbol, string side, string quantity, string limitPrice)
    {
        await EnterTextAsync(Symbol, symbol);
        await EnterTextAsync(Side, side);
        await EnterTextAsync(OrderType, "Limit");
        await EnterTextAsync(Quantity, quantity);
        await EnterTextAsync(LimitPrice, limitPrice);
        await TapAsync(SubmitButton);
    }

    /// <summary>Submits whatever is currently on the form, without filling anything in.</summary>
    public Task SubmitAsync() => TapAsync(SubmitButton);

    /// <summary>Sets the instrument only.</summary>
    public Task ChooseInstrumentAsync(string symbol) => EnterTextAsync(Symbol, symbol);

    /// <summary>Sets the side only.</summary>
    public Task ChooseSideAsync(string side) => EnterTextAsync(Side, side);

    /// <summary>Waits for the confirmation and returns whether it appeared.</summary>
    public Task<bool> ConfirmationAppearsAsync() => AppearsAsync(Reference, Timeouts.Submit);

    /// <summary>
    /// Whether an acknowledgement is on screen right now, without waiting for one.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="ConfirmationAppearsAsync"/>, and the difference matters for
    /// run time: proving an acknowledgement is <i>absent</i> through the waiting version costs
    /// the full submit timeout on every negative scenario. Where a step has already established
    /// that a rejection message is displayed, the acknowledgement's absence can be checked
    /// instantly.
    /// </remarks>
    public Task<bool> IsConfirmationDisplayedAsync() => IsDisplayedAsync(Reference);

    /// <summary>The reference of the order just placed.</summary>
    public Task<string> OrderReferenceAsync() => TextAsync(Reference);

    /// <summary>Waits for a rejection message and returns whether it appeared.</summary>
    public Task<bool> RejectionAppearsAsync() => AppearsAsync(ErrorMessage, Timeouts.Submit);

    /// <summary>The rejection message.</summary>
    public Task<string> RejectionMessageAsync() => TextAsync(ErrorMessage);

    /// <summary>Navigates to the order history screen.</summary>
    public Task GoToOrderHistoryAsync() => TapAsync(OrderHistoryLink);
}
