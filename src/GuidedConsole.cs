// A scrolling, keyboard-driven interface: no alternate-screen tricks or raw key
// reads, so PowerShell can own its confirmation prompts in the same terminal.
internal static class GuidedConsole
{
    internal static async Task<int> Run(LocalState local, Func<string[], Task<int>> execute)
    {
        var lastCode = 0;
        var lastAction = "Ready. No customer authentication has started.";
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("============================================================");
            Console.WriteLine(" GDAP ACCEPTOR | Operator workspace");
            Console.WriteLine("============================================================");
            Console.WriteLine(lastAction);
            Console.WriteLine("1. Accept invitation");
            Console.WriteLine("2. Queue and recovery");
            Console.WriteLine("3. CIPP connection settings");
            Console.WriteLine("4. Export diagnostics");
            Console.WriteLine("5. Open CIPP onboarding");
            Console.WriteLine("6. Create/resume invitation through CIPP, then accept");
            Console.WriteLine("0. Exit");
            Console.Write("Choose an action (or paste a Microsoft invitation): ");
            var choice = Console.ReadLine()?.Trim();
            if (choice is null) return lastCode;
            if (choice == "0") return 0;
            if (choice == "1")
            {
                Console.WriteLine("ACCEPT INVITATION | Step 1 of 4: choose the invitation");
                Console.Write("Paste the GDAP invitation URL from CIPP (blank to return): ");
                choice = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(choice)) continue;
                await AcceptInvitation(choice);
                continue;
            }
            if (choice == "2")
            {
                try
                {
                    var active = local.Active();
                    var pending = local.Pending();
                    Console.WriteLine("QUEUE AND RECOVERY | Local state, not CIPP task status");
                    Console.WriteLine($"Pending invitations: {pending.Count}");
                    foreach (var item in pending) Console.WriteLine($"  {Text(item.RelationshipId)} (instance {Text(item.InstanceId)})");
                    if (active is null) Console.WriteLine("No active reservation needs review.");
                    else
                    {
                        Console.WriteLine($"Active or needs review: {Text(active.Invitation.RelationshipId)}");
                        Console.WriteLine($"CIPP: {Text(active.Instance.BaseUrl)} | Partner: {Text(active.Instance.PartnerTenantId)}");
                        Console.WriteLine("A reservation does not prove a process is running or approval succeeded.");
                        Console.Write("R. Review and resolve this reservation | Enter to return: ");
                        if (string.Equals(Console.ReadLine()?.Trim(), "r", StringComparison.OrdinalIgnoreCase))
                            lastCode = await execute(["queue", "resolve"]);
                    }
                    lastAction = "Queue review finished. No invitation was automatically retried.";
                }
                catch
                {
                    lastCode = 1;
                    lastAction = "Could not read local queue state. It was not reset. Stop prior sessions and inspect local state before retrying.";
                }
            }
            else if (choice == "3")
            {
                try
                {
                    Console.WriteLine("CIPP CONNECTION SETTINGS | Saved connections");
                    var instances = local.ReadInstances();
                    if (instances.Count == 0) Console.WriteLine("No CIPP connection is enrolled yet.");
                    foreach (var item in instances.OrderBy(pair => pair.Key))
                        Console.WriteLine($"{Text(item.Value.BaseUrl)} | Partner: {Text(item.Value.PartnerTenantId)}");
                    Console.WriteLine("These settings do not configure CIPP Automated Onboarding or store customer credentials.");
                    Console.WriteLine("A. Add a trusted CIPP origin | C. Configure invitation connector | D. Disconnect connector settings | L. Remove legacy local API credential");
                    Console.Write("Choose a settings action, or Enter to return: ");
                    var setting = Console.ReadLine()?.Trim().ToLowerInvariant();
                    if (setting == "a") lastCode = await execute(["configure"]);
                    else if (setting == "c") lastCode = await execute(["connector", "configure"]);
                    else if (setting == "d") lastCode = await execute(["cipp", "disconnect"]);
                    else if (setting == "l") lastCode = await execute(["cipp", "remove-legacy-credential"]);
                    lastAction = "Settings finished. No customer authentication was started.";
                }
                catch
                {
                    lastCode = 1;
                    lastAction = "Could not read connection settings. Existing enrollment was not replaced.";
                }
            }
            else if (choice == "4")
            {
                Console.WriteLine("EXPORT DIAGNOSTICS | Local state events only; not browser or CIPP logs");
                Console.Write("New output file path (blank to return; existing files are never overwritten): ");
                var path = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(path)) continue;
                lastCode = await execute(["diagnostics", "export", path]);
                lastAction = lastCode == 0
                    ? "Diagnostics exported. Review identifiers before sharing the file."
                    : "Export did not complete. Check the destination and choose a new file path.";
            }
            else if (choice == "5")
            {
                Console.WriteLine("CIPP ONBOARDING | Opens the relationship page in your default browser; no job submission");
                Console.Write("Paste the Microsoft invitation URL to match (blank to return): ");
                var invitation = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(invitation)) continue;
                lastCode = await execute(["cipp", "open", invitation]);
                lastAction = "CIPP page handoff finished. GDAP access was not changed.";
            }
            else if (choice == "6")
            {
                lastCode = await execute(["create"]);
                lastAction = "Create/resume workflow finished. Review its result above; uncertain requests are not retried.";
            }
            else if (choice.StartsWith("https://", StringComparison.Ordinal))
                await AcceptInvitation(choice);
            else lastAction = "Select a listed action or paste a full Microsoft invitation URL.";
        }

        async Task AcceptInvitation(string invitation)
        {
            // Validate before command dispatch: text pasted at a subprompt must
            // never become a launcher command (including configure/self-test).
            try { Acceptor.ParseMicrosoftInvitation(invitation); }
            catch (ArgumentException error) { lastCode = 2; lastAction = error.Message; return; }
            lastCode = await execute([invitation]);
            lastAction = lastCode == 0
                ? "GDAP active. Continue on the CIPP onboarding page. Do not repeat approval."
                : "Acceptance not confirmed. Inspect Queue and recovery before retrying.";
        }
    }

    private static string Text(string value) => System.Text.RegularExpressions.Regex.Replace(value, "[\\p{Cc}\\p{Cf}]", "");
}
