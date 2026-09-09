namespace QaFramework.Mobile.Simulation;

/// <summary>
/// Names of the values a simulation fixture may refer to instead of hardcoding them.
/// </summary>
/// <remarks>
/// This exists for one reason: a committed fixture must not contain a credential, not even a
/// throwaway one. The simulated sign-in screen has to decide whether the entered password is
/// correct, which means the expected value has to come from somewhere - and the only acceptable
/// somewhere is the same configuration chain every other suite uses, where the value can be an
/// environment variable in CI.
/// <para>
/// So the fixture says <c>"$expectedPassword"</c> and the suite's hook supplies the value. The
/// side benefit is that the simulated run exercises the real configuration path rather than
/// bypassing it, which is precisely the wiring the simulated target is meant to prove.
/// </para>
/// <para>
/// An unresolved token is a hard failure, never an empty string. An empty expected password
/// would make the "wrong password is refused" scenario pass for the wrong reason.
/// </para>
/// </remarks>
public static class SimulationTokens
{
    /// <summary>The user name the simulated application accepts.</summary>
    public const string ExpectedUsername = "expectedUsername";

    /// <summary>The password the simulated application accepts.</summary>
    public const string ExpectedPassword = "expectedPassword";

    /// <summary>Builds the token set for a signed-in user.</summary>
    public static IReadOnlyDictionary<string, string> ForUser(string username, string password) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExpectedUsername] = username,
            [ExpectedPassword] = password
        };
}
