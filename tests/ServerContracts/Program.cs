using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gdap.Server;
using Gdap.Status;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Tokens;

var root = Path.Combine(Path.GetTempPath(), "gdap-server-contracts-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var secretFile = Path.Combine(root, "synthetic-secret");
await File.WriteAllTextAsync(secretFile, "synthetic-cipp-secret");
var settings = new ServiceSettings
{
    IdentityTenantId = "11111111-1111-1111-1111-111111111111", Audience = "22222222-2222-2222-2222-222222222222",
    DesktopClientId = "33333333-3333-3333-3333-333333333333", PartnerTenantId = "44444444-4444-4444-4444-444444444444",
    CippOrigin = "https://cipp.example", CippApiOrigin = "https://api.cipp.example",
    CippAuthenticationTenantId = "55555555-5555-5555-5555-555555555555", CippClientId = "66666666-6666-6666-6666-666666666666",
    CippScope = "api://66666666-6666-6666-6666-666666666666/.default", SecretFile = secretFile
};
using var rsa = RSA.Create(2048);
var key = new RsaSecurityKey(rsa) { KeyId = "synthetic-test-key" };
var peer = new Peer();
var clock = new Clock();
var builder = WebApplication.CreateBuilder();
builder.WebHost.UseTestServer();
await using var app = CentralHost.Build(builder, settings, services =>
{
    services.AddSingleton<TimeProvider>(clock);
    services.AddHttpClient("cipp").ConfigurePrimaryHttpMessageHandler(() => peer);
    services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        var metadata = new OpenIdConnectConfiguration { Issuer = settings.Authority };
        metadata.SigningKeys.Add(key);
        options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
    });
});
try
{
    await app.StartAsync();
    JournalProbeContracts.Run(root);
    await InvitationContracts.Run(settings, root);
    using var client = app.GetTestClient();
    async Task<HttpResponseMessage> Get(string path, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-MS-CLIENT-PRINCIPAL", "spoofed-admin");
        request.Headers.Add("X-Forwarded-For", "127.0.0.1");
        return await client.SendAsync(request);
    }
    string Token(string? omit = null, string? audience = null, string? issuer = null, DateTime? expiry = null, bool wrongKey = false, bool unsigned = false, string? replace = null, bool invitations = false)
    {
        var claims = new[] { new Claim("tid", settings.IdentityTenantId), new Claim("ver", "2.0"), new Claim("azp", settings.DesktopClientId),
            new Claim("scp", invitations ? InvitationProtocol.Scope : "Status.Read"), new Claim("roles", invitations ? InvitationProtocol.Role : "Onboarding.Read"), new Claim("oid", "77777777-7777-7777-7777-777777777777") }.Where(c => c.Type != omit).Select(c => c.Type == replace ? new Claim(c.Type, "88888888-8888-8888-8888-888888888888") : c);
        using var other = RSA.Create(2048);
        var signing = unsigned ? null : new SigningCredentials(wrongKey ? new RsaSecurityKey(other) { KeyId = key.KeyId } : key, SecurityAlgorithms.RsaSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer ?? settings.Authority, audience ?? settings.Audience, claims,
            DateTime.UtcNow.AddHours(-2), expiry ?? DateTime.UtcNow.AddMinutes(5), signing));
    }
    var health = await Get("/healthz");
    Assert(health.StatusCode == HttpStatusCode.OK && peer.Calls.Count == 0, "Liveness called CIPP");
    foreach (var token in new string?[] { null, "not-a-token", Token(wrongKey: true), Token(unsigned: true), Token(audience: settings.DesktopClientId), Token(issuer: "https://untrusted.example"), Token(expiry: DateTime.UtcNow.AddMinutes(-5)) })
    {
        using var denied = await Get("/v1/onboarding/relationship-1", token);
        Assert(denied.StatusCode == HttpStatusCode.Unauthorized && peer.Calls.Count == 0, "Invalid token reached CIPP");
    }
    foreach (var claim in new[] { "tid", "ver", "azp", "scp", "roles", "oid" })
    {
        using var denied = await Get("/v1/onboarding/relationship-1", Token(omit: claim));
        Assert(denied.StatusCode == HttpStatusCode.Forbidden && peer.Calls.Count == 0, "Missing required staff claim was accepted: " + claim);
    }
    foreach (var claim in new[] { "tid", "ver", "azp", "scp", "roles" })
    {
        using var denied = await Get("/v1/onboarding/relationship-1", Token(replace: claim));
        Assert(denied.StatusCode == HttpStatusCode.Forbidden && peer.Calls.Count == 0, "Wrong staff claim accepted: " + claim);
    }
    Console.WriteLine("PASS: real JWT middleware denies invalid signatures, issuer, audience, expiry, missing staff claims and spoofed identity headers before upstream access");
    var valid = Token();
    foreach (var claim in new[] { "tid", "ver", "azp", "scp", "roles", "oid" })
    {
        using var denied = await Get("/v1/invitations/connection", Token(omit: claim, invitations: true));
        Assert(denied.StatusCode == HttpStatusCode.Forbidden && peer.Calls.Count == 0, "Invitation identity claim missing: " + claim);
    }
    using var creatorConnection = await Get("/v1/invitations/connection", Token(invitations: true));
    Assert(creatorConnection.IsSuccessStatusCode && peer.Calls.Count == 0, "Creator metadata requires old status role");
    using var gated = new HttpRequestMessage(HttpMethod.Post, "/v1/invitations") { Content = JsonContent.Create(new { }) };
    gated.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(invitations: true));
    using var gatedResponse = await client.SendAsync(gated);
    Assert(gatedResponse.StatusCode == HttpStatusCode.ServiceUnavailable && peer.Calls.Count == 0, "Storage gate missing");
    using var gatedTemplates = await Get("/v1/invitations/templates", Token(invitations: true));
    Assert(gatedTemplates.StatusCode == HttpStatusCode.ServiceUnavailable && peer.Calls.Count == 0, "Incomplete deployment offered creation templates");
    settings.InvitationJournalDirectory = root;
    foreach (var body in new[] { "{\"operationId\":\"a\",\"operationId\":\"b\"}", "{\"url\":\"https://attacker.example\"}", new string('x', 5000) })
    {
        using var malformed = new HttpRequestMessage(HttpMethod.Post, "/v1/invitations") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        malformed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(invitations: true));
        using var response = await client.SendAsync(malformed);
        Assert(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge && peer.Calls.Count == 0, "Malformed/oversized creation reached CIPP");
    }
    settings.InvitationJournalDirectory = null;
    Console.WriteLine("PASS: creator-specific HTTP authorization, bounded strict input and storage gate; status readers cannot create");
    using var forbiddenCreate = new HttpRequestMessage(HttpMethod.Post, "/v1/invitations") { Content = JsonContent.Create(new { operationId = Guid.NewGuid().ToString() }) };
    forbiddenCreate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", valid);
    using var deniedCreate = await client.SendAsync(forbiddenCreate);
    Assert(deniedCreate.StatusCode == HttpStatusCode.Forbidden && peer.Calls.Count == 0, "Status reader gained invitation creation permission");
    using var connection = await Get("/v1/connection", valid);
    var metadata = await connection.Content.ReadFromJsonAsync<ConnectionInfo>();
    Assert(connection.IsSuccessStatusCode && metadata == new ConnectionInfo(1, settings.CippOrigin, settings.PartnerTenantId) && peer.Calls.Count == 0, "Wrong authenticated metadata");
    using var invalid = await Get("/v1/onboarding/relationship-1?url=https://attacker.example", valid);
    Assert(invalid.StatusCode == HttpStatusCode.BadRequest && peer.Calls.Count == 0, "Caller-supplied destination accepted");
    using var write = new HttpRequestMessage(HttpMethod.Post, "/v1/onboarding/relationship-1");
    write.Headers.Authorization = new AuthenticationHeaderValue("Bearer", valid);
    using var rejected = await client.SendAsync(write);
    Assert(rejected.StatusCode == HttpStatusCode.MethodNotAllowed && peer.Calls.Count == 0, "Mutation endpoint exists");
    using var status = await Get("/v1/onboarding/relationship-1", valid);
    var content = await status.Content.ReadAsStringAsync();
    var result = JsonSerializer.Deserialize<OnboardingStatus>(content, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    Assert(status.IsSuccessStatusCode && result?.Status == "queued" && result.RelationshipId == "relationship-1" && result.PartnerTenantId == settings.PartnerTenantId && !content.Contains("PRIVATE_LOG") && !content.Contains("relationship-2"), "Raw table leaked or queued changed to running");
    Assert(peer.Calls.SequenceEqual(new[] { "POST https://login.microsoftonline.com/55555555-5555-5555-5555-555555555555/oauth2/v2.0/token", "GET https://api.cipp.example/api/ListTenantOnboarding" }), "Unexpected upstream operation");
    var reads = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Get("/v1/onboarding/relationship-2", valid)));
    foreach (var read in reads) { Assert(read.IsSuccessStatusCode, "Concurrent status failed"); read.Dispose(); }
    Assert(peer.Calls.Count == 2, "Read cache did not coalesce callers");
    using var absent = await Get("/v1/onboarding/missing", valid);
    Assert((await absent.Content.ReadFromJsonAsync<OnboardingStatus>())?.Status == "waiting", "Missing record invented success");
    Console.WriteLine("PASS: only exact status projection exposed; read-only upstream; concurrent and missing-relationship reads use one cached table");
    clock.Advance(31);
    peer.Response = HttpStatusCode.TooManyRequests;
    using var limited = await Get("/v1/onboarding/relationship-1", valid);
    var limitedText = await limited.Content.ReadAsStringAsync();
    Assert(limited.StatusCode == HttpStatusCode.ServiceUnavailable && limited.Headers.RetryAfter?.Delta == TimeSpan.FromSeconds(90) && !limitedText.Contains("PRIVATE_LOG") && !limitedText.Contains("queued"), "Rate-limit returned stale data or upstream text");
    var callCount = peer.Calls.Count;
    using var cooldown = await Get("/v1/onboarding/relationship-1", valid);
    Assert(cooldown.StatusCode == HttpStatusCode.ServiceUnavailable && peer.Calls.Count == callCount, "Cooldown retried upstream");
    peer.Response = HttpStatusCode.OK;
    foreach (var malformed in new[] { "{}", "[{\"RowKey\":\"relationship-1\",\"Status\":\"other\"}]", "[{\"RowKey\":\"relationship-1\",\"Status\":\"running\"},{\"RowKey\":\"relationship-1\",\"Status\":\"failed\"}]", "[{\"RowKey\":\"relationship-1\",\"Status\":\"running\",\"Relationship\":{\"id\":\"different\"}}]", "[{\"RowKey\":\"relationship-1\",\"Status\":\"running\",\"Relationship\":{\"id\":\"relationship-1\",\"id\":\"relationship-1\"}}]", "[{\"RowKey\":\"relationship-1\",\"Status\":\"running\",\"Relationship\":{\"partner\":{\"tenantId\":\"wrong-partner\"}}}]" })
    {
        clock.Advance(100);
        peer.Rows = malformed;
        using var failure = await Get("/v1/onboarding/relationship-1", valid);
        Assert(failure.StatusCode == HttpStatusCode.ServiceUnavailable, "Malformed or conflicting CIPP identity accepted");
    }
    Console.WriteLine("PASS: cooldown, malformed evidence and identity conflicts fail closed without stale success or raw upstream errors");
    peer.Rows = "[]";
    clock.Advance(100);
    using var recovered = await Get("/v1/onboarding/relationship-1", valid);
    Assert(recovered.IsSuccessStatusCode, "No recovery after cooldown");
    var blocked = false;
    for (var i = 0; i < 130; i++) { using var response = await Get("/v1/connection", valid); if ((int)response.StatusCode == 429) { blocked = true; break; } }
    Assert(blocked, "Global request limit missing");
    Console.WriteLine("PASS: bounded public request rate; no live identity provider, CIPP, tenant or server contacted");
}
finally { await app.StopAsync(); Directory.Delete(root, recursive: true); } // exact synthetic fixture root

static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
sealed class Clock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(int seconds) => now = now.AddSeconds(seconds);
}
sealed class Peer : HttpMessageHandler
{
    public List<string> Calls = [];
    public HttpStatusCode Response = HttpStatusCode.OK;
    public string Rows = "[{\"RowKey\":\"relationship-1\",\"Status\":\"queued\",\"Logs\":\"PRIVATE_LOG\"},{\"RowKey\":\"relationship-2\",\"Status\":\"running\"}]";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Calls.Add(request.Method + " " + request.RequestUri);
        if (request.Method == HttpMethod.Post)
        {
            if (!(await request.Content!.ReadAsStringAsync(token)).Contains("client_secret=synthetic-cipp-secret")) throw new Exception("Wrong secret source");
            return Json("{\"access_token\":\"synthetic-token\",\"token_type\":\"Bearer\",\"expires_in\":3600}");
        }
        if (request.Headers.Authorization?.Parameter != "synthetic-token") throw new Exception("Missing CIPP token");
        var response = Json(Response == HttpStatusCode.OK ? Rows : "PRIVATE_LOG", Response);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
        return response;
    }
    private static HttpResponseMessage Json(string text, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
}
