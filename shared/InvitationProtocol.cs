using System.Text.Json;

namespace Gdap.Status;

public sealed record InviteRole(string RoleDefinitionId, string RoleName, string GroupId, string GroupName);
public sealed record InviteTemplate(string Id, string Fingerprint, InviteRole[] Roles);
public sealed record InviteTemplates(int Version, string CippOrigin, string PartnerTenantId, InviteTemplate[] Templates);
public sealed record CreateInvitation(string OperationId, string TemplateId, string Fingerprint, string Reference);
public sealed record CreatedInvitation(int Version, string OperationId, string CippOrigin, string PartnerTenantId, string RelationshipId, string InviteUrl, string OnboardingUrl);

public static class InvitationProtocol
{
    public const string Scope = "Invitations.Create";
    public const string Role = "Invitations.Create";
    public const string MicrosoftPrefix = "https://admin.microsoft.com/AdminPortal/Home#/partners/invitation/granularAdminRelationships/";
    public static string Onboarding(string origin, string relationship) => StatusProtocol.Origin(origin) + "/tenant/gdap-management/onboarding/start?id=" + Uri.EscapeDataString(relationship);
    public static void Validate(CreateInvitation request)
    {
        if (StatusProtocol.GuidValue(request.OperationId) != request.OperationId || !Text(request.TemplateId, 200) ||
            request.Fingerprint.Length != 64 || request.Fingerprint.Any(c => !char.IsAsciiHexDigit(c)) ||
            !Text(request.Reference, 200, allowEmpty: true)) throw new InvalidDataException();
    }
    public static bool Text(string value, int max, bool allowEmpty = false) => (allowEmpty || value.Length > 0) && value.Length <= max &&
        !value.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format);
    public static void Validate(CreatedInvitation result, string operation, string origin, string partner)
    {
        if (result.Version != 1 || result.OperationId != operation || result.CippOrigin != origin || result.PartnerTenantId != partner ||
            result.RelationshipId.Length != 73 || result.RelationshipId[36] != '-' ||
            StatusProtocol.GuidValue(result.RelationshipId[..36]) != result.RelationshipId[..36] || result.RelationshipId[37..] != partner ||
            result.InviteUrl != MicrosoftPrefix + result.RelationshipId || result.OnboardingUrl != Onboarding(origin, result.RelationshipId)) throw new InvalidDataException();
    }
    public static void UniqueTree(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object) { StatusProtocol.UniqueObject(value); foreach (var property in value.EnumerateObject()) UniqueTree(property.Value); }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) UniqueTree(item);
    }
}
