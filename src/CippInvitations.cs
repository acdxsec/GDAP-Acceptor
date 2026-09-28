using System.Text.Json;
using Gdap.Status;

internal sealed record LocalCreation(Instance Instance, string ConnectorOrigin, CreateInvitation Request, bool Completed = false);

internal sealed class CippInvitations(string directory, CippStatus connector)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal Task<int> Configure(string id, Instance instance) => connector.Configure(id, instance, invitations: true);
    internal async Task<int> Run(string id, Instance instance, Func<Invitation, Instance, Task<int>> accept)
    {
        var path = Path.Combine(directory, "invitation-create-" + StatusProtocol.GuidValue(id) + ".json");
        try
        {
            var connection = connector.Load(id, instance);
            if (connection is null) { Console.WriteLine("Configure the invitation connector in settings (3 → C) first. Pasting an existing invitation remains available."); return 1; }
            // Never race two local create/resume sessions. Server reservation also
            // protects repeated operation IDs across hosts and container restarts.
            using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            LocalCreation? work = null;
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 16384) throw new InvalidDataException();
                work = JsonSerializer.Deserialize<LocalCreation>(File.ReadAllText(path)) ?? throw new InvalidDataException();
                if (work.Instance != instance || work.ConnectorOrigin != connection.Origin) throw new InvalidDataException();
                InvitationProtocol.Validate(work.Request);
                if (work.Completed)
                {
                    Console.Write("Previous invitation was accepted. Type NEW to generate another, or Enter to return: ");
                    if (Console.ReadLine() != "NEW") return 0;
                    File.Move(path, path + "." + work.Request.OperationId + ".completed");
                    work = null;
                }
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            CreatedInvitation created;
            if (work is not null)
            {
                Console.WriteLine($"Recovering creation {work.Request.OperationId}. Only a read is sent; no new invitation will be generated.");
                using var result = await connector.Send(connection, "/v1/invitations/operations/" + work.Request.OperationId, timeout.Token);
                created = result.RootElement.Deserialize<CreatedInvitation>(Json) ?? throw new InvalidDataException();
            }
            else
            {
                using var result = await connector.Send(connection, "/v1/invitations/templates", timeout.Token);
                var list = result.RootElement.Deserialize<InviteTemplates>(Json) ?? throw new InvalidDataException();
                if (list.Version != 1 || list.CippOrigin != instance.BaseUrl || list.PartnerTenantId != instance.PartnerTenantId || list.Templates.Length is < 1 or > 100) throw new InvalidDataException();
                for (var i = 0; i < list.Templates.Length; i++)
                {
                    if (!InvitationProtocol.Text(list.Templates[i].Id, 200)) throw new InvalidDataException();
                    Console.WriteLine($"{i + 1}. {list.Templates[i].Id}");
                }
                Console.Write("Select the existing CIPP role template (blank to cancel): ");
                if (!int.TryParse(Console.ReadLine(), out var selection) || selection < 1 || selection > list.Templates.Length) return 1;
                var template = list.Templates[selection - 1];
                if (template.Roles.Length is < 1 or > 100) throw new InvalidDataException();
                foreach (var role in template.Roles)
                {
                    if (!InvitationProtocol.Text(role.RoleName, 200) || !InvitationProtocol.Text(role.GroupName, 200)) throw new InvalidDataException();
                    if (StatusProtocol.GuidValue(role.RoleDefinitionId) != role.RoleDefinitionId || StatusProtocol.GuidValue(role.GroupId) != role.GroupId) throw new InvalidDataException();
                    Console.WriteLine($"  {role.RoleName} ({role.RoleDefinitionId}) → {role.GroupName} ({role.GroupId})");
                }
                Console.Write("Client/ticket reference (optional; no credentials): ");
                var reference = Console.ReadLine() ?? "";
                var request = new CreateInvitation(Guid.NewGuid().ToString(), template.Id, template.Fingerprint, reference);
                InvitationProtocol.Validate(request);
                Console.WriteLine($"CIPP: {instance.BaseUrl}\nPartner: {instance.PartnerTenantId}\nTemplate: {template.Id}");
                Console.Write("Type CREATE to generate ONE invitation in CIPP, then continue to customer sign-in: ");
                if (Console.ReadLine() != "CREATE") return 1;
                work = new(instance, connection.Origin, request);
                Save(path, work, overwrite: false); // durable before HTTP
                using var response = await connector.Send(connection, "/v1/invitations", timeout.Token, request);
                created = response.RootElement.Deserialize<CreatedInvitation>(Json) ?? throw new InvalidDataException();
            }
            InvitationProtocol.Validate(created, work.Request.OperationId, instance.BaseUrl, instance.PartnerTenantId);
            Console.WriteLine($"CIPP invitation ready: {created.InviteUrl}\nOnboarding page: {created.OnboardingUrl}");
            var code = await accept(new Invitation(id, created.RelationshipId), instance);
            if (code == 0) Save(path, work with { Completed = true }, overwrite: true);
            return code;
        }
        catch
        {
            Console.WriteLine("Invitation creation or recovery did not complete. Any saved attempt is retained. Choose Create/resume again for read-only recovery; do not generate a replacement until the outcome has been reviewed in CIPP. No automatic create retry was sent.");
            return 1;
        }
    }
    private static void Save(string path, LocalCreation value, bool overwrite)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, value); file.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
