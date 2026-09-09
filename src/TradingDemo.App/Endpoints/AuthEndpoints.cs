using Dapper;
using Microsoft.Data.Sqlite;
using TradingDemo.App.Domain;

namespace TradingDemo.App.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", (LoginRequest request, DemoDatabase db, TokenService tokens) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.BadRequest(new ValidationProblem("Username and password are required.",
                [
                    .. string.IsNullOrWhiteSpace(request.Username)
                        ? new[] { new ValidationDetail("username", "Username is required.") } : [],
                    .. string.IsNullOrWhiteSpace(request.Password)
                        ? new[] { new ValidationDetail("password", "Password is required.") } : []
                ]));
            }

            using SqliteConnection connection = db.OpenConnection();
            var user = connection.QuerySingleOrDefault<UserRow>(
                """
                SELECT id AS Id, username AS Username, password_hash AS PasswordHash,
                       password_salt AS PasswordSalt, status AS Status
                FROM users WHERE username = @username
                """,
                new { username = request.Username });

            // One indistinguishable failure for "no such user" and "wrong password" - an
            // enumeration-safe response. The suspended case is separated only because the
            // product requires a distinct message for it.
            if (user is null || !PasswordHasher.Verify(user.PasswordSalt, request.Password, user.PasswordHash))
            {
                RecordAudit(connection, user?.Id, "LoginFailed", request.Username);
                return Results.Json(ValidationProblem.ForMessage("Invalid username or password."), statusCode: 401);
            }

            if (!string.Equals(user.Status, "Active", StringComparison.Ordinal))
            {
                RecordAudit(connection, user.Id, "LoginBlocked", $"account {user.Status.ToLowerInvariant()}");
                return Results.Json(ValidationProblem.ForMessage($"This account is {user.Status.ToLowerInvariant()}."),
                    statusCode: 403);
            }

            (string token, DateTime expiresAt) = tokens.Issue(user.Id, user.Username);
            RecordAudit(connection, user.Id, "LoginSucceeded", user.Username);

            return Results.Ok(new LoginResponse(token, user.Id, user.Username, expiresAt));
        })
        .WithName("Login");
    }

    private static void RecordAudit(SqliteConnection connection, int? userId, string eventType, string? detail) =>
        connection.Execute(
            "INSERT INTO audit_events (user_id, event_type, detail) VALUES (@userId, @eventType, @detail)",
            new { userId, eventType, detail });
}
