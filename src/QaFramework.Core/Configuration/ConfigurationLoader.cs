using Microsoft.Extensions.Configuration;

namespace QaFramework.Core.Configuration;

/// <summary>
/// Resolves the active environment and binds the settings files into typed objects.
/// </summary>
/// <remarks>
/// The precedence chain is the part worth reading, and it is deliberately explicit rather than
/// implicit in a framework's conventions:
/// <code>
///   environment variable  QA_ENVIRONMENT      (CI sets this)
///        overrides
///   runsettings.json      Environment          (developer default, committed)
///        overrides
///   TestEnvironment.Local (hard fallback)
/// </code>
/// And within a chosen environment:
/// <code>
///   environment variables      (QA_Users__ActiveTrader__Password)   - real secrets live here
///        override
///   Environment.Overrides.json (git-ignored, per-developer)
///        overrides
///   Environment.{Env}.json     (committed)
/// </code>
/// <para>
/// Note the override file is called <c>Environment.Overrides.json</c> rather than
/// <c>Environment.Local.json</c>. The obvious name collides with the environment file for the
/// <see cref="TestEnvironment.Local"/> environment, which would make one file serve two
/// unrelated purposes and quietly break the moment someone git-ignored it.
/// </para>
/// A framework that hides this ordering costs its team hours the first time a value does not
/// take effect, so it is documented here, in the README, and covered by unit tests in
/// Framework.Tests/Configuration.
/// </remarks>
public static class ConfigurationLoader
{
    /// <summary>Environment variable that selects the environment.</summary>
    public const string EnvironmentVariableName = "QA_ENVIRONMENT";

    /// <summary>
    /// Prefix for configuration overrides. <c>QA_Users__ActiveTrader__Password=...</c> sets
    /// <c>Users:ActiveTrader:Password</c> - the double underscore is the .NET convention for
    /// nesting, and it is how a CI secret reaches the suite without ever touching a file.
    /// </summary>
    public const string EnvironmentVariablePrefix = "QA_";

