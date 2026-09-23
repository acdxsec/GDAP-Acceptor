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
    var menu = await Run(Path.Combine(root, "menu-exit"), [], "0\n", MustNotAccept);
    Assert(menu.Code == 0 && menu.Output.Contains("1. Accept invitation") && menu.Output.Contains("2. Queue and recovery"), "Guided home menu is missing or exiting starts acceptance");
    Pass("guided home menu exits without setup or authentication");

    var reviewRoot = Path.Combine(root, "menu-review");
    var reviewState = new LocalState(reviewRoot);
    reviewState.Enroll(instanceId, trusted);
    var reviewInvitation = new Invitation(instanceId, relationship);
    reviewState.Enqueue(reviewInvitation);
    reviewState.TryClaim(reviewInvitation)!.Dispose();
    menu = await Run(reviewRoot, [], "2\nr\nNO\n0\n", MustNotAccept);
    Assert(menu.Output.Contains("Active or needs review") && menu.Output.Contains(relationship) && menu.Output.Contains("Recovery cancelled") && reviewState.Active() is not null, "Guided queue review did not display and retain cancelled recovery");
    menu = await Run(reviewRoot, [], "2\nr\nRESOLVED\n0\n", MustNotAccept);
    Assert(menu.Output.Contains("No invitation was replayed") && reviewState.Active() is null, "Guided recovery did not resolve explicitly without replay");
    Pass("guided queue displays interrupted work and resolves only after explicit review");

    var settingsRoot = Path.Combine(root, "menu-settings");
    menu = await Run(settingsRoot, [], $"3\na\n{trusted.BaseUrl}\n{partner}\nTRUST\n3\n\n0\n", MustNotAccept);
    Assert(new LocalState(settingsRoot).ReadInstances().Values.Contains(trusted) && menu.Output.Contains("Saved connections") && menu.Output.Contains(trusted.BaseUrl), "Guided settings did not enroll and display the trusted connection");
    Pass("guided settings enroll and display trusted connections without signing in");

    var diagnosticPath = Path.Combine(root, "guided-diagnostics.jsonl");
    menu = await Run(settingsRoot, [], $"4\n{diagnosticPath}\n0\n", MustNotAccept);
    Assert(File.Exists(diagnosticPath) && menu.Output.Contains("Exported sanitized state logs"), "Guided diagnostic export is missing");
    File.WriteAllText(diagnosticPath, "keep existing file");
    menu = await Run(settingsRoot, [], $"4\n{diagnosticPath}\n0\n", MustNotAccept);
    Assert(File.ReadAllText(diagnosticPath) == "keep existing file", "Guided export overwrote an existing file");
    Pass("guided diagnostics export without authentication and never overwrite a file");

    menu = await Run(settingsRoot, [], "1\n3\n0\n", MustNotAccept);
    Assert(!menu.Output.Contains("CIPP CONNECTION SETTINGS | Saved connections") && menu.Output.Contains("Paste the full Microsoft"), "Invitation input was interpreted as a menu command");
    menu = await Run(settingsRoot, [], "1\n\n4\n\ninvalid\n0\n", MustNotAccept);
    Assert(menu.Code == 0 && menu.Output.Contains("Select a listed action"), "Cancelled or invalid menu input did not return safely");
    menu = await Run(settingsRoot, [], "", MustNotAccept);
    Assert(menu.Code == 0, "End of input did not exit the menu");
    Pass("guided subprompts do not execute menu commands; blank input, invalid choices and EOF are safe");

    var menuCalls = 0;
    menu = await Run(settingsRoot, [], $"1\n{url}\n2\n0\n", (_, _) => { menuCalls++; return Task.FromResult(AcceptanceOutcome.Active); });
    Assert(menuCalls == 1 && menu.Output.Contains("CIPP onboarding has NOT been verified") && menu.Output.Contains("No active reservation needs review"), "Guided acceptance did not return to the workspace with an accurate outcome");
    menuCalls = 0;
    menu = await Run(settingsRoot, [], $"1\n{url}\n1\n{url}\n2\n\n0\n", (_, _) => { menuCalls++; return Task.FromResult(AcceptanceOutcome.NeedsReview); });
    Assert(menuCalls == 1 && menu.Output.Contains("already queued or active") && menu.Output.Contains("Active or needs review") && new LocalState(settingsRoot).Active() is not null, "Guided flow replayed an uncertain approval or hid its reservation");
    Pass("guided acceptance returns to the menu; uncertain approval is visible and cannot be replayed");

    var corruptRoot = Path.Combine(root, "menu-corrupt");
    var corruptState = new LocalState(corruptRoot);
    corruptState.Enroll(instanceId, trusted);
    var corruptFile = Path.Combine(corruptRoot, "active-v1.json");
    File.WriteAllText(corruptFile, "invalid state fixture");
    menu = await Run(corruptRoot, [], "2\n0\n", MustNotAccept);
    Assert(menu.Output.Contains("Could not read local queue state") && File.ReadAllText(corruptFile) == "invalid state fixture", "Menu reset or concealed corrupt queue state");
    Pass("guided queue reports corrupt state without resetting it or authenticating");

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
        return Task.FromResult(AcceptanceOutcome.Active);
    });
    Assert(result.Code == 0 && calls == 1 && result.Output.Contains("Saved locally"), "First use did not enroll and accept exactly once");
    Assert(new LocalState(first).Active() is null && new LocalState(first).Pending().Count == 0, "Successful child did not release its reservation");
    Pass("paste, first-use trust, durable claim, customer-discovery command and successful completion");

    result = await Run(first, [], url + "\n", (_, instance) =>
    {
        Assert(instance == trusted, "Saved partner was not reused");
        return Task.FromResult(AcceptanceOutcome.Stopped);
    });
    Assert(result.Code == 1 && !result.Output.Contains("One-time local setup") && new LocalState(first).Active() is null, "Stopped child was reported as success or enrollment was requested again");
    Pass("saved enrollment is reused; a stopped child is not success");

    // Real approval core -> wrapper subprocess -> launcher -> persisted review.
    // Only the external portal and browser entry point are synthetic.
    var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    var payload = Directory.CreateDirectory(Path.Combine(root, "outcome-payload")).FullName;
    File.Copy(Path.Combine(source, "scripts/Invoke-Acceptance.ps1"), Path.Combine(payload, "Invoke-Acceptance.ps1"));
    File.Copy(Path.Combine(source, "scripts/Approve-GdapRelationship.ps1"), Path.Combine(payload, "ApprovalCore.ps1"));
    File.Copy(Path.Combine(source, "tests/fixtures/ApprovalOutcome.ps1"), Path.Combine(payload, "Approve-GdapRelationship.ps1"));
    foreach (var scenario in new[] { "post-timeout", "readback-error", "readback-identity", "activation-timeout", "cleanup-error", "already-approved", "already-activating", "abnormal-exit", "cancelled", "preflight-error", "success", "already-active" })
    {
        var requiresReview = scenario is not ("cancelled" or "preflight-error" or "success" or "already-active");
        var active = scenario is "success" or "already-active";
        var expectedPosts = scenario is "post-timeout" or "readback-error" or "readback-identity" or "activation-timeout" or "cleanup-error" or "success" ? 1 : 0;
        var uncertain = Path.Combine(root, "outcome-" + scenario);
        new LocalState(uncertain).Enroll(instanceId, trusted);
        result = await Run(uncertain, [url], "", async (invitation, instance) =>
        {
            var start = Acceptor.AcceptanceCommand("pwsh", Path.Combine(payload, "Invoke-Acceptance.ps1"), invitation, instance);
            start.ArgumentList.Insert(0, "-NonInteractive");
            start.Environment["GDAP_TEST_RESULT"] = scenario;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            using var child = System.Diagnostics.Process.Start(start)!;
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { child.Kill(entireProcessTree: true); throw new Exception("Synthetic approval subprocess timed out"); }
            var output = await stdout + await stderr;
            Assert(output.Split("SYNTHETIC_POST").Length - 1 == expectedPosts, $"Unexpected POST count for {scenario}");
            Assert(!output.Contains("SYNTHETIC_SECRET_MUST_NOT_APPEAR"), "Raw portal error leaked");
            var expectedExit = scenario == "abnormal-exit" ? 17 : active ? 0 : requiresReview ? 3 : 2;
            Assert(child.ExitCode == expectedExit, $"Wrong wrapper outcome for {scenario}: {child.ExitCode}. {output}");
            return Acceptor.ClassifyAcceptanceExit(child.ExitCode);
        });
        Assert(result.Code == (active ? 0 : 1), $"Wrong launcher outcome for {scenario}");
        var queue = await Run(uncertain, ["queue", "status"], "", MustNotAccept);
        Assert(queue.Code == 0 && queue.Output.Contains(relationship) == requiresReview, $"Wrong durable reservation for {scenario}");
        if (requiresReview)
        {
            result = await Run(uncertain, [url], "", MustNotAccept);
            Assert(result.Code != 0 && result.Output.Contains("already queued or active"), "Uncertain approval was replayed before outcome review");
            result = await Run(uncertain, ["queue", "resolve"], "NO\n", MustNotAccept);
            Assert(result.Code != 0, "Cancelled outcome review succeeded");
            result = await Run(uncertain, [url], "", MustNotAccept);
            Assert(result.Code != 0 && result.Output.Contains("already queued or active"), "Cancelled review allowed replay");
            result = await Run(uncertain, ["queue", "resolve"], "RESOLVED\n", MustNotAccept);
            Assert(result.Code == 0 && result.Output.Contains("No invitation was replayed"), "Outcome review did not archive without replay");
            queue = await Run(uncertain, ["queue", "status"], "", MustNotAccept);
            Assert(!queue.Output.Contains(relationship), "Reviewed reservation remains active");
        }
        Pass($"real approval/wrapper/launcher outcome: {scenario}; correct state and no automatic retry");
    }

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
        return Task.FromResult(AcceptanceOutcome.Active);
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

static async Task<(int Code, string Output)> Run(string state, string[] arguments, string input, Func<Invitation, Instance, Task<AcceptanceOutcome>> accept)
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
Task<AcceptanceOutcome> MustNotAccept(Invitation invitation, Instance instance) { unexpectedAcceptances++; return Task.FromResult(AcceptanceOutcome.Stopped); }
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
void Pass(string message) { checks++; Console.WriteLine("PASS: " + message); }
