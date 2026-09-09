using System.Security.Cryptography;
using System.Text;

namespace TradingDemo.App.Domain;

/// <summary>
/// Salted SHA-256 password verification for the demo application.
/// </summary>
/// <remarks>
/// Deliberately simple, and deliberately annotated: SHA-256 is <b>not</b> an appropriate
/// password hash for production software because it is fast, which is precisely what an
/// attacker wants. A real system uses a memory-hard KDF - Argon2id, scrypt or PBKDF2 with a
/// high iteration count.
/// <para>
/// It is used here because the goal is a hash the committed seed script can reproduce in one
/// readable line, and because the accounts guard nothing. Knowing where a shortcut has been
/// taken, and saying so, is the difference between a simplification and a vulnerability.
/// </para>
/// </remarks>
public static class PasswordHasher
{
    public static string Hash(string salt, string password)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{salt}:{password}"));
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Compares in fixed time. Even in a demo, a timing-safe comparison costs one method call,
    /// and an equality check on a secret is a habit worth keeping.
    /// </summary>
    public static bool Verify(string salt, string password, string expectedHash)
    {
        byte[] actual = Encoding.UTF8.GetBytes(Hash(salt, password));
        byte[] expected = Encoding.UTF8.GetBytes(expectedHash);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
