using System.Net;
using System.Text;
using System.Text.Json;
using Gdap.Server;
using Gdap.Status;

internal static class InvitationContracts
{
    internal static async Task Run(ServiceSettings settings, string root)
    {
        var directory = Path.Combine(root, "invitation-journal");
        Directory.CreateDirectory(directory);
        settings.InvitationJournalDirectory = directory;
        var peer = new InvitationPeer(settings.PartnerTenantId);
        var factory = new Factory(peer);
        CippInvitations NewServer() => new(settings, factory, new InvitationJournal(settings));
        var connector = NewServer();
        var template = (await connector.Templates(default)).Templates.Single();
        var staff = "77777777-7777-7777-7777-777777777777";
        CreateInvitation NewRequest() => new(Guid.NewGuid().ToString(), template.Id, template.Fingerprint, "Customer reference");
        var request = NewRequest();
        var first = await connector.Create(request, staff, default);
        Check(first?.OnboardingUrl == $"https://cipp.example/tenant/gdap-management/onboarding/start?id={peer.Relationship}", "Wrong relationship handoff");
        Check(peer.Creates == 1 && peer.Bodies.Single().GetProperty("Action").GetString() == "Create", "Incorrect create action");
        var restarted = NewServer();
        Check(await restarted.Create(request, staff, default) == first && peer.Creates == 1, "Restart duplicated creation");
        await Reject(() => restarted.Create(request with { Reference = "changed" }, staff, default));
        await Reject(() => restarted.Recover(request.OperationId, "88888888-8888-8888-8888-888888888888", default));
        await Reject(() => restarted.Create(NewRequest() with { Fingerprint = new string('a', 64) }, staff, default));
        Check(peer.Creates == 1, "Conflicting request or changed template mutated CIPP");
        Console.WriteLine("PASS: server-authoritative templates; restart-safe duplicate suppression; staff, request and template binding");

        request = NewRequest(); peer.LoseResponse = true;
        await Reject(() => connector.Create(request, staff, default));
        var before = peer.Creates;
        var recovered = await NewServer().Recover(request.OperationId, staff, default);
        Check(recovered?.OperationId == request.OperationId && peer.Creates == before, "Lost-response recovery retried creation");
        peer.LoseResponse = false;
        request = NewRequest(); peer.NoRecord = true;
        await Reject(() => connector.Create(request, staff, default));
        before = peer.Creates;
        Check(await NewServer().Recover(request.OperationId, staff, default) is null, "Missing evidence invented success");
        Check(await NewServer().Create(request, staff, default) is null && peer.Creates == before, "Uncertain write was replayed");
        peer.NoRecord = false;
        Console.WriteLine("PASS: lost responses recover by read only; absent evidence stays uncertain and cannot replay POST");

        request = NewRequest(); peer.LoseResponse = true; peer.DuplicateRecord = true;
        await Reject(() => connector.Create(request, staff, default));
        before = peer.Creates;
        await Reject(() => NewServer().Recover(request.OperationId, staff, default));
        Check(peer.Creates == before, "Conflicting evidence retried creation");
        peer.LoseResponse = false; peer.DuplicateRecord = false;
        Console.WriteLine("PASS: duplicate recovery evidence fails closed without creating a replacement");

        request = NewRequest();
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        { try { return await NewServer().Create(request, staff, default); } catch { return null; } }));
        Check(peer.Creates == before + 1 && concurrent.Any(r => r is not null), "Concurrent operations duplicated creation");
        request = NewRequest(); peer.WrongPartner = true;
        await Reject(() => connector.Create(request, staff, default));
        peer.WrongPartner = false;
        settings.InvitationJournalDirectory = null;
        before = peer.Creates;
        await Reject(() => NewServer().Create(NewRequest(), staff, default));
        Check(peer.Creates == before, "Creation without durable journal reached CIPP");
        Check(peer.Paths.All(p => p is "ExecGDAPInvite" or "ExecGDAPRoleTemplate" or "ListGDAPInvite"), "Unexpected CIPP endpoint");
        Console.WriteLine("PASS: concurrent reservation, wrong-partner rejection, missing-storage write gate; no onboarding submissions");
        Directory.Delete(directory, recursive: true); // exact synthetic fixture only
    }
    private static async Task Reject(Func<Task> action)
    { try { await action(); } catch { return; } throw new Exception("Expected failure was accepted"); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Factory(HttpMessageHandler peer) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(peer, disposeHandler: false); }
    private sealed class InvitationPeer(string partner) : HttpMessageHandler
    {
        internal readonly string Relationship = "99999999-9999-9999-9999-999999999999-" + partner;
        internal int Creates;
        internal bool LoseResponse, NoRecord, WrongPartner, DuplicateRecord;
        internal readonly List<JsonElement> Bodies = [];
        internal readonly List<string> Paths = [];
        private readonly List<object> rows = [];
        private static readonly object[] roles = [new { roleDefinitionId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", RoleName = "Helpdesk Administrator", GroupId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", GroupName = "Technicians" }];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com") return Json(new { access_token = "synthetic-token", token_type = "Bearer" });
            Check(request.RequestUri.Host == "api.cipp.example" && request.Headers.Authorization?.Parameter == "synthetic-token", "Wrong upstream origin/authentication");
            var endpoint = request.RequestUri.Segments.Last(); Paths.Add(endpoint);
            if (endpoint == "ExecGDAPRoleTemplate") return Json(new { Results = new[] { new { TemplateId = "CIPP Defaults", RoleMappings = roles } } });
            if (endpoint == "ListGDAPInvite") return Json(rows);
            Check(endpoint == "ExecGDAPInvite" && request.Method == HttpMethod.Post, "Unexpected mutation");
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation)).RootElement.Clone(); Bodies.Add(body);
            Interlocked.Increment(ref Creates);
            if (NoRecord) throw new HttpRequestException("synthetic timeout before receipt");
            var id = WrongPartner ? "99999999-9999-9999-9999-999999999999-88888888-8888-8888-8888-888888888888" : Relationship;
            var row = new { RowKey = id, Reference = body.GetProperty("Reference").GetString(), RoleMappings = JsonSerializer.Serialize(roles), InviteUrl = InvitationProtocol.MicrosoftPrefix + id, OnboardingUrl = "https://attacker.example/ignored" };
            rows.Add(row);
            if (DuplicateRecord) rows.Add(row);
            if (LoseResponse) throw new HttpRequestException("synthetic timeout after mutation");
            return Json(new { Invite = row });
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
