using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal interface ICredentialVault
{
    Task<string?> Read(string key, CancellationToken token);
    Task Write(string key, string value, CancellationToken token);
    Task Delete(string key, CancellationToken token);
}

// Independent of approval and its reservation. Only OAuth POST and one fixed
// CIPP GET are available here; there is no general-purpose request interface.
internal sealed class CippStatus : IDisposable
{
    private readonly ICredentialVault vault;
    private readonly HttpClient http;
    private readonly Func<string> readSecret;
    private readonly string keyPrefix;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    internal CippStatus(string stateDirectory) : this(stateDirectory, new OsCredentialVault(),
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false }, ReadHiddenSecret) { }

    internal CippStatus(string stateDirectory, ICredentialVault vault, HttpMessageHandler transport, Func<string> readSecret,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.vault = vault;
        this.readSecret = readSecret;
        this.delay = delay ?? Task.Delay;
        keyPrefix = "gdap-acceptor/cipp-status/v1/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(stateDirectory)))) + "/";
        http = new HttpClient(transport) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
    }

    internal Task<int> Configure(string id, Instance instance) => Guard(async () =>
    {
        Console.WriteLine("CIPP read-only status setup. Use a dedicated API client restricted to onboarding reads. This does not create or change that client.");
        var connection = new Connection
        {
            Instance = instance,
            ApiOrigin = ApiOrigin(Ask("API URL from CIPP Integrations (HTTPS origin or /api): ")),
            TenantId = GuidValue(Ask("CIPP authentication tenant ID: ")),
            ClientId = GuidValue(Ask("API application/client ID: ")),
            Scope = Ask("API scope copied from CIPP: ")
        };
        ValidateScope(connection.Scope);
        Console.WriteLine($"CIPP: {instance.BaseUrl}\nPartner: {instance.PartnerTenantId}\nRead endpoint: {connection.ApiOrigin}/api/ListTenantOnboarding\nAuthentication: https://login.microsoftonline.com/{connection.TenantId}/oauth2/v2.0/token\nClient: {connection.ClientId}\nScope: {connection.Scope}");
        Console.WriteLine("This API reads the instance's onboarding table, not just one customer. Only the matching relationship is displayed. No onboarding is started or retried.");
        if (Ask("Type CONNECT to test and save this connection (replaces any previous local connection): ") != "CONNECT")
        { Console.WriteLine("Connection setup cancelled. Existing credentials are unchanged."); return 1; }
        Console.Write("API client secret (hidden): ");
        connection.ClientSecret = readSecret();
        Validate(connection, instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var rows = await ReadRows(connection, timeout.Token);
        await vault.Write(Key(id), JsonSerializer.Serialize(connection), timeout.Token);
        Console.WriteLine("CIPP read-only connection saved in the operating system credential vault. No secret was saved in settings or diagnostics.");
        return 0;
    });

    internal Task<int> Check(Invitation invitation, Instance instance) => Guard(async () =>
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var connection = await Load(invitation.InstanceId, instance, timeout.Token);
        if (connection is null) { Console.WriteLine("CIPP status is not configured. Use CIPP connection settings; onboarding remains unverified."); return 1; }
        using var rows = await ReadRows(connection, timeout.Token);
        var status = Observe(rows.RootElement, invitation.RelationshipId, instance);
        Show(status, invitation, instance);
        return status is "running" or "succeeded" ? 0 : 1;
    });

    internal Task<int> Disconnect(string id) => Guard(async () =>
    {
        if (Ask("Type DISCONNECT to remove the local CIPP API credential (does not revoke the API client in CIPP): ") != "DISCONNECT")
        { Console.WriteLine("Disconnect cancelled. Credential unchanged."); return 1; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await vault.Delete(Key(id), deadline.Token);
        Console.WriteLine("Local CIPP API credential removed. CIPP and GDAP access are unchanged.");
        return 0;
    });

    internal Task<int> Watch(Invitation invitation, Instance instance) => Guard(async () =>
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var connection = await Load(invitation.InstanceId, instance, stop.Token);
            if (connection is null) { Console.WriteLine("CIPP status is not configured; onboarding remains unverified. Configure it in CIPP connection settings."); return 1; }
            Console.WriteLine("Watching CIPP for onboarding start (up to 20 minutes). Ctrl+C stops this watch only. No approval or onboarding request will be submitted.");
            for (var attempt = 0; attempt < 40; attempt++)
            {
                using var rows = await ReadRows(connection, stop.Token);
                var status = Observe(rows.RootElement, invitation.RelationshipId, instance);
                Console.WriteLine("Status checked at " + DateTimeOffset.Now.ToString("HH:mm:ss zzz"));
                Show(status, invitation, instance);
                if (status is not ("waiting" or "pending" or "queued")) return status is "running" or "succeeded" ? 0 : 1;
                if (attempt < 39) await delay(TimeSpan.FromSeconds(30), stop.Token);
            }
            Console.WriteLine("CIPP watch limit reached. A running onboarding job has not been confirmed. CIPP may continue independently; use Check CIPP onboarding later. Do not repeat approval.");
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    });

    private static string Observe(JsonElement rows, string relationshipId, Instance instance)
    {
        JsonElement? matched = null;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || row.GetProperty("RowKey").ValueKind != JsonValueKind.String) throw new InvalidDataException();
            if (row.GetProperty("RowKey").GetString() != relationshipId) continue;
            if (matched is not null || row.EnumerateObject().GroupBy(property => property.Name).Any(group => group.Count() > 1)) throw new InvalidDataException();
            matched = row;
        }
        if (matched is not JsonElement selected) return "waiting";
        if (selected.TryGetProperty("Relationship", out var relationship))
        {
            if (relationship.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            if (relationship.TryGetProperty("id", out var id) && id.GetString() != relationshipId) throw new InvalidDataException();
            if (relationship.TryGetProperty("partner", out var partner) && partner.TryGetProperty("tenantId", out var tenant) &&
                !string.Equals(tenant.GetString(), instance.PartnerTenantId, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        }
        var status = selected.GetProperty("Status").GetString();
        return status is "pending" or "queued" or "running" or "succeeded" or "failed" or "cancelled" ? status : throw new InvalidDataException();
    }

    private static void Show(string status, Invitation invitation, Instance instance)
    {
        Console.WriteLine($"Relationship: {invitation.RelationshipId}");
        Console.WriteLine(status switch
        {
            "waiting" => "Waiting for a matching CIPP onboarding record. Onboarding is not confirmed.",
            "queued" => "CIPP onboarding: queued; not running.",
            "pending" => "CIPP onboarding: pending; not running.",
            "running" => "CIPP onboarding: running (reported by CIPP).",
            "succeeded" => "CIPP onboarding: succeeded (reported by CIPP).",
            "failed" => "CIPP onboarding: failed; inspect CIPP logs. No retry was submitted.",
            _ => "CIPP onboarding: cancelled. No retry was submitted."
        });
        Console.WriteLine("CIPP details: " + Acceptor.OnboardingUrl(instance));
    }

    private async Task<Connection?> Load(string id, Instance instance, CancellationToken token)
    {
        var saved = await vault.Read(Key(id), token);
        if (saved is null) return null;
        var connection = JsonSerializer.Deserialize<Connection>(saved) ?? throw new InvalidDataException();
        Validate(connection, instance);
        return connection;
    }

    private async Task<JsonDocument> ReadRows(Connection connection, CancellationToken token)
    {
        if (connection.TokenExpires <= DateTimeOffset.UtcNow)
        {
            using var authentication = new HttpRequestMessage(HttpMethod.Post, $"https://login.microsoftonline.com/{connection.TenantId}/oauth2/v2.0/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = connection.ClientId, ["client_secret"] = connection.ClientSecret,
                    ["scope"] = connection.Scope, ["grant_type"] = "client_credentials"
                })
            };
            using var tokenJson = await Send(authentication, token);
            var accessToken = tokenJson.RootElement.GetProperty("access_token").GetString();
            var seconds = tokenJson.RootElement.GetProperty("expires_in").GetInt32();
            if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 32768 || !Regex.IsMatch(accessToken, "\\A[A-Za-z0-9._~+/-]+=*\\z") || seconds <= 60 || seconds > 86400 ||
                !string.Equals(tokenJson.RootElement.GetProperty("token_type").GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            connection.Token = accessToken;
            connection.TokenExpires = DateTimeOffset.UtcNow.AddSeconds(seconds - 60);
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, connection.ApiOrigin + "/api/ListTenantOnboarding");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
        var rows = await Send(request, token);
        if (rows.RootElement.ValueKind != JsonValueKind.Array) { rows.Dispose(); throw new InvalidDataException(); }
        return rows;
    }

    private async Task<JsonDocument> Send(HttpRequestMessage request, CancellationToken token)
    {
        using var response = await http.SendAsync(request, token);
        // Redirects and response bodies must never be used as UI error messages.
        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            var message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Authentication rejected. Check the CIPP API client, tenant, scope and secret; the secret may have expired.",
                HttpStatusCode.Forbidden => "Access denied. Check the CIPP API client's read role, enabled state and allowed IP ranges.",
                HttpStatusCode.TooManyRequests => "Rate limited. Stop polling and wait at least " + Math.Ceiling(Math.Max(1, (response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(60)).TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " seconds before another check.",
                HttpStatusCode.BadRequest when request.Method == HttpMethod.Post => "Authentication rejected. Verify the API authentication tenant, client ID, scope and secret.",
                _ when code >= 300 && code < 400 => "Redirect refused. Use the API URL shown in CIPP Integrations, not a sign-in or frontend redirect.",
                _ => "CIPP status unavailable (HTTP " + code + "). Check the API endpoint and CIPP health."
            };
            throw new StatusProblem(message);
        }
        var type = response.Content.Headers.ContentType?.MediaType;
        if (type != "application/json" && !(type?.EndsWith("+json", StringComparison.Ordinal) ?? false)) throw new InvalidDataException();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token), new JsonDocumentOptions { MaxDepth = 48 });
    }

    private string Key(string id) => keyPrefix + Guid.ParseExact(id, "D").ToString();
    private static string Ask(string prompt) { Console.Write(prompt); return (Console.ReadLine() ?? "").Trim(); }
    private static string ReadHiddenSecret()
    {
        if (Console.IsInputRedirected) throw new InvalidOperationException("Use an interactive terminal for secret entry.");
        var secret = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return secret.ToString(); }
            if (key.Key == ConsoleKey.Escape) { Console.WriteLine(); throw new OperationCanceledException(); }
            if (key.Key == ConsoleKey.Backspace) { if (secret.Length > 0) secret.Length--; }
            else if (!char.IsControl(key.KeyChar)) { if (secret.Length >= 1024) throw new InvalidDataException(); secret.Append(key.KeyChar); }
        }
    }
    private static string GuidValue(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id.ToString() : throw new InvalidDataException();
    private static string ApiOrigin(string value)
    {
        if (value.EndsWith("/api/", StringComparison.Ordinal)) value = value[..^5];
        else if (value.EndsWith("/api", StringComparison.Ordinal)) value = value[..^4];
        return Acceptor.ValidateBaseUrl(value);
    }
    private static void ValidateScope(string scope)
    {
        if (scope.Length > 2048 || scope.Any(char.IsWhiteSpace) || Regex.IsMatch(scope, "[\\p{Cc}\\p{Cf}]") ||
            !scope.EndsWith("/.default", StringComparison.Ordinal) || !Uri.TryCreate(scope, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "api") || uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) throw new InvalidDataException();
    }
    private static void Validate(Connection connection, Instance instance)
    {
        if (connection.Instance != instance || ApiOrigin(connection.ApiOrigin) != connection.ApiOrigin || GuidValue(connection.TenantId) != connection.TenantId ||
            GuidValue(connection.ClientId) != connection.ClientId || string.IsNullOrEmpty(connection.ClientSecret) || connection.ClientSecret.Length > 1024 ||
            connection.ClientSecret.Any(char.IsControl)) throw new InvalidDataException();
        ValidateScope(connection.Scope);
    }
    private static async Task<int> Guard(Func<Task<int>> action)
    {
        try { return await action(); }
        catch (StatusProblem error) { Console.WriteLine(error.Message + " No onboarding was started or retried; GDAP approval is unchanged."); return 1; }
        catch (OperationCanceledException) { Console.WriteLine("CIPP status check stopped or timed out. Its latest result is not a new approval result. GDAP and CIPP onboarding are unchanged; no retry was submitted."); return 1; }
        catch { Console.WriteLine("CIPP status unavailable. Check API settings, read permissions and the credential vault. No onboarding was started or retried; GDAP approval is unchanged."); return 1; }
    }
    public void Dispose() => http.Dispose();
    private sealed class StatusProblem(string message) : Exception(message);

    // Deliberately not a record: its generated ToString must never reveal secrets.
    private sealed class Connection
    {
        public Instance Instance { get; set; } = new("", "");
        public string ApiOrigin { get; set; } = "";
        public string TenantId { get; set; } = "";
        public string ClientId { get; set; } = "";
        public string Scope { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        internal string Token = "";
        internal DateTimeOffset TokenExpires;
    }
}
