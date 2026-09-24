using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gdap.Status;

public sealed record ConnectionInfo(int Version, string CippOrigin, string PartnerTenantId);
public sealed record OnboardingStatus(int Version, string CippOrigin, string PartnerTenantId, string RelationshipId, string Status, DateTimeOffset ObservedAt);

public static class StatusProtocol
{
    public const string Scope = "Status.Read";
    public const string Role = "Onboarding.Read";
    public static bool IsRelationship(string id) => Regex.IsMatch(id, "\\A[A-Za-z0-9][A-Za-z0-9_-]{0,255}\\z", RegexOptions.CultureInvariant);
    public static bool IsStatus(string? value) => value is "waiting" or "pending" or "queued" or "running" or "succeeded" or "failed" or "cancelled";
    public static string GuidValue(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id.ToString() : throw new InvalidDataException();
    public static string Origin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/") throw new InvalidDataException();
        return uri.GetLeftPart(UriPartial.Authority);
    }
    public static void UniqueObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1)) throw new InvalidDataException();
    }
    public static Dictionary<string, string> ReadCippRows(JsonElement rows, string partnerTenantId)
    {
        if (rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            UniqueObject(row);
            var id = row.GetProperty("RowKey").GetString() ?? throw new InvalidDataException();
            var status = row.GetProperty("Status").GetString();
            if (!IsRelationship(id) || !IsStatus(status) || status == "waiting" || !statuses.TryAdd(id, status!)) throw new InvalidDataException();
            if (!row.TryGetProperty("Relationship", out var relationship)) continue;
            UniqueObject(relationship);
            if (relationship.TryGetProperty("id", out var identity) && identity.GetString() != id) throw new InvalidDataException();
            if (relationship.TryGetProperty("partner", out var partner))
            {
                UniqueObject(partner);
                if (partner.TryGetProperty("tenantId", out var tenant) &&
                    !string.Equals(tenant.GetString(), partnerTenantId, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            }
        }
        return statuses;
    }
}
