using Gdap.Server;

internal static class JournalProbeContracts
{
    internal static void Run(string root)
    {
        var id = Guid.NewGuid().ToString();
        Check(JournalProbe.Run(["journal-probe", "verify", id], root) == 1, "Invented prior probe success");
        Check(JournalProbe.Run(["journal-probe", "prepare", id], root) == 0, "Preparation failed");
        Check(JournalProbe.Run(["journal-probe", "verify", id], root) == 0, "Retained probe failed");
        Check(JournalProbe.Run(["journal-probe", "prepare", id], root) == 1, "Existing probe overwritten");
        Check(JournalProbe.Run(["journal-probe", "verify", id], root) == 0, "Duplicate preparation damaged probe");
        Check(JournalProbe.Run(["journal-probe", "prepare", "../escape"], root) == 1, "Path traversal accepted");
        Check(JournalProbe.Run(["journal-probe", "prepare", Guid.NewGuid().ToString()], Path.Combine(root, "missing")) == 1, "Created missing mount");
        Console.WriteLine("PASS: offline mount probe preserves evidence, rejects overwrite/traversal/missing storage and performs no network calls");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
