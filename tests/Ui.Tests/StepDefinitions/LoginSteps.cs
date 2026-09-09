using QaFramework.Core.Configuration;
using QaFramework.Web.Components.Visual;
using QaFramework.Web.Setup;
using QaFramework.Web.Synchronisation;
using TradingDemo.AppModel.Pages;
using TradingDemo.AppModel.Setup;

namespace Ui.Tests.StepDefinitions;

/// <summary>
/// Steps for signing in through the browser.
/// </summary>
/// <remarks>
/// The page objects arrive by constructor injection and are resolved automatically by Reqnroll's
/// container from the registered <see cref="WebTestContext"/> - no registration code per page.
/// Notice how little is in each step: the page objects and components carry the waits and the
/// locators, so a step is a single sentence of intent.
/// </remarks>
[Binding]
public sealed class LoginSteps(
    ScenarioSession session,
    WebTestContext context,
    LoginPage loginPage,
    AccountPage accountPage)
{
    [Given("the sign-in page is open")]
    public Task GivenTheSignInPageIsOpen() => loginPage.OpenAsync();

    [When("the active trader signs in through the browser")]
    public Task WhenTheActiveTraderSignsIn() => SignInAsAsync("ActiveTrader");

    [When("the suspended trader signs in through the browser")]
    public Task WhenTheSuspendedTraderSignsIn() => SignInAsAsync("SuspendedTrader");

    [When("someone signs in as the active trader with the password {string}")]
    public Task WhenSomeoneSignsInWithPassword(string password)
    {
        TestUser user = session.Configuration.User("ActiveTrader");
        return loginPage.SignInAsync(user.Username, password);
    }

    [When("the sign-in form is submitted with no credentials")]
    public Task WhenTheFormIsSubmittedEmpty() => loginPage.SignInAsync(string.Empty, string.Empty);

    /// <summary>
    /// Navigates straight to a protected page with no session.
    /// </summary>
    /// <remarks>
    /// A fresh browser context per scenario means there is genuinely no session storage to
    /// clear first, so this is a real unauthenticated visit rather than a simulated one. That
    /// isolation is what makes the assertion trustworthy.
    /// </remarks>
    [When("an unauthenticated visitor navigates directly to {string}")]
    public async Task WhenAnUnauthenticatedVisitorNavigatesTo(string page)
    {
        await context.NavigateToAsync(page);
        await context.WaitForPageReadyAsync();
    }

    [Then("the account page is displayed")]
    public Task ThenTheAccountPageIsDisplayed() => accountPage.ShouldBeDisplayedAsync();

    [Then("the account details are shown for the active trader")]
    public async Task ThenTheAccountDetailsAreShown()
    {
        TestUser user = session.Configuration.User("ActiveTrader");

        await accountPage.Username.ShouldHaveTextAsync(user.Username);
        await accountPage.AccountNumber.ShouldBePopulatedAsync();
        await accountPage.Balance.ShouldBePopulatedAsync();
    }

    [Then("an error is shown on the sign-in page")]
    public Task ThenAnErrorIsShownOnTheSignInPage() => loginPage.Alert.ShouldShowAsync(AlertKind.Error);

    [Then("the error message reads {string}")]
    public Task ThenTheErrorMessageReads(string expected) => loginPage.Alert.ShouldHaveTextAsync(expected);

    [Then("the error message mentions {string}")]
    public Task ThenTheErrorMessageMentions(string fragment) => loginPage.Alert.ShouldContainTextAsync(fragment);

    [Then("{int} validation details are listed")]
    public Task ThenNValidationDetailsAreListed(int expected) =>
        loginPage.Alert.ShouldHaveDetailCountAsync(expected);

    /// <summary>
    /// Asserts the user is still on the sign-in page.
    /// </summary>
    /// <remarks>
    /// Not redundant with "an error is shown". A page that displays an error <i>and</i>
    /// navigates away has still let the user through, which is a serious authentication defect
    /// that an error-message assertion alone would not catch.
    /// </remarks>
    [Then("the user remains on the sign-in page")]
    public Task ThenTheUserRemainsOnTheSignInPage() => loginPage.ShouldBeDisplayedAsync();

    [Then("the sign-in page is displayed")]
    public Task ThenTheSignInPageIsDisplayed() => loginPage.ShouldBeDisplayedAsync();

    private Task SignInAsAsync(string role)
    {
        TestUser user = session.Configuration.User(role);
        return loginPage.SignInAsync(user.Username, user.Password);
    }
}
