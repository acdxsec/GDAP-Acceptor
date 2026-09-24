using System.Diagnostics;
using System.Runtime.InteropServices;

// Migration-only deletion. No read, write or upload of legacy CIPP secrets.
internal sealed class OsCredentialVault : ILegacyCredentialStore
{
    public async Task Delete(string key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            if (!CredDelete(key, 1, 0) && Marshal.GetLastWin32Error() != 1168) throw new IOException();
            return;
        }
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/secret-tool")) throw new IOException();
        var start = new ProcessStartInfo("/usr/bin/secret-tool") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "clear", "application", "gdap-acceptor", "connection", key }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException();
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token);
            await output;
            var errorText = await error;
            if (process.ExitCode != 0 && !(process.ExitCode == 1 && errorText.Length == 0)) throw new IOException();
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            try { await output; await error; } catch (OperationCanceledException) { }
        }
    }
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
}
