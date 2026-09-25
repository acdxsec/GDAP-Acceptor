using System.Net;
using System.Text;
using System.Text.Json;
using Gdap.Status;

internal static class InvitationContracts
{
    internal static async Task Run(string root)
    {
        var directory = Path.Combine(root, "create-flow");
        var id = "11111111-1111-1111-1111-111111111111";
        var partner = "22222222-2222-2222-2222-222222222222";
        var instance = new Instance("https://cipp.example", partner);
        var connection = new CentralConnection(instance, "https://connector.example", partner, "33333333-3333-3333-3333-333333333333", "44444444-4444-4444-4444-444444444444");
        new LocalState(directory).Enroll(id, instance);
        File.WriteAllText(Path.Combine(directory, "central-status-" + id + ".json"), JsonSerializer.Serialize(connection));
        var peer = new Peer(partner);
        var signIns = 0;
        using var transport = new CippStatus(directory, peer, (_, _) => { signIns++; return Task.FromResult("synthetic-staff-token"); }, new Legacy());
        var invitations = new CippInvitations(directory, transport);
        var accepts = 0;
        var opened = new List<string>();
        var outcome = AcceptanceOutcome.Active;
        var browserFails = false;
        async Task<(int Code, string Output)> Run(string[] args, string input)
        {
            var original = (Console.In, Console.Out, Console.Error);
            using var reader = new StringReader(input); using var writer = new StringWriter();
            try
            {
                Console.SetIn(reader); Console.SetOut(writer); Console.SetError(writer);
                var code = await Acceptor.Run(args, directory, (invitation, selected) =>
                { Check(selected == instance && invitation.RelationshipId == peer.Relationship, "Wrong generated invitation accepted"); accepts++; return Task.FromResult(outcome); }, transport, invitations,
                    url => { if (browserFails) throw new IOException("synthetic browser failure"); opened.Add(url); });
                return (code, writer.ToString());
            }
            finally { Console.SetIn(original.In); Console.SetOut(original.Out); Console.SetError(original.Error); }
        }
        var cancelled = await Run(["create"], "1\nTicket 10\nNO\n");
        Check(cancelled.Code != 0 && peer.Posts == 0 && accepts == 0, "Unconfirmed creation mutated or accepted");
        peer.LoseResponse = true;
        var uncertain = await Run(["create"], "1\nTicket 10\nCREATE\n");
        Check(uncertain.Code != 0 && peer.Posts == 1 && accepts == 0 && opened.Count == 0, "Unknown creation continued into approval");
        var state = Path.Combine(directory, "invitation-create-" + id + ".json");
        Check(File.Exists(state) && !File.ReadAllText(state).Contains("synthetic-staff-token"), "Creation attempt missing or token persisted");
        peer.LoseResponse = false;
        var recovered = await Run(["create"], "");
        Check(recovered.Code == 0 && peer.Posts == 1 && peer.Recoveries == 1 && accepts == 1 && opened.Single() == InvitationProtocol.Onboarding(instance.BaseUrl, peer.Relationship), "Recover-to-approval-to-CIPP handoff failed");
        Check(new LocalState(directory).Active() is null, "Active approval reservation retained");
        var before = signIns;
        var complete = await Run(["create"], "\n");
        Check(complete.Code == 0 && signIns == before && peer.Posts == 1 && accepts == 1, "Completed operation silently generated another invite");
        var pasted = await Run([InvitationProtocol.MicrosoftPrefix + peer.Relationship], "");
        Check(pasted.Code == 0 && accepts == 2 && signIns == before, "Paste workflow requires staff authentication or status polling");
        browserFails = true;
        var browserFailure = await Run([InvitationProtocol.MicrosoftPrefix + peer.Relationship], "");
        Check(browserFailure.Code == 0 && new LocalState(directory).Active() is null && browserFailure.Output.Contains("Could not open the default browser"), "Browser failure invalidated completed acceptance");
        browserFails = false;
        outcome = AcceptanceOutcome.NeedsReview;
        var review = await Run(["create"], "NEW\n1\nTicket 11\nCREATE\n");
        Check(review.Code != 0 && peer.Posts == 2 && new LocalState(directory).Active() is not null && opened.Count == 2, "Uncertain approval was cleared or opened onboarding");
        var repeated = await Run(["create"], "");
        Check(repeated.Code != 0 && peer.Posts == 2 && accepts == 4, "Resume repeated uncertain approval");
        Console.WriteLine("PASS: create confirmation, durable desktop recovery, generated-invite acceptance, exact browser handoff, no status watch, no replay after uncertain approval");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Legacy : ILegacyCredentialStore { public Task Delete(string key, CancellationToken cancellation) => throw new Exception("Unexpected vault access"); }
    private sealed class Peer(string partner) : HttpMessageHandler
    {
        internal readonly string Relationship = "99999999-9999-9999-9999-999999999999-" + partner;
        internal bool LoseResponse;
        internal int Posts, Recoveries;
        private CreateInvitation? last;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Check(request.RequestUri!.Host == "connector.example" && request.Headers.Authorization?.Parameter == "synthetic-staff-token", "Credentials sent to wrong destination");
            if (request.RequestUri.AbsolutePath == "/v1/invitations/templates")
                return Json(new InviteTemplates(1, "https://cipp.example", partner, [new("CIPP Defaults", new string('a', 64), [new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Helpdesk Administrator", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "Technicians")])]));
            if (request.Method == HttpMethod.Post)
            {
                Check(request.RequestUri.AbsolutePath == "/v1/invitations", "Unexpected write route");
                Posts++;
                last = JsonSerializer.Deserialize<CreateInvitation>(await request.Content!.ReadAsStringAsync(cancellation), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (LoseResponse) throw new HttpRequestException("SYNTHETIC_SECRET_DO_NOT_DISPLAY");
            }
            else { Check(request.RequestUri.AbsolutePath == "/v1/invitations/operations/" + last!.OperationId, "Unexpected read/status request"); Recoveries++; }
            return Json(new CreatedInvitation(1, last!.OperationId, "https://cipp.example", partner, Relationship, InvitationProtocol.MicrosoftPrefix + Relationship, InvitationProtocol.Onboarding("https://cipp.example", Relationship)));
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };
    }
}
