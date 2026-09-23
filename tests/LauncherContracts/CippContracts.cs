using System.Net;

internal static class CippContracts
{
    internal static async Task Run(string root)
    {
        var state = Path.Combine(root, "cipp-status");
        var instance = new Instance("https://cipp.example", "22222222-2222-2222-2222-222222222222");
        var id = "11111111-1111-1111-1111-111111111111";
        var url = "https://admin.microsoft.com/AdminPortal/Home#/partners/invitation/granularAdminRelationships/relationship-1";
        new LocalState(state).Enroll(id, instance);
        var vault = new FakeVault();
        var http = new Peer();
        using var connector = new CippStatus(state, vault, http, () => "synthetic-secret");
        var result = await Launch(state, ["cipp", "status", url], "", connector);
        Assert(result.Code != 0 && result.Output.Contains("not configured") && http.Calls.Count == 0, "Unconfigured status authenticated or reported success");
        result = await Launch(state, ["cipp", "configure"], "https://api.cipp.example\n22222222-2222-2222-2222-222222222222\n33333333-3333-3333-3333-333333333333\napi://33333333-3333-3333-3333-333333333333/.default\nCONNECT\n", connector);
        Assert(result.Code == 0 && result.Output.Contains("read-only connection saved") && vault.Value is not null, "CIPP connection was not saved after read-only verification");
        Assert(!result.Output.Contains("synthetic-secret") && !Directory.GetFiles(state).Any(file => File.ReadAllText(file).Contains("synthetic-secret")), "Credential leaked outside vault");
        Assert(http.Calls.SequenceEqual(new[] { "POST https://login.microsoftonline.com/22222222-2222-2222-2222-222222222222/oauth2/v2.0/token", "GET https://api.cipp.example/api/ListTenantOnboarding" }), "Setup contacted an unexpected endpoint");
        Console.WriteLine("PASS: CIPP setup verifies read-only access, saves credentials only in the vault, and never authenticates when unconfigured");
        foreach (var (status, expected) in new[] { ("queued", "queued; not running"), ("running", "running"), ("failed", "failed; inspect CIPP"), ("succeeded", "succeeded") })
        {
            http.Rows = "[{\"RowKey\":\"another-relationship\",\"Status\":\"running\"},{\"RowKey\":\"relationship-1\",\"Status\":\"" + status + "\",\"Relationship\":{\"id\":\"relationship-1\"}}]";
            result = await Launch(state, ["cipp", "status", url], "", connector);
            Assert(result.Output.Contains("CIPP onboarding: " + expected) && !result.Output.Contains("another-relationship"), "Wrong relationship or status displayed: " + status);
        }
        foreach (var bad in new[] { "{\"Results\":[]}", "[{\"RowKey\":\"relationship-1\",\"Status\":\"unexpected-secret\"}]", "[{\"RowKey\":\"relationship-1\",\"Status\":\"running\",\"Relationship\":{\"id\":\"different\"}}]", "[{\"RowKey\":\"relationship-1\",\"Status\":\"running\"},{\"RowKey\":\"relationship-1\",\"Status\":\"failed\"}]" })
        {
            http.Rows = bad;
            result = await Launch(state, ["cipp", "status", url], "", connector);
            Assert(result.Code != 0 && result.Output.Contains("unavailable") && !result.Output.Contains("unexpected-secret") && !result.Output.Contains("CIPP onboarding: running"), "Unsafe status evidence was accepted");
        }
        http.Rows = "[]";
        result = await Launch(state, ["cipp", "status", url], "", connector);
        Assert(result.Code != 0 && result.Output.Contains("Waiting for a matching"), "Missing record presented as onboarded");
        Console.WriteLine("PASS: exact relationship correlation; queued is not running; malformed, conflicting and duplicate evidence fails closed");
        var poll = 0;
        using var watcher = new CippStatus(state, vault, new Peer
        {
            NextRows = () => ++poll == 1 ? "[]" : poll == 2 ? "[{\"RowKey\":\"relationship-1\",\"Status\":\"queued\"}]" : "[{\"RowKey\":\"relationship-1\",\"Status\":\"running\"}]"
        }, () => "synthetic-secret", (_, _) => Task.CompletedTask);
        var approvalCalls = 0;
        result = await Launch(state, [url], "", watcher, (_, _) => { approvalCalls++; return Task.FromResult(AcceptanceOutcome.Active); });
        Assert(result.Code == 0 && approvalCalls == 1 && poll == 3 && result.Output.Contains("queued; not running") && result.Output.Contains("CIPP onboarding: running") && new LocalState(state).Active() is null, "Acceptance was not followed by independent CIPP status observation");
        using var broken = new CippStatus(state, vault, new Peer { Failure = true }, () => "synthetic-secret");
        result = await Launch(state, [url], "", broken, (_, _) => Task.FromResult(AcceptanceOutcome.Active));
        Assert(result.Code == 0 && result.Output.Contains("unavailable") && new LocalState(state).Active() is null, "CIPP failure changed successful GDAP acceptance or retained its lock");
        var beforeStop = poll;
        result = await Launch(state, [url], "", watcher, (_, _) => Task.FromResult(AcceptanceOutcome.Stopped));
        Assert(poll == beforeStop, "A stopped approval contacted CIPP");
        Console.WriteLine("PASS: verified active approval is followed by bounded read-only watch; CIPP failure never changes acceptance or retries approval");
        http.Rows = "[{\"RowKey\":\"relationship-1\",\"Status\":\"running\"}]";
        result = await Launch(state, [], $"5\n{url}\n\n0\n", connector);
        Assert(result.Code == 0 && result.Output.Contains("5. Check CIPP onboarding") && result.Output.Contains("CIPP onboarding: running"), "Guided status check is missing");
        result = await Launch(state, [], "3\nc\nhttps://api.cipp.example\n22222222-2222-2222-2222-222222222222\n33333333-3333-3333-3333-333333333333\napi://33333333-3333-3333-3333-333333333333/.default\nNO\n0\n", connector);
        Assert(result.Output.Contains("Connection setup cancelled"), "Guided CIPP configuration is missing");
        Console.WriteLine("PASS: guided settings and independent status checks use the same connector without approval");
        Assert(!vault.Value!.Contains("synthetic-token"), "Access token was persisted in the vault");
        var savedCredential = vault.Value;
        foreach (var (code, message) in new[] { (HttpStatusCode.Unauthorized, "Authentication rejected"), (HttpStatusCode.Forbidden, "Access denied"), (HttpStatusCode.TooManyRequests, "Rate limited"), (HttpStatusCode.Redirect, "Redirect refused") })
        {
            var errorPeer = new Peer { ApiStatus = code };
            using var errorConnector = new CippStatus(state, vault, errorPeer, () => "synthetic-secret");
            result = await Launch(state, ["cipp", "status", url], "", errorConnector);
            Assert(result.Code != 0 && result.Output.Contains(message) && !result.Output.Contains("SYNTHETIC_SECRET") && errorPeer.Calls.Count == 2, "HTTP error leaked data, retried or lacked actionable guidance");
            result = await Launch(state, ["cipp", "configure"], "https://api.cipp.example\n22222222-2222-2222-2222-222222222222\n33333333-3333-3333-3333-333333333333\napi://33333333-3333-3333-3333-333333333333/.default\nCONNECT\n", errorConnector);
            Assert(result.Code != 0 && vault.Value == savedCredential, "Failed configuration replaced a working connection");
        }
        Console.WriteLine("PASS: authentication, permission, rate-limit and redirect errors are safe and do not replace existing credentials");

        var boundedPeer = new Peer();
        using var bounded = new CippStatus(state, vault, boundedPeer, () => "synthetic-secret", (_, _) => Task.CompletedTask);
        result = await Launch(state, ["cipp", "watch", url], "", bounded);
        Assert(result.Code != 0 && result.Output.Contains("watch limit reached") && boundedPeer.Calls.Count(call => call.StartsWith("GET ")) == 40 && boundedPeer.Calls.Count(call => call.StartsWith("POST ")) == 1, "Watch was unbounded or did not reuse its in-memory token");
        using var cancelledWatch = new CippStatus(state, vault, new Peer(), () => "synthetic-secret", (_, _) => throw new OperationCanceledException());
        result = await Launch(state, [url], "", cancelledWatch, (_, _) => Task.FromResult(AcceptanceOutcome.Active));
        Assert(result.Code == 0 && result.Output.Contains("stopped or timed out") && new LocalState(state).Active() is null, "Cancelling status observation changed successful approval");
        Console.WriteLine("PASS: watch is bounded, tokens are memory-only, and cancelling observation leaves accepted GDAP intact");
        var beforeInvalid = http.Calls.Count;
        foreach (var invalidOrigin in new[] { "http://api.cipp.example", "https://user:secret@api.cipp.example", "https://api.cipp.example/path", "https://api.cipp.example/?secret=x" })
        {
            result = await Launch(state, ["cipp", "configure"], invalidOrigin + "\n", connector);
            Assert(result.Code != 0 && vault.Value == savedCredential && http.Calls.Count == beforeInvalid && !result.Output.Contains("secret@"), "Unsafe API destination accepted or leaked");
        }
        var local = new LocalState(state);
        local.RemoveInstance(id);
        local.Enroll(id, new Instance("https://different.example", instance.PartnerTenantId));
        result = await Launch(state, ["cipp", "status", url], "", connector);
        Assert(result.Code != 0 && http.Calls.Count == beforeInvalid, "Changed CIPP enrollment reused an old credential");
        local.RemoveInstance(id); local.Enroll(id, instance);
        vault.ThrowOnWrite = true;
        result = await Launch(state, ["cipp", "configure"], "https://api.cipp.example\n22222222-2222-2222-2222-222222222222\n33333333-3333-3333-3333-333333333333\napi://33333333-3333-3333-3333-333333333333/.default\nCONNECT\n", connector);
        Assert(result.Code != 0 && vault.Value == savedCredential && !Directory.GetFiles(state).Any(file => File.ReadAllText(file).Contains("synthetic-secret")), "Vault failure wrote an insecure fallback");
        vault.ThrowOnWrite = false;
        Console.WriteLine("PASS: unsafe destinations, enrollment changes and vault failures cannot leak or silently retarget credentials");
        result = await Launch(state, ["cipp", "disconnect"], "NO\n", connector);
        Assert(vault.Value is not null, "Cancelled disconnect deleted the credential");
        result = await Launch(state, ["cipp", "disconnect"], "DISCONNECT\n", connector);
        Assert(result.Code == 0 && vault.Value is null, "Explicit local disconnect did not remove the credential");
        if (OperatingSystem.IsWindows())
        {
            // Unique fixture state gives a unique vault target; never touches a real enrollment.
            using var native = new CippStatus(state, new OsCredentialVault(), new Peer(), () => "synthetic-secret");
            try
            {
                result = await Launch(state, ["cipp", "configure"], "https://api.cipp.example\n22222222-2222-2222-2222-222222222222\n33333333-3333-3333-3333-333333333333\napi://33333333-3333-3333-3333-333333333333/.default\nCONNECT\n", native);
                Assert(result.Code == 0, "Windows credential vault write failed");
                result = await Launch(state, ["cipp", "status", url], "", native);
                Assert(result.Output.Contains("Waiting for a matching"), "Windows credential vault read failed");
            }
            finally { await Launch(state, ["cipp", "disconnect"], "DISCONNECT\n", native); }
            result = await Launch(state, ["cipp", "status", url], "", native);
            Assert(result.Output.Contains("not configured"), "Windows credential vault delete failed");
            Console.WriteLine("PASS: real Windows vault round-trip via launcher commands with synthetic credentials and HTTP");
        }
        Console.WriteLine("PASS: disconnect requires explicit consent and removes only local API credentials");
    }

