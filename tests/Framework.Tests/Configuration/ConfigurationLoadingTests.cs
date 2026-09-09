using Framework.Tests.Support;
using QaFramework.Core.Configuration;

namespace Framework.Tests.Configuration;

/// <summary>
/// Tests for <see cref="ConfigurationLoader.Load"/> against real files on disk.
/// </summary>
/// <remarks>
/// Real files, in a temporary directory, rather than a mocked file system: the behaviour under
/// test <i>is</i> the interaction between several files and the environment, so faking the file
/// system would only test the fake. Each test writes exactly the files it needs, which makes the
/// precedence assertions readable as "these two files disagree; this one wins".
/// <para>
/// <see cref="NonParallelizableAttribute"/> at fixture level because the override channel being
/// tested is process-global environment variables.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class ConfigurationLoadingTests
{
    private const string PasswordVariable = "QA_Users__ActiveTrader__Password";
    private const string BaseUrlVariable = "QA_BaseUrl";

    private TemporaryDirectory directory = null!;
    private EnvironmentVariableScope environment = null!;

    [SetUp]
    public void SetUp()
    {
        directory = new TemporaryDirectory();

        // Every variable the loader reads is cleared, so the fixture behaves identically on a
        // developer machine that exports them and on a clean agent that does not.
        environment = new EnvironmentVariableScope()
            .Clear(ConfigurationLoader.EnvironmentVariableName)
            .Clear(PasswordVariable)
            .Clear(BaseUrlVariable);
    }

    [TearDown]
    public void TearDown()
    {
        environment.Dispose();
        directory.Dispose();
    }

    [Test]
    public void Load_WithValidFiles_BindsBothHowAndWhereWeTest()
    {
        // Non-default values throughout. Binding against the defaults would pass even if the
        // loader ignored the file entirely, which is the one bug this test has to catch.
        WriteRunSettings();
        WriteEnvironmentSettings(TestEnvironment.Local, baseUrl: "https://demo.invalid:8443");

        TestConfiguration configuration = ConfigurationLoader.Load(directory.Path);

        using (new AssertionScope())
        {
            configuration.Environment.Should().Be(TestEnvironment.Local);
            configuration.Run.Browser.Name.Should().Be("firefox");
            configuration.Run.Browser.Headless.Should().BeFalse();
            configuration.Run.Browser.SlowMotionMs.Should().Be(50);
            configuration.Run.Evidence.OutputDirectory.Should().Be("artefacts");
            configuration.Run.Evidence.RecordVideo.Should().BeTrue();
            configuration.Timeouts.PollIntervalMs.Should().Be(250);
            configuration.Target.BaseUrl.Should().Be("https://demo.invalid:8443");
            configuration.Target.StartApplicationUnderTest.Should().BeFalse();
            configuration.Target.Users.Should().HaveCount(2);
            configuration.User("ActiveTrader").Username.Should().Be("trader.demo");
        }
    }

    [Test]
    public void Load_ExposesTheTimeoutsFromTheRunSettings()
    {
        // Timeouts is a projection through Run. Worth one assertion because every page object
        // and step reads it via this path, so a projection pointing at a fresh TimeoutSettings
        // would hand the whole suite the defaults while runsettings.json appeared to be honoured.
        WriteRunSettings(pollIntervalMs: 125, eventualConsistencyMs: 9_000);
        WriteEnvironmentSettings(TestEnvironment.Local);

        TestConfiguration configuration = ConfigurationLoader.Load(directory.Path);

        configuration.Timeouts.Should().BeSameAs(configuration.Run.Timeouts);
        configuration.Timeouts.EventualConsistency.Should().Be(TimeSpan.FromSeconds(9));
    }

    [Test]
    public void Load_WithNoRunSettingsFile_ThrowsNamingThePathItLookedIn()
    {
        // The most common first-run failure there is, and the message has to answer "where did
        // you look?". A bare FileNotFoundException sends a new joiner searching the repository
        // for a file that is present in source and simply was not copied to the output directory.
        string expectedPath = directory.PathTo(SettingsFileBuilder.RunSettingsFileName);

        Action load = () => ConfigurationLoader.Load(directory.Path);

        load.Should().Throw<FileNotFoundException>()
            .WithMessage($"*{expectedPath}*")
            .WithMessage("*copied to the output directory*")
            .Which.FileName.Should().Be(expectedPath);
    }

    [Test]
    public void Load_WithASelectedEnvironmentThatHasNoFile_NamesTheMissingFile()
    {
        // The failure mode when someone adds an environment to the enum, or points CI at
        // Staging, and forgets the settings file. Naming the exact file and the variable that
        // selected it is what makes this a one-line fix rather than an investigation.
        WriteRunSettings();
        WriteEnvironmentSettings(TestEnvironment.Local);
        environment.Set(ConfigurationLoader.EnvironmentVariableName, "Staging");

        Action load = () => ConfigurationLoader.Load(directory.Path);

        load.Should().Throw<FileNotFoundException>()
            .WithMessage("*Environment 'Staging' was selected*")
            .WithMessage($"*{SettingsFileBuilder.EnvironmentFileName("Staging")}*")
            .WithMessage($"*{ConfigurationLoader.EnvironmentVariableName}*");
    }

    [Test]
    public void Load_WhenTheEnvironmentVariableSelectsTheEnvironment_ReadsThatEnvironmentsFile()
    {
        // Proves the precedence chain is wired through Load and not only through
        // ResolveEnvironment: the file that gets read is the observable consequence, and two
        // files with different BaseUrls is the only way to tell which one was used.
        WriteRunSettings(environment: "Local");
        WriteEnvironmentSettings(TestEnvironment.Local, baseUrl: "http://local.invalid");
        WriteEnvironmentSettings(TestEnvironment.Ci, baseUrl: "http://ci.invalid");
        environment.Set(ConfigurationLoader.EnvironmentVariableName, "Ci");

        TestConfiguration configuration = ConfigurationLoader.Load(directory.Path);

        configuration.Environment.Should().Be(TestEnvironment.Ci);
        configuration.Target.BaseUrl.Should().Be("http://ci.invalid");
    }

    [Test]
    public void Load_WithABlankPassword_NamesTheRoleAndTheVariableThatWouldSupplyIt()
    {
        // The single most valuable guard in the loader. Defaulting a missing password to an
        // empty string produces a suite in which every authentication test fails with "invalid
        // credentials", which reads as a product defect and sends the on-call engineer to the
        // wrong system entirely. The real cause is an unset CI variable.
        //
        // Both halves of the message are asserted because both are needed: the role says which
        // user, and the variable name says how to fix it without reading the loader's source.
        WriteRunSettings();
        WriteEnvironmentSettings(TestEnvironment.Local, activeTraderPassword: "");

        Action load = () => ConfigurationLoader.Load(directory.Path);

        load.Should().Throw<InvalidOperationException>()
            .WithMessage("*No password is configured for user role(s): ActiveTrader*")
            .WithMessage($"*{PasswordVariable}*")
            .WithMessage("*look like product defects*");
    }

    [Test]
    public void Load_WithAnEnvironmentFileContainingNoUsers_ExplainsThatAtLeastOneIsRequired()
    {
        // ------------------------------------------------------------------------------------
        // This test found a real bug, and the bug is worth recording because it is a trap
        // anyone binding configuration to a dictionary will meet.
        //
        // ConfigurationLoader.ValidateUsers opened with a guard whose message was exactly
        // right: "No users are configured in '<file>'. At least one is required." That guard
        // was unreachable. Microsoft.Extensions.Configuration binds an empty "Users": {}
        // section - and an absent one - to NULL rather than to an empty dictionary, so
        // `settings.Users.Count` threw a NullReferenceException before the message could be
        // emitted.
        //
        // The result was precisely the failure mode the loader exists to prevent: a settings
        // mistake surfacing as a bare "Object reference not set" from inside the framework,
        // which reads as a framework defect rather than as a missing section in a file.
        //
        // Fixed with `settings.Users is null or { Count: 0 }`. This test pins the outcome that
        // matters: the reader is told what is wrong and which file to fix.
        // ------------------------------------------------------------------------------------
        WriteRunSettings();
        directory.WriteFile(
            SettingsFileBuilder.EnvironmentFileName(nameof(TestEnvironment.Local)),
            SettingsFileBuilder.EnvironmentSettingsWithNoUsers);

        Action load = () => ConfigurationLoader.Load(directory.Path);

        load.Should().Throw<InvalidOperationException>()
            .WithMessage("*No users are configured*")
            .WithMessage("*At least one is required*")
            // The file path is the actionable part: it turns the message into an instruction.
            .WithMessage($"*{SettingsFileBuilder.EnvironmentFileName(nameof(TestEnvironment.Local))}*");
    }

    [Test]
    public void Load_WithAnOverridesFile_LetsItBeatTheCommittedEnvironmentFile()
    {
        // The per-developer channel. Environment.Overrides.json is git-ignored, so this is how
        // someone points at a local port or supplies their own credential without producing a
        // diff that will eventually be committed by accident.
        WriteRunSettings();
        WriteEnvironmentSettings(TestEnvironment.Local, baseUrl: "http://committed.invalid");
        directory.WriteFile(
            SettingsFileBuilder.OverridesFileName,
            """{ "BaseUrl": "http://my-machine.invalid:9999" }""");

        TestConfiguration configuration = ConfigurationLoader.Load(directory.Path);

        configuration.Target.BaseUrl.Should().Be("http://my-machine.invalid:9999");
    }

    [Test]
    public void Load_WithNoOverridesFile_StillLoads()
    {
        // The overrides file is optional by design: the suite has to run for someone who has
        // only just cloned the repository. Marking it non-optional would be an easy and
        // completely invisible mistake to make - every existing developer already has the file.
        WriteRunSettings();
        WriteEnvironmentSettings(TestEnvironment.Local);

        File.Exists(directory.PathTo(SettingsFileBuilder.OverridesFileName)).Should().BeFalse();

        Action load = () => ConfigurationLoader.Load(directory.Path);

        load.Should().NotThrow();
    }

    [Test]
    public void Load_WithAnEnvironmentVariable_BeatsBothFiles()
    {
        // The top of the within-environment chain, and the only channel a real secret should
        // use. If the overrides file could beat a variable, a stale local file on an agent would
        // silently override the pipeline's own configuration.
        WriteRunSettings();
        WriteEnvironmentSettings(TestEnvironment.Local, baseUrl: "http://committed.invalid");
        directory.WriteFile(
            SettingsFileBuilder.OverridesFileName,
            """{ "BaseUrl": "http://overrides.invalid" }""");
        environment.Set(BaseUrlVariable, "http://from-ci.invalid");

        TestConfiguration configuration = ConfigurationLoader.Load(directory.Path);

        configuration.Target.BaseUrl.Should().Be("http://from-ci.invalid");
    }

    [Test]
    public void Load_WithAPasswordSuppliedOnlyByVariable_Succeeds()
    {
        // The intended CI shape: the committed file carries no password at all and the pipeline
        // injects it. This is the positive counterpart to the blank-password guard - it is what
        // proves the guard is not simply rejecting the arrangement CI actually uses.
        WriteRunSettings();
        WriteEnvironmentSettings(TestEnvironment.Local, activeTraderPassword: "");
        environment.Set(PasswordVariable, "supplied-by-the-pipeline");

        TestConfiguration configuration = ConfigurationLoader.Load(directory.Path);

        configuration.User("ActiveTrader").Password.Should().Be("supplied-by-the-pipeline");
    }

    [Test]
    public void Load_WithInvalidTimeouts_ValidatesThemDuringLoad()
    {
        // Validation has to be invoked by the loader, not merely available on the type. A
        // TimeoutSettings.Validate that nothing calls is indistinguishable from no validation.
        WriteRunSettings(pollIntervalMs: 30_000, eventualConsistencyMs: 20_000);
        WriteEnvironmentSettings(TestEnvironment.Local);

        Action load = () => ConfigurationLoader.Load(directory.Path);

        load.Should().Throw<InvalidOperationException>().WithMessage("*single attempt*");
    }

    [Test]
    public void Load_WithAValueOfTheWrongType_ReportsWhichFileFailedToBind()
    {
        // The binder's own message says which property failed but not which of several files it
        // came from. With three candidate sources per run - the committed file, the overrides
        // file and the environment - "which file" is the question the reader actually has.
        string runSettingsPath = WriteRunSettings(elementMs: "\"not-a-number\"");
        WriteEnvironmentSettings(TestEnvironment.Local);

        Action load = () => ConfigurationLoader.Load(directory.Path);

        load.Should().Throw<InvalidOperationException>()
            .WithMessage("*Failed to bind RunSettings*")
            .WithMessage($"*{runSettingsPath}*")
            .WithMessage("*RunSettings*");
    }

    [Test]
    public void User_WithAnUnknownRole_ThrowsListingTheConfiguredRoles()
    {
        // A typo in a feature file - "an activetrader is signed in" - is the common case, and
        // the list of configured roles turns it into a one-second fix. Sorted, so the message is
        // stable between runs and diffable in a CI log.
        WriteRunSettings();
        WriteEnvironmentSettings(TestEnvironment.Local);
        TestConfiguration configuration = ConfigurationLoader.Load(directory.Path);

        Action lookUp = () => configuration.User("ActiveTradr");

        lookUp.Should().Throw<KeyNotFoundException>()
            .WithMessage("*No test user is configured for role 'ActiveTradr'*")
            .WithMessage("*ActiveTrader, SecondTrader*");
    }

    private string WriteRunSettings(
        string? environment = "Local",
        int pollIntervalMs = 250,
        int eventualConsistencyMs = 20_000,
        string elementMs = "5000") =>
        directory.WriteFile(
            SettingsFileBuilder.RunSettingsFileName,
            SettingsFileBuilder.RunSettings(environment, pollIntervalMs, eventualConsistencyMs, elementMs));

    private string WriteEnvironmentSettings(
        TestEnvironment target,
        string baseUrl = "http://localhost:5199",
        string activeTraderPassword = "not-a-real-password") =>
        directory.WriteFile(
            SettingsFileBuilder.EnvironmentFileName(target.ToString()),
            SettingsFileBuilder.EnvironmentSettings(baseUrl, activeTraderPassword));
}
