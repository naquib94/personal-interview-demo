using System.Diagnostics;
using NUnit.Framework;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using QaFramework.Core.Configuration;
using TradingDemo.AppModel.Setup;

namespace Ui.Tests.Selenium;

/// <summary>
/// The same two scenarios, written with Selenium instead of Playwright.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists.</b> Playwright is the primary web driver in this framework,
/// and Selenium is the tool named in far more job specifications. Rather than claim depth in
/// both, this file does something more useful: it implements two scenarios twice so the
/// differences are concrete and arguable rather than a matter of preference.</para>
///
/// <para>These are plain NUnit tests rather than BDD scenarios, on purpose. The point of
/// comparison is the <i>synchronisation and locator code</i>, and a step definition would hide
/// exactly that behind a page object.</para>
///
/// <para><b>What the comparison actually shows.</b> Look at
/// <see cref="SignIn_WithValidCredentials_ReachesTheAccountPage"/> below and at
/// <c>LoginSteps</c> plus the components in <c>QaFramework.Web</c>:</para>
/// <list type="bullet">
/// <item><b>Waiting.</b> Selenium needs an explicit <see cref="WebDriverWait"/> per
/// interaction, and every one is a decision the author must remember to make. Playwright's
/// locators auto-wait, so the framework only has to add the <i>application-specific</i> wait
/// (the busy indicator). That difference is the single largest source of flakiness in Selenium
/// suites, and it is a difference in defaults rather than in capability - a well-built Selenium
/// framework wraps waits into its components exactly as this one does.</item>
/// <item><b>Stale elements.</b> A Selenium <see cref="IWebElement"/> is a handle to a specific
/// DOM node and goes stale when the page re-renders. A Playwright locator is a lazy description
/// re-resolved on use, which is why <c>WebComponent.Locator</c> can be a property. Under
/// Selenium the equivalent robustness has to be built by re-finding elements or retrying on
/// <c>StaleElementReferenceException</c>.</item>
/// <item><b>Assertions.</b> Playwright's web-first assertions retry until a timeout, so
/// "the balance is displayed" is one call. With Selenium the retry has to be expressed as a
/// wait around the assertion, which is easy to forget - and forgetting it produces a test that
/// passes on a fast machine and fails in CI.</item>
/// <item><b>Setup.</b> Selenium Manager (4.6+) resolves the driver binary automatically, which
/// closes what used to be Selenium's biggest operational complaint. But it still needs a real
/// Chrome installed, whereas Playwright manages its own browser binaries - which is why these
/// tests are tagged for exclusion in CI and the Playwright ones are not.</item>
/// </list>
///
/// <para><b>The honest conclusion.</b> Selenium is a W3C standard with unmatched grid, language
/// and legacy-browser support; Playwright has better defaults for a modern single-page
/// application and a much better developer loop. For a greenfield SPA I would choose Playwright.
/// For a large estate already on Selenium Grid, or one needing browsers Playwright does not
/// ship, I would not propose a rewrite - I would put the effort into wrapping waits into
/// components, which is where the actual flakiness lives either way.</para>
///
/// <para><b>Tagged <c>Selenium</c> so CI can exclude it</b>
/// (<c>--filter "TestCategory!=Selenium"</c>). Skipping rather than failing when Chrome is
/// absent is deliberate: a test that cannot run must say so, not pretend to pass.</para>
/// </remarks>
[TestFixture]
[Category("Selenium")]
[NonParallelizable]
public sealed class SeleniumComparisonTests
{
    private IWebDriver? driver;
    private WebDriverWait? wait;
    private TestConfiguration configuration = null!;

    [OneTimeSetUp]
    public async Task StartApplication()
    {
        // These tests do not run through Reqnroll, so the shared BeforeTestRun hook does not
        // apply and the configuration and application must be initialised here. The
        // initialisation is idempotent, so it attaches to an already-running instance rather
        // than starting a second one.
        await TestRunContext.InitialiseAsync();
        configuration = TestRunContext.Configuration;
    }

