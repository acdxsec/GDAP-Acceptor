using System.Text.Json;
using Gdap.Status;

namespace Gdap.Server;

internal sealed record InvitationAttempt(string StaffId, string CippOrigin, string PartnerTenantId, CreateInvitation Request, InviteRole[] Roles);

// This directory MUST be a dedicated persistent shared filesystem, never an
// ACA container's ephemeral disk. No expiry or automatic reset: uncertain writes
// must never turn back into permission to create another relationship.
internal sealed class InvitationJournal(ServiceSettings settings)
{
    private string PathFor(string operation, string suffix)
    {
        if (settings.InvitationJournalDirectory is null) throw new InvalidOperationException("Invitation creation is disabled until persistent storage is configured.");
        if (StatusProtocol.GuidValue(operation) != operation) throw new InvalidDataException();
        if (!Directory.Exists(settings.InvitationJournalDirectory)) throw new IOException();
        return Path.Combine(settings.InvitationJournalDirectory, operation + suffix);
    }
    internal bool Reserve(InvitationAttempt attempt)
    {
        var path = PathFor(attempt.Request.OperationId, ".attempt.json");
        try { WriteNew(path, attempt); return true; }
        catch (IOException) when (File.Exists(path)) { return false; }
    }
    internal InvitationAttempt Read(string operation, string staff)
    {
        var attempt = Read<InvitationAttempt>(PathFor(operation, ".attempt.json"));
        if (attempt.StaffId != staff || attempt.Request.OperationId != operation || attempt.CippOrigin != settings.CippOrigin ||
            attempt.PartnerTenantId != settings.PartnerTenantId) throw new InvalidDataException();
        return attempt;
    }
    internal CreatedInvitation? Result(string operation)
    {
        var path = PathFor(operation, ".result.json");
        return File.Exists(path) ? Read<CreatedInvitation>(path) : null;
    }
    internal void Complete(CreatedInvitation result)
    {
        var path = PathFor(result.OperationId, ".result.json");
        try { WriteNew(path, result); }
        catch (IOException) when (File.Exists(path)) { if (Read<CreatedInvitation>(path) != result) throw new InvalidDataException(); }
    }
    private static T Read<T>(string path)
    {
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException();
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException();
    }
    private static void WriteNew<T>(string path, T value)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(file, value);
        file.Flush(flushToDisk: true);
    }
}
