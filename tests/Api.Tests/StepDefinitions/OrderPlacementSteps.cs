using System.Globalization;
using System.Net;
using QaFramework.Api.Assertions;
using QaFramework.Api.Responses;
using QaFramework.Core.Database;
using TradingDemo.AppModel.Contracts;
using TradingDemo.AppModel.Setup;

namespace Api.Tests.StepDefinitions;

[Binding]
public sealed class OrderPlacementSteps(ScenarioSession session, DatabaseVerifier database)
{
    private ApiResponse<OrderResponse> Response => session.LastResponseAs<ApiResponse<OrderResponse>>();

    // -------------------------------------------------------------------------------------
    // When
    // -------------------------------------------------------------------------------------

    [When("the trader places a market order to buy {decimal} of {string}")]
    public Task WhenTheTraderPlacesAMarketOrder(decimal quantity, string symbol) =>
        PlaceAsync(new PlaceOrderRequest(symbol, "Buy", "Market", quantity, null), session.RequireToken());

    [When("the trader places a limit order to sell {decimal} of {string} at {decimal}")]
    public Task WhenTheTraderPlacesALimitOrder(decimal quantity, string symbol, decimal limitPrice) =>
        PlaceAsync(new PlaceOrderRequest(symbol, "Sell", "Limit", quantity, limitPrice), session.RequireToken());

    [When("the trader submits an order with no symbol, side {string} and quantity {decimal}")]
    public Task WhenTheTraderSubmitsAMalformedOrder(string side, decimal quantity) =>
        PlaceAsync(new PlaceOrderRequest(string.Empty, side, "Market", quantity, null), session.RequireToken());

    [When("the trader submits a {string} order for {string} with quantity {decimal} and limit price {string}")]
    public Task WhenTheTraderSubmitsAnOrderWithLimitPrice(
        string orderType, string symbol, decimal quantity, string limitPrice) =>
        PlaceAsync(
            new PlaceOrderRequest(symbol, "Buy", orderType, quantity, ParseNullableDecimal(limitPrice)),
            session.RequireToken());

    [When("an unauthenticated caller attempts to place a market order to buy {decimal} of {string}")]
    public Task WhenAnUnauthenticatedCallerPlacesAnOrder(decimal quantity, string symbol) =>
        PlaceAsync(new PlaceOrderRequest(symbol, "Buy", "Market", quantity, null), token: null);

    /// <summary>
    /// Submits an order with a signature-invalid token.
    /// </summary>
    /// <remarks>
    /// A structurally valid token whose signature does not verify, rather than random text. The
    /// distinction matters: rejecting "garbage" only proves the parser works, whereas rejecting
    /// a well-formed but re-signed token proves the signature is actually checked. Skipping the
    /// second case is how token forgery gets missed.
    /// </remarks>
    [When("a caller with a tampered token attempts to place a market order to buy {decimal} of {string}")]
    public async Task WhenACallerWithATamperedTokenPlacesAnOrder(decimal quantity, string symbol)
    {
        string valid = session.RequireToken();
        string[] parts = valid.Split('.');

        // A character in the MIDDLE of the signature is altered, not the last one.
        //
        // This detail was a genuine bug in the first version of this test, and it is a nice
        // illustration of a negative test that passes without testing anything. A 32-byte HMAC
        // is 43 base64url characters, and 43 characters encode 258 bits - two more than the 256
        // the signature actually uses. The final character's low bits are therefore padding, so
        // flipping 'A' to 'B' there decodes to the *identical* byte array, the signature still
        // verified, and the order was accepted. The test failed for the right reason and I very
        // nearly "fixed" it by loosening the assertion.
        //
        // Altering a middle character guarantees a different byte array, so this exercises what
        // it claims to: a well-formed token whose signature does not verify. That is a stronger
        // test than sending random text, which only proves the parser rejects nonsense rather
        // than proving the signature is checked at all.
        int middle = parts[1].Length / 2;
        char replacement = parts[1][middle] == 'A' ? 'B' : 'A';
        string tamperedSignature = parts[1][..middle] + replacement + parts[1][(middle + 1)..];

        await PlaceAsync(
            new PlaceOrderRequest(symbol, "Buy", "Market", quantity, null),
            $"{parts[0]}.{tamperedSignature}");
    }

    private async Task PlaceAsync(PlaceOrderRequest request, string? token)
    {
        ApiResponse<OrderResponse> response = await session.Trading.PlaceOrderAsync(token, request);
        session.LastResponse = response;

        if (response.StatusCode == HttpStatusCode.Created)
        {
            session.PlacedOrder = response.RequireData();
            await session.TrackOrderForCleanupAsync(session.PlacedOrder.OrderReference);
        }
    }

    // -------------------------------------------------------------------------------------
    // Then
    // -------------------------------------------------------------------------------------

    [Then("the order is accepted")]
    public void ThenTheOrderIsAccepted() =>
        Response.ShouldHaveStatus(HttpStatusCode.Created).ShouldBeJson();

    [Then("the order status is {string}")]
    public void ThenTheOrderStatusIs(string expected) =>
        Response.RequireData().Status.Should().Be(expected);

    [Then("the order is rejected as a bad request")]
    public void ThenRejectedAsBadRequest() =>
        Response.ShouldHaveStatus(HttpStatusCode.BadRequest).ShouldMatchSchema(ResponseSchemas.ValidationProblem);

