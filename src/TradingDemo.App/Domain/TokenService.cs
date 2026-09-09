using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TradingDemo.App.Domain;

/// <summary>
/// Issues and validates opaque bearer tokens for the demo API.
/// </summary>
/// <remarks>
/// The signing key is generated at startup and held in memory only. It is never written to
/// disk and never committed, so there is no secret in this repository to leak. Restarting the
/// application invalidates every previously issued token, which is exactly the behaviour a
/// test suite wants: no state survives between runs.
/// </remarks>
public sealed class TokenService
{
    private readonly byte[] signingKey = RandomNumberGenerator.GetBytes(32);
    private readonly TimeSpan lifetime = TimeSpan.FromHours(1);
    private readonly TimeProvider clock;

    public TokenService(TimeProvider? clock = null) => this.clock = clock ?? TimeProvider.System;

    public (string Token, DateTime ExpiresAtUtc) Issue(int userId, string username)
    {
        DateTime expiresAt = clock.GetUtcNow().UtcDateTime.Add(lifetime);
        string payload = $"{userId}|{username}|{expiresAt:O}";
        string encodedPayload = Base64Url(Encoding.UTF8.GetBytes(payload));
        string signature = Base64Url(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(encodedPayload)));
        return ($"{encodedPayload}.{signature}", expiresAt);
    }

    /// <summary>
    /// Returns the authenticated principal, or null when the token is absent, malformed,
    /// tampered with, or expired. A single null return keeps the endpoint code free of
    /// branching over four different failure modes that all produce the same 401.
    /// </summary>
    public AuthenticatedUser? Validate(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader)) return null;

        string raw = authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorizationHeader["Bearer ".Length..].Trim()
            : authorizationHeader.Trim();

        string[] parts = raw.Split('.');
        if (parts.Length != 2) return null;

        byte[] expectedSignature = HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(parts[0]));
        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, FromBase64Url(parts[1]))) return null;

        string[] payload = Encoding.UTF8.GetString(FromBase64Url(parts[0])).Split('|');
        if (payload.Length != 3) return null;
        if (!int.TryParse(payload[0], out int userId)) return null;
        if (!DateTime.TryParse(payload[2], null, DateTimeStyles.RoundtripKind, out DateTime expiresAt)) return null;
        if (expiresAt <= clock.GetUtcNow().UtcDateTime) return null;

        return new AuthenticatedUser(userId, payload[1]);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        try { return Convert.FromBase64String(padded); }
        catch (FormatException) { return []; }
    }
}

public sealed record AuthenticatedUser(int UserId, string Username);
