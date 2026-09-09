using System.Text;
using QaFramework.Core.Configuration;
using QaFramework.Core.Logging;
using QaFramework.Mobile.Drivers;

namespace Mobile.Tests.Support;

/// <summary>
/// Writes failure evidence for a scenario.
/// </summary>
/// <remarks>
/// Separated from the hooks because capture and orchestration fail for different reasons and one
/// of them must not take the other down: a full disk should not be able to turn a scenario's real
/// failure into an <c>IOException</c> in teardown, which is what happens when the capture code
/// lives inline in an <c>[AfterScenario]</c>.
/// <para>
/// Evidence is captured on failure only, never per step. A screenshot per step produces a
/// filmstrip that is pleasant to look at, measurably slows the suite and is read by nobody;
/// failures are what need evidence. See <see cref="EvidenceSettings"/>.
/// </para>
/// </remarks>
public sealed class EvidenceRecorder(EvidenceSettings settings)
{
    /// <summary>
    /// Captures a screenshot and the page source for a failed scenario.
    /// </summary>
    /// <returns>The paths written, for logging. Empty when nothing was captured.</returns>
    public async Task<IReadOnlyList<string>> CaptureAsync(
        IMobileDriver driver, string scenarioTitle, Exception error)
    {
        List<string> written = [];

        try
        {
            string directory = ResolveDirectory();
            Directory.CreateDirectory(directory);

            // Timestamp first in the file name so that a directory listing sorts chronologically,
            // which is the order a person triaging a run actually wants.
            string stem = Path.Combine(
                directory,
                $"{DateTime.UtcNow:yyyyMMdd-HHmmss}_{Sanitise(scenarioTitle)}");

            if (settings.CaptureScreenshotOnFailure)
            {
                string path = $"{stem}.png";
                await File.WriteAllBytesAsync(path, await driver.TakeScreenshotAsync());
                written.Add(path);
            }

            if (settings.CapturePageSourceOnFailure)
            {
                string path = $"{stem}.source.xml";
                await File.WriteAllTextAsync(path, await driver.GetPageSourceAsync());
                written.Add(path);
            }

            // The failure message is written alongside the artefacts. Without it, a published
            // artefact folder is a set of files nobody can match to a test result, because CI
            // logs are rotated long before artefacts are.
            string notes = $"{stem}.txt";
            StringBuilder summary = new();
            summary.AppendLine($"Scenario : {scenarioTitle}");
            summary.AppendLine($"Platform : {driver.Platform}");
            summary.AppendLine($"Target   : {driver.Target}");

            if (driver.Target == RunTarget.Simulated)
                summary.AppendLine(
                    "Note     : this evidence comes from the simulated driver. No device, " +
                    "emulator or Appium server was involved, and the screenshot is a blank " +
                    "placeholder rather than a capture of a real screen.");

            summary.AppendLine();
            summary.AppendLine(error.ToString());

            await File.WriteAllTextAsync(notes, summary.ToString());
            written.Add(notes);

            TestLog.Info($"Failure evidence written to '{directory}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logged, never rethrown. The scenario has already failed and its own exception is
            // the news; replacing it with a filesystem error would cost a triage session.
            TestLog.Warning($"Could not write failure evidence: {ex.Message}");
        }

        return written;
    }

    /// <summary>
    /// Turns the configured directory into an absolute path.
    /// </summary>
    /// <remarks>
    /// Relative to the test output directory rather than the current working directory. The
    /// working directory differs between <c>dotnet test</c>, an IDE runner and a CI step, so
    /// resolving against it is how evidence ends up somewhere nobody can find.
    /// </remarks>
    private string ResolveDirectory() =>
        Path.IsPathRooted(settings.OutputDirectory)
            ? settings.OutputDirectory
            : Path.Combine(AppContext.BaseDirectory, settings.OutputDirectory);

    private static string Sanitise(string title)
    {
        StringBuilder safe = new(title.Length);

        foreach (char character in title)
            safe.Append(char.IsLetterOrDigit(character) ? character : '-');

        // Truncated, because a scenario title plus a path plus a timestamp can exceed the path
        // length limit on Windows agents, and a failed write here loses the evidence entirely.
        string result = safe.ToString().Trim('-');

        return result.Length <= 80 ? result : result[..80];
    }
}
