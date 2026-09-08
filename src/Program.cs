using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var result = await Acceptor.Run(args);
if (args.Length == 1 && args[0].StartsWith("gdap-acceptor://", StringComparison.Ordinal) && !Console.IsInputRedirected)
{
    Console.WriteLine("Press Enter to close this window.");
    Console.ReadLine();
}
return result;

internal sealed record Instance(string BaseUrl, string PartnerTenantId);
internal sealed record Invitation(string InstanceId, string RelationshipId);

internal static class Acceptor
{
    private static readonly string State = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "gdap-acceptor");
    private static readonly string ConfigPath = Path.Combine(State, "instances.json");
    private static readonly Regex IdPattern = new("\\A[A-Za-z0-9][A-Za-z0-9_-]{0,255}\\z", RegexOptions.CultureInvariant);

    internal static Invitation ParseInvitation(string text)
    {
        if (text.Length > 1024 || !text.StartsWith("gdap-acceptor://v1/accept/", StringComparison.Ordinal)) throw new ArgumentException("Unsupported invitation URI.");
        var parts = text["gdap-acceptor://v1/accept/".Length..].Split('/');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "D", out var instance) || instance == Guid.Empty) throw new ArgumentException("Invalid instance identity.");
        var id = Uri.UnescapeDataString(parts[1]);
        if (!IdPattern.IsMatch(id) || parts[1] != Uri.EscapeDataString(id)) throw new ArgumentException("Invalid relationship identity.");
        return new(instance.ToString(), id);
    }

    internal static string ValidateBaseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("CIPP must be an HTTPS origin, without credentials, path, query, or fragment.");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static Dictionary<string, Instance> ReadInstances() => File.Exists(ConfigPath)
        ? JsonSerializer.Deserialize<Dictionary<string, Instance>>(File.ReadAllText(ConfigPath)) ?? new()
        : new();

    private static void CreatePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal static async Task<int> Run(string[] args)
    {
        try
        {
            if (args.SequenceEqual(new[] { "self-test" })) { SelfTest(); return 0; }
            CreatePrivateDirectory(State);
            if (args.Length == 3 && args[0] == "diagnostics" && args[1] == "export")
            {
                using var output = new FileStream(Path.GetFullPath(args[2]), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                foreach (var log in Directory.EnumerateFiles(State, "diagnostics-????-??-??.jsonl").Order())
                {
                    if (File.GetLastWriteTimeUtc(log) < DateTime.UtcNow.AddDays(-7)) continue;
                    using var input = File.OpenRead(log);
                    await input.CopyToAsync(output);
                }
                Console.WriteLine("Exported sanitized state logs. No authentication material is recorded in these logs.");
                return 0;
            }
            if (args.Length == 5 && args[0] == "instance" && args[1] == "add")
            {
                if (!Guid.TryParseExact(args[2], "D", out var instanceId) || instanceId == Guid.Empty ||
                    !Guid.TryParseExact(args[4], "D", out var partner) || partner == Guid.Empty) throw new ArgumentException("Valid instance and partner tenant IDs are required.");
                var instance = new Instance(ValidateBaseUrl(args[3]), partner.ToString());
                Console.WriteLine($"Trust CIPP {instance.BaseUrl}, instance {instanceId}, partner {partner}? Type TRUST to save:");
                if (Console.ReadLine() != "TRUST") return 1;
                var instances = ReadInstances();
                if (instances.TryGetValue(instanceId.ToString(), out var old) && old != instance) throw new ArgumentException("Instance is already enrolled with a different URL or partner. Remove it explicitly before changing its identity.");
                instances[instanceId.ToString()] = instance;
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(instances, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (args.Length == 3 && args[0] == "instance" && args[1] == "remove")
            {
                var instances = ReadInstances();
                instances.Remove(Guid.ParseExact(args[2], "D").ToString());
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(instances));
                return 0;
            }
            if (args.Length != 1) { Console.WriteLine("gdap-acceptor <invitation-uri> | instance add <instance-id> <https-origin> <partner-tenant-id> | instance remove <instance-id> | diagnostics export <new-file> | self-test"); return 2; }
            var invitation = ParseInvitation(args[0]);
            var settings = ReadInstances();
            if (!settings.ContainsKey(invitation.InstanceId)) throw new ArgumentException("CIPP instance is not enrolled. Run instance add first.");
            var queue = Path.Combine(State, "queue");
            CreatePrivateDirectory(queue);
            if (Directory.EnumerateFiles(queue, "*.json").Count() >= 20) throw new ArgumentException("The local invitation queue is full.");
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(invitation.InstanceId + "/" + invitation.RelationshipId)));
            var pending = Path.Combine(queue, key + ".json");
            try { using var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None); JsonSerializer.Serialize(file, invitation); }
            catch (IOException) when (File.Exists(pending)) { Console.WriteLine("This invitation is already queued or active."); }
            Console.WriteLine("Waiting for the local acceptance window...");
            var deadline = DateTime.UtcNow.AddMinutes(10);
            var ownResult = 1;
            while (File.Exists(pending) && DateTime.UtcNow < deadline)
            {
                FileStream lease;
                try { lease = new FileStream(Path.Combine(State, "acceptance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { await Task.Delay(500); continue; }
                using (lease)
                {
                    foreach (var file in Directory.EnumerateFiles(queue, "*.json").OrderBy(File.GetCreationTimeUtc))
                    {
                        try
                        {
                            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromMinutes(10)) { Console.WriteLine("An expired queued invitation was discarded; launch it again if needed."); continue; }
                            var next = JsonSerializer.Deserialize<Invitation>(File.ReadAllText(file)) ?? throw new ArgumentException("Invalid queued invitation.");
                            next = ParseInvitation($"gdap-acceptor://v1/accept/{next.InstanceId}/{next.RelationshipId}");
                            var current = ReadInstances();
                            if (!current.TryGetValue(next.InstanceId, out var enrolled)) throw new ArgumentException("Queued instance was removed.");
                            var accepted = await Accept(next, enrolled);
                            if (file == pending) ownResult = accepted ? 0 : 1;
                        }
                        catch { Console.WriteLine("Acceptance stopped. Inspect the Microsoft invitation and CIPP status before retrying."); }
                        finally { File.Delete(file); }
                    }
                }
            }
            // A different process may have drained our queue entry. Without its
            // result, never represent disappearance as confirmed acceptance.
            return File.Exists(pending) ? 1 : ownResult;
        }
        catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }
        catch { Console.Error.WriteLine("GDAP Acceptor stopped. Check local prerequisites and configuration."); return 1; }
    }

    private static async Task<bool> Accept(Invitation invitation, Instance instance)
    {
        var baseUrl = ValidateBaseUrl(instance.BaseUrl);
        var partner = Guid.ParseExact(instance.PartnerTenantId, "D");
        if (partner == Guid.Empty) throw new ArgumentException("Invalid enrolled partner.");
        Console.WriteLine($"CIPP: {baseUrl}\nPartner tenant: {partner}\nRelationship: {invitation.RelationshipId}");
        Console.Write("Expected CUSTOMER tenant ID (before authentication): ");
        if (!Guid.TryParseExact(Console.ReadLine(), "D", out var customer) || customer == Guid.Empty) throw new ArgumentException("A customer tenant ID is required.");
        var fallback = $"https://admin.microsoft.com/AdminPortal/Home#/partners/invitation/granularAdminRelationships/{Uri.EscapeDataString(invitation.RelationshipId)}";
        Console.WriteLine($"Microsoft fallback: {fallback}");
        var pwsh = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe")
            : "/usr/bin/pwsh";
        if (!File.Exists(pwsh)) throw new ArgumentException("PowerShell 7 is required at its standard installation location.");
        var wrapper = Path.Combine(AppContext.BaseDirectory, "scripts", "Invoke-Acceptance.ps1");
        if (!File.Exists(wrapper)) throw new ArgumentException("The bundled acceptance payload is missing. Install a complete package.");
        var start = new ProcessStartInfo(pwsh) { UseShellExecute = false };
        foreach (var value in new[] { "-NoLogo", "-NoProfile", "-File", wrapper, "-RelationshipId", invitation.RelationshipId,
            "-ExpectedTenantId", customer.ToString(), "-ExpectedPartnerTenantId", partner.ToString() }) start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        await process.WaitForExitAsync();
        var status = process.ExitCode == 0 ? "relationshipActive" : "acceptanceStopped";
        var log = Path.Combine(State, "diagnostics-" + DateTime.UtcNow.ToString("yyyy-MM-dd") + ".jsonl");
        File.AppendAllText(log, JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, invitation.InstanceId, invitation.RelationshipId, tenantId = customer, status }) + "\n");
        foreach (var old in Directory.EnumerateFiles(State, "diagnostics-????-??-??.jsonl")) if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-7)) File.Delete(old);
        if (process.ExitCode != 0) { Console.WriteLine("Acceptance not confirmed. Use the Microsoft fallback if needed."); return false; }
        Console.WriteLine("Relationship active. Returning to CIPP to observe onboarding start.");
        var returnUrl = baseUrl + "/tenant/gdap-management/onboarding/status?id=" + Uri.EscapeDataString(invitation.RelationshipId);
        Process.Start(new ProcessStartInfo(returnUrl) { UseShellExecute = true });
        return true;
    }

    private static void SelfTest()
    {
        var composite = "5d027261-d21f-4aa9-b7db-7fa1f56fb163-8777b240-c6f0-4469-9e98-a3205431b836";
        var prefix = "gdap-acceptor://v1/accept/11111111-1111-1111-1111-111111111111/";
        if (ParseInvitation(prefix + composite).RelationshipId != composite) throw new Exception("Composite identifier lost");
        foreach (var invalid in new[] { "../x", "a%2fb", "id?x=1", "id#x", "id\n", "%252f", new string('a', 257) })
        { try { ParseInvitation(prefix + invalid); } catch (ArgumentException) { continue; } throw new Exception("Unsafe URI accepted"); }
        foreach (var invalid in new[] { "http://example.com", "https://user@example.com", "https://example.com/path", "https://example.com/?x=1" })
        { try { ValidateBaseUrl(invalid); } catch (ArgumentException) { continue; } throw new Exception("Unsafe origin accepted"); }
        Console.WriteLine("PASS: companion URI and enrollment contracts");
    }
}
