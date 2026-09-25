using Gdap.Status;

namespace Gdap.Server;

// Operator-invoked, offline mount check. It cannot authenticate, call CIPP,
// approve GDAP, delete files or alter a real operation's journal directory.
internal static class JournalProbe
{
    internal static int Run(string[] arguments, string mount = "/var/lib/gdap-journal")
    {
        try
        {
            if (arguments.Length != 3 || arguments[0] != "journal-probe" || arguments[1] is not ("prepare" or "verify")) throw new InvalidDataException();
            var id = StatusProtocol.GuidValue(arguments[2]);
            if (id != arguments[2] || !Directory.Exists(mount)) throw new InvalidDataException();
            var directory = Path.Combine(mount, ".probe-" + id);
            const string partner = "11111111-1111-1111-1111-111111111111";
            const string staff = "22222222-2222-2222-2222-222222222222";
            const string origin = "https://probe.invalid";
            var settings = new ServiceSettings { InvitationJournalDirectory = directory, CippOrigin = origin, PartnerTenantId = partner };
            var journal = new InvitationJournal(settings);
            var request = new CreateInvitation(id, "OFFLINE MOUNT PROBE", new string('a', 64), "No CIPP request");
            var attempt = new InvitationAttempt(staff, origin, partner, request, []);
            var relationship = id + "-" + partner;
            var expected = new CreatedInvitation(1, id, origin, partner, relationship,
                InvitationProtocol.MicrosoftPrefix + relationship, InvitationProtocol.Onboarding(origin, relationship));
            if (arguments[1] == "prepare")
            {
                Directory.CreateDirectory(directory);
                var winners = 0;
                Parallel.For(0, 16, _ => { if (journal.Reserve(attempt)) Interlocked.Increment(ref winners); });
                if (winners != 1) throw new IOException();
                journal.Complete(expected);
            }
            var saved = journal.Read(id, staff);
            if (saved.Request != request || saved.Roles.Length != 0 || journal.Result(id) != expected || journal.Reserve(attempt)) throw new IOException();
            Console.WriteLine(arguments[1] == "prepare"
                ? "PROBE PREPARED: one reservation won; flushed test data retained. Verify this same ID after revision replacement. No CIPP call made."
                : "PROBE VERIFIED: retained data matches and cannot be reserved again. No CIPP call made.");
            return 0;
        }
        catch
        {
            Console.Error.WriteLine("PROBE FAILED: keep invitation creation disabled. Check mount, permissions and probe ID; no existing record was removed or reset.");
            return 1;
        }
    }
}
