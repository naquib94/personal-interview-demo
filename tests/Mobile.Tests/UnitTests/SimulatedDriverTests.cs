using AwesomeAssertions;
using NUnit.Framework;
using QaFramework.Mobile.Capabilities;
using QaFramework.Mobile.Drivers;
using QaFramework.Mobile.Elements;
using QaFramework.Mobile.Simulation;

namespace Mobile.Tests.UnitTests;

/// <summary>
/// Tests for the simulated driver, and for the committed fixture it loads.
/// </summary>
/// <remarks>
/// Testing a test double looks circular, and here it is not: the scenarios depend on the double
/// behaving consistently, so a defect in it produces a red suite that points at the application
/// rather than at the fixture. These tests keep that misdirection from happening.
/// <para>
/// The iOS cases are the valuable ones. The scenarios run as Android in the committed
/// configuration, so without a test that drives the same fixture as iOS, the platform-pair code
/// path would compile and never execute.
/// </para>
/// </remarks>
[TestFixture]
public sealed class SimulatedDriverTests
{
    private const string Username = "trader.demo";
    private const string Password = "not-a-real-credential";

    private static readonly MobileLocator LoginHeading =
        MobileLocator.Shared("the sign-in heading", "login-heading");

    private static readonly MobileLocator UsernameField =
        MobileLocator.Shared("the username field", "login-username-input");

    private static readonly MobileLocator PasswordField =
        MobileLocator.Shared("the password field", "login-password-input");

    private static readonly MobileLocator SubmitButton =
        MobileLocator.Shared("the sign-in button", "login-submit-button");

    private static readonly MobileLocator ErrorMessage =
        MobileLocator.Shared("the sign-in error", "login-error-text");

    private static readonly MobileLocator AccountHeading =
        new("the account heading", "account-heading", "accountHeading");

    private static readonly MobileLocator OrderHistoryRows =
        MobileLocator.Shared("the order history rows", "order-history-grid-row");

    private static SimulatedMobileDriver CreateDriver(Platform platform = Platform.Android)
    {
        // Loaded from the committed fixture rather than from an inline string, so that these
        // tests fail if the fixture itself is broken - which is the file the scenarios depend on.
        MobileRunSettings settings = MobileSettingsLoader.Load(AppContext.BaseDirectory);

        SimulatedApp app = SimulatedApp.LoadFromFile(
            Path.Combine(AppContext.BaseDirectory, settings.SimulationFixture),
            SimulationTokens.ForUser(Username, Password));

        return new SimulatedMobileDriver(app, platform);
    }

    [Test]
    public async Task The_session_starts_on_the_sign_in_screen()
    {
        await using SimulatedMobileDriver driver = CreateDriver();

        (await driver.IsDisplayedAsync(LoginHeading)).Should().BeTrue();
        (await driver.GetTextAsync(LoginHeading)).Should().Be("Sign in");
    }

    [Test]
    public async Task An_element_on_another_screen_is_not_displayed()
    {
        await using SimulatedMobileDriver driver = CreateDriver();

        (await driver.IsDisplayedAsync(AccountHeading)).Should().BeFalse();
    }

    [Test]
    public async Task Entering_text_mutates_the_element()
    {
        await using SimulatedMobileDriver driver = CreateDriver();

        await driver.EnterTextAsync(UsernameField, "someone");

        (await driver.GetTextAsync(UsernameField)).Should().Be("someone");
    }

    [Test]
    public async Task Entering_text_replaces_rather_than_appends()
    {
        // The behaviour that keeps a retried step from submitting the previous attempt's value
        // concatenated with this one - the classic mobile flake.
        await using SimulatedMobileDriver driver = CreateDriver();

        await driver.EnterTextAsync(UsernameField, "first");
        await driver.EnterTextAsync(UsernameField, "second");

        (await driver.GetTextAsync(UsernameField)).Should().Be("second");
    }

    [Test]
    public async Task Correct_credentials_navigate_to_the_account_screen()
    {
        await using SimulatedMobileDriver driver = CreateDriver();

        await SignInAsync(driver, Username, Password);

        driver.CurrentScreen.Should().Be("account");
        (await driver.IsDisplayedAsync(AccountHeading)).Should().BeTrue();
    }

