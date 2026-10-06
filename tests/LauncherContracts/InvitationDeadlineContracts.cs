using System.Net;
using System.Text;
using System.Text.Json;
using Gdap.Status;

internal static class InvitationDeadlineContracts
{
    internal static async Task Run(string root)
    {
        foreach (var cancelSubmission in new[] { false, true })
        {
            var directory = Path.Combine(root, "review-deadline-" + cancelSubmission);
            Directory.CreateDirectory(directory);
            const string id = "11111111-1111-1111-1111-111111111111";
            const string partner = "22222222-2222-2222-2222-222222222222";
            const string app = "33333333-3333-3333-3333-333333333333";
            var instance = new Instance("https://cipp.example", partner);
            var connection = new CentralConnection(instance, "https://connector.example", partner, app, app);
            File.WriteAllText(Path.Combine(directory, "central-status-" + id + ".json"), JsonSerializer.Serialize(connection));
            var deadlines = new List<CancellationTokenSource>();
            CancellationTokenSource Deadline()
            {
                var next = new CancellationTokenSource();
                deadlines.Add(next);
                if (cancelSubmission && deadlines.Count == 2) next.Cancel();
                return next;
            }
            var peer = new Peer(partner);
            using var transport = new CippStatus(directory, peer, (_, token) =>
            { token.ThrowIfCancellationRequested(); return Task.FromResult("synthetic-token"); }, new Legacy());
            var original = Console.In;
            try
            {
                Console.SetIn(new ReviewInput(() =>
                {
                    // Simulate any deadline running during human review expiring.
                    // Disposed network-stage deadlines cannot affect later requests.
                    foreach (var timer in deadlines)
                        try { timer.Cancel(); } catch (ObjectDisposedException) { }
                }));
                var code = await new CippInvitations(directory, transport, Deadline).Run(id, instance, (_, _) => Task.FromResult(0));
                var saved = File.Exists(Path.Combine(directory, "invitation-create-" + id + ".json"));
                if (cancelSubmission)
                    Check(code == 1 && peer.Posts == 0 && !saved, "Already-cancelled submission saved an uncertain attempt or sent a POST");
                else
                    Check(code == 0 && peer.Posts == 1 && saved && deadlines.Count == 2, "Human review exhausted the submission deadline");
            }
            finally { Console.SetIn(original); foreach (var timer in deadlines) timer.Dispose(); }
        }
        Console.WriteLine("PASS: human review consumes no submission timeout; pre-cancelled submissions neither persist nor send");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class ReviewInput(Action elapsed) : TextReader
    {
        private int line;
        public override string ReadLine()
        { elapsed(); return ++line switch { 1 => "1", 2 => "", 3 => "CREATE", _ => throw new Exception("Unexpected input") }; }
    }
    private sealed class Legacy : ILegacyCredentialStore
    { public Task Delete(string key, CancellationToken token) => throw new Exception("Unexpected credential access"); }
    private sealed class Peer(string partner) : HttpMessageHandler
    {
        internal int Posts;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            object value;
            if (request.Method == HttpMethod.Get)
            {
                Check(request.RequestUri!.AbsolutePath == "/v1/invitations/templates", "Unexpected GET");
                value = new InviteTemplates(1, "https://cipp.example", partner,
                    [new("CIPP Defaults", new string('a', 64), [new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Helpdesk Administrator", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "Technicians")])]);
            }
            else
            {
                Check(request.RequestUri!.AbsolutePath == "/v1/invitations", "Unexpected POST");
                Posts++;
                var input = JsonSerializer.Deserialize<CreateInvitation>(await request.Content!.ReadAsStringAsync(cancellation), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                var relationship = "99999999-9999-9999-9999-999999999999-" + partner;
                value = new CreatedInvitation(1, input.OperationId, "https://cipp.example", partner, relationship,
                    InvitationProtocol.MicrosoftPrefix + relationship, InvitationProtocol.Onboarding("https://cipp.example", relationship));
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };
        }
    }
}
