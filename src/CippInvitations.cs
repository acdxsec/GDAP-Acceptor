using System.Text.Json;
using Gdap.Status;

internal sealed record LocalCreation(Instance Instance, string ConnectorOrigin, CreateInvitation Request, bool Completed = false);

internal sealed class CippInvitations(string directory, CippStatus connector, Func<CancellationTokenSource>? requestDeadline = null)
{
    private CancellationTokenSource Deadline() => requestDeadline?.Invoke() ?? new(TimeSpan.FromMinutes(5));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal Task<int> Configure(string id, Instance instance, DeploymentProfile? profile = null) => connector.Configure(id, instance, invitations: true, profile: profile);
    private string AttemptFile(string id) => Path.Combine(directory, "invitation-create-" + StatusProtocol.GuidValue(id) + ".json");
    private static LocalCreation ReadAttempt(string path, Instance instance)
    {
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException();
        var attempt = JsonSerializer.Deserialize<LocalCreation>(File.ReadAllText(path)) ?? throw new InvalidDataException();
        InvitationProtocol.Validate(attempt.Request);
        if (attempt.Instance != instance || StatusProtocol.Origin(attempt.ConnectorOrigin) != attempt.ConnectorOrigin) throw new InvalidDataException();
        return attempt;
    }
    internal bool ShowPending(IReadOnlyDictionary<string, Instance> instances)
    {
        var found = false;
        foreach (var (id, instance) in instances)
        {
            var path = AttemptFile(id);
            if (!File.Exists(path)) continue;
            var attempt = ReadAttempt(path, instance);
            if (attempt.Completed) continue;
            found = true;
            Console.WriteLine($"Saved invitation creation: {attempt.Request.OperationId}\nCIPP: {instance.BaseUrl}");
            Console.WriteLine("Creation outcome needs review. This is separate from the customer approval queue; option 6 performs read-only recovery.");
        }
        return found;
    }
    internal int Review(string id, Instance instance)
    {
        var path = AttemptFile(id);
        using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!File.Exists(path)) { Console.WriteLine("No saved creation attempt needs review. Nothing changed."); return 0; }
        var attempt = ReadAttempt(path, instance);
        if (attempt.Completed) { Console.WriteLine("This invitation was already accepted. No state changed."); return 0; }
        Console.WriteLine($"Operation: {attempt.Request.OperationId}\nCIPP: {instance.BaseUrl}\nPartner: {instance.PartnerTenantId}\nConnector: {attempt.ConnectorOrigin}");
        Console.WriteLine($"Search CIPP invitations for [GDAP-Acceptor:{attempt.Request.OperationId}]. Stop other launcher sessions and allow any in-flight server request to finish.");
        Console.WriteLine("If an invitation exists, use its URL with Accept invitation; do not create a replacement. A timeout or missing server record alone is not proof of failure.");
        Console.Write("Type RESOLVED only after reviewing the outcome. This archives local state and sends no request: ");
        if (Console.ReadLine() != "RESOLVED") { Console.WriteLine("Creation recovery cancelled. Saved attempt unchanged."); return 1; }
        // Revalidate the saved identity as well as holding the workflow lock.
        if (ReadAttempt(path, instance) != attempt) throw new IOException();
        File.Move(path, path + "." + attempt.Request.OperationId + "." + Guid.NewGuid().ToString("N") + ".reviewed");
        Console.WriteLine("Saved creation attempt archived locally. No invitation was created, approved, revoked or retried. Existing enrollment is unchanged.");
        return 0;
    }
    internal async Task<int> Run(string id, Instance instance, Func<Invitation, Instance, Task<int>> accept, DeploymentProfile? profile = null)
    {
        var path = AttemptFile(id);
        var stage = "loading local connection";
        var outcomeMayBeUncertain = false;
        try
        {
            var connection = connector.Load(id, instance);
            if (connection is null && profile?.Connection.Instance == instance)
            {
                if (await Configure(id, instance, profile) != 0) return 1;
                connection = connector.Load(id, instance);
            }
            if (connection is null) { Console.WriteLine("Configure the invitation connector in settings (3 → C) first. Pasting an existing invitation remains available."); return 1; }
            // Never race two local create/resume sessions. Server reservation also
            // protects repeated operation IDs across hosts and container restarts.
            stage = "locking local invitation workflow";
            using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            LocalCreation? work = null;
            if (File.Exists(path))
            {
                stage = "reading saved invitation attempt";
                outcomeMayBeUncertain = true;
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
                    outcomeMayBeUncertain = false;
                }
            }
            CreatedInvitation created;
            if (work is not null)
            {
                stage = "recovering saved invitation (read-only)";
                Console.WriteLine($"Recovering creation {work.Request.OperationId}. Only a read is sent; no new invitation will be generated.");
                using var timeout = Deadline();
                using var result = await connector.Send(connection, "/v1/invitations/operations/" + work.Request.OperationId, timeout.Token);
                created = result.RootElement.Deserialize<CreatedInvitation>(Json) ?? throw new InvalidDataException();
            }
            else
            {
                stage = "loading CIPP invitation templates";
                Console.WriteLine("Loading CIPP invitation templates. Staff sign-in may open in your browser.");
                InviteTemplates list;
                using (var templateDeadline = Deadline())
                using (var result = await connector.Send(connection, "/v1/invitations/templates", templateDeadline.Token))
                    list = result.RootElement.Deserialize<InviteTemplates>(Json) ?? throw new InvalidDataException();
                // No network deadline runs while the operator reviews access.
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
                stage = "preparing invitation submission";
                using var timeout = Deadline();
                timeout.Token.ThrowIfCancellationRequested();
                work = new(instance, connection.Origin, request);
                stage = "saving invitation request before submission";
                Save(path, work, overwrite: false); // durable before HTTP
                outcomeMayBeUncertain = true;
                stage = "submitting invitation creation";
                using var response = await connector.Send(connection, "/v1/invitations", timeout.Token, request);
                created = response.RootElement.Deserialize<CreatedInvitation>(Json) ?? throw new InvalidDataException();
            }
            stage = "validating returned invitation";
            InvitationProtocol.Validate(created, work.Request.OperationId, instance.BaseUrl, instance.PartnerTenantId);
            Console.WriteLine($"CIPP invitation ready: {created.InviteUrl}\nOnboarding page: {created.OnboardingUrl}");
            stage = "customer acceptance";
            var code = await accept(new Invitation(id, created.RelationshipId), instance);
            if (code == 0) Save(path, work with { Completed = true }, overwrite: true);
            return code;
        }
        catch (Exception error)
        {
            Console.WriteLine($"Invitation workflow stopped while {stage}.");
            Console.WriteLine(error switch
            {
                CippStatus.StatusProblem problem => problem.Message,
                Microsoft.Identity.Client.MsalException => "Staff sign-in failed or was cancelled. No authentication details are displayed. Check the sign-in window and staff access.",
                OperationCanceledException => "The operation was cancelled or timed out.",
                HttpRequestException => "The connector could not be reached or its response was interrupted. Check connectivity and connector health.",
                System.Text.Json.JsonException or InvalidDataException => "The data did not match the expected format or trusted CIPP/partner binding. Validation stopped.",
                IOException => "Local invitation state could not be accessed. Another companion may be running, or the file may be inaccessible.",
                _ => "An unexpected error occurred. No raw error details are displayed because they may contain credentials."
            });
            Console.WriteLine(outcomeMayBeUncertain
                ? "The saved attempt is retained. Choose Create/resume only for read-only recovery; do not generate a replacement until its outcome is reviewed in CIPP. No automatic create retry was sent."
                : "No invitation creation request was sent in this run. Existing saved attempts, if any, are unchanged.");
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