    [SetUp]
    public void StartBrowser()
    {
        ChromeOptions options = new();
        if (configuration.Run.Browser.Headless) options.AddArgument("--headless=new");
        options.AddArgument("--window-size=1920,1080");
        // Required on many CI agents, where the sandbox cannot be initialised in a container.
        options.AddArgument("--no-sandbox");
        options.AddArgument("--disable-dev-shm-usage");

        try
        {
            // Selenium Manager downloads the matching driver automatically, so no driver binary
            // is pinned or committed. It still requires a real Chrome to be installed.
            driver = new ChromeDriver(options);
        }
        catch (WebDriverException ex)
        {
            Assert.Ignore(
                "Chrome is not available on this machine, so the Selenium comparison tests " +
                "cannot run. They are a deliberate side-by-side illustration rather than part " +
                $"of the suite's coverage - the Playwright tests cover the same scenarios. ({ex.Message})");
        }

        wait = new WebDriverWait(driver, configuration.Timeouts.PageLoad);
    }

    [TearDown]
    public void StopBrowser()
    {
        driver?.Quit();
        driver?.Dispose();
        driver = null;
    }

    /// <summary>
    /// The Playwright equivalent is <c>LoginSteps</c> driving <c>LoginPage.SignInAsync</c>.
    /// </summary>
    /// <remarks>
    /// Count the explicit waits below. Each one is a judgement the author had to make and could
    /// have omitted, and omitting any of them produces a test that passes locally and fails
    /// intermittently in CI. The Playwright version has none, because the waiting is either
    /// built into the locator or built once into the component.
    /// </remarks>
    [Test]
    public void SignIn_WithValidCredentials_ReachesTheAccountPage()
    {
        TestUser user = configuration.User("ActiveTrader");
        string baseUrl = configuration.Target.BaseUrl.TrimEnd('/');

        driver!.Navigate().GoToUrl($"{baseUrl}/index.html");

        // Wait 1: the form must exist before it can be filled.
        IWebElement username = wait!.Until(d => d.FindElement(By.CssSelector("[data-testid='login-username-input']")));
        username.SendKeys(user.Username);

        driver.FindElement(By.CssSelector("[data-testid='login-password-input']")).SendKeys(user.Password);
        driver.FindElement(By.CssSelector("[data-testid='login-submit-button']")).Click();

        // Wait 2: the navigation is client-side, so there is no page-load event to hang on.
        wait.Until(d => d.Url.Contains("account.html", StringComparison.OrdinalIgnoreCase));

        // Wait 3: and then the data has to arrive. Asserting immediately here would read the
        // em-dash placeholder and pass or fail depending on how fast the machine is - which is
        // precisely the bug class TextElement.ShouldBePopulatedAsync exists to prevent.
        string accountNumber = wait.Until(d =>
        {
            string text = d.FindElement(By.CssSelector("[data-testid='account-number-text']")).Text;
            return text is "\u2014" or "" ? null : text;
        })!;

        accountNumber.Should().StartWith("DEMO-");
    }

    /// <summary>
    /// The Playwright equivalent is the "An error is shown when the password is wrong" scenario.
    /// </summary>
    /// <remarks>
    /// Note the elapsed-time assertion. It is here to make a point that is easy to assert and
    /// hard to argue with: an explicit wait returns as soon as its condition holds, so a
    /// correctly written Selenium test is not inherently slower than a Playwright one. Selenium's
    /// reputation for slowness comes from implicit waits and hardcoded sleeps, which are choices
    /// rather than properties of the tool.
    /// </remarks>
    [Test]
    public void SignIn_WithAnIncorrectPassword_ShowsAnError()
    {
        TestUser user = configuration.User("ActiveTrader");
        string baseUrl = configuration.Target.BaseUrl.TrimEnd('/');

        Stopwatch elapsed = Stopwatch.StartNew();

        driver!.Navigate().GoToUrl($"{baseUrl}/index.html");

        wait!.Until(d => d.FindElement(By.CssSelector("[data-testid='login-username-input']")))
            .SendKeys(user.Username);
        driver.FindElement(By.CssSelector("[data-testid='login-password-input']")).SendKeys("not-the-password");
        driver.FindElement(By.CssSelector("[data-testid='login-submit-button']")).Click();

        IWebElement alert = wait.Until(d => d.FindElement(By.CssSelector("[data-testid='alert-message']")));

        using (new AssertionScope())
        {
            alert.GetAttribute("data-alert-kind").Should().Be("error");
            alert.FindElement(By.CssSelector("[data-testid='alert-text']"))
                .Text.Should().Be("Invalid username or password.");
            driver.Url.Should().Contain("index.html", "a failed sign-in must not navigate away");

            elapsed.Elapsed.Should().BeLessThan(configuration.Timeouts.PageLoad,
                "an explicit wait returns as soon as its condition is met, so a correctly " +
                "written Selenium test is not inherently slower than its Playwright equivalent");
        }
    }
}
