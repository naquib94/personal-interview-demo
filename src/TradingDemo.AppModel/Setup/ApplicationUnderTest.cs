using System.Diagnostics;
using QaFramework.Core.Configuration;
using QaFramework.Core.Logging;
using QaFramework.Core.Synchronisation;

namespace TradingDemo.AppModel.Setup;

/// <summary>
/// Starts and stops the demo application for the duration of a test run.
/// </summary>
/// <remarks>
/// <para><b>Why the suite owns the application.</b> Every reference framework I studied points
/// at a long-lived shared deployment, and pays for it continuously: 90-second timeouts to
/// absorb other people's load, retry counts to absorb other people's deploys, test data
/// accumulating for years, and an entire class of failure that is nobody's fault. Owning the
/// application removes all of that. The suite starts a known version against a known seeded
/// database, and any failure is a real finding.</para>
///
/// <para>It is not always possible - some systems are far too large to start on an agent - but
/// where it is possible it is worth a great deal, and it is the first thing I would try to
/// arrange on a new project.</para>
///
/// <para><b>Startup is idempotent.</b> If something is already healthy at the configured
/// address, this attaches to it instead of starting a second copy. That is what makes the
/// developer loop pleasant: run the app once in a terminal, then run tests repeatedly without
/// a 5-second start each time. It is also what makes the whole suite work unchanged against a
/// deployed environment.</para>
/// </remarks>
public sealed class ApplicationUnderTest(EnvironmentSettings settings, TimeoutSettings timeouts) : IAsyncDisposable
{
    private const string ProjectRelativePath = @"src\TradingDemo.App";
    private const string AssemblyName = "TradingDemo.App.dll";

    private Process? process;

    /// <summary>Whether this instance started the process, and is therefore responsible for it.</summary>
    public bool OwnsProcess => process is not null;

    public async Task StartAsync()
    {
        if (await IsHealthyAsync())
        {
            TestLog.Info(
                $"An instance is already responding at {settings.BaseUrl}; attaching to it rather " +
                "than starting another.");
            return;
        }

        if (!settings.StartApplicationUnderTest)
            throw TestLog.Failure(
                $"Nothing is responding at {settings.BaseUrl} and this environment is configured " +
                "not to start the application under test (Target:StartApplicationUnderTest is " +
                "false). Either start the application, or point Target:BaseUrl at a running " +
                "instance.");

        string assemblyPath = LocateAssembly();

        // The suite tells the application where to keep its database, rather than guessing
        // where the application will put it.
        //
        // This is what makes suites independent. Each suite has its own port and its own
        // database file, so the API suite and the UI suite own separate application instances
        // and cannot corrupt each other's data - which matters because both call
        // /test-support/reset. Sharing one instance meant whichever test assembly finished
        // first killed the application the others were still using, and every remaining
        // scenario failed with a connection error that looked nothing like the real cause.
        string databasePath = QaFramework.Core.Database.DatabasePath.Resolve(settings.DatabasePath);

        process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList =
            {
                assemblyPath,
                "--urls", settings.BaseUrl,
                $"--Demo:DatabasePath={databasePath}"
            },
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)!,
            // Redirected so the application's own log output does not interleave with the test
            // runner's, while remaining available if startup fails.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw TestLog.Failure($"Failed to start a process for '{assemblyPath}'.");

        TestLog.Info($"Started the application under test (pid {process.Id}) at {settings.BaseUrl}.");

        try
        {
            await Wait.UntilAsync(
                IsHealthyAsync,
                $"the application under test to become healthy at {settings.BaseUrl}",
                timeouts.ApplicationStartup,
                TimeSpan.FromMilliseconds(200));
        }
        catch (TimeoutException)
        {
            // The application's own stderr is very often the whole explanation - a port
            // already in use, a missing SQL script. Surfacing it here saves the reader from
            // having to reproduce the startup by hand.
            string diagnostics = await ReadProcessOutputAsync();
            await StopAsync();

            throw TestLog.Failure(
                $"The application under test did not become healthy within " +
                $"{timeouts.ApplicationStartup.TotalSeconds:F0}s.",
                diagnostics);
        }
    }

    private async Task<bool> IsHealthyAsync()
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(2) };

        try
        {
            HttpResponseMessage response =
                await client.GetAsync($"{settings.BaseUrl.TrimEnd('/')}{ApiClients.Endpoints.Health}");
            return response.IsSuccessStatusCode;
        }
        // Expected while the application is still starting. Narrow on purpose: only the
        // exceptions that genuinely mean "not listening yet".
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }

    /// <summary>
    /// Finds the built application assembly.
    /// </summary>
    /// <remarks>
    /// Walks up from the test assembly's own location looking for the repository root, then
    /// uses the same build configuration the tests were built in. The alternative - a relative
    /// path with a fixed number of <c>..</c> segments - breaks the moment a project moves or the
    /// output layout changes, and fails with a bare FileNotFoundException.
    /// <para>
    /// <c>QA_APP_ASSEMBLY</c> overrides the search, which is what a published or containerised
    /// layout would use.
    /// </para>
    /// </remarks>
    private static string LocateAssembly()
    {
        string? overridePath = Environment.GetEnvironmentVariable("QA_APP_ASSEMBLY");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return File.Exists(overridePath)
                ? overridePath
                : throw TestLog.Failure(
                    $"QA_APP_ASSEMBLY is set to '{overridePath}' but no file exists there.");
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        string configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName, ProjectRelativePath, "bin", configuration, "net10.0", AssemblyName);

            if (File.Exists(candidate)) return candidate;

            directory = directory.Parent;
        }

        throw TestLog.Failure(
            $"Could not find '{AssemblyName}'. Expected it at " +
            $"'<repository root>/{ProjectRelativePath}/bin/{configuration}/net10.0/{AssemblyName}', " +
            $"searching upwards from '{AppContext.BaseDirectory}'.",
            "Build the whole solution before running the tests ('dotnet build'), or set " +
            "QA_APP_ASSEMBLY to the assembly's full path.");
    }

    private async Task<string> ReadProcessOutputAsync()
    {
        if (process is null) return "(no process was started)";

        try
        {
            // Bounded by a short timeout: a hung process would otherwise make the diagnostic
            // read hang too, replacing a useful failure with a stalled test run.
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(3));
            string output = await process.StandardOutput.ReadToEndAsync(cancellation.Token);
            string error = await process.StandardError.ReadToEndAsync(cancellation.Token);

            return $"Application stdout:{Environment.NewLine}{Trim(output)}{Environment.NewLine}" +
                   $"Application stderr:{Environment.NewLine}{Trim(error)}";
        }
        catch (Exception ex)
        {
            return $"(the application's output could not be read: {ex.GetType().Name})";
        }

        static string Trim(string value) =>
            string.IsNullOrWhiteSpace(value) ? "(empty)"
            : value.Length <= 4000 ? value
            : value[^4000..];
    }

    public async Task StopAsync()
    {
        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                // entireProcessTree: dotnet launches the application as a child, so killing
                // only the parent leaves the real listener holding the port - and the next run
                // then fails with "address already in use", which is a confusing way to
                // discover a teardown bug.
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            TestLog.Warning($"Could not stop the application under test cleanly: {ex.Message}");
        }
        finally
        {
            process.Dispose();
            process = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
