using System.Net;
using System.Text;
using System.Text.Json;

internal static class SingleRegistrationContracts
{
    internal static async Task Run(string root)
    {
        const string appId = "33333333-3333-3333-3333-333333333333";
        const string tenant = "22222222-2222-2222-2222-222222222222";
        var instance = new Instance("https://cipp.example", tenant);
        var directory = Path.Combine(root, "single-registration");
        var signIns = 0;
        using var connector = new CippStatus(directory, new Peer(), (connection, _) =>
        {
            Check(connection.ClientId == appId && connection.ApiId == appId && connection.TenantId == tenant, "Setup must use the one supplied app ID for client and resource");
            signIns++;
            return Task.FromResult("synthetic-staff-token");
        }, new Legacy());
        var original = (Console.In, Console.Out, Console.Error);
        using var input = new StringReader($"https://connector.example\n{tenant}\n{appId}\nCONNECT\n");
        using var output = new StringWriter();
        int result;
        try
        {
            Console.SetIn(input); Console.SetOut(output); Console.SetError(output);
            result = await connector.Configure(appId, instance, invitations: true);
        }
        finally { Console.SetIn(original.In); Console.SetOut(original.Out); Console.SetError(original.Error); }
        Check(result == 0 && signIns == 1, "Single-registration setup still asks for a second application ID");
        var saved = connector.Load(appId, instance);
        Check(saved?.ClientId == appId && saved.ApiId == appId, "Saved settings split the registration");
        Check(StaffAuthentication.RequestScope(saved!, "Invitations.Create") == $"{appId}/Invitations.Create", "MSAL must request the GUID-based self-resource scope");
        var separate = saved! with { ApiId = "44444444-4444-4444-4444-444444444444" };
        Check(StaffAuthentication.RequestScope(separate, "Invitations.Create") == "api://44444444-4444-4444-4444-444444444444/Invitations.Create", "Existing split-registration scope changed");
        Check(!File.ReadAllText(Path.Combine(directory, "central-status-" + appId + ".json")).Contains("synthetic-staff-token"), "Setup saved staff token");
        Check(output.ToString().Contains($"Scope: {appId}/Invitations.Create"), "Single-registration setup must show GUID-resource scope");
        Console.WriteLine("PASS: invitation setup asks for one app ID, uses it for both identities and saves no credentials");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Legacy : ILegacyCredentialStore { public Task Delete(string key, CancellationToken cancellation) => throw new Exception("Unexpected vault access"); }
    private sealed class Peer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Check(request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri == "https://connector.example/v1/invitations/connection", "Unexpected setup request");
            Check(request.Headers.Authorization?.Parameter == "synthetic-staff-token", "Missing staff authentication");
            var body = JsonSerializer.Serialize(new { version = 1, cippOrigin = "https://cipp.example", partnerTenantId = "22222222-2222-2222-2222-222222222222" });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
