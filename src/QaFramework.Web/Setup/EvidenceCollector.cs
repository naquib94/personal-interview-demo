using Microsoft.Playwright;
using QaFramework.Core.Configuration;
using QaFramework.Core.Logging;

namespace QaFramework.Web.Setup;

/// <summary>
/// Captures diagnostic evidence when a scenario fails.
/// </summary>
/// <remarks>
/// <para><b>On failure, not on every step.</b> Capturing a screenshot after each step produces
/// a filmstrip that looks impressive and is almost never read, while adding a measurable amount
/// to every run and filling a build agent's disk. The evidence that gets used is the state at
/// the moment of failure. One of the reference suites I studied screenshots every step
/// unconditionally; the cost was real and the benefit was not.</para>
///
/// <para><b>Page source alongside the screenshot.</b> Cheap, and frequently more diagnostic
/// than the image: it answers "was the element absent, or present but invisible, or present
/// with different text?" - which a screenshot cannot.</para>
///
/// <para><b>Filenames identify the scenario.</b> Not <c>{Guid}.png</c>. Twenty GUID-named
/// screenshots in a CI artefact are unusable; <c>Place_a_valid_market_order_20240412-143022.png</c>
/// is immediately correlatable with the failing test.</para>
///
/// <para><b>Nothing here may throw.</b> Every method swallows its own errors and reports them
/// as warnings. An evidence collector that fails during teardown replaces the real failure
/// message with its own, which is the single most frustrating thing a framework can do to
/// someone triaging a red build.</para>
/// </remarks>
public sealed class EvidenceCollector(EvidenceSettings settings)
{
    /// <summary>
    /// Captures the configured evidence for a failed scenario.
    /// </summary>
    /// <returns>Paths of the artefacts written, for attaching to a report.</returns>
    public async Task<IReadOnlyList<string>> CaptureFailureAsync(IPage page, string scenarioName)
    {
        List<string> artefacts = [];
        string safeName = Sanitise(scenarioName);
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

        if (settings.CaptureScreenshotOnFailure)
        {
            string path = Path.Combine(settings.OutputDirectory, "screenshots", $"{safeName}_{stamp}.png");
            if (await TryCaptureAsync(path, () => page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = path,
                // Full page, not just the viewport: the failure is often below the fold, and a
                // viewport screenshot of a long form is the least useful possible artefact.
                FullPage = true
            }), "screenshot"))
                artefacts.Add(path);
        }

        if (settings.CapturePageSourceOnFailure)
        {
            string path = Path.Combine(settings.OutputDirectory, "page-source", $"{safeName}_{stamp}.html");
            if (await TryCaptureAsync(path, async () =>
                await File.WriteAllTextAsync(path, await page.ContentAsync()), "page source"))
                artefacts.Add(path);
        }

        if (artefacts.Count > 0)
            TestLog.Info($"Failure evidence captured: {string.Join(", ", artefacts)}");

        return artefacts;
    }

    private static async Task<bool> TryCaptureAsync(string path, Func<Task> capture, string kind)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await capture();
            return true;
        }
        catch (Exception ex)
        {
            // Broad on purpose, and the one place in this framework where that is correct: a
            // closed page, a full disk or a permission problem must not mask the failure the
            // evidence was being collected for.
            TestLog.Warning($"Could not capture {kind} to '{path}': {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Makes a scenario title safe for a file name on any operating system.</summary>
    private static string Sanitise(string name)
    {
        string cleaned = string.Concat(name.Select(c =>
            char.IsLetterOrDigit(c) ? c : '_'));

        // Truncated because Windows still has a 260-character path limit by default, and a
        // long scenario title plus a deep artefact directory reaches it more easily than
        // people expect.
        return cleaned.Length <= 80 ? cleaned : cleaned[..80];
    }
}