    /// <summary>
    /// Asserts a 422 - the request was understood and the business refused it.
    /// </summary>
    /// <remarks>
    /// Kept distinct from the 400 assertion above because the distinction is a requirement, not
    /// an implementation detail. A client must retry-with-correction on 400 and show the user a
    /// message on 422, and an API that returns 400 for both makes that impossible.
    /// </remarks>
    [Then("the order is refused by the business rules")]
    public void ThenRefusedByBusinessRules() =>
        Response.ShouldHaveStatus(HttpStatusCode.UnprocessableEntity)
                .ShouldMatchSchema(ResponseSchemas.ValidationProblem);

    // The two generic error-envelope assertions ("mentions the field", "mention the fields")
    // live in CommonSteps because they apply to several features. Only the order-specific
    // reason assertions below belong here.

    [Then("the rejection reason mentions that the instrument is not tradable")]
    public void ThenReasonMentionsNotTradable() =>
        RequireProblem().ReasonFor("symbol").Should().Contain("not currently tradable");

    [Then("the rejection reason mentions the permitted quantity range")]
    public void ThenReasonMentionsQuantityRange() =>
        RequireProblem().ReasonFor("quantity").Should().Contain("must be between");

    [Then("the rejection reason mentions insufficient funds")]
    public void ThenReasonMentionsInsufficientFunds() =>
        RequireProblem().ReasonFor("quantity").Should().Contain("Insufficient funds");

    /// <summary>
    /// Verifies the order was persisted, against the database.
    /// </summary>
    /// <remarks>
    /// The assertion that closes the loop. Every field is checked against a single joined query
    /// so that a mis-resolved instrument or an order attributed to the wrong account is caught -
    /// neither of which the API response could reveal, because it echoes back what was sent.
    /// </remarks>
    [Then("the order is persisted against the trader's own account")]
    public void ThenTheOrderIsPersisted()
    {
        OrderResponse placed = session.PlacedOrder
            ?? throw new InvalidOperationException("No order was placed by this scenario.");

        PersistedOrder stored = database.QuerySingle<PersistedOrder>(
            TradingQueries.OrderByReference,
            new { orderReference = placed.OrderReference },
            because: $"order '{placed.OrderReference}' was reported as created by the API, so a " +
                     "row must exist in the database");

        using (new AssertionScope())
        {
            stored.Symbol.Should().Be(placed.Symbol);
            stored.Side.Should().Be(placed.Side);
            stored.OrderType.Should().Be(placed.OrderType);
            stored.Quantity.Should().Be(placed.Quantity);
            stored.Status.Should().Be(placed.Status);
            stored.Username.Should().Be(session.CurrentUser!.Username,
                "the order must be attributed to the trader who placed it");
        }
    }

    /// <summary>
    /// Asserts a rejected order left nothing behind.
    /// </summary>
    /// <remarks>
    /// The half that is usually forgotten. An endpoint that writes the row, then fails a
    /// downstream rule and returns 422 without rolling back, passes every status-code assertion
    /// while silently corrupting the customer's order history.
    /// </remarks>
    [Then("no order is persisted")]
    public void ThenNoOrderIsPersisted()
    {
        // A rejected request returns no reference, so uniqueness is verified by counting the
        // trader's orders instead - the seeded baseline must be unchanged.
        long count = database.Count(
            """
            SELECT COUNT(*)
            FROM orders o
                INNER JOIN accounts a ON a.id = o.account_id
                INNER JOIN users    u ON u.id = a.user_id
            WHERE u.username = @username
              AND o.created_at >= @since
            """,
            new
            {
                username = session.CurrentUser!.Username,
                since = DateTime.UtcNow.AddMinutes(-5).ToString("yyyy-MM-dd HH:mm:ss")
            });

        count.Should().Be(0,
            "a rejected order must not leave a row behind; {0} recent order(s) were found for '{1}'",
            count, session.CurrentUser.Username);
    }

    [Then("the order appears in the trader's order history")]
    public async Task ThenTheOrderAppearsInHistory()
    {
        OrderResponse placed = session.PlacedOrder!;

        ApiResponse<PagedResponse<OrderResponse>> history =
            await session.Trading.GetOrderHistoryAsync(session.RequireToken(), pageSize: 100);

        history.ShouldHaveStatus(HttpStatusCode.OK)
            .RequireData().Items
            .Select(o => o.OrderReference)
            .Should().Contain(placed.OrderReference);
    }

    [Then("the order response matches the order schema")]
    public void ThenTheOrderResponseMatchesTheSchema() =>
        Response.ShouldHaveStatus(HttpStatusCode.Created).ShouldMatchSchema(ResponseSchemas.Order);

    // -------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------

    private ValidationProblem RequireProblem() =>
        Response.As<ValidationProblem>()
        ?? throw QaFramework.Core.Logging.TestLog.Failure(
            "Expected the API's standard error envelope but the body could not be deserialised " +
            "into one. A single, predictable error shape is itself a requirement.",
            $"Raw body:{Environment.NewLine}{Response.RawBody}");

    private static decimal? ParseNullableDecimal(string value) =>
        string.Equals(value, "null", StringComparison.OrdinalIgnoreCase)
            ? null
            : decimal.Parse(value, CultureInfo.InvariantCulture);
}
