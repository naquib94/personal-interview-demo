namespace Framework.Tests.Support;

/// <summary>
/// Produces the settings-file content the configuration tests load.
/// </summary>
/// <remarks>
/// Built here rather than committed as fixture files so that each test states, in its own body,
/// the one value it depends on - "a run settings file whose configured environment is Ci", "an
/// environment file whose active trader has no password". A shared fixture file drifts: someone
/// adds a user for one test and quietly changes the meaning of six others.
/// </remarks>
internal static class SettingsFileBuilder
{
    public const string RunSettingsFileName = "runsettings.json";
    public const string OverridesFileName = "Environment.Overrides.json";

    public static string EnvironmentFileName(string environment) => $"Environment.{environment}.json";

    /// <summary>
    /// A valid <c>runsettings.json</c>. Pass <c>null</c> for <paramref name="environment"/> to
    /// omit the key entirely, which is how the hard <c>Local</c> fallback is exercised.
    /// </summary>
    public static string RunSettings(
        string? environment = "Local",
        int pollIntervalMs = 250,
        int eventualConsistencyMs = 20_000,
        string elementMs = "5000") =>
        $$"""
        {
          {{(environment is null ? "" : $"\"Environment\": \"{environment}\",")}}
          "RunSettings": {
            "Browser": {
              "Name": "firefox",
              "Headless": false,
              "ViewportWidth": 1280,
              "ViewportHeight": 720,
              "SlowMotionMs": 50
            },
            "Timeouts": {
              "ElementMs": {{elementMs}},
              "PageLoadMs": 15000,
              "SubmitMs": 10000,
              "AbsenceMs": 2000,
              "ApiRequestMs": 30000,
              "EventualConsistencyMs": {{eventualConsistencyMs}},
              "PollIntervalMs": {{pollIntervalMs}},
              "ApplicationStartupMs": 60000
            },
            "Evidence": {
              "CaptureScreenshotOnFailure": true,
              "CapturePageSourceOnFailure": false,
              "RecordVideo": true,
              "OutputDirectory": "artefacts"
            }
          }
        }
        """;

    /// <summary>
    /// A valid <c>Environment.{Name}.json</c> with two users. The passwords are literals in a
    /// temporary file that exists for the duration of one test; nothing here is a secret.
    /// </summary>
    public static string EnvironmentSettings(
        string baseUrl = "http://localhost:5199",
        string activeTraderPassword = "not-a-real-password",
        string databasePath = "") =>
        $$"""
        {
          "BaseUrl": "{{baseUrl}}",
          "StartApplicationUnderTest": false,
          "DatabasePath": "{{databasePath}}",
          "Users": {
            "ActiveTrader": {
              "Username": "trader.demo",
              "Password": "{{activeTraderPassword}}",
              "Description": "Funded account with order history."
            },
            "SecondTrader": {
              "Username": "trader.active",
              "Password": "not-a-real-password"
            }
          }
        }
        """;

    /// <summary>An environment file with no users at all, to exercise the empty-users guard.</summary>
    public const string EnvironmentSettingsWithNoUsers =
        """
        {
          "BaseUrl": "http://localhost:5199",
          "Users": {}
        }
        """;
}
