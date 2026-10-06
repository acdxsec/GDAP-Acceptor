using System.Net;
using System.Text;
using System.Text.Json;

internal static class DeploymentProfileContracts
{
    internal static async Task Run(string root)
    {
        const string partner = "22222222-2222-2222-2222-222222222222";
        const string app = "33333333-3333-3333-3333-333333333333";
        var instance = new Instance("https://cipp.example", partner);
        var connection = new CentralConnection(instance, "https://connector.example", partner, app, app);
        var json = JsonSerializer.Serialize(new DeploymentProfile(1, connection), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var profile = DeploymentProfile.Parse(json);
        Check(profile.Connection == connection, "Profile changed identifiers");
        foreach (var invalid in new[] {
            json.Replace("\"version\":1", "\"version\":2"),
            json.Replace("\"version\":1", "\"version\":1,\"secret\":\"forbidden\""),
            json.Replace("\"version\":1", "\"version\":1,\"version\":1"),
            json.Replace("https://connector.example", "http://connector.example"),
            json.Replace("https://cipp.example", "https://cipp.example/path"),
            json.Replace("\"apiId\":\"" + app, "\"apiId\":\"44444444-4444-4444-4444-444444444444") })
        {
            var rejected = false;
            try { DeploymentProfile.Parse(invalid); } catch { rejected = true; }
            Check(rejected, "Unsafe deployment profile accepted");
        }

        var path = Path.Combine(root, "packaged-setup");
        var peer = new Peer(partner);
        var signIns = 0;
        using var connector = new CippStatus(path, peer, (selected, _) =>
        { Check(selected == connection, "Sign-in redirected from packaged binding"); signIns++; return Task.FromResult("synthetic-token"); }, new Legacy());
        var invitations = new CippInvitations(path, connector);
        async Task<(int Code, string Output)> Launch(string[] arguments, string input, DeploymentProfile? supplied = null)
        {
            var original = (Console.In, Console.Out, Console.Error);
            using var reader = new StringReader(input); using var writer = new StringWriter();
            try
            {
                Console.SetIn(reader); Console.SetOut(writer); Console.SetError(writer);
                var code = await Acceptor.Run(arguments, path, (_, _) => throw new Exception("Unexpected acceptance"), connector, invitations, profile: supplied ?? profile);
                return (code, writer.ToString());
            }
            finally { Console.SetIn(original.In); Console.SetOut(original.Out); Console.SetError(original.Error); }
        }
        var cancelled = await Launch(["connector", "configure"], "NO\n");
        Check(cancelled.Code != 0 && signIns == 0 && new LocalState(path).ReadInstances().Count == 0, "Cancelled trust enrolled or signed in");
        cancelled = await Launch(["connector", "configure"], "TRUST\nNO\n");
        var id = new LocalState(path).ReadInstances().Single().Key;
        Check(cancelled.Code != 0 && signIns == 0 && connector.Load(id, instance) is null, "Cancelled connection saved or signed in");
        peer.WrongPartner = true;
        var mismatch = await Launch(["connector", "configure"], "CONNECT\n");
        Check(mismatch.Code != 0 && connector.Load(id, instance) is null, "Wrong remote partner saved");
        peer.WrongPartner = false;
        var result = await Launch([], "3\nc\nCONNECT\n0\n");
        Check(result.Code == 0 && connector.Load(id, instance) == connection && peer.Requests == 2, "Menu did not configure from packaged values");
        Check(!result.Output.Contains("STAFF sign-in tenant ID:") && !result.Output.Contains("address (from Azure deployment):"), "Packaged setup still requires identifiers");
        Check(!File.ReadAllText(Path.Combine(path, "central-status-" + id + ".json")).Contains("synthetic-token"), "Token persisted");
        // Existing settings take priority; a newer package must not silently redirect staff tokens.
        var replacement = new DeploymentProfile(1, connection with { Origin = "https://different.example" });
        result = await Launch(["connector", "configure"], "CONNECT\n", replacement);
        Check(result.Code == 0 && connector.Load(id, instance) == connection, "Package overwrote an existing connection");
        // A mismatched enrolled CIPP must not receive the packaged connector.
        var before = signIns;
        var originalInput = Console.In;
        using var blank = new StringReader("");
        try { Console.SetIn(blank); Check(await connector.Configure(app, instance with { BaseUrl = "https://other.example" }, true, profile) != 0, "Mismatched CIPP accepted"); }
        finally { Console.SetIn(originalInput); }
        Check(signIns == before, "Mismatched profile authenticated");
        Console.WriteLine("PASS: packaged setup, cancellation, remote binding, existing settings, mismatched CIPP and secret-free profile validation");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Legacy : ILegacyCredentialStore { public Task Delete(string key, CancellationToken token) => throw new Exception("Unexpected vault access"); }
    private sealed class Peer(string partner) : HttpMessageHandler
    {
        internal bool WrongPartner;
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Check(request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri == "https://connector.example/v1/invitations/connection", "Unexpected destination or write");
            Requests++;
            var json = JsonSerializer.Serialize(new { version = 1, cippOrigin = "https://cipp.example", partnerTenantId = WrongPartner ? "44444444-4444-4444-4444-444444444444" : partner });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
