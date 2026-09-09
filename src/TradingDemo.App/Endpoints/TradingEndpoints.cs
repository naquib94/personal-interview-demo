using Dapper;
using Microsoft.Data.Sqlite;
using TradingDemo.App.Domain;

namespace TradingDemo.App.Endpoints;

public static class TradingEndpoints
{
    private const int MaxPageSize = 100;

    public static void MapTradingEndpoints(this IEndpointRouteBuilder app)
    {
        // ---------------------------------------------------------------------------------
        // GET /api/accounts/me
        // ---------------------------------------------------------------------------------
        app.MapGet("/api/accounts/me", (HttpRequest http, DemoDatabase db, TokenService tokens) =>
        {
            if (Authenticate(http, tokens) is not { } principal) return Unauthorised();

            using SqliteConnection connection = db.OpenConnection();
            var account = connection.QuerySingleOrDefault<AccountRow>(
                """
                SELECT a.id AS Id, a.account_number AS AccountNumber, a.currency AS Currency,
                       a.balance AS Balance, a.account_type AS AccountType, u.username AS Username
                FROM accounts a
                    INNER JOIN users u ON u.id = a.user_id
                WHERE a.user_id = @userId
                ORDER BY a.id
                LIMIT 1
                """,
                new { userId = principal.UserId });

            // A user with no account is a real state in the seed data (analyst.readonly), so
            // this 404 is exercised by a test rather than being dead code.
            return account is null
                ? Results.NotFound(ValidationProblem.ForMessage("No account exists for this user."))
                : Results.Ok(account.ToResponse());
        })
        .WithName("GetMyAccount");

        // ---------------------------------------------------------------------------------
        // GET /api/instruments  and  GET /api/instruments/{symbol}
        // ---------------------------------------------------------------------------------
        app.MapGet("/api/instruments", (string? assetClass, DemoDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            var instruments = connection.Query<InstrumentRow>(
                """
                SELECT symbol AS Symbol, display_name AS DisplayName, asset_class AS AssetClass,
                       bid AS Bid, ask AS Ask,
                       min_quantity AS MinQuantity, max_quantity AS MaxQuantity,
                       is_tradable AS IsTradable
                FROM instruments
                WHERE (@assetClass IS NULL OR asset_class = @assetClass)
                ORDER BY symbol
                """,
                new { assetClass }).Select(i => i.ToResponse()).ToList();

            return Results.Ok(instruments);
        })
        .WithName("GetInstruments");

        app.MapGet("/api/instruments/{symbol}", (string symbol, DemoDatabase db) =>
        {
            using SqliteConnection connection = db.OpenConnection();
            var instrument = connection.QuerySingleOrDefault<InstrumentRow>(
                """
                SELECT symbol AS Symbol, display_name AS DisplayName, asset_class AS AssetClass,
                       bid AS Bid, ask AS Ask,
                       min_quantity AS MinQuantity, max_quantity AS MaxQuantity,
                       is_tradable AS IsTradable
                FROM instruments WHERE symbol = @symbol
                """,
                new { symbol });

            return instrument is null
                ? Results.NotFound(ValidationProblem.ForMessage($"Instrument '{symbol}' was not found."))
                : Results.Ok(instrument.ToResponse());
        })
        .WithName("GetInstrument");

        // ---------------------------------------------------------------------------------
        // POST /api/orders
        // ---------------------------------------------------------------------------------
        app.MapPost("/api/orders", (PlaceOrderRequest request, HttpRequest http, DemoDatabase db, TokenService tokens) =>
        {
            if (Authenticate(http, tokens) is not { } principal) return Unauthorised();

            using SqliteConnection connection = db.OpenConnection();

            var account = connection.QuerySingleOrDefault<AccountRow>(
                "SELECT id AS Id, balance AS Balance FROM accounts WHERE user_id = @userId ORDER BY id LIMIT 1",
                new { userId = principal.UserId });

            if (account is null)
                return Results.NotFound(ValidationProblem.ForMessage("No account exists for this user."));

            // ---- Field-level validation. Collected, not short-circuited: returning every
            // ---- problem at once is both better UX and far easier to assert against.
            List<ValidationDetail> errors = [];

            if (string.IsNullOrWhiteSpace(request.Symbol))
                errors.Add(new ValidationDetail("symbol", "Symbol is required."));
            if (!IsOneOf(request.Side, "Buy", "Sell"))
                errors.Add(new ValidationDetail("side", "Side must be either 'Buy' or 'Sell'."));
            if (!IsOneOf(request.OrderType, "Market", "Limit"))
                errors.Add(new ValidationDetail("orderType", "Order type must be either 'Market' or 'Limit'."));
            if (request.Quantity is null or <= 0)
                errors.Add(new ValidationDetail("quantity", "Quantity must be greater than zero."));
            if (string.Equals(request.OrderType, "Limit", StringComparison.OrdinalIgnoreCase) && request.LimitPrice is null or <= 0)
                errors.Add(new ValidationDetail("limitPrice", "A limit order requires a positive limit price."));
            if (string.Equals(request.OrderType, "Market", StringComparison.OrdinalIgnoreCase) && request.LimitPrice is not null)
                errors.Add(new ValidationDetail("limitPrice", "A market order must not specify a limit price."));

            if (errors.Count > 0)
                return Results.BadRequest(new ValidationProblem("The order request is invalid.", errors));

            var instrument = connection.QuerySingleOrDefault<InstrumentRow>(
                """
                SELECT id AS Id, symbol AS Symbol, display_name AS DisplayName,
                       asset_class AS AssetClass, ask AS Ask, bid AS Bid,
                       min_quantity AS MinQuantity, max_quantity AS MaxQuantity,
                       is_tradable AS IsTradable
                FROM instruments WHERE symbol = @symbol
                """,
                new { symbol = request.Symbol });

            if (instrument is null)
                return Results.BadRequest(ValidationProblem.Single(
                    "The order request is invalid.", "symbol", $"Instrument '{request.Symbol}' was not found."));

            // ---- Business rules. Separated from field validation and answered with 422 rather
            // ---- than 400: the request was well-formed, the *business* refused it. Tests assert
            // ---- on that distinction, which is why it is worth making.
            if (!instrument.IsTradable)
                return UnprocessableEntity(ValidationProblem.Single(
                    "The order was rejected.", "symbol", $"Instrument '{instrument.Symbol}' is not currently tradable."));

            decimal quantity = request.Quantity!.Value;

            if (quantity < instrument.MinQuantity || quantity > instrument.MaxQuantity)
                return UnprocessableEntity(ValidationProblem.Single(
                    "The order was rejected.", "quantity",
                    $"Quantity must be between {instrument.MinQuantity} and {instrument.MaxQuantity} for {instrument.Symbol}."));

            bool isBuy = string.Equals(request.Side, "Buy", StringComparison.OrdinalIgnoreCase);
            decimal executionPrice = isBuy ? instrument.Ask : instrument.Bid;
            decimal notional = quantity * executionPrice;

            if (isBuy && notional > account.Balance)
                return UnprocessableEntity(ValidationProblem.Single(
                    "The order was rejected.", "quantity",
                    $"Insufficient funds: the order requires {notional:F2} but the account balance is {account.Balance:F2}."));

            // ---- Persist. Market orders fill immediately; limit orders rest as Pending. The
            // ---- whole write is one transaction so a failure cannot leave a half-placed order.
            bool isMarket = string.Equals(request.OrderType, "Market", StringComparison.OrdinalIgnoreCase);
            string status = isMarket ? "Filled" : "Pending";
            string reference = $"ORD-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

            using SqliteTransaction transaction = connection.BeginTransaction();

            int orderId = connection.QuerySingle<int>(
                """
                INSERT INTO orders (order_reference, account_id, instrument_id, side, order_type,
                                    quantity, limit_price, status)
                VALUES (@reference, @accountId, @instrumentId, @side, @orderType, @quantity, @limitPrice, @status);
                SELECT last_insert_rowid();
                """,
                new
                {
                    reference,
                    accountId = account.Id,
                    instrumentId = instrument.Id,
                    side = Normalise(request.Side!),
                    orderType = Normalise(request.OrderType!),
                    quantity,
                    limitPrice = request.LimitPrice,
                    status
                },
                transaction);

            if (isMarket)
            {
                connection.Execute(
                    "INSERT INTO order_fills (order_id, fill_quantity, fill_price) VALUES (@orderId, @quantity, @price)",
                    new { orderId, quantity, price = executionPrice }, transaction);

                connection.Execute(
                    "UPDATE accounts SET balance = balance + @delta WHERE id = @accountId",
                    new { delta = isBuy ? -notional : notional, accountId = account.Id }, transaction);
            }

            connection.Execute(
                "INSERT INTO audit_events (user_id, event_type, detail) VALUES (@userId, 'OrderPlaced', @detail)",
                new { userId = principal.UserId, detail = reference }, transaction);

            transaction.Commit();

            OrderResponse response = new(reference, instrument.Symbol, Normalise(request.Side!),
                Normalise(request.OrderType!), quantity, request.LimitPrice, status, DateTime.UtcNow);

            return Results.Created($"/api/orders/{reference}", response);
        })
        .WithName("PlaceOrder");

        // ---------------------------------------------------------------------------------
        // GET /api/orders  and  GET /api/orders/{reference}
        // ---------------------------------------------------------------------------------
        app.MapGet("/api/orders", (string? status, int? page, int? pageSize, HttpRequest http, DemoDatabase db, TokenService tokens) =>
        {
            if (Authenticate(http, tokens) is not { } principal) return Unauthorised();

            int currentPage = Math.Max(page ?? 1, 1);
            int size = Math.Clamp(pageSize ?? 20, 1, MaxPageSize);

            using SqliteConnection connection = db.OpenConnection();

            const string filter =
                """
                FROM orders o
                    INNER JOIN instruments i ON i.id = o.instrument_id
                    INNER JOIN accounts    a ON a.id = o.account_id
                WHERE a.user_id = @userId
                  AND (@status IS NULL OR o.status = @status)
                """;

            int total = connection.QuerySingle<int>($"SELECT COUNT(*) {filter}",
                new { userId = principal.UserId, status });

            var orders = connection.Query<OrderRow>(
                $"""
                SELECT o.order_reference AS OrderReference, i.symbol AS Symbol, o.side AS Side,
                       o.order_type AS OrderType, o.quantity AS Quantity, o.limit_price AS LimitPrice,
                       o.status AS Status, o.created_at AS CreatedAt
                {filter}
                ORDER BY o.created_at DESC, o.id DESC
                LIMIT @size OFFSET @offset
                """,
                new { userId = principal.UserId, status, size, offset = (currentPage - 1) * size })
                .Select(o => o.ToResponse()).ToList();

            return Results.Ok(new PagedResponse<OrderResponse>(orders, currentPage, size, total));
        })
        .WithName("GetOrderHistory");

        app.MapGet("/api/orders/{reference}", (string reference, HttpRequest http, DemoDatabase db, TokenService tokens) =>
        {
            if (Authenticate(http, tokens) is not { } principal) return Unauthorised();

            using SqliteConnection connection = db.OpenConnection();
            var order = connection.QuerySingleOrDefault<OrderRow>(
                """
                SELECT o.order_reference AS OrderReference, i.symbol AS Symbol, o.side AS Side,
                       o.order_type AS OrderType, o.quantity AS Quantity, o.limit_price AS LimitPrice,
                       o.status AS Status, o.created_at AS CreatedAt
                FROM orders o
                    INNER JOIN instruments i ON i.id = o.instrument_id
                    INNER JOIN accounts    a ON a.id = o.account_id
                WHERE o.order_reference = @reference AND a.user_id = @userId
                """,
                new { reference, userId = principal.UserId });

            // Returning 404 rather than 403 for another user's order avoids confirming that the
            // reference exists at all. Worth a security test in its own right.
            return order is null
                ? Results.NotFound(ValidationProblem.ForMessage($"Order '{reference}' was not found."))
                : Results.Ok(order.ToResponse());
        })
        .WithName("GetOrder");
    }

    private static AuthenticatedUser? Authenticate(HttpRequest http, TokenService tokens) =>
        tokens.Validate(http.Headers.Authorization.FirstOrDefault());

    private static IResult Unauthorised() =>
        Results.Json(ValidationProblem.ForMessage("A valid bearer token is required."), statusCode: 401);

    private static IResult UnprocessableEntity(ValidationProblem problem) =>
        Results.Json(problem, statusCode: 422);

    private static bool IsOneOf(string? value, params string[] allowed) =>
        value is not null && allowed.Any(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Normalises casing so stored values always match the CHECK constraints.</summary>
    private static string Normalise(string value) =>
        string.Concat(char.ToUpperInvariant(value[0]), value[1..].ToLowerInvariant());
}
