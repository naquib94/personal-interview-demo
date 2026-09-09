using QaFramework.Web.Components.Actions;
using QaFramework.Web.Components.DataEntry;
using QaFramework.Web.Components.Visual;
using QaFramework.Web.Setup;
using QaFramework.Web.Synchronisation;

namespace TradingDemo.AppModel.Pages;

/// <summary>
/// The sign-in page.
/// </summary>
/// <remarks>
/// Does not derive from <see cref="ApplicationPage"/>: there is no navigation bar before
/// signing in, and inheriting components that are not on the page invites tests that assert on
/// elements which cannot exist. Modelling a page as having controls it does not have is how a
/// suite acquires assertions that pass for the wrong reason.
/// </remarks>
public sealed class LoginPage(WebTestContext context)
{
    public const string Route = "index.html";

    public TextElement Heading { get; } = new(context, "login-heading", "the Sign in heading");
    public TextInput Username { get; } = new(context, "login-username-input", "the Username field");
    public TextInput Password { get; } = new(context, "login-password-input", "the Password field");
    public Button SubmitButton { get; } = new(context, "login-submit-button", "the Sign in button");
    public AlertMessage Alert { get; } = new(context);

    public async Task OpenAsync()
    {
        await context.NavigateToAsync(Route);
        await context.WaitForPageReadyAsync();
    }

    public Task ShouldBeDisplayedAsync() => Heading.ShouldHaveTextAsync("Sign in");

    /// <summary>
    /// Fills the form and submits it.
    /// </summary>
    /// <remarks>
    /// <para>The one composite action in this class, because "sign in" is a genuine business
    /// action rather than three unrelated UI operations. It deliberately does <b>not</b> assert
    /// success: both the happy path and the four failure paths (wrong password, unknown user,
    /// suspended account, empty fields) use this same method, and each asserts its own
    /// expected outcome.</para>
    ///
    /// <para>A <c>SignIn</c> that asserted it landed on the account page would be unusable for
    /// every negative test, which is how suites end up with a second, near-duplicate
    /// <c>SignInExpectingFailure</c> method.</para>
    /// </remarks>
    public async Task SignInAsync(string? username, string? password)
    {
        await Username.EnterAsync(username ?? string.Empty);
        await Password.EnterAsync(password ?? string.Empty);
        await SubmitButton.ClickAsync();
    }
}
