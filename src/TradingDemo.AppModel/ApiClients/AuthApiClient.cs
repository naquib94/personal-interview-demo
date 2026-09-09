using System.Net;
using QaFramework.Api.Assertions;
using QaFramework.Api.Requests;
using QaFramework.Api.Responses;
using QaFramework.Core.Configuration;
using TradingDemo.AppModel.Contracts;

namespace TradingDemo.AppModel.ApiClients;

/// <summary>
/// Authentication.
/// </summary>
/// <remarks>
/// <para>Two method shapes appear throughout the API clients, and the distinction is
/// deliberate:</para>
/// <list type="bullet">
/// <item><b><c>...Async</c></b> returns the raw <see cref="ApiResponse{T}"/> and asserts
/// nothing. This is what a test <i>about</i> authentication uses, because it needs to assert on
/// a 401 or inspect the error envelope.</item>
/// <item><b><c>...OrFailAsync</c></b> asserts success and returns the payload. This is what
/// every <i>other</i> test uses when it just needs to be signed in. A UI test about order
/// history should not contain code that could fail for an authentication reason without saying
/// so clearly.</item>
/// </list>
/// <para>Collapsing these into one method is the mistake: either negative tests fight the
/// framework, or setup failures surface as confusing null references twelve lines later.</para>
/// </remarks>
public sealed class AuthApiClient(ApiClient client)
{
    /// <summary>
    /// Attempts a login and returns the raw response.
    /// </summary>
    /// <remarks>
    /// Note that body logging is <b>not</b> enabled. This is the one endpoint where it would be
    /// actively harmful: the request carries a password, and CI logs are retained for months
    /// and readable by anyone with pipeline access.
    /// </remarks>
    public Task<ApiResponse<LoginResponse>> LoginAsync(string? username, string? password) =>
        client.SendAsync<LoginResponse>(
            ApiRequestBuilder.Post(Endpoints.Login)
                .WithJsonBody(new LoginRequest(username, password)));

    public Task<ApiResponse<LoginResponse>> LoginAsync(TestUser user) =>
        LoginAsync(user.Username, user.Password);

    /// <summary>
    /// Signs in, failing the test with a clear message if authentication does not succeed.
    /// </summary>
    /// <remarks>
    /// The custom message matters. Without it, a misconfigured password produces
    /// "expected 200 but got 401" in the setup of an unrelated test, and the reader's first
    /// assumption is a product defect in login. Naming configuration as the likely cause
    /// redirects them in one line.
    /// </remarks>
    public async Task<string> GetTokenOrFailAsync(TestUser user)
    {
        ApiResponse<LoginResponse> response = await LoginAsync(user);

        if (response.StatusCode != HttpStatusCode.OK)
            throw QaFramework.Core.Logging.TestLog.Failure(
                $"Could not sign in as '{user.Username}' during test setup: the API returned " +
                $"{response.StatusCodeValue} {response.StatusCode}. This is usually a " +
                "configuration problem rather than a product defect - check that the password " +
                "for this role is set for the current environment.",
                $"Response body:{Environment.NewLine}{response.RawBody}");

        return response.RequireData().Token;
    }
}
