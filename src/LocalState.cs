using System.Diagnostics;
using System.Text.Json;

internal sealed class LocalState
{
    private readonly string root;
    private readonly TimeProvider clock;
    internal LocalState(string root, TimeProvider? clock = null)
    {
        this.root = Path.GetFullPath(root);
        this.clock = clock ?? TimeProvider.System;
        Directory.CreateDirectory(this.root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(this.root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal void Enroll(string id, Instance instance)
    {
        using var lease = Lock();
        var instances = Read<Dictionary<string, Instance>>("instances.json") ?? new();
        if (instances.TryGetValue(id, out var old) && old != instance)
            throw new ArgumentException("Instance is already enrolled with a different URL or partner. Remove it explicitly before changing its identity.");
        instances[id] = instance;
        Write("instances.json", instances);
    }

    internal Dictionary<string, Instance> ReadInstances()
    {
        using var lease = Lock();
        return Read<Dictionary<string, Instance>>("instances.json") ?? new();
    }

    internal void RemoveInstance(string id)
    {
        using var lease = Lock();
        var instances = Read<Dictionary<string, Instance>>("instances.json") ?? new();
        instances.Remove(id);
        Write("instances.json", instances);
    }

    internal bool Enqueue(Invitation invitation)
    {
        invitation = Acceptor.ParseInvitation($"gdap-acceptor://v1/accept/{invitation.InstanceId}/{invitation.RelationshipId}");
        using var lease = Lock();
        CheckLegacyQueue();
        var instances = Read<Dictionary<string, Instance>>("instances.json") ?? new();
        if (!instances.ContainsKey(invitation.InstanceId)) throw new ArgumentException("CIPP instance is not enrolled. Run instance add first.");
        if (Read<ActiveAcceptance>("active-v1.json")?.Invitation == invitation) return false;
        var pending = ReadPending();
        if (pending.Any(entry => entry.Invitation == invitation)) return false;
        if (pending.Count >= 20) throw new ArgumentException("The local invitation queue is full.");
        pending.Add(new PendingInvitation(invitation, clock.GetUtcNow(), instances[invitation.InstanceId]));
        Write("queue-v1.json", pending);
        return true;
    }

    internal IReadOnlyList<Invitation> Pending()
    {
        using var lease = Lock();
        CheckLegacyQueue();
        return ReadPending().Select(entry => entry.Invitation).ToArray();
    }

    internal ActiveAcceptance? Active()
    {
        using var lease = Lock();
        CheckLegacyQueue();
        return Read<ActiveAcceptance>("active-v1.json");
    }

    internal AcceptanceLease? TryClaim(Invitation invitation)
    {
        using var lease = Lock();
        CheckLegacyQueue();
        if (Read<ActiveAcceptance>("active-v1.json") is not null) return null;
        var pending = ReadPending();
        var queued = pending.SingleOrDefault(entry => entry.Invitation == invitation);
        if (queued is null) return null;
        var instances = Read<Dictionary<string, Instance>>("instances.json") ?? new();
        if (!instances.TryGetValue(invitation.InstanceId, out var instance)) throw new ArgumentException("Queued instance was removed.");
        if (queued.Instance != instance) throw new ArgumentException("Enrollment changed after this invitation was queued. Let it expire and launch again.");
        FileStream execution;
        try { execution = new FileStream(Path.Combine(root, "acceptance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
        try
        {
            var active = new ActiveAcceptance(Guid.NewGuid(), invitation, instance);
            // Persist the reservation before removing pending work. A crash in
            // between is conservative: active blocks replay until investigated.
            Write("active-v1.json", active);
            pending.RemoveAll(entry => entry.Invitation == invitation);
            Write("queue-v1.json", pending);
            return new AcceptanceLease(active, execution);
        }
        catch { execution.Dispose(); throw; }
    }

    internal void Complete(AcceptanceLease claim)
    {
        using var lease = Lock();
        var active = Read<ActiveAcceptance>("active-v1.json");
        if (claim.IsDisposed || active?.Token != claim.Active.Token) throw new InvalidOperationException("Acceptance reservation does not match.");
        var pending = ReadPending();
        pending.RemoveAll(entry => entry.Invitation == active.Invitation);
        Write("queue-v1.json", pending);
        File.Delete(Path.Combine(root, "active-v1.json"));
        claim.Dispose();
    }

    private List<PendingInvitation> ReadPending() => (Read<List<PendingInvitation>>("queue-v1.json") ?? new())
        .Where(entry => clock.GetUtcNow() - entry.AddedAt < TimeSpan.FromMinutes(10) && entry.AddedAt <= clock.GetUtcNow()).ToList();

    private FileStream Lock()
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(Path.Combine(root, "state.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(5)) { Thread.Sleep(20); }
        }
    }

    private void CheckLegacyQueue()
    {
        var legacy = Path.Combine(root, "queue");
        if (Directory.Exists(legacy) && Directory.EnumerateFiles(legacy, "*.json").Any())
            throw new InvalidOperationException("Legacy pending work requires operator review before using this version.");
    }

    private T? Read<T>(string name) where T : class
    {
        var path = Path.Combine(root, name);
        return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException("Local state is invalid; inspect it before retrying.") : null;
    }

    private void Write<T>(string name, T value)
    {
        var temporary = Path.Combine(root, ".state-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(file, value);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path.Combine(root, name), overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}

internal sealed record PendingInvitation(Invitation Invitation, DateTimeOffset AddedAt, Instance Instance);

internal sealed record ActiveAcceptance(Guid Token, Invitation Invitation, Instance Instance);

internal sealed class AcceptanceLease(ActiveAcceptance active, FileStream execution) : IDisposable
{
    internal ActiveAcceptance Active { get; } = active;
    internal bool IsDisposed { get; private set; }
    // Releasing an OS handle is not proof that a child process/approval stopped.
    // Only Complete removes the durable reservation.
    public void Dispose() { IsDisposed = true; execution.Dispose(); }
}
