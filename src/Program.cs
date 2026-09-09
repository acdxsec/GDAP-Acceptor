using System.Diagnostics;
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

    internal static async Task<int> Run(string[] args)
    {
        try
        {
            if (args.SequenceEqual(new[] { "self-test" })) { SelfTest(); return 0; }
            var local = new LocalState(State);
            if (args.SequenceEqual(new[] { "queue", "status" }))
            {
                var active = local.Active();
                Console.WriteLine(JsonSerializer.Serialize(new { pending = local.Pending(), activeOrNeedsReview = active?.Invitation }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("An active reservation is not proof of a running process. Inspect stopped runs before recovery; reservations never expire automatically.");
                return 0;
            }
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
                local.Enroll(instanceId.ToString(), instance);
                return 0;
            }
            if (args.Length == 3 && args[0] == "instance" && args[1] == "remove")
            {
                local.RemoveInstance(Guid.ParseExact(args[2], "D").ToString());
                return 0;
            }
            if (args.Length != 1) { Console.WriteLine("gdap-acceptor <invitation-uri> | instance add <instance-id> <https-origin> <partner-tenant-id> | instance remove <instance-id> | queue status | diagnostics export <new-file> | self-test"); return 2; }
            var invitation = ParseInvitation(args[0]);
            if (!local.Enqueue(invitation)) { Console.WriteLine("This invitation is already queued or active. Use queue status to inspect it; no acceptance was started here."); return 1; }
            Console.WriteLine("Waiting for the local acceptance window (up to ten minutes). Use queue status to inspect active or interrupted work.");
            var waiting = Stopwatch.StartNew();
            while (waiting.Elapsed < TimeSpan.FromMinutes(10) && local.Pending().Contains(invitation))
            {
                using var claim = local.TryClaim(invitation);
                if (claim is null) { await Task.Delay(500); continue; }
                var accepted = await Accept(claim.Active.Invitation, claim.Active.Instance);
                // An exception or process death leaves the durable claim intact.
                // A normal return means preflight stopped or the child exited.
                local.Complete(claim);
                return accepted ? 0 : 1;
            }
            Console.WriteLine("The pending invitation expired or is no longer available. Acceptance was not confirmed.");
            return 1;
        }
        catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }
        catch { Console.Error.WriteLine("GDAP Acceptor stopped. Check prerequisites, configuration and queue status. Legacy pending work or an interrupted acceptance requires operator review; no automatic reset was attempted."); return 1; }
    }

    private static async Task<bool> Accept(Invitation invitation, Instance instance)
    {
        var baseUrl = ValidateBaseUrl(instance.BaseUrl);
        var partner = Guid.ParseExact(instance.PartnerTenantId, "D");
        if (partner == Guid.Empty) throw new ArgumentException("Invalid enrolled partner.");
        Console.WriteLine($"CIPP: {baseUrl}\nPartner tenant: {partner}\nRelationship: {invitation.RelationshipId}");
        Console.Write("Expected CUSTOMER tenant ID (before authentication): ");
        if (!Guid.TryParseExact(Console.ReadLine(), "D", out var customer) || customer == Guid.Empty) { Console.WriteLine("A customer tenant ID is required. No authentication was started."); return false; }
        var fallback = $"https://admin.microsoft.com/AdminPortal/Home#/partners/invitation/granularAdminRelationships/{Uri.EscapeDataString(invitation.RelationshipId)}";
        Console.WriteLine($"Microsoft fallback: {fallback}");
        var pwsh = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe")
            : "/usr/bin/pwsh";
        if (!File.Exists(pwsh)) { Console.WriteLine("PowerShell 7 is required at its standard installation location. No authentication was started."); return false; }
        var wrapper = Path.Combine(AppContext.BaseDirectory, "scripts", "Invoke-Acceptance.ps1");
        if (!File.Exists(wrapper)) { Console.WriteLine("The bundled acceptance payload is missing. Install a complete package. No authentication was started."); return false; }
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
