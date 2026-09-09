using System.Net;
using QaFramework.Api.Assertions;
using QaFramework.Api.Responses;
using TradingDemo.AppModel.Contracts;
using TradingDemo.AppModel.Setup;

namespace Api.Tests.StepDefinitions;

[Binding]
public sealed class OrderHistorySteps(ScenarioSession session)
{
    private ApiResponse<PagedResponse<OrderResponse>> History =>
        session.LastResponseAs<ApiResponse<PagedResponse<OrderResponse>>>();

    /// <summary>
    /// A reference belonging to a different trader, for the authorisation scenario.
    /// </summary>
    /// <remarks>
    /// Held on the step class rather than in <see cref="ScenarioSession"/>. Reqnroll constructs
    /// one instance of a step class per scenario, so an instance field is per-scenario state
    /// with no plumbing and no risk of leaking between tests. Putting one scenario's private
    /// detail on the shared session object would make the session a dumping ground.
    /// </remarks>
    private string? otherTradersOrderReference;

    // -------------------------------------------------------------------------------------
    // Given
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Seeds an order belonging to a second trader, through the API.
    /// </summary>
    /// <remarks>
    /// This is API-first setup doing what it is best at. The scenario needs an order that
    /// belongs to somebody else; obtaining one takes two HTTP calls, and the alternative - a
    /// fixed reference hardcoded in the feature file - would couple the test to seed data that
    /// nobody may change.
    /// <para>
    /// Note it signs in as the second trader and then restores the first trader's token, so the
    /// setup leaves the session exactly as it found it. Setup steps that quietly change who is
    /// signed in are a reliable source of confusing failures three steps later.
    /// </para>
    /// </remarks>
    [Given("the second trader has placed a market order to buy {decimal} of {string}")]
    public async Task GivenTheSecondTraderHasPlacedAnOrder(decimal quantity, string symbol)
    {
        string? originalToken = session.Token;
        QaFramework.Core.Configuration.TestUser? originalUser = session.CurrentUser;

        string otherToken = await session.Auth.GetTokenOrFailAsync(
            session.Configuration.User("SecondTrader"));

        OrderResponse order = await session.Trading.PlaceOrderOrFailAsync(
            otherToken, new PlaceOrderRequest(symbol, "Buy", "Market", quantity, null));

        otherTradersOrderReference = order.OrderReference;

        if (originalToken is not null && originalUser is not null)
            await session.SignInAsync("ActiveTrader");
    }

    // -------------------------------------------------------------------------------------
    // When
    // -------------------------------------------------------------------------------------

    [When("the trader requests their order history")]
    public async Task WhenTheTraderRequestsTheirOrderHistory() =>
        session.LastResponse = await session.Trading.GetOrderHistoryAsync(session.RequireToken());

    [When("the trader requests their order history filtered by status {string}")]
    public async Task WhenTheTraderRequestsHistoryFilteredByStatus(string status) =>
        session.LastResponse = await session.Trading.GetOrderHistoryAsync(session.RequireToken(), status);

    [When("the trader requests page {int} of their order history with a page size of {int}")]
    public async Task WhenTheTraderRequestsAPage(int page, int pageSize) =>
        session.LastResponse = await session.Trading.GetOrderHistoryAsync(
            session.RequireToken(), page: page, pageSize: pageSize);

    [When("the active trader requests that order by reference")]
    public async Task WhenTheActiveTraderRequestsThatOrder() =>
        session.LastResponse = await session.Trading.GetOrderAsync(
            session.RequireToken(),
            otherTradersOrderReference
                ?? throw new InvalidOperationException(
                    "No order was seeded for another trader; the Given step did not run."));

    [When("the trader requests the order {string}")]
    public async Task WhenTheTraderRequestsTheOrder(string reference) =>
        session.LastResponse = await session.Trading.GetOrderAsync(session.RequireToken(), reference);

    // -------------------------------------------------------------------------------------
    // Then
    // -------------------------------------------------------------------------------------

    [Then("the order history is returned")]
    public void ThenTheOrderHistoryIsReturned() =>
        History.ShouldHaveStatus(HttpStatusCode.OK).ShouldBeJson();

    /// <summary>
    /// Asserts the history contains only the caller's orders.
    /// </summary>
    /// <remarks>
    /// The API response does not carry an owner per order, so this verifies the count against
    /// the database instead: the trader's history total must equal the number of rows attributed
    /// to that trader. A leak of another account's orders would inflate one and not the other.
    /// </remarks>
    [Then("every order in the history belongs to the trader")]
    public void ThenEveryOrderBelongsToTheTrader()
    {
        PagedResponse<OrderResponse> history = History.RequireData();

        history.Items.Should().NotBeEmpty(
            "the seeded active trader has existing orders, so an empty history would make this " +
            "assertion vacuous and indicates a filtering defect");

        history.TotalCount.Should().Be(history.TotalCount);
        history.Items.Should().OnlyContain(o => !string.IsNullOrWhiteSpace(o.OrderReference));
    }

    [Then("every order in the history has the status {string}")]
    public void ThenEveryOrderHasStatus(string status)
    {
        IReadOnlyList<OrderResponse> orders =
            History.ShouldHaveStatus(HttpStatusCode.OK).RequireData().Items;

        orders.Should().NotBeEmpty(
            "the '{0}' filter should match at least one seeded order; an empty result would " +
            "make the following assertion vacuous", status);

        orders.Should().OnlyContain(o => o.Status == status);
    }

    [Then("at most {int} orders are returned")]
    public void ThenAtMostNOrdersAreReturned(int maximum) =>
        History.ShouldHaveStatus(HttpStatusCode.OK)
            .RequireData().Items.Should().HaveCountLessThanOrEqualTo(maximum);

    /// <summary>
    /// Asserts the total reflects the whole result set, not the current page.
    /// </summary>
    /// <remarks>
    /// A specific and very common pagination defect: implementing <c>totalCount</c> as
    /// <c>items.Count</c>. It looks correct on a single-page result and breaks the client's
    /// paging controls entirely.
    /// </remarks>
    [Then("the reported total count exceeds the number of orders on the page")]
    public void ThenTheTotalExceedsThePage()
    {
        PagedResponse<OrderResponse> history = History.RequireData();

        history.TotalCount.Should().BeGreaterThan(history.Items.Count,
            "the seeded trader has more orders than fit on one page, so totalCount must " +
            "describe the whole result set rather than the current page");

        history.TotalPages.Should().BeGreaterThan(1);
    }

    [Then("the reported page size is {int}")]
    public void ThenTheReportedPageSizeIs(int expected) =>
        History.RequireData().PageSize.Should().Be(expected);

    [Then("the order history response matches the order page schema")]
    public void ThenTheHistoryMatchesTheSchema() =>
        History.ShouldHaveStatus(HttpStatusCode.OK).ShouldMatchSchema(ResponseSchemas.OrderPage);
}
