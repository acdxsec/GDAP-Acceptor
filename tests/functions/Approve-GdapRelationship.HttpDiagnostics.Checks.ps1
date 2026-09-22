[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ModulePath,
    [string]$ApprovalScript = "$PSScriptRoot/../../scripts/Approve-GdapRelationship.ps1"
)
$ErrorActionPreference = 'Stop'
$helper = Join-Path (Split-Path $ApprovalScript) 'GdapHttpDiagnostics.ps1'
if (-not (Test-Path $helper)) { throw 'Missing bounded HTTP diagnostics helper' }
# An actual HTTP peer that accepts a connection but never sends response headers.
# Exercise the real upstream request function and PowerShell transport, not a
# mock that merely asserts a timeout parameter exists.
Add-Type @'
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
public sealed class GdapStalledPeer : IDisposable {
    readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    TcpClient client;
    public int Port { get; private set; }
    public GdapStalledPeer() {
        listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Accept();
    }
    async Task Accept() {
        try { client = await listener.AcceptTcpClientAsync(); }
        catch (ObjectDisposedException) {} catch (SocketException) {}
    }
    public void Dispose() { listener.Stop(); client?.Dispose(); }
}
'@
$module = Import-Module $ModulePath -PassThru
$peer = [GdapStalledPeer]::new()
$clock = [Diagnostics.Stopwatch]::StartNew()
try {
    $output = & $module {
        param($Helper, $Port)
        $before = Get-Command Invoke-WebRequest
        & {
            $state = @{ Sequence = 0; TimedOut = $false }
            . $Helper -TimeoutSeconds 1 -State $state
            $web = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
            $failure = $null
            try {
                $null = Invoke-M365PortalRequest -Uri "http://127.0.0.1:$Port/adminportal/home/ClassicModernAdminDataStream?SECRET_SENTINEL" -WebSession $web -Headers @{ Authorization = 'SECRET_SENTINEL' } -RawResponse -SkipAutoHeal -SkipConnectionRefresh
            } catch { $failure = $_.Exception.Message }
            # Upstream adds the URI to its exception; the owned entry point
            # already suppresses that exception. Only emitted progress is safe
            # to show, and is checked below independently of the caught error.
            if (-not $failure) { throw 'Missing transport failure' }
            if (-not $state.TimedOut -or $state.Sequence -ne 1) { throw 'Stalled request was not recorded as a single timed-out attempt' }
            try { $null = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/" } catch { }
            if ($state.Sequence -ne 1) { throw 'A request was retried after timeout' }
        }
        if ((Get-Command Invoke-WebRequest) -ne $before) { throw 'Diagnostic transport escaped its call scope' }
    } $helper $peer.Port 6>&1
    $messages = ($output | Where-Object { $_ -is [System.Management.Automation.InformationRecord] } | ForEach-Object ToString) -join "`n"
    if ($messages -notmatch 'GDAP HTTP #1.*Starting.*tenant bootstrap' -or $messages -notmatch 'GDAP HTTP #1.*timed out') { throw 'Missing request start/timeout progress' }
    if ($messages -match 'SECRET_SENTINEL|127\.0\.0\.1|Authorization') { throw 'Unsafe diagnostic output' }
    if ($clock.Elapsed.TotalSeconds -gt 8) { throw 'Stalled HTTP transport exceeded its test deadline' }
    Write-Host $messages
    Write-Output 'PASS: real stalled HTTP request stops, emits safe progress, is not retried, and leaves module transport unchanged'
} finally { $peer.Dispose() }

foreach ($scenario in @('Success', 'Failure', 'WriteTimeout')) {
    $output = & $module {
        param($Helper, $Scenario)
        & {
            $probe = @{ Calls = 0 }
            function Invoke-WebRequest {
                [CmdletBinding()]
                param($Uri, $Method, $Body, $Headers, $ConnectionTimeoutSeconds, $OperationTimeoutSeconds, $MaximumRetryCount)
                $probe.Calls++
                if ($ConnectionTimeoutSeconds -ne 30 -or $OperationTimeoutSeconds -ne 30 -or $MaximumRetryCount -ne 0) { throw 'Wrong transport policy' }
                if ($Scenario -eq 'Failure') { throw [InvalidOperationException]::new('SECRET_SENTINEL') }
                if ($Scenario -eq 'WriteTimeout') {
                    if ($Method -ne 'Post' -or $Body -ne 'SECRET_SENTINEL') { throw 'Write forwarding changed' }
                    throw [TimeoutException]::new('SECRET_SENTINEL')
                }
                [pscustomobject]@{ StatusCode = 200; Content = 'SECRET_SENTINEL' }
            }
            . $Helper -State @{ Sequence = 0; TimedOut = $false }
            $failure = $null
            $result = $null
            $method = if ($Scenario -eq 'WriteTimeout') { 'Post' } else { 'Get' }
            try { $result = Invoke-WebRequest -Uri 'https://example.invalid/?SECRET_SENTINEL' -Method $method -Body 'SECRET_SENTINEL' -Headers @{ Authorization = 'SECRET_SENTINEL' } }
            catch { $failure = $_.Exception.Message }
            if ($probe.Calls -ne 1) { throw 'Transport was skipped or retried' }
            if ($Scenario -eq 'Success') {
                if ($failure -or $result.StatusCode -ne 200 -or $result.Content -ne 'SECRET_SENTINEL') { throw 'Response forwarding changed' }
            } elseif (-not $failure -or $failure -match 'SECRET_SENTINEL') { throw 'Unsafe failure forwarding' }
        }
    } $helper $scenario 6>&1
    $messages = ($output | ForEach-Object ToString) -join "`n"
    if ($messages -match 'SECRET_SENTINEL|example.invalid|Authorization') { throw 'Diagnostic output leaked synthetic secret' }
    if ($scenario -eq 'WriteTimeout' -and $messages -notmatch 'outcome may be unknown') { throw 'Missing ambiguous-write warning' }
    Write-Output "PASS: HTTP diagnostics $scenario; one transport attempt and no raw authentication/response output"
}
