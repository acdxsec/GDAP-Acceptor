using System.Text.Json;
using Gdap.Status;

internal static class InvitationReviewContracts
{
    internal static async Task Run(string root)
    {
        var directory = Path.Combine(root, "creation-review");
        const string id = "11111111-1111-1111-1111-111111111111";
        var instance = new Instance("https://cipp.example", "22222222-2222-2222-2222-222222222222");
        new LocalState(directory).Enroll(id, instance);
        var path = Path.Combine(directory, "invitation-create-" + id + ".json");
        var attempt = new LocalCreation(instance, "https://connector.example",
            new CreateInvitation("44444444-4444-4444-4444-444444444444", "CIPP Defaults", new string('a', 64), ""));
        var originalJson = JsonSerializer.Serialize(attempt);
        File.WriteAllText(path, originalJson);
        using var connection = new CippStatus(directory, new NoNetwork(), (_, _) => throw new Exception("Unexpected authentication"), new Legacy());
        var invitations = new CippInvitations(directory, connection);
        async Task<(int Code, string Output)> Run(string[] args, string input)
        {
            var original = (Console.In, Console.Out);
            using var reader = new StringReader(input); using var output = new StringWriter();
            try
            {
                Console.SetIn(reader); Console.SetOut(output);
                var code = await Acceptor.Run(args, directory, (_, _) => throw new Exception("Unexpected acceptance"), invitations: invitations);
                return (code, output.ToString());
            }
            finally { Console.SetIn(original.In); Console.SetOut(original.Out); }
        }
        var cancelled = await Run([], "2\nc\nNO\n0\n");
        Check(cancelled.Output.Contains("Saved invitation creation: " + attempt.Request.OperationId) && cancelled.Output.Contains("Creation recovery cancelled") && File.ReadAllText(path) == originalJson,
            "Empty approval queue hid creation state or cancelled review changed it");
        using (var held = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var locked = await Run(["create", "review"], "RESOLVED\n");
            Check(locked.Code != 0 && File.Exists(path), "Creation review bypassed active workflow lock");
        }
        var reviewed = await Run([], "2\nc\nRESOLVED\n0\n");
        var archives = Directory.GetFiles(directory, "*.reviewed");
        Check(reviewed.Output.Contains("No invitation was created, approved, revoked or retried") && !File.Exists(path) && archives.Length == 1 && File.ReadAllText(archives[0]) == originalJson,
            "Reviewed attempt was not preserved without replay");
        Check(new LocalState(directory).ReadInstances()[id] == instance, "Review altered enrollment");
        File.WriteAllText(path, "malformed");
        var malformed = await Run(["create", "review"], "RESOLVED\n");
        Check(malformed.Code != 0 && File.ReadAllText(path) == "malformed", "Malformed creation state was cleared");
        Console.WriteLine("PASS: queue displays creation attempts; review cancellation, active lock, malformed state and archival are safe and network-free");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class NoNetwork : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new Exception("Unexpected network request"); }
    private sealed class Legacy : ILegacyCredentialStore
    { public Task Delete(string key, CancellationToken token) => throw new Exception("Unexpected credential access"); }
}
