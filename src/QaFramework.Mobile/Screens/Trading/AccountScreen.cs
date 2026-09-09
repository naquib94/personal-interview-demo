using QaFramework.Core.Configuration;
using QaFramework.Mobile.Drivers;
using QaFramework.Mobile.Elements;

namespace QaFramework.Mobile.Screens.Trading;

/// <summary>
/// The account summary, which is where a successful sign-in lands.
/// </summary>
public sealed class AccountScreen(IMobileDriver driver, TimeoutSettings timeouts)
    : BaseScreen(driver, timeouts)
{
    /// <summary>
    /// The one locator in this suite whose identifier differs per platform.
    /// </summary>
    /// <remarks>
    /// Kept as a genuine platform pair rather than normalised away, because a real product always
    /// has a few of these - a screen built by a different squad, a control from a native
    /// component library - and a framework that has never resolved one has not proved it can.
    /// </remarks>
    private static readonly MobileLocator Heading =
        new("the account heading", "account-heading", "accountHeading");

    private static readonly MobileLocator Username =
        MobileLocator.Shared("the signed-in user name", "account-username-text");

    private static readonly MobileLocator AccountNumber =
        MobileLocator.Shared("the account number", "account-number-text");

    private static readonly MobileLocator Balance =
        MobileLocator.Shared("the account balance", "account-balance-text");

    private static readonly MobileLocator Currency =
        MobileLocator.Shared("the account currency", "account-currency-text");

    private static readonly MobileLocator NewOrderLink =
        MobileLocator.Shared("the new order navigation item", "nav-new-order-link");

    private static readonly MobileLocator OrderHistoryLink =
        MobileLocator.Shared("the order history navigation item", "nav-order-history-link");

    /// <inheritdoc />
    protected override MobileLocator Anchor => Heading;

    /// <inheritdoc />
    protected override string ScreenName => "the account screen";

    /// <summary>The user name shown as signed in.</summary>
    public Task<string> SignedInUserAsync() => TextAsync(Username);

    /// <summary>The account number shown.</summary>
    public Task<string> AccountNumberAsync() => TextAsync(AccountNumber);

    /// <summary>The balance shown, as displayed rather than parsed.</summary>
    /// <remarks>
    /// Returned as a string on purpose. Parsing it here would decide a formatting question -
    /// thousands separators, currency symbols - on behalf of an assertion that may well be about
    /// exactly that formatting.
    /// </remarks>
    public Task<string> BalanceAsync() => TextAsync(Balance);

    /// <summary>The currency shown.</summary>
    public Task<string> CurrencyAsync() => TextAsync(Currency);

    /// <summary>Navigates to the new order screen.</summary>
    public Task GoToNewOrderAsync() => TapAsync(NewOrderLink);

    /// <summary>Navigates to the order history screen.</summary>
    public Task GoToOrderHistoryAsync() => TapAsync(OrderHistoryLink);
}
