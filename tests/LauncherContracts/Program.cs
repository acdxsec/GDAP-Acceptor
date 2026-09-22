// Exercise the real paste/configuration/queue flow with a non-authenticating
// acceptance adapter. All state is confined to a new temporary directory.
var root = Directory.CreateTempSubdirectory("gdap-launcher-contract-").FullName;
const string partner = "22222222-2222-2222-2222-222222222222";
const string instanceId = "11111111-1111-1111-1111-111111111111";
const string relationship = "33333333-3333-3333-3333-333333333333-" + partner;
const string url = "https://admin.microsoft.com/AdminPortal/Home#/partners/invitation/granularAdminRelationships/" + relationship;
var trusted = new Instance("https://cipp.example", partner);
var checks = 0;
var unexpectedAcceptances = 0;
try
{
    var first = Path.Combine(root, "first-use");
    var calls = 0;
    var result = await Run(first, [], $"{url}\n{trusted.BaseUrl}\n{partner}\nTRUST\n", (invitation, instance) =>
    {
        calls++;
        Assert(invitation.RelationshipId == relationship && instance == trusted, "Pasted invitation or independently enrolled partner changed");
        Assert(new LocalState(first).Active()?.Invitation == invitation, "Acceptance started without a durable reservation");
        var start = Acceptor.AcceptanceCommand("/path with spaces/pwsh", "/payload with spaces/scripts/Invoke-Acceptance.ps1", invitation, instance);
        Assert(!start.UseShellExecute && start.FileName == "/path with spaces/pwsh", "Shell interpolation enabled");
        Assert(start.ArgumentList.SequenceEqual(new[] { "-NoLogo", "-NoProfile", "-File", "/payload with spaces/scripts/Invoke-Acceptance.ps1", "-RelationshipId", relationship, "-ConfirmAuthenticatedTenant", "-ExpectedPartnerTenantId", partner }), "Launcher did not request confirmed customer discovery with the enrolled partner");
        return Task.FromResult(true);
    });
    Assert(result.Code == 0 && calls == 1 && result.Output.Contains("Saved locally"), "First use did not enroll and accept exactly once");
    Assert(new LocalState(first).Active() is null && new LocalState(first).Pending().Count == 0, "Successful child did not release its reservation");
    Pass("paste, first-use trust, durable claim, customer-discovery command and successful completion");

    result = await Run(first, [], url + "\n", (_, instance) =>
    {
        Assert(instance == trusted, "Saved partner was not reused");
        return Task.FromResult(false);
    });
    Assert(result.Code == 1 && !result.Output.Contains("One-time local setup") && new LocalState(first).Active() is null, "Stopped child was reported as success or enrollment was requested again");
    Pass("saved enrollment is reused; a stopped child is not success");

    var cancelled = Path.Combine(root, "cancelled");
    result = await Run(cancelled, [], $"{url}\n{trusted.BaseUrl}\n{partner}\nNO\n", MustNotAccept);
    Assert(result.Code != 0 && new LocalState(cancelled).ReadInstances().Count == 0, "Cancelled trust was saved");
    Pass("cancelled setup never authenticates or saves trust");

    result = await Run(Path.Combine(root, "invalid"), [], "https://evil.example/invitation\n", MustNotAccept);
    Assert(result.Code != 0, "Invalid URL accepted");
    Pass("invalid invitation never reaches acceptance");

    var multiple = Path.Combine(root, "multiple");
    var local = new LocalState(multiple);
    local.Enroll(instanceId, trusted);
    var second = new Instance("https://second.example", "44444444-4444-4444-4444-444444444444");
    local.Enroll("55555555-5555-5555-5555-555555555555", second);
    result = await Run(multiple, [url], "2\n", (_, instance) =>
    {
        Assert(instance == second, "Wrong instance selected");
        return Task.FromResult(true);
    });
    Assert(result.Code == 0, "Instance selection failed");
    result = await Run(multiple, [url], "0\n", MustNotAccept);
    Assert(result.Code != 0, "Invalid instance selection accepted");
    Pass("multiple enrollments require a valid explicit selection");

    var interrupted = Path.Combine(root, "interrupted");
    result = await Run(interrupted, [], $"{url}\n{trusted.BaseUrl}\n{partner}\nTRUST\n", (_, _) => throw new IOException("synthetic interrupted child"));
    Assert(result.Code != 0 && new LocalState(interrupted).Active() is not null, "Interrupted acceptance lost its reservation");
    result = await Run(interrupted, [url], "", MustNotAccept);
    Assert(result.Code != 0 && result.Output.Contains("already queued or active"), "Interrupted invitation was replayed");
    Pass("interrupted acceptance remains reserved and cannot be replayed");
    result = await Run(interrupted, ["queue", "resolve"], "NO\n", MustNotAccept);
    Assert(result.Code != 0 && new LocalState(interrupted).Active() is not null, "Cancelled recovery cleared state");
    result = await Run(interrupted, ["queue", "resolve"], "RESOLVED\n", MustNotAccept);
    Assert(result.Code == 0 && new LocalState(interrupted).Active() is null && Directory.GetFiles(interrupted, "reviewed-*.json").Length == 1, "Reviewed reservation was not archived");
    Assert(new LocalState(interrupted).Pending().Count == 0 && result.Output.Contains("No invitation was replayed"), "Recovery retained replayable work");
    Pass("explicit reviewed recovery archives state; cancellation preserves it; neither authenticates");

    var busyRoot = Path.Combine(root, "busy");
    var busy = new LocalState(busyRoot);
    busy.Enroll(instanceId, trusted);
    var busyInvitation = new Invitation(instanceId, relationship);
    busy.Enqueue(busyInvitation);
    using (var claim = busy.TryClaim(busyInvitation))
    {
        Assert(claim is not null, "Could not make busy fixture");
        result = await Run(busyRoot, ["queue", "resolve"], "RESOLVED\n", MustNotAccept);
        Assert(result.Code != 0 && busy.Active() == claim!.Active, "Recovery bypassed a held execution lock");
        busy.Complete(claim!);
    }
    Pass("review cannot clear a reservation held by a running acceptance");

    // Review is tied to the exact reservation, not merely the relationship ID.
    busy.Enqueue(busyInvitation);
    var oldClaim = busy.TryClaim(busyInvitation)!;
    var reviewed = oldClaim.Active;
    busy.Complete(oldClaim);
    busy.Enqueue(busyInvitation);
    var replacement = busy.TryClaim(busyInvitation)!;
    var replacementActive = replacement.Active;
    replacement.Dispose();
    var changedRefused = false;
    try { busy.ResolveReviewed(reviewed); } catch (InvalidOperationException) { changedRefused = true; }
    Assert(changedRefused && busy.Active() == replacementActive && Directory.GetFiles(busyRoot, "reviewed-*.json").Length == 0, "Stale review cleared a replacement reservation");
    Pass("stale review cannot clear a different reservation for the same invitation");
    Assert(unexpectedAcceptances == 0, "A rejected input unexpectedly reached acceptance");
    Console.WriteLine($"PASS: {checks} launcher workflow contracts; no browser, portal, CIPP or real local-state access.");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }
finally { Console.WriteLine($"Isolated fixtures: {root}"); }

static async Task<(int Code, string Output)> Run(string state, string[] arguments, string input, Func<Invitation, Instance, Task<bool>> accept)
{
    var originalInput = Console.In;
    var originalOutput = Console.Out;
    var originalError = Console.Error;
    using var reader = new StringReader(input);
    using var writer = new StringWriter();
    try
    {
        Console.SetIn(reader);
        Console.SetOut(writer);
        Console.SetError(writer);
        var code = await Acceptor.Run(arguments, state, accept);
        return (code, writer.ToString());
    }
    finally { Console.SetIn(originalInput); Console.SetOut(originalOutput); Console.SetError(originalError); }
}
Task<bool> MustNotAccept(Invitation invitation, Instance instance) { unexpectedAcceptances++; return Task.FromResult(false); }
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
void Pass(string message) { checks++; Console.WriteLine("PASS: " + message); }
