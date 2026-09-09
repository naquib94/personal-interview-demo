using AwesomeAssertions;
using Mobile.Tests.Support;
using NUnit.Framework;
using QaFramework.Core.Configuration;
using QaFramework.Mobile.Capabilities;
using QaFramework.Mobile.Drivers;
using QaFramework.Mobile.Simulation;

namespace Mobile.Tests.UnitTests;

/// <summary>
/// Tests for the failure-evidence path.
/// </summary>
/// <remarks>
/// Evidence capture is the code that only ever runs when something has already gone wrong, which
/// makes it the code least likely to be exercised and most annoying to find broken. Testing it
/// directly is cheaper than discovering on a bad day that the artefact folder has been empty for
/// a month.
/// </remarks>
[TestFixture]
public sealed class EvidenceRecorderTests
{
    private string directory = string.Empty;

    [SetUp]
    public void CreateTemporaryDirectory()
    {
        // A unique directory per test, and an absolute one. Writing into the shared output
        // directory would make these tests depend on each other's leftovers.
        directory = Path.Combine(Path.GetTempPath(), $"qa-mobile-evidence-{Guid.NewGuid():N}");
    }

    [TearDown]
    public void RemoveTemporaryDirectory()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    [Test]
    public async Task A_failure_produces_a_screenshot_a_page_source_and_a_summary()
    {
        await using IMobileDriver driver = CreateSimulatedDriver();

        EvidenceRecorder recorder = new(new EvidenceSettings
        {
            CaptureScreenshotOnFailure = true,
            CapturePageSourceOnFailure = true,
            OutputDirectory = directory
        });

        IReadOnlyList<string> written = await recorder.CaptureAsync(
            driver, "An active trader reaches their account", new InvalidOperationException("boom"));

        written.Should().HaveCount(3);
        written.Should().Contain(path => path.EndsWith(".png", StringComparison.Ordinal));
        written.Should().Contain(path => path.EndsWith(".source.xml", StringComparison.Ordinal));

        string summary = await File.ReadAllTextAsync(
            written.Single(path => path.EndsWith(".txt", StringComparison.Ordinal)));

        summary.Should().Contain("boom");

        // The annotation that keeps a published artefact from implying a device was used. If this
        // assertion ever fails, the framework has started overstating what it tested.
        summary.Should().Contain("No device, emulator or Appium server was involved");
    }

    [Test]
    public async Task Capture_can_be_turned_off_per_artefact()
    {
        await using IMobileDriver driver = CreateSimulatedDriver();

        EvidenceRecorder recorder = new(new EvidenceSettings
        {
            CaptureScreenshotOnFailure = false,
            CapturePageSourceOnFailure = true,
            OutputDirectory = directory
        });

        IReadOnlyList<string> written = await recorder.CaptureAsync(
            driver, "A scenario", new InvalidOperationException("boom"));

        written.Should().NotContain(path => path.EndsWith(".png", StringComparison.Ordinal));
    }

    [Test]
    public async Task A_scenario_title_with_punctuation_becomes_a_usable_file_name()
    {
        await using IMobileDriver driver = CreateSimulatedDriver();

        EvidenceRecorder recorder = new(new EvidenceSettings { OutputDirectory = directory });

        IReadOnlyList<string> written = await recorder.CaptureAsync(
            driver,
            "Sign-in is refused with \"a message\": really?",
            new InvalidOperationException("boom"));

        // Invalid path characters in a scenario title are a real failure mode, and one that only
        // appears for the one scenario whose title contains a colon.
        written.Should().NotBeEmpty();
        written.Should().AllSatisfy(path => Path.GetFileName(path).Should().NotContain(":"));
    }

    private static IMobileDriver CreateSimulatedDriver()
    {
        MobileRunSettings settings = MobileSettingsLoader.Load(AppContext.BaseDirectory);

        SimulatedApp app = SimulatedApp.LoadFromFile(
            Path.Combine(AppContext.BaseDirectory, settings.SimulationFixture),
            SimulationTokens.ForUser("trader.demo", "not-a-real-credential"));

        return new SimulatedMobileDriver(app, settings.Platform);
    }
}