    public static TestEnvironment ResolveEnvironment(string? configuredDefault = null)
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariableName);

        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            // Note the failure mode: an unrecognised value throws. Falling back to a default
            // here would mean a typo in a pipeline variable silently runs the suite against
            // the wrong environment, which is worse than not running it at all.
            return Parse(fromEnvironment, $"the {EnvironmentVariableName} environment variable");
        }

        if (!string.IsNullOrWhiteSpace(configuredDefault))
            return Parse(configuredDefault, "the 'Environment' setting in runsettings.json");

        return TestEnvironment.Local;
    }

    /// <summary>
    /// Parses an environment name, rejecting anything that is not a declared member.
    /// </summary>
    /// <remarks>
    /// <para>Both guards are necessary, and neither is obvious.</para>
    /// <para><c>Enum.TryParse</c> accepts <i>numeric</i> strings. Without the digit check,
    /// <c>QA_ENVIRONMENT=1</c> would quietly resolve to <see cref="TestEnvironment.Ci"/> - a
    /// perfectly valid member, so <see cref="Enum.IsDefined{T}"/> does not catch it - and
    /// <c>QA_ENVIRONMENT=99</c> would produce the undefined value <c>(TestEnvironment)99</c>,
    /// failing much later while hunting for a file called <c>Environment.99.json</c>.</para>
    /// <para>Either outcome defeats the entire reason for choosing an enum over a string: an
    /// invalid value should not get past the front door. Found by a unit test of the loader,
    /// not by any test of the application.</para>
    /// </remarks>
    private static TestEnvironment Parse(string value, string source)
    {
        string trimmed = value.Trim();

        bool looksNumeric = trimmed.Length > 0 &&
            (char.IsAsciiDigit(trimmed[0]) || trimmed[0] is '-' or '+');

        if (!looksNumeric &&
            Enum.TryParse(trimmed, ignoreCase: true, out TestEnvironment parsed) &&
            Enum.IsDefined(parsed))
            return parsed;

        throw new InvalidOperationException(
            $"'{value}' in {source} is not a recognised environment. " +
            $"Expected one of: {string.Join(", ", Enum.GetNames<TestEnvironment>())}. " +
            "Environments are named rather than numbered, so a numeric value is rejected even " +
            "when it happens to match an underlying enum value.");
    }

    /// <summary>
    /// Loads and binds every settings file from the given directory, which is normally the
    /// test assembly's output directory.
    /// </summary>
    public static TestConfiguration Load(string configurationDirectory)
    {
        string runSettingsPath = Path.Combine(configurationDirectory, "runsettings.json");

        if (!File.Exists(runSettingsPath))
            throw new FileNotFoundException(
                $"Could not find 'runsettings.json' at '{runSettingsPath}'. Configuration files are " +
                "copied to the output directory by the test project; a missing file usually means " +
                "the project was not rebuilt after they were added.", runSettingsPath);

        IConfigurationRoot runRoot = new ConfigurationBuilder()
            .AddJsonFile(runSettingsPath, optional: false)
            .AddEnvironmentVariables(EnvironmentVariablePrefix)
            .Build();

        TestEnvironment environment = ResolveEnvironment(runRoot["Environment"]);

        RunSettings runSettings = Bind<RunSettings>(runRoot, "RunSettings", runSettingsPath);
        runSettings.Timeouts.Validate();

        string environmentPath = Path.Combine(configurationDirectory, $"Environment.{environment}.json");

        if (!File.Exists(environmentPath))
            throw new FileNotFoundException(
                $"Environment '{environment}' was selected but '{environmentPath}' does not exist. " +
                $"Either add the file or choose a different environment via {EnvironmentVariableName}.",
                environmentPath);

        IConfigurationRoot environmentRoot = new ConfigurationBuilder()
            // Committed, contains no secrets.
            .AddJsonFile(environmentPath, optional: false)
            // Git-ignored per-developer override. Optional by design: the suite must run for a
            // new joiner who has only just cloned the repository.
            .AddJsonFile(Path.Combine(configurationDirectory, "Environment.Overrides.json"), optional: true)
            // Highest precedence. This is the only channel a real secret should ever use.
            .AddEnvironmentVariables(EnvironmentVariablePrefix)
            .Build();

        EnvironmentSettings environmentSettings =
            Bind<EnvironmentSettings>(environmentRoot, null, environmentPath);

        ValidateUsers(environmentSettings, environmentPath);

        return new TestConfiguration(environment, runSettings, environmentSettings);
    }

    private static T Bind<T>(IConfiguration configuration, string? section, string sourcePath)
    {
        IConfiguration source = section is null ? configuration : configuration.GetSection(section);

        try
        {
            return source.Get<T>()
                   ?? throw new InvalidOperationException($"section bound to null");
        }
        catch (Exception ex)
        {
            // Wrapped so the message names the file. Binder exceptions on their own say which
            // property failed but not which of several files it came from.
            throw new InvalidOperationException(
                $"Failed to bind {typeof(T).Name} from '{sourcePath}'" +
                (section is null ? "" : $" (section '{section}')") + $". {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Fails fast on a user with no password.
    /// </summary>
    /// <remarks>
    /// This is the single most valuable guard in the loader. The alternative - defaulting a
    /// missing password to an empty string - produces a suite in which every authentication
    /// test fails with "invalid credentials", sending the on-call engineer to look for a
    /// product defect when the real cause is an unset CI variable.
    /// </remarks>
    private static void ValidateUsers(EnvironmentSettings settings, string sourcePath)
    {
        // Note the null check. Microsoft.Extensions.Configuration binds an absent or empty
        // "Users": {} section to null rather than to an empty dictionary, so the obvious
        // `settings.Users.Count == 0` threw a NullReferenceException from inside the framework
        // before this carefully-worded message could ever be shown - producing exactly the
        // opaque failure this guard exists to prevent. Found by a unit test of the loader.
        if (settings.Users is null or { Count: 0 })
            throw new InvalidOperationException(
                $"No users are configured in '{sourcePath}'. At least one is required, because " +
                "scenarios address users by role rather than by name.");

        List<string> missing = settings.Users
            .Where(pair => string.IsNullOrWhiteSpace(pair.Value.Password))
            .Select(pair => pair.Key)
            .ToList();

        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"No password is configured for user role(s): {string.Join(", ", missing)}. " +
                $"Set it in Environment.Overrides.json for local runs, or supply it as an " +
                $"environment variable such as " +
                $"'{EnvironmentVariablePrefix}Users__{missing[0]}__Password' in CI. " +
                "The suite fails here rather than running with an empty password, because an " +
                "empty password produces authentication failures that look like product defects.");
    }
}

/// <summary>
/// The fully-resolved configuration for a run. Immutable: resolved once, read everywhere.
/// </summary>
public sealed record TestConfiguration(
    TestEnvironment Environment,
    RunSettings Run,
    EnvironmentSettings Target)
{
    public TimeoutSettings Timeouts => Run.Timeouts;

    /// <summary>
    /// Looks up a user by role, failing with the list of available roles when the role is
    /// unknown. That list turns a typo in a feature file into a one-second fix.
    /// </summary>
    public TestUser User(string role) =>
        Target.Users.TryGetValue(role, out TestUser? user)
            ? user
            : throw new KeyNotFoundException(
                $"No test user is configured for role '{role}'. Configured roles are: " +
                $"{string.Join(", ", Target.Users.Keys.Order())}.");
}
