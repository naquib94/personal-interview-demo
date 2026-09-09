using System.Net;
using QaFramework.Api.Assertions;
using QaFramework.Api.Responses;
using TradingDemo.AppModel.Contracts;
using TradingDemo.AppModel.Setup;

namespace Api.Tests.StepDefinitions;

[Binding]
public sealed class AccountAndMarketSteps(ScenarioSession session)
{
    // -------------------------------------------------------------------------------------
    // Account
    // -------------------------------------------------------------------------------------

    [When("the trader requests their account")]
    public async Task WhenTheTraderRequestsTheirAccount() =>
        session.LastResponse = await session.Trading.GetMyAccountAsync(session.RequireToken());

    [When("an unauthenticated caller requests an account")]
    public async Task WhenAnUnauthenticatedCallerRequestsAnAccount() =>
        session.LastResponse = await session.Trading.GetMyAccountAsync(token: null);

    [Then("the account details are returned")]
    public void ThenTheAccountDetailsAreReturned() =>
        Account.ShouldHaveStatus(HttpStatusCode.OK).ShouldBeJson();

    /// <summary>
    /// Asserts the account belongs to the caller.
    /// </summary>
    /// <remarks>
    /// Not redundant with the 200. A horizontal-privilege defect - resolving the account from a
    /// query parameter rather than from the token - returns a perfectly valid 200 containing
    /// somebody else's balance. This assertion is what would catch it.
    /// </remarks>
    [Then("the account belongs to the signed-in trader")]
    public void ThenTheAccountBelongsToTheTrader() =>
        Account.RequireData().Username.Should().Be(session.CurrentUser!.Username,
            "the account must be resolved from the bearer token, not from any client-supplied value");

    [Then("the account response matches the account schema")]
    public void ThenTheAccountMatchesTheSchema() =>
        Account.ShouldHaveStatus(HttpStatusCode.OK).ShouldMatchSchema(ResponseSchemas.Account);

    // -------------------------------------------------------------------------------------
    // Instruments
    // -------------------------------------------------------------------------------------

    [When("the instrument list is requested")]
    public async Task WhenTheInstrumentListIsRequested() =>
        session.LastResponse = await session.Trading.GetInstrumentsAsync();

    [When("the instrument list is requested for the asset class {string}")]
    public async Task WhenTheInstrumentListIsRequestedForAssetClass(string assetClass) =>
        session.LastResponse = await session.Trading.GetInstrumentsAsync(assetClass);

    [When("the instrument {string} is requested")]
    public async Task WhenTheInstrumentIsRequested(string symbol) =>
        session.LastResponse = await session.Trading.GetInstrumentAsync(symbol);

    [Then("at least {int} instrument is returned")]
    public void ThenAtLeastNInstrumentsAreReturned(int minimum) =>
        Instruments.ShouldHaveStatus(HttpStatusCode.OK)
            .RequireData().Should().HaveCountGreaterThanOrEqualTo(minimum);

    /// <summary>
    /// Asserts a business invariant across every returned row.
    /// </summary>
    /// <remarks>
    /// A negative spread means the bid exceeds the ask, which is arbitrage-free-market
    /// nonsense and would be a serious pricing defect. Applying the rule to every row rather
    /// than a sampled one is what makes the assertion worth having, and costs nothing.
    /// </remarks>
    [Then("every instrument has a non-negative spread")]
    public void ThenEveryInstrumentHasANonNegativeSpread()
    {
        using AssertionScope scope = new();

        foreach (InstrumentResponse instrument in Instruments.RequireData())
        {
            instrument.Spread.Should().BeGreaterThanOrEqualTo(0,
                "the spread for {0} must not be negative (bid {1}, ask {2})",
                instrument.Symbol, instrument.Bid, instrument.Ask);

            instrument.Ask.Should().BeGreaterThanOrEqualTo(instrument.Bid,
                "the ask for {0} must not be below the bid", instrument.Symbol);
        }
    }

    [Then("every returned instrument belongs to the asset class {string}")]
    public void ThenEveryInstrumentBelongsToAssetClass(string assetClass)
    {
        IReadOnlyList<InstrumentResponse> instruments =
            Instruments.ShouldHaveStatus(HttpStatusCode.OK).RequireData();

        // Asserting non-empty first. Without it, a filter that matches nothing passes this
        // scenario vacuously - "every element of an empty set satisfies the predicate" is true
        // and useless, and it is the most common way a filter test stops testing anything.
        instruments.Should().NotBeEmpty(
            "the '{0}' filter should match at least one seeded instrument; an empty result " +
            "would make the following assertion vacuous", assetClass);

        instruments.Should().OnlyContain(i => i.AssetClass == assetClass);
    }

    [Then("the instrument list response matches the instrument list schema")]
    public void ThenTheInstrumentListMatchesTheSchema() =>
        Instruments.ShouldHaveStatus(HttpStatusCode.OK).ShouldMatchSchema(ResponseSchemas.InstrumentList);

    private ApiResponse<AccountResponse> Account => session.LastResponseAs<ApiResponse<AccountResponse>>();

    private ApiResponse<List<InstrumentResponse>> Instruments =>
        session.LastResponseAs<ApiResponse<List<InstrumentResponse>>>();
}
