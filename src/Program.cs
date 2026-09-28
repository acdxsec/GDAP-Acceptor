using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

var result = await Acceptor.Run(args);
if ((args.Length == 1 && args[0].StartsWith("gdap-acceptor://", StringComparison.Ordinal)) && !Console.IsInputRedirected)
{
    Console.WriteLine("Press Enter to close this window.");
    Console.ReadLine();
}
return result;

internal sealed record Instance(string BaseUrl, string PartnerTenantId);
internal sealed record Invitation(string InstanceId, string RelationshipId);
internal enum AcceptanceOutcome { Stopped, Active, NeedsReview }

internal static class Acceptor
{
    private static readonly string State = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "gdap-acceptor");
    private static readonly Regex IdPattern = new("\\A[A-Za-z0-9][A-Za-z0-9_-]{0,255}\\z", RegexOptions.CultureInvariant);

    internal static string ParseMicrosoftInvitation(string text)
    {
        const string prefix = "https://admin.microsoft.com/AdminPortal/Home#/partners/invitation/granularAdminRelationships/";
        text = text.Trim();
        if (text.Length > 1024 || !text.StartsWith(prefix, StringComparison.Ordinal))
            throw new ArgumentException("Paste the full Microsoft GDAP invitation URL copied from CIPP.");
        var id = text[prefix.Length..];
        if (!IdPattern.IsMatch(id)) throw new ArgumentException("Invalid Microsoft invitation relationship ID.");
        return id;
    }

    internal static string OnboardingUrl(Instance instance) => ValidateBaseUrl(instance.BaseUrl) + "/tenant/gdap-management/onboarding";
    internal static string OnboardingUrl(Instance instance, string relationship) => Gdap.Status.InvitationProtocol.Onboarding(ValidateBaseUrl(instance.BaseUrl), relationship);

    private static string Configure(LocalState local)
    {
        Console.WriteLine("One-time local setup. This does not change CIPP or create a GDAP invitation.");
        Console.Write("CIPP URL (https://your-cipp-host): ");
        var origin = ValidateBaseUrl(Console.ReadLine() ?? "");
        Console.Write("Your PARTNER tenant ID (not the customer): ");
        if (!Guid.TryParseExact(Console.ReadLine(), "D", out var partner) || partner == Guid.Empty)
            throw new ArgumentException("A valid partner tenant ID is required.");
        Console.WriteLine($"Trust {origin} for partner {partner}? Type TRUST to save:");
        if (Console.ReadLine() != "TRUST") throw new ArgumentException("Setup cancelled. Nothing was enrolled.");
        var instance = new Instance(origin, partner.ToString());
        var existing = local.ReadInstances().FirstOrDefault(pair => pair.Value == instance);
        var id = existing.Key ?? Guid.NewGuid().ToString();
        local.Enroll(id, instance);
        Console.WriteLine("Saved locally. CIPP Automated Onboarding must already be enabled; this launcher does not configure it.");
        return id;
    }

    private static string SelectInstance(LocalState local)
    {
        var instances = local.ReadInstances().OrderBy(pair => pair.Key).ToArray();
        if (instances.Length == 0) return Configure(local);
        if (instances.Length == 1) return instances[0].Key;
        for (var i = 0; i < instances.Length; i++)
            Console.WriteLine($"{i + 1}. {instances[i].Value.BaseUrl} (partner {instances[i].Value.PartnerTenantId})");
        Console.Write("Select the enrolled CIPP instance number: ");
        if (!int.TryParse(Console.ReadLine(), out var index) || index < 1 || index > instances.Length)
            throw new ArgumentException("No valid CIPP instance selected. No authentication was started.");
        return instances[index - 1].Key;
    }

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
        using var cipp = new CippStatus(State);
        using var creationConnection = new CippStatus(State, new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false }, new StaffAuthentication().InvitationToken, new OsCredentialVault());
        return await Run(args, State, (invitation, instance) => Accept(invitation, instance, State), cipp, new CippInvitations(State, creationConnection), OpenBrowser);
    }

    // Tests supply isolated state and an acceptance adapter; no CLI/environment
    // override can redirect the production payload or silently select a tenant.
    internal static async Task<int> Run(string[] args, string stateDirectory, Func<Invitation, Instance, Task<AcceptanceOutcome>> accept, CippStatus? cipp = null, CippInvitations? invitations = null, Action<string>? openBrowser = null)
    {
        try
        {
            if (args.SequenceEqual(new[] { "self-test" })) { SelfTest(); return 0; }
            if (args.SequenceEqual(new[] { "--help" }) || args.SequenceEqual(new[] { "-h" }))
            {
                Console.WriteLine("gdap-acceptor [<Microsoft-invitation-url>] | configure | queue status | queue resolve | diagnostics export <new-file> | self-test");
                Console.WriteLine("With no arguments: open the guided workspace for acceptance, queue/recovery, settings and diagnostics. You can still paste an invitation at its home prompt. Customer identity is confirmed after fresh browser sign-in.");
                Console.WriteLine("Invitation workflow: create | connector configure | cipp open <Microsoft-invitation-url>. Existing-invitation acceptance needs no connector.");
                return 0;
            }
            var local = new LocalState(stateDirectory);
            if (args.Length == 0) return await GuidedConsole.Run(local, command => Run(command, stateDirectory, accept, cipp, invitations, openBrowser));
            if (args.SequenceEqual(new[] { "connector", "configure" }) || args.SequenceEqual(new[] { "create" }))
            {
                if (invitations is null) { Console.WriteLine("Invitation connector is unavailable."); return 1; }
                var id = SelectInstance(local);
                var instance = local.ReadInstances()[id];
                return args[0] == "connector" ? await invitations.Configure(id, instance) : await invitations.Run(id, instance,
                    (invite, _) => Run([$"gdap-acceptor://v1/accept/{invite.InstanceId}/{invite.RelationshipId}"], stateDirectory, accept, cipp, invitations, openBrowser));
            }
            if (args.Length == 3 && args[0] == "cipp" && args[1] == "open")
            {
                var relationship = ParseMicrosoftInvitation(args[2]);
                OpenOnboarding(local.ReadInstances()[SelectInstance(local)], relationship, openBrowser);
                return 0;
            }
            if (cipp is not null && args.SequenceEqual(new[] { "cipp", "configure" }))
            {
                var id = SelectInstance(local);
                return await cipp.Configure(id, local.ReadInstances()[id]);
            }
            if (cipp is not null && args.SequenceEqual(new[] { "cipp", "disconnect" }))
                return await cipp.Disconnect(SelectInstance(local));
            if (cipp is not null && args.SequenceEqual(new[] { "cipp", "remove-legacy-credential" }))
                return await cipp.RemoveLegacyCredential(SelectInstance(local));
            if (cipp is not null && args.Length == 3 && args[0] == "cipp" && args[1] is "status" or "watch")
            {
                var relationship = ParseMicrosoftInvitation(args[2]);
                var id = SelectInstance(local);
                return args[1] == "watch"
                    ? await cipp.Watch(new Invitation(id, relationship), local.ReadInstances()[id])
                    : await cipp.Check(new Invitation(id, relationship), local.ReadInstances()[id]);
            }
            if (args.SequenceEqual(new[] { "configure" })) { Configure(local); return 0; }
            if (args.SequenceEqual(new[] { "queue", "status" }))
            {
                var active = local.Active();
                Console.WriteLine(JsonSerializer.Serialize(new { pending = local.Pending(), activeOrNeedsReview = active?.Invitation }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("An active reservation is not proof of a running process. After stopping the prior run and reviewing its outcome, use queue resolve. Reservations never expire automatically.");
                return 0;
            }
            if (args.SequenceEqual(new[] { "queue", "resolve" }))
            {
                var active = local.Active();
                if (active is null) { Console.WriteLine("No active reservation needs review. Nothing changed."); return 0; }
                Console.WriteLine($"Reserved relationship: {active.Invitation.RelationshipId}\nCIPP: {active.Instance.BaseUrl}\nPartner tenant: {active.Instance.PartnerTenantId}");
                Console.WriteLine("Stop the previous launcher AND its PowerShell/browser session first. Confirm the relationship's outcome in Microsoft/CIPP; do not retry an uncertain approval.");
                Console.WriteLine("Type RESOLVED only after reviewing that outcome. This archives the local reservation; it does not approve, revoke, retry or start onboarding.");
                if (Console.ReadLine() != "RESOLVED") { Console.WriteLine("Recovery cancelled. Reservation unchanged."); return 1; }
                try { local.ResolveReviewed(active); }
                catch (IOException) { Console.WriteLine("The acceptance lock is busy or state could not be archived. No automatic reset was attempted."); return 1; }
                catch (InvalidOperationException) { Console.WriteLine("The reservation changed during review. Nothing was cleared; inspect queue status again."); return 1; }
                Console.WriteLine("Reviewed reservation archived locally. No invitation was replayed. Run gdap-acceptor for a new invitation.");
                return 0;
            }
            if (args.Length == 3 && args[0] == "diagnostics" && args[1] == "export")
            {
                using var output = new FileStream(Path.GetFullPath(args[2]), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                foreach (var log in Directory.EnumerateFiles(stateDirectory, "diagnostics-????-??-??.jsonl").Order())
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
            if (args.Length > 1) { Console.WriteLine("Run gdap-acceptor --help for usage."); return 2; }
            var pastedInput = args[0];
            Invitation invitation;
            if (pastedInput.StartsWith("gdap-acceptor://", StringComparison.Ordinal)) invitation = ParseInvitation(pastedInput);
            else
            {
                var relationship = ParseMicrosoftInvitation(pastedInput);
                invitation = new Invitation(SelectInstance(local), relationship);
            }
            if (!local.Enqueue(invitation)) { Console.WriteLine("This invitation is already queued or active. Use queue status to inspect it; no acceptance was started here. For a stopped, reviewed run, use queue resolve."); return 1; }
            Console.WriteLine("Waiting for the local acceptance window (up to ten minutes). Use queue status to inspect active or interrupted work.");
            var waiting = Stopwatch.StartNew();
            while (waiting.Elapsed < TimeSpan.FromMinutes(10) && local.Pending().Contains(invitation))
            {
                using var claim = local.TryClaim(invitation);
                if (claim is null) { await Task.Delay(500); continue; }
                var accepted = await accept(claim.Active.Invitation, claim.Active.Instance);
                // Only a known preflight stop or verified active result can
                // clear state. Unknown outcomes and abnormal child exits need
                // explicit operator review, even when the child has stopped.
                if (accepted is AcceptanceOutcome.Stopped or AcceptanceOutcome.Active) local.Complete(claim);
                else Console.WriteLine("Acceptance outcome requires review. Reservation retained. Inspect Microsoft/CIPP, then use queue resolve; do not retry approval.");
                // Completion hands off to CIPP's own page, never staff status polling.
                if (accepted == AcceptanceOutcome.Active)
                {
                    Console.WriteLine("CIPP onboarding has NOT been verified here. Existing CIPP automation handles the approval event; do not approve again or submit another onboarding job.");
                    OpenOnboarding(claim.Active.Instance, claim.Active.Invitation.RelationshipId, openBrowser);
                }
                return accepted == AcceptanceOutcome.Active ? 0 : 1;
            }
            Console.WriteLine("The pending invitation expired or is no longer available. Acceptance was not confirmed.");
            return 1;
        }
        catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }
        catch { Console.Error.WriteLine("GDAP Acceptor stopped. Check prerequisites, configuration and queue status. Legacy pending work or an interrupted acceptance requires operator review; no automatic reset was attempted."); return 1; }
    }

    internal static ProcessStartInfo AcceptanceCommand(string pwsh, string wrapper, Invitation invitation, Instance instance)
    {
        var start = new ProcessStartInfo(pwsh) { UseShellExecute = false };
        foreach (var value in new[] { "-NoLogo", "-NoProfile", "-File", wrapper, "-RelationshipId", invitation.RelationshipId,
            "-ConfirmAuthenticatedTenant", "-ExpectedPartnerTenantId", instance.PartnerTenantId }) start.ArgumentList.Add(value);
        return start;
    }

    internal static AcceptanceOutcome ClassifyAcceptanceExit(int exitCode) => exitCode switch
    {
        0 => AcceptanceOutcome.Active,
        2 => AcceptanceOutcome.Stopped,
        _ => AcceptanceOutcome.NeedsReview // Includes 3 and unexpected/crashed-child exits.
    };

    private static async Task<AcceptanceOutcome> Accept(Invitation invitation, Instance instance, string stateDirectory)
    {
        var baseUrl = ValidateBaseUrl(instance.BaseUrl);
        var partner = Guid.ParseExact(instance.PartnerTenantId, "D");
        if (partner == Guid.Empty) throw new ArgumentException("Invalid enrolled partner.");
        Console.WriteLine("ACCEPT INVITATION | Step 2 of 4: sign in and confirm the customer");
        Console.WriteLine($"CIPP: {baseUrl}\nPartner tenant: {partner}\nRelationship: {invitation.RelationshipId}");
        Console.WriteLine("Sign in as the CUSTOMER administrator in the fresh private browser. Confirm the customer tenant in this terminal before the invitation opens.");
        var fallback = $"https://admin.microsoft.com/AdminPortal/Home#/partners/invitation/granularAdminRelationships/{Uri.EscapeDataString(invitation.RelationshipId)}";
        Console.WriteLine($"Microsoft fallback: {fallback}");
        var pwsh = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe")
            : "/usr/bin/pwsh";
        if (!File.Exists(pwsh)) { Console.WriteLine("PowerShell 7 is required at its standard installation location. No authentication was started."); return AcceptanceOutcome.Stopped; }
        var wrapper = Path.Combine(AppContext.BaseDirectory, "scripts", "Invoke-Acceptance.ps1");
        if (!File.Exists(wrapper)) { Console.WriteLine("The bundled acceptance payload is missing. Install a complete package. No authentication was started."); return AcceptanceOutcome.Stopped; }
        var start = AcceptanceCommand(pwsh, wrapper, invitation, instance);
        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        await process.WaitForExitAsync();
        var outcome = ClassifyAcceptanceExit(process.ExitCode);
        var status = outcome switch { AcceptanceOutcome.Active => "relationshipActive", AcceptanceOutcome.Stopped => "acceptanceStopped", _ => "acceptanceNeedsReview" };
        var log = Path.Combine(stateDirectory, "diagnostics-" + DateTime.UtcNow.ToString("yyyy-MM-dd") + ".jsonl");
        try
        {
            File.AppendAllText(log, JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, invitation.InstanceId, invitation.RelationshipId, status }) + "\n");
            foreach (var old in Directory.EnumerateFiles(stateDirectory, "diagnostics-????-??-??.jsonl")) if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-7)) File.Delete(old);
        }
        catch (IOException) { Console.WriteLine("Could not save local diagnostics. The acceptance result below is unchanged."); }
        catch (UnauthorizedAccessException) { Console.WriteLine("Could not save local diagnostics. The acceptance result below is unchanged."); }
        if (outcome != AcceptanceOutcome.Active) { Console.WriteLine("Acceptance not confirmed. Inspect the Microsoft relationship outcome before any further action."); return outcome; }
        Console.WriteLine("ACCEPT INVITATION | Step 4 of 4: return to CIPP");
        Console.WriteLine("GDAP relationship is ACTIVE.");
        Console.WriteLine("Existing CIPP Automated Onboarding processes Microsoft's approval event on its schedule; Microsoft propagation can add a further delay. Do not approve again.");
        return AcceptanceOutcome.Active;
    }

    private static void OpenBrowser(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private static void OpenOnboarding(Instance instance, string relationship, Action<string>? openBrowser)
    {
        var url = OnboardingUrl(instance, relationship);
        Console.WriteLine($"CIPP onboarding: {url}\nRelationship: {relationship}");
        Console.WriteLine("CIPP may need time to receive the webhook. Refresh its page if the record is not visible yet.");
        try { openBrowser?.Invoke(url); }
        catch { Console.WriteLine("Could not open the default browser. Use the CIPP URL above; do not repeat approval."); }
    }

    private static void SelfTest()
    {
        var composite = "5d027261-d21f-4aa9-b7db-7fa1f56fb163-8777b240-c6f0-4469-9e98-a3205431b836";
        var prefix = "gdap-acceptor://v1/accept/11111111-1111-1111-1111-111111111111/";
        if (ParseInvitation(prefix + composite).RelationshipId != composite) throw new Exception("Composite identifier lost");
        var microsoftPrefix = "https://admin.microsoft.com/AdminPortal/Home#/partners/invitation/granularAdminRelationships/";
        if (ParseMicrosoftInvitation(microsoftPrefix + composite) != composite) throw new Exception("Microsoft composite identifier lost");
        if (ParseMicrosoftInvitation("  " + microsoftPrefix + "Case-Sensitive_ID " ) != "Case-Sensitive_ID") throw new Exception("Pasted ID changed");
        foreach (var invalid in new[] { "http://admin.microsoft.com/", "https://evil.example/", microsoftPrefix + "a%2fb", microsoftPrefix + "../x", microsoftPrefix + "id?x=1", microsoftPrefix + "id#x", microsoftPrefix + "id\ninjected", microsoftPrefix + new string('a', 257) })
        { try { ParseMicrosoftInvitation(invalid); } catch (ArgumentException) { continue; } throw new Exception("Unsafe Microsoft invitation accepted"); }
        if (OnboardingUrl(new Instance("https://cipp.example/", "22222222-2222-2222-2222-222222222222")) != "https://cipp.example/tenant/gdap-management/onboarding") throw new Exception("Custom CIPP route returned");
        foreach (var invalid in new[] { "../x", "a%2fb", "id?x=1", "id#x", "id\n", "%252f", new string('a', 257) })
        { try { ParseInvitation(prefix + invalid); } catch (ArgumentException) { continue; } throw new Exception("Unsafe URI accepted"); }
        foreach (var invalid in new[] { "http://example.com", "https://user@example.com", "https://example.com/path", "https://example.com/?x=1" })
        { try { ValidateBaseUrl(invalid); } catch (ArgumentException) { continue; } throw new Exception("Unsafe origin accepted"); }
        Console.WriteLine("PASS: pasted Microsoft invitation, legacy URI, stock CIPP return and enrollment contracts");
    }
}
