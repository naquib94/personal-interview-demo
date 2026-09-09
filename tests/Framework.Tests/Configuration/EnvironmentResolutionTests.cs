using Framework.Tests.Support;
using QaFramework.Core.Configuration;

namespace Framework.Tests.Configuration;

/// <summary>
/// Tests for the documented environment precedence chain.
/// </summary>
/// <remarks>
/// This is the highest-consequence logic in the framework. Getting it wrong does not break a
/// test; it points the entire suite at a different system than the one the reader believes it is
/// testing, and everything still goes green. The documented order is:
/// <code>
///   QA_ENVIRONMENT  >  runsettings.json "Environment"  >  TestEnvironment.Local
/// </code>
/// <para>
/// The whole fixture is <see cref="NonParallelizableAttribute"/> because QA_ENVIRONMENT is
/// process-global: one test setting it while another reads it would produce an order-dependent
/// suite, which is precisely the defect this repository exists to argue against.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class EnvironmentResolutionTests
{
    private EnvironmentVariableScope environment = null!;

    [SetUp]
    public void SetUp() =>
        // Cleared rather than assumed absent. A developer or a CI agent may legitimately have
        // QA_ENVIRONMENT exported, and a test that only passes on a machine without it is worse
        // than no test.
        environment = new EnvironmentVariableScope().Clear(ConfigurationLoader.EnvironmentVariableName);

    [TearDown]
    public void TearDown() => environment.Dispose();

    [Test]
    public void ResolveEnvironment_WithNoVariableAndNoConfiguredDefault_FallsBackToLocal()
    {
        // The hard fallback. It exists so that a fresh clone with no configuration at all still
        // runs against the safest possible target rather than refusing to start.
        ConfigurationLoader.ResolveEnvironment().Should().Be(TestEnvironment.Local);
    }

    [TestCase("")]
    [TestCase("   ")]
    public void ResolveEnvironment_WithABlankVariable_FallsThroughToTheConfiguredDefault(string blank)
    {
        // A pipeline that declares QA_ENVIRONMENT but does not populate it - a very common shape
        // for an optional variable - must not be treated as a request for an empty environment.
        environment.Set(ConfigurationLoader.EnvironmentVariableName, blank);

        ConfigurationLoader.ResolveEnvironment("Staging").Should().Be(TestEnvironment.Staging);
    }

    [Test]
    public void ResolveEnvironment_WithOnlyAConfiguredDefault_UsesIt()
    {
        // The committed developer default beats the Local fallback, otherwise runsettings.json
        // would have no effect at all and nobody would notice until Staging was needed.
        ConfigurationLoader.ResolveEnvironment("Ci").Should().Be(TestEnvironment.Ci);
    }

    [Test]
    public void ResolveEnvironment_WithBothSet_LetsTheEnvironmentVariableWin()
    {
        // The load-bearing assertion of the whole chain. CI sets QA_ENVIRONMENT and must not be
        // silently overruled by the developer default that happens to be committed in the repo.
        environment.Set(ConfigurationLoader.EnvironmentVariableName, "Staging");

        ConfigurationLoader.ResolveEnvironment("Ci").Should().Be(TestEnvironment.Staging);
    }

    [TestCase("ci", TestEnvironment.Ci)]
    [TestCase("CI", TestEnvironment.Ci)]
    [TestCase("sTaGiNg", TestEnvironment.Staging)]
    [TestCase("local", TestEnvironment.Local)]
    public void ResolveEnvironment_ParsesTheVariableCaseInsensitively(string value, TestEnvironment expected)
    {
        // Pipeline variables are written by hand, frequently in upper case by convention. Making
        // "CI" a hard failure would be technically defensible and practically obstructive.
        environment.Set(ConfigurationLoader.EnvironmentVariableName, value);

        ConfigurationLoader.ResolveEnvironment().Should().Be(expected);
    }

    [TestCase("ci", TestEnvironment.Ci)]
    [TestCase("STAGING", TestEnvironment.Staging)]
    public void ResolveEnvironment_ParsesTheConfiguredDefaultCaseInsensitively(
        string configured,
        TestEnvironment expected)
    {
        // Same tolerance on both channels. Differing case rules between the file and the
        // variable would be an unpleasant surprise for whoever hit it first.
        ConfigurationLoader.ResolveEnvironment(configured).Should().Be(expected);
    }

    [Test]
    public void ResolveEnvironment_WithAnUnrecognisedVariable_ThrowsListingTheValidNames()
    {
        // The deliberate fail-fast, and the reason the framework uses an enum rather than a
        // string. Falling back to a default here would mean "QA_ENVIRONMENT=Prd" ran the full
        // regression suite against Local and reported a clean pass.
        //
        // Asserting on the content, not just the type: the list of valid names is what turns a
        // typo in a pipeline variable into a ten-second fix instead of a code-reading exercise.
        environment.Set(ConfigurationLoader.EnvironmentVariableName, "Prd");

        Action resolve = () => ConfigurationLoader.ResolveEnvironment("Local");

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*'Prd'*is not a recognised environment*")
            .WithMessage($"*{ConfigurationLoader.EnvironmentVariableName}*")
            .WithMessage("*Local, Ci, Staging*");
    }

    [TestCase("1", TestName = "a numeric value matching a declared member")]
    [TestCase("99", TestName = "a numeric value matching nothing")]
    [TestCase("-1", TestName = "a negative numeric value")]
    public void ResolveEnvironment_WithANumericVariable_IsRejected(string value)
    {
        // A second bug this suite found. Enum.TryParse also accepts NUMERIC strings, so
        // QA_ENVIRONMENT=1 silently resolved to Ci and QA_ENVIRONMENT=99 produced the undefined
        // value (TestEnvironment)99 - which then failed much later, hunting for a file called
        // Environment.99.json.
        //
        // Either outcome defeats the entire reason for choosing an enum over a string: the
        // whole point is that an invalid value cannot get past the front door. Fixed by pairing
        // TryParse with Enum.IsDefined.
        environment.Set(ConfigurationLoader.EnvironmentVariableName, value);

        Action resolve = () => ConfigurationLoader.ResolveEnvironment("Local");

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*is not a recognised environment*")
            .WithMessage("*Local, Ci, Staging*");
    }

    [Test]
    public void ResolveEnvironment_WithAnUnrecognisedConfiguredDefault_NamesTheFile()
    {
        // Same failure, different source, and the message has to say which. "'Prod' is not
        // recognised" without "in runsettings.json" sends the reader hunting through pipeline
        // variables for a value that is committed in the repository.
        Action resolve = () => ConfigurationLoader.ResolveEnvironment("Prod");

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*'Prod'*runsettings.json*is not a recognised environment*")
            .WithMessage("*Local, Ci, Staging*");
    }

    [Test]
    public void ResolveEnvironment_WithAnUnrecognisedVariable_DoesNotSilentlyPreferTheDefault()
    {
        // Stated separately from the message assertion because it is a different claim: the
        // fail-fast must not be reachable-past. An implementation that logged a warning and
        // returned the configured default would satisfy "it throws for a bad value" nowhere and
        // still look reasonable in review.
        environment.Set(ConfigurationLoader.EnvironmentVariableName, "Producton");

        Action resolve = () => ConfigurationLoader.ResolveEnvironment("Ci");

        resolve.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void EnvironmentVariablePrefix_IsTheDotNetNestingConvention()
    {
        // Asserted because it is a contract with CI, not an implementation detail: the pipeline
        // and the documentation both spell out QA_Users__ActiveTrader__Password, and changing
        // the prefix would break every secret without breaking a single compile.
        ConfigurationLoader.EnvironmentVariablePrefix.Should().Be("QA_");
        ConfigurationLoader.EnvironmentVariableName.Should().Be("QA_ENVIRONMENT");
    }
}
