using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

var trusted = new Instance("https://cipp.example", "22222222-2222-2222-2222-222222222222");
if (args.Length == 3 && args[0] is "enroll-worker" or "enqueue-worker" or "claim-worker")
{
    Console.WriteLine("READY");
    if (Console.ReadLine() != "GO") return 2;
    var state = new LocalState(args[1]);
    if (args[0] == "enroll-worker") state.Enroll(args[2], trusted);
    else if (args[0] == "enqueue-worker")
    {
        try { state.Enqueue(new Invitation("11111111-1111-1111-1111-111111111111", args[2])); }
        catch (ArgumentException) { return 3; }
    }
    else
    {
        using var claim = state.TryClaim(new Invitation("11111111-1111-1111-1111-111111111111", args[2]));
        if (claim is null) return 4;
        Console.WriteLine("CLAIMED");
        await Task.Delay(Timeout.InfiniteTimeSpan); // Parent forcibly stops this disposable worker.
    }
    return 0;
}
var root = Directory.CreateTempSubdirectory("gdap-state-contract-").FullName;
try
{
    var ids = Enumerable.Range(1, 8).Select(n => $"11111111-1111-1111-1111-{n:D12}").ToArray();
    var workers = ids.Select(id => StartWorker("enroll-worker", root, id)).ToArray();
    try
    {
        foreach (var worker in workers)
            Assert(await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) == "READY", "Worker did not reach the start barrier");
        foreach (var worker in workers) await worker.StandardInput.WriteLineAsync("GO");
        foreach (var worker in workers)
        {
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert(worker.ExitCode == 0, "Concurrent enrollment failed: " + await worker.StandardError.ReadToEndAsync());
        }
        var enrolled = new LocalState(root).ReadInstances();
        Assert(enrolled.Count == 8 && ids.All(id => enrolled.TryGetValue(id, out var value) && value == trusted), "Concurrent enrollment lost a trusted instance");
        Console.WriteLine("PASS: separate processes preserve every concurrent enrollment");
    }
    finally
    {
        foreach (var worker in workers) { if (!worker.HasExited) worker.Kill(entireProcessTree: true); worker.Dispose(); }
    }
    var clock = new TestClock();
    var queue = new LocalState(root, clock);
    for (var n = 0; n < 20; n++) queue.Enqueue(new Invitation(ids[0], "relationship-" + n));
    Assert(!queue.Enqueue(new Invitation(ids[0], "relationship-0")), "Duplicate was not recognized in a full queue");
    clock.Advance(TimeSpan.FromMinutes(11));
    var fresh = new Invitation(ids[0], "fresh-relationship");
    Assert(queue.Enqueue(fresh) && queue.Pending().SequenceEqual(new[] { fresh }), "Expired entries kept the queue full or were replayed");
    Console.WriteLine("PASS: a full queue recognizes duplicates and reclaims expired pending entries");
    using (var claim = queue.TryClaim(fresh))
    {
        Assert(claim is not null, "Pending invitation could not be claimed");
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert(!queue.Enqueue(fresh), "An active invitation expired and was duplicated");
        var next = new Invitation(ids[0], "next-relationship");
        queue.Enqueue(next);
        Assert(new LocalState(root, clock).TryClaim(next) is null, "Another acceptance started while one was active");
        queue.Complete(claim!);
        using var nextClaim = queue.TryClaim(next);
        Assert(nextClaim is not null, "Completed acceptance did not release the next invitation");
        queue.Complete(nextClaim!);
    }
    Console.WriteLine("PASS: active work never expires or overlaps another acceptance");
    var changed = new Invitation(ids[0], "changed-enrollment");
    queue.Enqueue(changed);
    queue.RemoveInstance(ids[0]);
    queue.Enroll(ids[0], trusted with { PartnerTenantId = "33333333-3333-3333-3333-333333333333" });
    try
    {
        using var changedClaim = queue.TryClaim(changed);
        Assert(changedClaim is null, "Queued work silently adopted a different enrolled partner");
    }
    catch (ArgumentException) { }
    Console.WriteLine("PASS: changing enrollment cannot retarget an already queued invitation");
    var parallelRoot = Path.Combine(root, "parallel-queue");
    var parallel = new LocalState(parallelRoot);
    var parallelId = "11111111-1111-1111-1111-111111111111";
    parallel.Enroll(parallelId, trusted);
    var producers = Enumerable.Range(0, 24).Select(n => StartWorker("enqueue-worker", parallelRoot, "parallel-" + n)).ToArray();
    try
    {
        foreach (var producer in producers) Assert(await producer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) == "READY", "Producer failed to initialize");
        foreach (var producer in producers) await producer.StandardInput.WriteLineAsync("GO");
        foreach (var producer in producers)
        {
            await producer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert(producer.ExitCode is 0 or 3, "Queue producer failed unexpectedly: " + await producer.StandardError.ReadToEndAsync());
        }
        Assert(producers.Count(p => p.ExitCode == 0) == 20 && parallel.Pending().Count == 20, "Concurrent producers exceeded or lost the queue capacity");
    }
    finally { foreach (var producer in producers) { if (!producer.HasExited) producer.Kill(entireProcessTree: true); producer.Dispose(); } }
    Console.WriteLine("PASS: concurrent processes enforce exactly 20 pending invitations");
    var interruptedRoot = Path.Combine(root, "interrupted");
    var interrupted = new LocalState(interruptedRoot);
    interrupted.Enroll(parallelId, trusted);
    var interruptedInvitation = new Invitation(parallelId, "interrupted-relationship");
    interrupted.Enqueue(interruptedInvitation);
    using (var worker = StartWorker("claim-worker", interruptedRoot, interruptedInvitation.RelationshipId))
    {
        try
        {
            Assert(await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) == "READY", "Claim worker failed to initialize");
            await worker.StandardInput.WriteLineAsync("GO");
            Assert(await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) == "CLAIMED", "Worker could not claim invitation");
        }
        finally { if (!worker.HasExited) worker.Kill(entireProcessTree: true); await worker.WaitForExitAsync(); }
    }
    var restarted = new LocalState(interruptedRoot);
    Assert(restarted.Active()?.Invitation == interruptedInvitation, "Operator cannot identify the interrupted acceptance");
    Assert(!restarted.Enqueue(interruptedInvitation) && restarted.TryClaim(interruptedInvitation) is null, "Process death replayed an uncertain acceptance");
    Console.WriteLine("PASS: killing the claiming process does not authorize replay on restart");
    var legacyRoot = Path.Combine(root, "legacy");
    Directory.CreateDirectory(Path.Combine(legacyRoot, "queue"));
    // Input fixture from the previous development payload. It cannot distinguish
    // queued from possibly in-flight work and must never be automatically replayed.
    File.WriteAllText(Path.Combine(legacyRoot, "instances.json"), JsonSerializer.Serialize(new Dictionary<string, Instance> { [parallelId] = trusted }));
    File.WriteAllText(Path.Combine(legacyRoot, "queue", "legacy.json"), JsonSerializer.Serialize(interruptedInvitation));
    var legacy = new LocalState(legacyRoot);
    Assert(legacy.ReadInstances()[parallelId] == trusted, "Existing enrollment was not preserved");
    var refusedLegacy = false;
    try { legacy.Enqueue(interruptedInvitation); } catch (InvalidOperationException) { refusedLegacy = true; }
    Assert(refusedLegacy, "Legacy uncertain work was silently bypassed");
    Console.WriteLine("PASS: existing enrollment survives while legacy pending work requires review");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
finally { Console.WriteLine($"Isolated test state retained for inspection: {root}"); }

static Process StartWorker(params string[] arguments)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    return Process.Start(start) ?? throw new Exception("Could not launch test worker");
}
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    internal void Advance(TimeSpan duration) => now += duration;
}
