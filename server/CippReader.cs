using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gdap.Status;

namespace Gdap.Server;

internal sealed class UpstreamUnavailable(int retrySeconds) : Exception
{
    internal int RetrySeconds { get; } = retrySeconds;
}

// Single replica: one full-table read per 30 seconds across all callers, not
// per technician. Store only status projections; never serve stale on failure.
internal sealed class CippReader(ServiceSettings settings, IHttpClientFactory clients, TimeProvider clock) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Dictionary<string, string>? rows;
    private DateTimeOffset observed, retryAt, tokenExpires;
    private string accessToken = "";

    internal async Task<OnboardingStatus> Read(string relationshipId, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            var now = clock.GetUtcNow();
            if (retryAt > now) throw new UpstreamUnavailable((int)Math.Ceiling((retryAt - now).TotalSeconds));
            if (rows is null || now - observed >= TimeSpan.FromSeconds(30))
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    using var client = clients.CreateClient("cipp");
                    if (tokenExpires <= now)
                    {
                        using var authentication = new HttpRequestMessage(HttpMethod.Post, $"https://login.microsoftonline.com/{settings.CippAuthenticationTenantId}/oauth2/v2.0/token")
                        {
                            Content = new FormUrlEncodedContent(new Dictionary<string, string>
                            {
                                ["client_id"] = settings.CippClientId, ["client_secret"] = settings.ReadSecret(),
                                ["scope"] = settings.CippScope, ["grant_type"] = "client_credentials"
                            })
                        };
                        using var result = await Send(client, authentication, deadline.Token);
                        StatusProtocol.UniqueObject(result.RootElement);
                        var bearer = result.RootElement.GetProperty("access_token").GetString();
                        var seconds = result.RootElement.GetProperty("expires_in").GetInt32();
                        if (bearer is null || bearer.Length is 0 or > 32768 || !Regex.IsMatch(bearer, "\\A[A-Za-z0-9._~+/-]+=*\\z") ||
                            seconds is <= 60 or > 86400 || !string.Equals(result.RootElement.GetProperty("token_type").GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                        accessToken = bearer;
                        tokenExpires = clock.GetUtcNow().AddSeconds(seconds - 60);
                    }
                    using var request = new HttpRequestMessage(HttpMethod.Get, settings.CippApiOrigin + "/api/ListTenantOnboarding");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    using var table = await Send(client, request, deadline.Token);
                    rows = StatusProtocol.ReadCippRows(table.RootElement, settings.PartnerTenantId);
                    observed = clock.GetUtcNow();
                }
                catch (Exception error)
                {
                    rows = null;
                    tokenExpires = default;
                    accessToken = "";
                    var seconds = error is UpstreamUnavailable problem ? problem.RetrySeconds : 60;
                    retryAt = clock.GetUtcNow().AddSeconds(seconds);
                    // No original exception or response body crosses this interface.
                    throw new UpstreamUnavailable(seconds);
                }
            }
            return new(1, settings.CippOrigin, settings.PartnerTenantId, relationshipId, rows.GetValueOrDefault(relationshipId, "waiting"), observed);
        }
        finally { gate.Release(); }
    }
    private async Task<JsonDocument> Send(HttpClient client, HttpRequestMessage request, CancellationToken cancellation)
    {
        using var response = await client.SendAsync(request, cancellation);
        if (!response.IsSuccessStatusCode)
        {
            var delay = response.StatusCode == HttpStatusCode.TooManyRequests
                ? response.Headers.RetryAfter?.Delta ?? response.Headers.RetryAfter?.Date - clock.GetUtcNow() ?? TimeSpan.FromSeconds(60)
                : TimeSpan.FromSeconds(60);
            throw new UpstreamUnavailable((int)Math.Clamp(Math.Ceiling(delay.TotalSeconds), 30, 3600));
        }
        var type = response.Content.Headers.ContentType?.MediaType;
        if (type != "application/json" && !(type?.EndsWith("+json", StringComparison.Ordinal) ?? false)) throw new InvalidDataException();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation), new JsonDocumentOptions { MaxDepth = 48 });
    }
    public void Dispose() => gate.Dispose();
}
