using QaFramework.Mobile.Capabilities;

namespace QaFramework.Mobile.Drivers;

/// <summary>
/// Works out which WebDriver endpoint a session should talk to.
/// </summary>
/// <remarks>
/// Separated from the factories so that "where do we connect" is decided once for both
/// platforms, and so the credential path has a single audited location.
/// </remarks>
internal static class MobileEndpoint
{
    internal static Uri Resolve(MobileRunSettings settings)
    {
        switch (settings.Target)
        {
            case RunTarget.LocalAppium:
            case RunTarget.Emulator:
                return new Uri(settings.AppiumServerUrl);

            case RunTarget.CloudGrid:
                CloudGridSettings grid = settings.CloudGrid
                    ?? throw new InvalidOperationException(
                        "Target 'CloudGrid' requires a Mobile.CloudGrid section naming the " +
                        "environment variables that carry the endpoint and credentials.");

                ResolvedCloudGrid resolved = grid.Resolve();

                // Credentials are placed in the endpoint's user information, which is the
                // convention most device grids accept. Some grids instead expect them as vendor
                // capabilities; that is a one-line change here, and keeping it here means it is
                // one line rather than one line per platform factory.
                //
                // The values themselves are never logged: ResolvedCloudGrid.ToString redacts the
                // access key, and this Uri is not written to the log by the factories - they log
                // the settings' host, not this object.
                return new UriBuilder(resolved.Endpoint)
                {
                    UserName = Uri.EscapeDataString(resolved.UserName),
                    Password = Uri.EscapeDataString(resolved.AccessKey)
                }.Uri;

            case RunTarget.Simulated:
                throw new InvalidOperationException(
                    "The 'Simulated' target has no endpoint: it never opens a session. This " +
                    "exception means a platform factory was called for a simulated run, which " +
                    "MobileDriverProvider exists to prevent.");

            default:
                throw new InvalidOperationException($"Unhandled run target '{settings.Target}'.");
        }
    }

    /// <summary>
    /// A log-safe description of the endpoint.
    /// </summary>
    /// <remarks>
    /// Exists because <see cref="Resolve"/> may return a URI containing an access key, and a
    /// URI is exactly the sort of value that gets interpolated into a log line without thinking.
    /// Callers that want to tell a human where they are connecting use this; nothing logs the
    /// resolved URI.
    /// </remarks>
    internal static string Describe(MobileRunSettings settings) => settings.Target switch
    {
        RunTarget.LocalAppium or RunTarget.Emulator => settings.AppiumServerUrl,
        RunTarget.CloudGrid => "a cloud device grid (endpoint and credentials from the environment)",
        RunTarget.Simulated => "no endpoint - simulated run",
        _ => "an unknown endpoint"
    };
}
