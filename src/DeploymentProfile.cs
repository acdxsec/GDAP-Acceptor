using System.Text.Json;
using System.Text.Json.Serialization;

// Deployment identifiers are built into the assembly covered by package signing.
// Never load an adjacent JSON file or accept credentials in this profile.
internal sealed record DeploymentProfile(int Version, CentralConnection Connection)
{
    internal static DeploymentProfile? Embedded()
    {
        using var stream = typeof(DeploymentProfile).Assembly.GetManifestResourceStream("Gdap.DeploymentProfile");
        return stream is null ? null : Parse(new StreamReader(stream).ReadToEnd());
    }

    internal static DeploymentProfile Parse(string json)
    {
        if (json.Length > 8192) throw new InvalidDataException("Deployment profile is too large.");
        using var document = JsonDocument.Parse(json);
        Gdap.Status.InvitationProtocol.UniqueTree(document.RootElement);
        var profile = JsonSerializer.Deserialize<DeploymentProfile>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new InvalidDataException("Deployment profile is missing.");
        if (profile.Version != 1 || profile.Connection is null || profile.Connection.Instance is null)
            throw new InvalidDataException("Unsupported deployment profile.");
        CippStatus.Validate(profile.Connection, profile.Connection.Instance);
        if (profile.Connection.ClientId != profile.Connection.ApiId)
            throw new InvalidDataException("The deployment must use one companion registration.");
        return profile;
    }
}