    [Test]
    public async Task Incorrect_credentials_reveal_the_error_and_stay_put()
    {
        await using SimulatedMobileDriver driver = CreateDriver();

        await SignInAsync(driver, Username, "wrong");

        driver.CurrentScreen.Should().Be("login");
        (await driver.IsDisplayedAsync(ErrorMessage)).Should().BeTrue();
        (await driver.IsDisplayedAsync(AccountHeading)).Should().BeFalse();
    }

    [Test]
    public async Task The_fixture_resolves_the_iOS_half_of_a_paired_locator()
    {
        // Android and iOS name the account heading differently in the fixture, so this passing
        // proves the platform pair is honoured rather than merely declared.
        await using SimulatedMobileDriver driver = CreateDriver(Platform.IOS);

        await SignInAsync(driver, Username, Password);

        (await driver.IsDisplayedAsync(AccountHeading)).Should().BeTrue();
    }

    [Test]
    public async Task An_unresolved_token_is_a_hard_failure()
    {
        // The guard that stops a fixture typo from making the "wrong password is refused"
        // scenario pass against an empty expected password.
        SimulatedApp app = SimulatedApp.LoadFromFile(
            Path.Combine(
                AppContext.BaseDirectory,
                MobileSettingsLoader.Load(AppContext.BaseDirectory).SimulationFixture));

        await using SimulatedMobileDriver driver = new(app, Platform.Android);

        await driver.EnterTextAsync(UsernameField, Username);
        await driver.EnterTextAsync(PasswordField, Password);

        Func<Task> tap = () => driver.TapAsync(SubmitButton);

        (await tap.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*token*");
    }

    [Test]
    public async Task Placing_an_order_grows_the_order_history()
    {
        await using SimulatedMobileDriver driver = CreateDriver();

        await SignInAsync(driver, Username, Password);
        await driver.TapAsync(MobileLocator.Shared("new order", "nav-new-order-link"));
        await driver.EnterTextAsync(
            MobileLocator.Shared("quantity", "new-order-quantity-input"), "3");
        await driver.TapAsync(MobileLocator.Shared("place order", "new-order-submit-button"));

        string reference = await driver.GetTextAsync(
            MobileLocator.Shared("reference", "new-order-reference-text"));

        await driver.TapAsync(MobileLocator.Shared("order history", "nav-order-history-link"));

        IReadOnlyList<string> rows = await driver.GetTextsAsync(OrderHistoryRows);

        // Three rows, not one: the fixture is seeded with existing orders, so "the new order
        // appears" is a real search rather than a read of the only row present.
        rows.Should().HaveCount(3);
        rows.Should().Contain(row => row.Contains(reference, StringComparison.Ordinal));
    }

    [Test]
    public async Task The_screenshot_is_a_real_png()
    {
        await using SimulatedMobileDriver driver = CreateDriver();

        byte[] screenshot = await driver.TakeScreenshotAsync();

        // The PNG signature. Asserted because the evidence path writes these bytes to a .png
        // file, and an artefact an image viewer cannot open produces a support question about
        // the framework rather than about the failure.
        screenshot.Take(4).Should().Equal([0x89, (byte)'P', (byte)'N', (byte)'G']);
    }

    [Test]
    public async Task The_page_source_says_it_is_simulated()
    {
        await using SimulatedMobileDriver driver = CreateDriver();

        string source = await driver.GetPageSourceAsync();

        // Non-negotiable. A published artefact from a simulated run must not be mistakable for a
        // capture from a device.
        source.Should().Contain("No device was involved");
        source.Should().Contain("login-heading");
    }

    [Test]
    public async Task Using_a_session_after_it_ends_fails_loudly()
    {
        SimulatedMobileDriver driver = CreateDriver();

        await driver.DisposeSessionAsync();

        Func<Task> read = () => driver.GetTextAsync(LoginHeading);

        await read.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Test]
    public async Task The_simulated_driver_reports_its_target_honestly()
    {
        await using SimulatedMobileDriver driver = CreateDriver();

        // Read by the evidence writer, which annotates every artefact from a simulated run. A
        // driver that misreported its target would let a report imply a device was used.
        driver.Target.Should().Be(RunTarget.Simulated);
    }

    private static async Task SignInAsync(
        SimulatedMobileDriver driver, string username, string password)
    {
        await driver.EnterTextAsync(UsernameField, username);
        await driver.EnterTextAsync(PasswordField, password);
        await driver.TapAsync(SubmitButton);
    }
}