    internal static async Task<(int Code, string Output)> Launch(string state, string[] args, string input, CippStatus connector, Func<Invitation, Instance, Task<AcceptanceOutcome>>? accept = null)
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

    internal static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal sealed class FakeVault : ICredentialVault
    {
        internal string? Value;
        internal bool ThrowOnWrite;
        public Task<string?> Read(string key, CancellationToken token) => Task.FromResult(Value);
        public Task Write(string key, string value, CancellationToken token) { if (ThrowOnWrite) throw new IOException("SYNTHETIC_SECRET_MUST_NOT_APPEAR"); Value = value; return Task.CompletedTask; }
        public Task Delete(string key, CancellationToken token) { Value = null; return Task.CompletedTask; }
    }
    internal sealed class Peer : HttpMessageHandler
    {
        internal List<string> Calls = [];
        internal string Rows = "[]";
        internal Func<string>? NextRows;
        internal bool Failure;
        internal HttpStatusCode ApiStatus = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls.Add(request.Method + " " + request.RequestUri);
            if (Failure) throw new HttpRequestException("SYNTHETIC_SECRET_MUST_NOT_APPEAR");
            if (request.Method == HttpMethod.Post)
            {
                Assert((await request.Content!.ReadAsStringAsync(token)).Contains("client_secret=synthetic-secret"), "Secret not sent as form data");
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"synthetic-token\",\"token_type\":\"Bearer\",\"expires_in\":3600}", System.Text.Encoding.UTF8, "application/json") };
            }
            Assert(request.Headers.Authorization?.ToString() == "Bearer synthetic-token", "API authorization missing");
            if (ApiStatus != HttpStatusCode.OK)
            {
                var failure = new HttpResponseMessage(ApiStatus) { Content = new StringContent("SYNTHETIC_SECRET_MUST_NOT_APPEAR") };
                failure.Headers.Location = new Uri("https://untrusted.example/");
                failure.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
                return failure;
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(NextRows?.Invoke() ?? Rows, System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
