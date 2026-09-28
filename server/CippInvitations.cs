using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Gdap.Status;

namespace Gdap.Server;

internal sealed class CippInvitations(ServiceSettings settings, IHttpClientFactory clients, InvitationJournal journal)
{
    internal async Task<InviteTemplates> Templates(CancellationToken cancellation)
    {
        using var document = await Request("ExecGDAPRoleTemplate", null, cancellation);
        var rows = document.RootElement.GetProperty("Results");
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 100) throw new InvalidDataException();
        var templates = rows.EnumerateArray().Select(row =>
        {
            var id = row.GetProperty("TemplateId").GetString() ?? "";
            if (!InvitationProtocol.Text(id, 200)) throw new InvalidDataException();
            var roles = Roles(row.GetProperty("RoleMappings"));
            return new InviteTemplate(id, Fingerprint(roles), roles);
        }).ToArray();
        if (templates.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != templates.Length) throw new InvalidDataException();
        return new(1, settings.CippOrigin, settings.PartnerTenantId, templates);
    }
    internal async Task<CreatedInvitation?> Create(CreateInvitation request, string staff, CancellationToken cancellation)
    {
        InvitationProtocol.Validate(request);
        // Validate the authoritative template again; never accept arbitrary roles
        // or group mappings sent by a workstation.
        var template = (await Templates(cancellation)).Templates.Single(t => t.Id == request.TemplateId);
        if (template.Fingerprint != request.Fingerprint) throw new InvalidDataException();
        var attempt = new InvitationAttempt(staff, settings.CippOrigin, settings.PartnerTenantId, request, template.Roles);
        if (!journal.Reserve(attempt))
        {
            var existing = journal.Read(request.OperationId, staff);
            if (existing.Request != request) throw new InvalidDataException();
            return await Recover(request.OperationId, staff, cancellation);
        }
        // No retry at this seam. Even cancellation before response receipt leaves
        // the durable attempt reserved. Recovery is read-only.
        using var result = await Request("ExecGDAPInvite", new
        {
            Action = "Create", Reference = Reference(request),
            roleMappings = template.Roles.Select(r => new { roleDefinitionId = r.RoleDefinitionId, RoleName = r.RoleName, GroupId = r.GroupId, GroupName = r.GroupName })
        }, cancellation);
        var created = Parse(result.RootElement.GetProperty("Invite"), attempt);
        journal.Complete(created);
        return created;
    }
    internal async Task<CreatedInvitation?> Recover(string operation, string staff, CancellationToken cancellation)
    {
        var attempt = journal.Read(operation, staff);
        var saved = journal.Result(operation);
        if (saved is not null) { InvitationProtocol.Validate(saved, operation, settings.CippOrigin, settings.PartnerTenantId); return saved; }
        using var table = await Request("ListGDAPInvite", null, cancellation);
        if (table.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
        var matches = table.RootElement.EnumerateArray().Where(r => r.TryGetProperty("Reference", out var reference) && reference.GetString() == Reference(attempt.Request)).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1) throw new InvalidDataException();
        var found = Parse(matches[0], attempt);
        journal.Complete(found);
        return found;
    }
    private CreatedInvitation Parse(JsonElement row, InvitationAttempt attempt)
    {
        var id = row.GetProperty("RowKey").GetString() ?? "";
        if (row.GetProperty("Reference").GetString() != Reference(attempt.Request) || Fingerprint(Roles(row.GetProperty("RoleMappings"))) != attempt.Request.Fingerprint) throw new InvalidDataException();
        // Use only the enrolled CIPP origin and validated relationship, never a
        // hostname derived from proxy headers or an arbitrary returned link.
        var result = new CreatedInvitation(1, attempt.Request.OperationId, settings.CippOrigin, settings.PartnerTenantId, id,
            row.GetProperty("InviteUrl").GetString() ?? "", InvitationProtocol.Onboarding(settings.CippOrigin, id));
        InvitationProtocol.Validate(result, attempt.Request.OperationId, settings.CippOrigin, settings.PartnerTenantId);
        return result;
    }
    private static string Reference(CreateInvitation request) => $"[GDAP-Acceptor:{request.OperationId}] {request.Reference}";
    private static string Fingerprint(InviteRole[] roles) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(roles)));
    private static InviteRole[] Roles(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            using var parsed = JsonDocument.Parse(value.GetString()!, new JsonDocumentOptions { MaxDepth = 8 });
            InvitationProtocol.UniqueTree(parsed.RootElement);
            return Roles(parsed.RootElement);
        }
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 100) throw new InvalidDataException();
        var roles = value.EnumerateArray().Select(r => new InviteRole(
            StatusProtocol.GuidValue(r.GetProperty("roleDefinitionId").GetString()!), r.GetProperty("RoleName").GetString()!,
            StatusProtocol.GuidValue(r.GetProperty("GroupId").GetString()!), r.GetProperty("GroupName").GetString()!))
            .OrderBy(r => r.RoleDefinitionId, StringComparer.Ordinal).ThenBy(r => r.GroupId, StringComparer.Ordinal).ToArray();
        if (roles.Any(r => !InvitationProtocol.Text(r.RoleName, 200) || !InvitationProtocol.Text(r.GroupName, 200)) ||
            roles.Select(r => (r.RoleDefinitionId, r.GroupId)).Distinct().Count() != roles.Length) throw new InvalidDataException();
        return roles;
    }
    private async Task<JsonDocument> Request(string endpoint, object? body, CancellationToken cancellation)
    {
        if (endpoint is not ("ExecGDAPRoleTemplate" or "ExecGDAPInvite" or "ListGDAPInvite")) throw new InvalidDataException();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        using var client = clients.CreateClient("cipp");
        using var auth = new HttpRequestMessage(HttpMethod.Post, $"https://login.microsoftonline.com/{settings.CippAuthenticationTenantId}/oauth2/v2.0/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = settings.CippClientId, ["client_secret"] = settings.ReadSecret(), ["scope"] = settings.CippScope, ["grant_type"] = "client_credentials" })
        };
        using var token = await Send(client, auth, deadline.Token);
        var bearer = token.RootElement.GetProperty("access_token").GetString();
        if (string.IsNullOrWhiteSpace(bearer) || bearer.Length > 32768 || bearer.Any(char.IsWhiteSpace) || token.RootElement.GetProperty("token_type").GetString() is not ("Bearer" or "bearer")) throw new InvalidDataException();
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, settings.CippApiOrigin + "/api/" + endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null) request.Content = JsonContent.Create(body, options: new JsonSerializerOptions());
        return await Send(client, request, deadline.Token);
    }
    private static async Task<JsonDocument> Send(HttpClient client, HttpRequestMessage request, CancellationToken cancellation)
    {
        using var response = await client.SendAsync(request, cancellation);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/json") throw new InvalidDataException();
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation), new JsonDocumentOptions { MaxDepth = 32 });
        try { InvitationProtocol.UniqueTree(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }
}
