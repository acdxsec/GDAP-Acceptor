using Microsoft.Identity.Client;

// MSAL owns authorization-code PKCE and the loopback callback. No persisted
// token cache, embedded browser or customer approval cookies are used here.
internal sealed class StaffAuthentication
{
    private CentralConnection? selected;
    private IPublicClientApplication? app;
    private IAccount? account;
    internal async Task<string> Token(CentralConnection connection, CancellationToken cancellation)
    {
        if (selected != connection)
        {
            app = PublicClientApplicationBuilder.Create(connection.ClientId)
                .WithAuthority(AzureCloudInstance.AzurePublic, connection.TenantId)
                .WithRedirectUri("http://localhost").Build();
            selected = connection; account = null;
        }
        var scopes = new[] { $"api://{connection.ApiId}/Status.Read" };
        AuthenticationResult result;
        try
        {
            if (account is null) throw new MsalUiRequiredException("staff_signin", "Staff sign-in required.");
            result = await app!.AcquireTokenSilent(scopes, account).ExecuteAsync(cancellation);
        }
        catch (MsalUiRequiredException)
        {
            Console.WriteLine("Sign in as an authorized STAFF member in your default browser. This is separate from CUSTOMER GDAP sign-in.");
            result = await app!.AcquireTokenInteractive(scopes).WithUseEmbeddedWebView(false)
                .WithPrompt(Prompt.SelectAccount).ExecuteAsync(cancellation);
        }
        if (!string.Equals(result.TenantId, connection.TenantId, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        account = result.Account;
        return result.AccessToken;
    }
}
