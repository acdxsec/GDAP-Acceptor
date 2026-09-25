using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gdap.Status;

internal sealed record CentralConnection(Instance Instance, string Origin, string TenantId, string ClientId, string ApiId);
internal interface ILegacyCredentialStore { Task Delete(string key, CancellationToken token); }

// Workstations know only central connection identifiers, never the CIPP secret.
// Approval and its reservation remain independent of status observation.
internal sealed class CippStatus : IDisposable
{
    private readonly string directory;
    private readonly HttpClient http;
    private readonly Func<CentralConnection, CancellationToken, Task<string>> signIn;
    private readonly ILegacyCredentialStore legacy;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal CippStatus(string stateDirectory) : this(stateDirectory,
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false },
        new StaffAuthentication().Token, new OsCredentialVault()) { }
    internal CippStatus(string stateDirectory, HttpMessageHandler transport,
        Func<CentralConnection, CancellationToken, Task<string>> signIn, ILegacyCredentialStore legacy,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        directory = Path.GetFullPath(stateDirectory);
        // Scale-to-zero hosts can need time to pull/start the container. This
        // remains bounded by the enclosing setup/check/watch cancellation too.
        http = new HttpClient(transport) { Timeout = TimeSpan.FromMinutes(2), MaxResponseContentBufferSize = 262144 };
        this.signIn = signIn; this.legacy = legacy; this.delay = delay ?? Task.Delay;
    }
    internal Task<int> Configure(string id, Instance instance, bool invitations = false) => Guard(async () =>
    {
        Console.WriteLine("CIPP connector setup. No CIPP API secret is needed on this computer. Use staff app IDs supplied by your server administrator.");
        var origin = Ask(invitations ? "Invitation connector HTTPS address (from Azure deployment): " : "Central HTTPS address [https://cippapi.fizlian.dev]: ");
        var connection = new CentralConnection(instance, StatusProtocol.Origin(origin.Length == 0 && !invitations ? "https://cippapi.fizlian.dev" : origin),
            StatusProtocol.GuidValue(Ask("STAFF sign-in tenant ID: ")),
            StatusProtocol.GuidValue(Ask("Companion desktop application/client ID: ")),
            StatusProtocol.GuidValue(Ask("Connector application/client ID: ")));
        Validate(connection, instance);
        Console.WriteLine($"Central host: {connection.Origin}\nStaff tenant: {connection.TenantId}\nDesktop client: {connection.ClientId}\nScope: api://{connection.ApiId}/{(invitations ? InvitationProtocol.Scope : StatusProtocol.Scope)}\nExpected CIPP: {instance.BaseUrl}\nExpected partner: {instance.PartnerTenantId}");
        if (Ask("Type CONNECT to sign in and save these non-secret settings: ") != "CONNECT")
        { Console.WriteLine("Connection setup cancelled. Existing settings are unchanged."); return 1; }
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        Console.WriteLine("Connecting. A sleeping host may take up to two minutes to respond.");
        using var metadata = await Send(connection, invitations ? "/v1/invitations/connection" : "/v1/connection", timeout.Token);
        var info = metadata.RootElement.Deserialize<ConnectionInfo>(Json) ?? throw new InvalidDataException();
        if (info.Version != 1 || info.CippOrigin != instance.BaseUrl || info.PartnerTenantId != instance.PartnerTenantId) throw new InvalidDataException();
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "central-" + Guid.NewGuid().ToString("N") + ".tmp");
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(connection), timeout.Token); File.Move(temporary, FileFor(id), overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Console.WriteLine("Central connection saved. Staff access and CIPP/partner binding verified. No credential or token saved.");
        return 0;
    });
    internal Task<int> Disconnect(string id) => Guard(() =>
    {
        if (Ask("Type DISCONNECT to remove this computer's central connection settings: ") != "DISCONNECT")
        { Console.WriteLine("Disconnect cancelled. Settings unchanged."); return Task.FromResult(1); }
        File.Delete(FileFor(id));
        Console.WriteLine("Local central settings removed. Close the companion to discard in-memory tokens. Server credentials and CIPP are unchanged.");
        return Task.FromResult(0);
    });
    internal Task<int> RemoveLegacyCredential(string id) => Guard(async () =>
    {
        Console.WriteLine("Version 0.3.0 could store a CIPP API secret in this user's vault. It is never read or used by this version.");
        if (Ask("Type REMOVE to delete only that legacy local credential (does not revoke it in CIPP): ") != "REMOVE") return 1;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var key = "gdap-acceptor/cipp-status/v1/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory))) + "/" + StatusProtocol.GuidValue(id);
        await legacy.Delete(key, timeout.Token);
        Console.WriteLine("Legacy local API credential removed. Ask the CIPP administrator to revoke any previously distributed credential; deletion here does not revoke it.");
        return 0;
    });
    internal Task<int> Check(Invitation invitation, Instance instance) => Guard(async () =>
    {
        var connection = Load(invitation.InstanceId, instance);
        if (connection is null) { NotConfigured(); return 1; }
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        Console.WriteLine("Reading central status. A sleeping host may take up to two minutes to respond.");
        var status = await Read(connection, invitation.RelationshipId, timeout.Token);
        Show(status, instance);
        return status.Status is "running" or "succeeded" ? 0 : 1;
    });
    internal Task<int> Watch(Invitation invitation, Instance instance) => Guard(async () =>
    {
        var connection = Load(invitation.InstanceId, instance);
        if (connection is null) { NotConfigured(); return 1; }
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            Console.WriteLine("Watching central status for CIPP onboarding start (up to 20 minutes, including staff sign-in). Ctrl+C stops only this watch. No approval or job is submitted.");
            Console.WriteLine("The first status request may take up to two minutes while the host starts.");
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var status = await Read(connection, invitation.RelationshipId, stop.Token);
                Show(status, instance);
                if (status.Status is not ("waiting" or "pending" or "queued")) return status.Status is "running" or "succeeded" ? 0 : 1;
                if (attempt < 39) await delay(TimeSpan.FromSeconds(30), stop.Token);
            }
            Console.WriteLine("CIPP watch limit reached; running onboarding has not been confirmed. Check later; do not repeat approval.");
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    });
    private async Task<OnboardingStatus> Read(CentralConnection connection, string relationship, CancellationToken cancellation)
    {
        if (!StatusProtocol.IsRelationship(relationship)) throw new InvalidDataException();
        using var document = await Send(connection, "/v1/onboarding/" + Uri.EscapeDataString(relationship), cancellation);
        var status = document.RootElement.Deserialize<OnboardingStatus>(Json) ?? throw new InvalidDataException();
        if (status.Version != 1 || status.RelationshipId != relationship || status.CippOrigin != connection.Instance.BaseUrl ||
            status.PartnerTenantId != connection.Instance.PartnerTenantId || !StatusProtocol.IsStatus(status.Status) ||
            status.ObservedAt > DateTimeOffset.UtcNow.AddMinutes(2) || status.ObservedAt < DateTimeOffset.UtcNow.AddMinutes(-2)) throw new InvalidDataException();
        return status;
    }
    internal async Task<JsonDocument> Send(CentralConnection connection, string path, CancellationToken cancellation, object? body = null)
    {
        var token = await signIn(connection, cancellation);
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl)) throw new InvalidDataException();
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, connection.Origin + path);
        if (body is not null) request.Content = System.Net.Http.Json.JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellation);
        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            throw new StatusProblem(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Staff authentication rejected. Check central app IDs and restart the companion to sign in again.",
                HttpStatusCode.Forbidden => "Staff access denied. Ask the administrator to verify the required connector role and desktop app permission.",
                HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout => "Central host is starting or unavailable. Wait and check status again; do not repeat GDAP approval.",
                HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => "Central status unavailable or rate limited. Wait at least " +
                    Math.Ceiling(Math.Clamp((response.Headers.RetryAfter?.Delta ?? response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow ?? TimeSpan.FromSeconds(60)).TotalSeconds, 1, 3600)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " seconds before checking again. No retry was submitted.",
                _ when code >= 300 && code < 400 => "Redirect refused. Configure the central HTTPS address, not a portal sign-in URL.",
                _ => "Central status unavailable (HTTP " + code + "). Ask the server administrator to check its configuration."
            });
        }
        var type = response.Content.Headers.ContentType?.MediaType;
        if (type != "application/json" && !(type?.EndsWith("+json", StringComparison.Ordinal) ?? false)) throw new InvalidDataException();
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation), new JsonDocumentOptions { MaxDepth = 8 });
        try { StatusProtocol.UniqueObject(document.RootElement); InvitationProtocol.UniqueTree(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }
    internal CentralConnection? Load(string id, Instance instance)
    {
        var file = FileFor(id);
        if (!File.Exists(file)) return null;
        if (new FileInfo(file).Length > 8192) throw new InvalidDataException();
        var connection = JsonSerializer.Deserialize<CentralConnection>(File.ReadAllText(file)) ?? throw new InvalidDataException();
        Validate(connection, instance);
        return connection;
    }
    private static void Validate(CentralConnection connection, Instance instance)
    {
        if (connection.Instance != instance || StatusProtocol.Origin(connection.Origin) != connection.Origin ||
            StatusProtocol.GuidValue(connection.TenantId) != connection.TenantId || StatusProtocol.GuidValue(connection.ClientId) != connection.ClientId ||
            StatusProtocol.GuidValue(connection.ApiId) != connection.ApiId) throw new InvalidDataException();
    }
    private string FileFor(string id) => Path.Combine(directory, "central-status-" + StatusProtocol.GuidValue(id) + ".json");
    private static string Ask(string prompt) { Console.Write(prompt); return (Console.ReadLine() ?? "").Trim(); }
    private static void NotConfigured() => Console.WriteLine("Central CIPP status is not configured. Use CIPP connection settings. Onboarding remains unverified here; the existing webhook flow is unchanged.");
    private static void Show(OnboardingStatus status, Instance instance)
    {
        Console.WriteLine($"Relationship: {status.RelationshipId}\nCIPP observed at: {status.ObservedAt:O}");
        Console.WriteLine(status.Status switch
        {
            "waiting" => "Waiting for a matching CIPP onboarding record. Onboarding is not confirmed.",
            "queued" => "CIPP onboarding: queued; not running.", "pending" => "CIPP onboarding: pending; not running.",
            "running" => "CIPP onboarding: running (reported by CIPP).", "succeeded" => "CIPP onboarding: succeeded (reported by CIPP).",
            "failed" => "CIPP onboarding: failed; inspect CIPP logs. No retry was submitted.", _ => "CIPP onboarding: cancelled. No retry was submitted."
        });
        Console.WriteLine("CIPP details: " + Acceptor.OnboardingUrl(instance));
    }
    private static async Task<int> Guard(Func<Task<int>> action)
    {
        try { return await action(); }
        catch (StatusProblem error) { Console.WriteLine(error.Message + " GDAP approval is unchanged."); return 1; }
        catch (OperationCanceledException) { Console.WriteLine("Central status stopped or timed out. GDAP approval and CIPP onboarding are unchanged; no retry was submitted."); return 1; }
        catch { Console.WriteLine("Central status unavailable. Check connection settings, staff access and server health. GDAP approval is unchanged; no retry was submitted."); return 1; }
    }
    public void Dispose() => http.Dispose();
    private sealed class StatusProblem(string message) : Exception(message);
}
