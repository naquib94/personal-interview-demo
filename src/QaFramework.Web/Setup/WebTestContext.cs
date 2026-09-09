using Microsoft.Playwright;
using QaFramework.Core.Configuration;

namespace QaFramework.Web.Setup;

/// <summary>
/// The scenario-scoped handle on the browser that every component and page depends on.
/// </summary>
/// <remarks>
/// <para><b>The <see cref="ActivePage"/> / <see cref="App"/> distinction is the most valuable
/// thing in this class</b>, and it is worth two minutes of anyone's time.</para>
///
/// <para><see cref="ActivePage"/> is the browser tab: navigation, load state, screenshots,
/// video. <see cref="App"/> is <i>the surface the application is rendered on</i>. Today they
/// are the same object. If the application is later embedded in an iframe - a portal shell, an
/// SSO wrapper, a host application - then <see cref="App"/> becomes
/// <c>ActivePage.FrameLocator(...)</c> and <b>only this one property changes</b>. Every
/// component and every page follows automatically, because they all locate through
/// <see cref="App"/>.</para>
///
/// <para>Without that seam, the same change means editing every locator in the suite. It costs
/// one property and three lines of comment to buy, and I have seen the alternative cost weeks.</para>
///
/// <para><b>On instance state instead of statics.</b> Everything here is instance state,
/// resolved per scenario through the BDD container. A <c>static</c> or <c>[ThreadStatic]</c>
/// browser handle - the pattern in several suites I have worked on - breaks under an async
/// test runner, because <c>[ThreadStatic]</c> does not follow an <c>await</c> onto a
/// continuation thread. The failure is intermittent and looks like product flakiness.</para>
/// </remarks>
public sealed class WebTestContext(TestConfiguration configuration)
{
    private IPlaywright? playwright;
    private IBrowser? browser;
    private IBrowserContext? browserContext;
    private IPage? page;

    public TestConfiguration Configuration { get; } = configuration;

    public TimeoutSettings Timeouts => Configuration.Timeouts;

    /// <summary>The browser tab. Use for navigation, load state and evidence capture.</summary>
    public IPage ActivePage => page
        ?? throw new InvalidOperationException(
            "The browser has not been started. A scenario must call StartBrowserAsync (normally " +
            "from a BeforeScenario hook) before using the browser.");

    /// <summary>
    /// The surface the application is rendered on. Every locator resolves from here.
    /// </summary>
    public IPage App => ActivePage;

    /// <summary>The signed-in user, or null. Enables login reuse and correct sign-out.</summary>
    public TestUser? ActiveUser { get; private set; }

    /// <summary>Bearer token for the signed-in session, when a scenario needs it for API setup.</summary>
    public string? AuthToken { get; private set; }

    public bool IsStarted => page is not null;

    public void RecordSignIn(TestUser user, string? token)
    {
        ActiveUser = user;
        AuthToken = token;
    }

    public void ClearSignIn()
    {
        ActiveUser = null;
        AuthToken = null;
    }

    public async Task StartBrowserAsync()
    {
        BrowserSettings settings = Configuration.Run.Browser;

        playwright = await Playwright.CreateAsync();

        IBrowserType browserType = settings.Name.ToLowerInvariant() switch
        {
            "chromium" or "chrome" => playwright.Chromium,
            "firefox" => playwright.Firefox,
            "webkit" or "safari" => playwright.Webkit,
            // Naming the valid values in the message turns a five-minute puzzle into a typo fix.
            _ => throw new InvalidOperationException(
                $"'{settings.Name}' is not a supported browser. Expected chromium, firefox or webkit.")
        };

        browser = await browserType.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = settings.Headless,
            SlowMo = settings.SlowMotionMs == 0 ? null : settings.SlowMotionMs,
            // Null rather than empty string: Playwright treats an empty channel as an invalid
            // channel rather than as "no channel", and fails to launch with a message that does
            // not mention the channel at all.
            Channel = string.IsNullOrWhiteSpace(settings.Channel) ? null : settings.Channel
        });

        browserContext = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = settings.ViewportWidth, Height = settings.ViewportHeight },
            RecordVideoDir = Configuration.Run.Evidence.RecordVideo
                ? Path.Combine(Configuration.Run.Evidence.OutputDirectory, "recordings")
                : null,
            // A fresh context per scenario means no cookies, no session storage and no local
            // storage carried over. That isolation is what allows scenarios to run in any order
            // and in parallel, and it is much cheaper than launching a whole browser each time.
            IgnoreHTTPSErrors = true
        });

        // Set once, centrally. Playwright's own default is 30 seconds for everything, which is
        // simultaneously too long for a missing element and too short for a slow page load.
        browserContext.SetDefaultTimeout(Timeouts.ElementMs);
        browserContext.SetDefaultNavigationTimeout(Timeouts.PageLoadMs);

        page = await browserContext.NewPageAsync();
    }

    /// <summary>Navigates to a path relative to the configured base URL.</summary>
    public async Task NavigateToAsync(string relativePath)
    {
        string baseUrl = Configuration.Target.BaseUrl.TrimEnd('/');
        await ActivePage.GotoAsync($"{baseUrl}/{relativePath.TrimStart('/')}");
    }

    /// <summary>
    /// Closes everything, in the order Playwright requires, tolerating a partially-started
    /// browser.
    /// </summary>
    /// <remarks>
    /// The null checks matter: if <see cref="StartBrowserAsync"/> failed halfway - browser
    /// launched, context creation failed - teardown must still release the browser process.
    /// Otherwise a configuration mistake leaks a Chromium process per scenario and eventually
    /// exhausts the agent.
    /// </remarks>
    public async Task StopBrowserAsync()
    {
        if (browserContext is not null) await browserContext.CloseAsync();
        if (browser is not null) await browser.CloseAsync();
        playwright?.Dispose();

        page = null;
        browserContext = null;
        browser = null;
        playwright = null;
        ClearSignIn();
    }
}
