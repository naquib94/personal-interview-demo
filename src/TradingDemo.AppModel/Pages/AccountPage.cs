using QaFramework.Web.Components.Visual;
using QaFramework.Web.Setup;

namespace TradingDemo.AppModel.Pages;

/// <summary>The account summary page.</summary>
public sealed class AccountPage(WebTestContext context) : ApplicationPage(context, "account.html")
{
    public TextElement Heading { get; } = new(context, "account-heading", "the Account heading");
    public TextElement Username { get; } = new(context, "account-username-text", "the signed-in username");
    public TextElement AccountNumber { get; } = new(context, "account-number-text", "the account number");
    public TextElement Currency { get; } = new(context, "account-currency-text", "the account currency");
    public TextElement Balance { get; } = new(context, "account-balance-text", "the account balance");
    public TextElement AccountType { get; } = new(context, "account-type-text", "the account type");

    /// <summary>
    /// Asserts the page is displayed <i>and populated</i>.
    /// </summary>
    /// <remarks>
    /// Both halves are necessary. The heading is static HTML, so it renders even when the
    /// account endpoint returns a 500 - a test asserting only on the heading would pass against
    /// a completely broken page. Adding the populated check on a value that must come from the
    /// backend is what makes this assertion mean "the page works".
    /// </remarks>
    public override async Task ShouldBeDisplayedAsync()
    {
        await Heading.ShouldHaveTextAsync("Account");
        await AccountNumber.ShouldBePopulatedAsync();
    }
}
