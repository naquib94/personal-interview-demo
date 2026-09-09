using AwesomeAssertions;
using NUnit.Framework;
using QaFramework.Mobile.Capabilities;

namespace Mobile.Tests.UnitTests;

/// <summary>
/// Tests that a grid run without credentials fails immediately and says why.
/// </summary>
/// <remarks>
/// This is a security test as much as a functional one. The property being protected is that no
/// default endpoint, user name or access key exists anywhere in the code path - so there is
/// nothing to accidentally commit, and nothing to accidentally point at.
/// <para>
/// Note that the variable reader is injected rather than the process environment being mutated.
/// Mutating <c>Environment</c> in a test is a write to process-wide state, and it makes the test
/// unsafe to run in parallel with anything else that reads the same variable - a flake that only
/// appears once the suite is fast enough to parallelise.
/// </para>
/// </remarks>
[TestFixture]
public sealed class CloudGridCredentialTests
{
    private static readonly CloudGridSettings Settings = new();

    [Test]
    public void Missing_variables_are_all_named_in_the_failure()
    {
        Action resolve = () => Settings.Resolve(_ => null);

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*QA_MOBILE_GRID_URL*")
            .WithMessage("*QA_MOBILE_GRID_USERNAME*")
            .WithMessage("*QA_MOBILE_GRID_ACCESS_KEY*");
    }

    [Test]
    public void A_partially_configured_grid_still_fails()
    {
        // The dangerous case. Two of three variables set is exactly the state a half-finished
        // pipeline edit leaves behind, and a framework that filled in the third from a default
        // would then run somewhere nobody intended.
        Action resolve = () => Settings.Resolve(name =>
            name == Settings.UrlVariable ? "https://grid.example.invalid/wd/hub" : null);

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*QA_MOBILE_GRID_USERNAME*");
    }

    [Test]
    public void A_grid_url_that_is_not_a_uri_is_rejected()
    {
        Action resolve = () => Settings.Resolve(name =>
            name == Settings.UrlVariable ? "grid.example.invalid" : "supplied");

        resolve.Should().Throw<InvalidOperationException>().WithMessage("*absolute URI*");
    }

    [Test]
    public void A_fully_configured_grid_resolves()
    {
        ResolvedCloudGrid resolved = Settings.Resolve(name =>
            name == Settings.UrlVariable ? "https://grid.example.invalid/wd/hub" : "supplied");

        resolved.Endpoint.Host.Should().Be("grid.example.invalid");
        resolved.UserName.Should().Be("supplied");
    }

    [Test]
    public void The_resolved_credentials_never_print_the_access_key()
    {
        ResolvedCloudGrid resolved = Settings.Resolve(name =>
            name == Settings.UrlVariable ? "https://grid.example.invalid/wd/hub"
            : name == Settings.UserNameVariable ? "grid-user"
            : "a-secret-value");

        // ToString is overridden precisely because this object can reach a log line through an
        // interpolated string, and a CI log is retained far longer than a session.
        resolved.ToString().Should().NotContain("a-secret-value");
        resolved.ToString().Should().Contain("redacted");
    }

    [Test]
    public void Requesting_a_grid_target_without_a_grid_section_is_rejected()
    {
        MobileRunSettings settings = new()
        {
            Platform = QaFramework.Mobile.Drivers.Platform.Android,
            Target = QaFramework.Mobile.Drivers.RunTarget.CloudGrid,
            Device = new DeviceCapabilities
            {
                PlatformName = "Android",
                AutomationName = "UiAutomator2"
            }
        };

        Action validate = settings.Validate;

        validate.Should().Throw<InvalidOperationException>().WithMessage("*CloudGrid*");
    }
}
