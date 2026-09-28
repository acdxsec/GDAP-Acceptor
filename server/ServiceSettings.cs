using System.Text.Json;
using Gdap.Status;

namespace Gdap.Server;

public sealed class ServiceSettings
{
    public string IdentityTenantId { get; set; } = "";
    public string Audience { get; set; } = "";
    public string DesktopClientId { get; set; } = "";
    public string CippOrigin { get; set; } = "";
    public string PartnerTenantId { get; set; } = "";
    public string CippApiOrigin { get; set; } = "";
    public string CippAuthenticationTenantId { get; set; } = "";
    public string CippClientId { get; set; } = "";
    public string CippScope { get; set; } = "";
    // Set by the host, never supplied over HTTP or serialized in metadata.
    internal string SecretFile { get; set; } = "/run/secrets/cipp-client-secret";
    // Null deliberately disables invitation writes in existing status deployments.
    internal string? InvitationJournalDirectory { get; set; }
    public string Authority => $"https://login.microsoftonline.com/{IdentityTenantId}/v2.0";

    internal void Validate()
    {
        foreach (var id in new[] { IdentityTenantId, Audience, DesktopClientId, PartnerTenantId, CippAuthenticationTenantId, CippClientId })
            if (StatusProtocol.GuidValue(id) != id) throw new InvalidDataException();
        if (StatusProtocol.Origin(CippOrigin) != CippOrigin || StatusProtocol.Origin(CippApiOrigin) != CippApiOrigin) throw new InvalidDataException();
        if (CippScope.Length > 2048 || CippScope.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
            !CippScope.EndsWith("/.default", StringComparison.Ordinal) || !Uri.TryCreate(CippScope, UriKind.Absolute, out var scope) ||
            scope.Scheme is not ("https" or "api") || scope.Host.Length == 0 || scope.UserInfo.Length != 0 || scope.Query.Length != 0 || scope.Fragment.Length != 0) throw new InvalidDataException();
        _ = ReadSecret();
    }
    internal string ReadSecret()
    {
        if (new FileInfo(SecretFile).Length is <= 0 or > 4096) throw new InvalidDataException();
        var secret = File.ReadAllText(SecretFile).TrimEnd('\r', '\n');
        if (secret.Length is 0 or > 1024 || secret.Any(char.IsControl) || secret.StartsWith("REPLACE_", StringComparison.Ordinal)) throw new InvalidDataException();
        return secret;
    }
    public static ServiceSettings Load()
    {
        var file = Environment.GetEnvironmentVariable("GDAP_SETTINGS_FILE") ?? "/config/settings.json";
        if (new FileInfo(file).Length > 16384) throw new InvalidDataException();
        var settings = JsonSerializer.Deserialize<ServiceSettings>(File.ReadAllText(file)) ?? throw new InvalidDataException();
        settings.SecretFile = Environment.GetEnvironmentVariable("GDAP_CIPP_SECRET_FILE") ?? settings.SecretFile;
        settings.InvitationJournalDirectory = Environment.GetEnvironmentVariable("GDAP_INVITATION_JOURNAL_DIR");
        settings.Validate();
        return settings;
    }
}
