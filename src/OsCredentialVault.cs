using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

// No plaintext-file fallback. The current user's OS vault is the trust boundary;
// software running as that user can access it. Nothing is shared in the package.
internal sealed class OsCredentialVault : ICredentialVault
{
    public Task<string?> Read(string key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return SecretTool("lookup", key, null, token);
        if (!CredRead(key, 1, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == 1168) return Task.FromResult<string?>(null);
            throw new IOException("Credential vault unavailable.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.BlobSize > 2560) throw new InvalidDataException();
            var bytes = new byte[credential.BlobSize];
            try { Marshal.Copy(credential.Blob, bytes, 0, bytes.Length); return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }

    public async Task Write(string key, string value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            if (bytes.Length > 2560) throw new InvalidDataException("Credential exceeds vault capacity.");
            if (!OperatingSystem.IsWindows()) { await SecretTool("store", key, value, token); return; }
            var pointer = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                var credential = new Credential { Type = 1, TargetName = key, BlobSize = (uint)bytes.Length, Blob = pointer, Persist = 2, UserName = "GDAP Acceptor CIPP API" };
                if (!CredWrite(ref credential, 0)) throw new IOException("Credential vault unavailable.");
            }
            finally { Marshal.Copy(new byte[bytes.Length], 0, pointer, bytes.Length); Marshal.FreeHGlobal(pointer); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public async Task Delete(string key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) { await SecretTool("clear", key, null, token); return; }
        if (!CredDelete(key, 1, 0) && Marshal.GetLastWin32Error() != 1168) throw new IOException("Credential vault unavailable.");
    }

    private static async Task<string?> SecretTool(string operation, string key, string? value, CancellationToken token)
    {
        const string executable = "/usr/bin/secret-tool";
        if (!OperatingSystem.IsLinux() || !File.Exists(executable)) throw new IOException("An operating system credential vault is required.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(operation);
        if (operation == "store") start.ArgumentList.Add("--label=GDAP Acceptor CIPP status");
        start.ArgumentList.Add("application"); start.ArgumentList.Add("gdap-acceptor");
        start.ArgumentList.Add("connection"); start.ArgumentList.Add(key);
        using var process = Process.Start(start) ?? throw new IOException();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var errors = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            // The credential is passed only on stdin, never in argv or env.
            if (value is not null) await process.StandardInput.WriteAsync(value.AsMemory(), deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            var text = await output;
            var error = await errors;
            if (process.ExitCode == 1 && operation == "lookup" && error.Length == 0) return null;
            if (process.ExitCode != 0 || text.Length > 4096) throw new IOException("Credential vault unavailable.");
            return operation == "lookup" ? text.TrimEnd('\r', '\n') : null;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            try { await output; await errors; } catch (OperationCanceledException) { }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr buffer);
}
