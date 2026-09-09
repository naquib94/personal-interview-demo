using QaFramework.Core.Configuration;
using QaFramework.Mobile.Drivers;
using QaFramework.Mobile.Elements;

namespace QaFramework.Mobile.Screens.Trading;

/// <summary>
/// The sign-in screen.
/// </summary>
/// <remarks>
/// Locators are declared as static readonly fields at the top of the screen they belong to,
/// rather than gathered into one repository-wide locator file. Both patterns are common; this one
/// was chosen because a locator and the method that uses it change together, and a central
/// locator file becomes a merge-conflict magnet on any team larger than one.
/// <para>
/// <see cref="MobileLocator.Shared"/> is used throughout, because the demo application's
/// <c>data-testid</c> values would be the same accessibility identifiers on both platforms. Where
/// that is not true in a real product - and there is always a handful - the platform pair is
/// spelled out, as on <see cref="AccountScreen"/>.
/// </para>
/// </remarks>
public sealed class LoginScreen(IMobileDriver driver, TimeoutSettings timeouts)
    : BaseScreen(driver, timeouts)
{
    private static readonly MobileLocator Heading =
        MobileLocator.Shared("the sign-in heading", "login-heading");

    private static readonly MobileLocator UsernameField =
        MobileLocator.Shared("the username field", "login-username-input");

    private static readonly MobileLocator PasswordField =
        MobileLocator.Shared("the password field", "login-password-input");

    private static readonly MobileLocator SubmitButton =
        MobileLocator.Shared("the sign-in button", "login-submit-button");

    private static readonly MobileLocator ErrorMessage =
        MobileLocator.Shared("the sign-in error message", "login-error-text");

    /// <inheritdoc />
    protected override MobileLocator Anchor => Heading;

    /// <inheritdoc />
    protected override string ScreenName => "the sign-in screen";

    /// <summary>
    /// Enters credentials and submits.
    /// </summary>
    /// <remarks>
    /// One method rather than three, because no scenario in this domain cares about the
    /// intermediate states, and a screen object that exposes every keystroke invites step
    /// definitions to narrate UI mechanics. The method deliberately does not assert on the
    /// outcome: the same call serves both the successful and the refused scenario.
    /// </remarks>
    public async Task SignInAsync(string username, string password)
    {
        await EnterTextAsync(UsernameField, username);
        await EnterTextAsync(PasswordField, password);
        await TapAsync(SubmitButton);
    }

    /// <summary>Waits briefly for the error message and returns whether it appeared.</summary>
    public Task<bool> ErrorAppearsAsync() => AppearsAsync(ErrorMessage, Timeouts.Submit);

    /// <summary>The text of the error message.</summary>
    public Task<string> ErrorMessageAsync() => TextAsync(ErrorMessage);
}
