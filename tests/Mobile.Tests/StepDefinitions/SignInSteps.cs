using AwesomeAssertions;
using QaFramework.Core.Configuration;
using QaFramework.Mobile.Screens.Trading;
using Reqnroll;

namespace Mobile.Tests.StepDefinitions;

/// <summary>
/// Steps about reaching, or failing to reach, an account.
/// </summary>
/// <remarks>
/// Dependencies arrive through the constructor. Reqnroll's container hands over the instances the
/// hooks registered, so this class has no knowledge of how a session was created and no static
/// state of its own - which is what allows the same steps to run against a device, an emulator or
/// the simulated target without a single change.
/// <para>
/// Note what these steps do <i>not</i> contain: no locators, no waits, no taps. Those belong to
/// the screen objects. A step definition's job is to translate a business sentence into an
/// intention and then assert on the outcome; when it starts naming elements, the feature file
/// stops being the readable artefact it was written to be.
/// </para>
/// </remarks>
[Binding]
public sealed class SignInSteps(
    LoginScreen loginScreen,
    AccountScreen accountScreen,
    TestConfiguration configuration)
{
    // The role, not the person. The user name and password behind it live in the environment
    // settings, so rotating a credential is a configuration edit rather than a change to a
    // feature file - and the Gherkin reads like a business rule rather than a login form.
    private const string TraderRole = "ActiveTrader";

    private TestUser Trader => configuration.User(TraderRole);

    [Given("the mobile trading app is open")]
    public Task TheAppIsOpenAsync() => loginScreen.WaitUntilLoadedAsync();

    [When("an active trader signs in")]
    public Task AnActiveTraderSignsInAsync() =>
        loginScreen.SignInAsync(Trader.Username, Trader.Password);

    [When("an active trader signs in with an incorrect password")]
    public Task AnActiveTraderSignsInWithAnIncorrectPasswordAsync() =>
        // A deliberately wrong value rather than a mutation of the real one. Appending a
        // character to a valid password reads as clever and breaks the day a rule about password
        // length or a truncating field makes the mutated value valid again.
        loginScreen.SignInAsync(Trader.Username, "not-the-configured-password");

    /// <summary>
    /// Signs in as a precondition for another feature.
    /// </summary>
    /// <remarks>
    /// A single step rather than three, because a Background that narrates the sign-in form makes
    /// every other feature file depend on the shape of a screen it is not about. Reusing the
    /// step definitions of another feature by calling them from Gherkin - the "step calls step"
    /// pattern - was rejected for the same reason it usually is: it produces scenarios whose
    /// failures point at the wrong feature.
    /// </remarks>
    [Given("an active trader is signed in to the mobile app")]
    public async Task AnActiveTraderIsSignedInAsync()
    {
        await loginScreen.WaitUntilLoadedAsync();
        await loginScreen.SignInAsync(Trader.Username, Trader.Password);
        await accountScreen.WaitUntilLoadedAsync();
    }

    [Then("their account summary is shown")]
    public async Task TheirAccountSummaryIsShownAsync()
    {
        await accountScreen.WaitUntilLoadedAsync();

        (await accountScreen.AccountNumberAsync())
            .Should().NotBeNullOrWhiteSpace(
                "the account summary is only meaningful if it names an account");
    }

    [Then("the summary identifies them as the trader who signed in")]
    public async Task TheSummaryIdentifiesTheTraderAsync() =>
        (await accountScreen.SignedInUserAsync())
            .Should().Be(
                Trader.Username,
                "the app must show the account of whoever signed in, not a cached or default one");

    [Then("sign-in is refused with the message {string}")]
    public async Task SignInIsRefusedWithTheMessageAsync(string expected)
    {
        (await loginScreen.ErrorAppearsAsync())
            .Should().BeTrue("a refused sign-in must tell the user it was refused");

        (await loginScreen.ErrorMessageAsync()).Should().Be(expected);
    }

    [Then("their account summary is not shown")]
    public async Task TheirAccountSummaryIsNotShownAsync() =>
        // Asserted through the screen's own anchor rather than by proving that no element
        // anywhere is visible. This costs one short absence wait and answers the question the
        // scenario actually asks: did the refused sign-in let anybody through.
        (await accountScreen.IsDisplayedAsync())
            .Should().BeFalse("a refused sign-in must not reveal an account");
}
