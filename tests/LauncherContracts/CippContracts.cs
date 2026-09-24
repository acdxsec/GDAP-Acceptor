using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gdap.Status;

internal static class CippContracts
{
    private const string Partner = "22222222-2222-2222-2222-222222222222";
    private const string Setup = "\n22222222-2222-2222-2222-222222222222\n33333333-3333-3333-3333-333333333333\n44444444-4444-4444-4444-444444444444\nCONNECT\n";
    internal static async Task Run(string root)
    {
        var state = Path.Combine(root, "central-status");
        var instance = new Instance("https://cipp.example", Partner);
        var id = "11111111-1111-1111-1111-111111111111";
        var url = "https://admin.microsoft.com/AdminPortal/Home#/partners/invitation/granularAdminRelationships/relationship-1";
        new LocalState(state).Enroll(id, instance);
        var legacy = new Legacy();
        var peer = new Peer();
        var signIns = 0;
        Task<string> SignIn(CentralConnection connection, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); signIns++;
            Assert(connection.Origin == "https://cippapi.fizlian.dev" && connection.Instance == instance && connection.ClientId == "33333333-3333-3333-3333-333333333333", "Unvalidated sign-in settings");
            return Task.FromResult("synthetic-staff-token");
        }
        using var connector = new CippStatus(state, peer, SignIn, legacy);
        var result = await Launch(state, ["cipp", "status", url], "", connector);
        Assert(result.Code != 0 && result.Output.Contains("not configured") && signIns == 0 && peer.Calls.Count == 0, "Unconfigured status performed authentication");
        result = await Launch(state, ["cipp", "configure"], Setup, connector);
        Assert(result.Code == 0 && result.Output.Contains("Central connection saved") && peer.Calls.SequenceEqual(new[] { "GET https://cippapi.fizlian.dev/v1/connection" }), "Central setup failed or called CIPP directly");
        var configFile = Path.Combine(state, "central-status-" + id + ".json");
        var saved = File.ReadAllText(configFile);
        Assert(!saved.Contains("synthetic-staff-token") && !saved.Contains("ClientSecret") && legacy.Keys.Count == 0, "Setup persisted credentials or accessed old vault");
        Console.WriteLine("PASS: central setup uses staff authentication; only non-secret settings are persisted; no direct CIPP access or legacy credential use");
        foreach (var status in new[] { "waiting", "pending", "queued", "running", "succeeded", "failed", "cancelled" })
        {
            peer.Status = status;
            result = await Launch(state, ["cipp", "status", url], "", connector);
            Assert((result.Code == 0) == (status is "running" or "succeeded"), "Incorrect status exit for " + status);
            if (status == "queued") Assert(result.Output.Contains("queued; not running"), "Queued misrepresented");
        }
        foreach (var bad in new[] {
            new OnboardingStatus(1, instance.BaseUrl, Partner, "different", "running", DateTimeOffset.UtcNow),
            new OnboardingStatus(1, "https://other.example", Partner, "relationship-1", "running", DateTimeOffset.UtcNow),
            new OnboardingStatus(1, instance.BaseUrl, id, "relationship-1", "running", DateTimeOffset.UtcNow),
            new OnboardingStatus(1, instance.BaseUrl, Partner, "relationship-1", "unknown-secret", DateTimeOffset.UtcNow),
            new OnboardingStatus(1, instance.BaseUrl, Partner, "relationship-1", "running", DateTimeOffset.UtcNow.AddHours(-1)),
            new OnboardingStatus(1, instance.BaseUrl, Partner, "relationship-1", "running", DateTimeOffset.UtcNow.AddHours(1)) })
        {
            peer.Override = JsonSerializer.Serialize(bad);
            result = await Launch(state, ["cipp", "status", url], "", connector);
            Assert(result.Code != 0 && !result.Output.Contains("CIPP onboarding: running") && !result.Output.Contains("unknown-secret"), "Conflicting or stale evidence accepted");
        }
        peer.Override = "{\"version\":1,\"version\":1}";
        result = await Launch(state, ["cipp", "status", url], "", connector);
        Assert(result.Code != 0, "Duplicate response keys accepted");
        peer.Override = null;
        Console.WriteLine("PASS: exact relationship/partner/CIPP binding, status distinctions, duplicate and stale-evidence rejection");
        foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable, HttpStatusCode.BadGateway, HttpStatusCode.GatewayTimeout, HttpStatusCode.Redirect })
        {
            peer.Code = code;
            var before = peer.Calls.Count;
            result = await Launch(state, ["cipp", "status", url], "", connector);
            Assert(result.Code != 0 && !result.Output.Contains("PRIVATE_SECRET") && peer.Calls.Count == before + 1, "HTTP error leaked or retried");
            result = await Launch(state, ["cipp", "configure"], Setup, connector);
            Assert(result.Code != 0 && File.ReadAllText(configFile) == saved, "Failed setup overwrote existing settings");
        }
        peer.Code = HttpStatusCode.OK;
        peer.Status = "running";
        // Real elapsed delay crosses the old 40-second timeout. Do not use a
        // shortened test-only timeout that cannot catch that regression.
        peer.ResponseDelay = TimeSpan.FromSeconds(45);
        var beforeColdStart = peer.Calls.Count;
        result = await Launch(state, ["cipp", "status", url], "", connector);
        Assert(result.Code == 0 && result.Output.Contains("sleeping host") && peer.Calls.Count == beforeColdStart + 1, "Cold-start status timed out or retried");
        peer.ResponseDelay = TimeSpan.Zero;
        Console.WriteLine("PASS: delayed cold-start status succeeds past 40 seconds without retry or approval; gateway errors remain fail-closed");
        result = await Launch(state, [], $"5\n{url}\n\n0\n", connector);
        Assert(result.Output.Contains("CIPP onboarding: running"), "Guided status check failed");
        var calls = 0;
        result = await Launch(state, [url], "", connector, (_, _) => { calls++; return Task.FromResult(AcceptanceOutcome.Active); });
        Assert(result.Code == 0 && calls == 1 && result.Output.Contains("CIPP onboarding: running") && new LocalState(state).Active() is null, "Independent watch after successful approval failed");
        peer.Code = HttpStatusCode.ServiceUnavailable;
        result = await Launch(state, [url], "", connector, (_, _) => Task.FromResult(AcceptanceOutcome.Active));
        Assert(result.Code == 0 && new LocalState(state).Active() is null, "Central failure invalidated approval or retained reservation");
        var beforeStop = peer.Calls.Count;
        result = await Launch(state, [url], "", connector, (_, _) => Task.FromResult(AcceptanceOutcome.Stopped));
        Assert(peer.Calls.Count == beforeStop, "Stopped approval read status");
        Console.WriteLine("PASS: menu and post-approval watch remain read-only; central outages cannot invalidate or retry approval");
        var boundedPeer = new Peer { Status = "queued" };
        using var bounded = new CippStatus(state, boundedPeer, SignIn, legacy, (_, _) => Task.CompletedTask);
        result = await Launch(state, ["cipp", "watch", url], "", bounded);
        Assert(result.Output.Contains("watch limit reached") && boundedPeer.Calls.Count == 40, "Unbounded watch");
        using var cancelled = new CippStatus(state, new Peer(), (_, _) => throw new OperationCanceledException(), legacy);
        result = await Launch(state, [url], "", cancelled, (_, _) => Task.FromResult(AcceptanceOutcome.Active));
        Assert(result.Code == 0 && result.Output.Contains("stopped or timed out") && new LocalState(state).Active() is null, "Staff cancellation invalidated approval");
        var beforeBad = signIns;
        foreach (var origin in new[] { "http://cippapi.fizlian.dev", "https://user:secret@cippapi.fizlian.dev", "https://cippapi.fizlian.dev/path", "https://cippapi.fizlian.dev/?token=secret" })
        {
            result = await Launch(state, ["cipp", "configure"], origin + "\n", connector);
            Assert(result.Code != 0 && File.ReadAllText(configFile) == saved && signIns == beforeBad, "Unsafe destination authenticated");
        }
        var local = new LocalState(state);
        local.RemoveInstance(id); local.Enroll(id, new Instance("https://different.example", Partner));
        result = await Launch(state, ["cipp", "status", url], "", connector);
        Assert(result.Code != 0 && signIns == beforeBad, "Changed enrollment used old central binding");
        local.RemoveInstance(id); local.Enroll(id, instance);
        result = await Launch(state, ["cipp", "remove-legacy-credential"], "NO\n", connector);
        Assert(legacy.Keys.Count == 0, "Legacy removal without consent");
        result = await Launch(state, ["cipp", "remove-legacy-credential"], "REMOVE\n", connector);
        var expectedKey = "gdap-acceptor/cipp-status/v1/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(state)))) + "/" + id;
        Assert(result.Code == 0 && legacy.Keys.SequenceEqual(new[] { expectedKey }) && File.Exists(configFile), "Wrong legacy credential target");
        if (OperatingSystem.IsWindows())
        {
            using var native = new CippStatus(state, new Peer(), SignIn, new OsCredentialVault());
            result = await Launch(state, ["cipp", "remove-legacy-credential"], "REMOVE\n", native);
            Assert(result.Code == 0, "Native idempotent deletion of absent fixture credential failed");
        }
        result = await Launch(state, ["cipp", "disconnect"], "NO\n", connector);
        Assert(File.Exists(configFile), "Cancelled disconnect removed settings");
        result = await Launch(state, ["cipp", "disconnect"], "DISCONNECT\n", connector);
        Assert(result.Code == 0 && !File.Exists(configFile), "Disconnect did not remove local settings");
        Console.WriteLine("PASS: bounded watch, cancellation, destination and enrollment checks, consent-based exact legacy cleanup and disconnect");
    }
    private static async Task<(int Code, string Output)> Launch(string state, string[] args, string input, CippStatus connector, Func<Invitation, Instance, Task<AcceptanceOutcome>>? accept = null)
    {
        var previous = (Console.In, Console.Out, Console.Error);
        using var reader = new StringReader(input);
        using var output = new StringWriter();
        try
        {
            Console.SetIn(reader); Console.SetOut(output); Console.SetError(output);
            var code = await Acceptor.Run(args, state, accept ?? ((_, _) => throw new Exception("Status invoked approval")), connector);
            return (code, output.ToString());
        }
        finally { Console.SetIn(previous.In); Console.SetOut(previous.Out); Console.SetError(previous.Error); }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Legacy : ILegacyCredentialStore
    {
        internal List<string> Keys = [];
        public Task Delete(string key, CancellationToken token) { Keys.Add(key); return Task.CompletedTask; }
    }
    private sealed class Peer : HttpMessageHandler
    {
        internal List<string> Calls = [];
        internal string Status = "waiting";
        internal string? Override;
        internal HttpStatusCode Code = HttpStatusCode.OK;
        internal TimeSpan ResponseDelay;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert(request.Method == HttpMethod.Get && request.RequestUri?.Host == "cippapi.fizlian.dev" && request.Headers.Authorization?.ToString() == "Bearer synthetic-staff-token", "Unexpected destination, method or credential");
            Calls.Add(request.Method + " " + request.RequestUri);
            if (ResponseDelay > TimeSpan.Zero) await Task.Delay(ResponseDelay, token);
            var json = Code != HttpStatusCode.OK ? "PRIVATE_SECRET" : request.RequestUri!.AbsolutePath == "/v1/connection"
                ? JsonSerializer.Serialize(new ConnectionInfo(1, "https://cipp.example", Partner))
                : Override ?? JsonSerializer.Serialize(new OnboardingStatus(1, "https://cipp.example", Partner, "relationship-1", Status, DateTimeOffset.UtcNow));
            var response = new HttpResponseMessage(Code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            response.Headers.Location = new Uri("https://attacker.example");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            return response;
        }
    }
}
