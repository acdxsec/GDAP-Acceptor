[CmdletBinding()]
param(
    [string]$ApprovalScript = "$PSScriptRoot/../../scripts/Approve-GdapRelationship.ps1",
    [string]$ModulePath = "$PSScriptRoot/../../M365Internals/M365Internals.psd1",
    [ValidateSet('Missing', 'Present')][string]$TenantCookie = 'Missing',
    [switch]$PortalRequestDiagnostics
)
$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop
# A real local HTTP response avoids OS-specific closed-port refusal timing while
# still exercising PowerShell's actual header validation and session mutation.
Add-Type @'
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
public sealed class GdapBootstrapPeer : IDisposable {
    readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stop = new CancellationTokenSource();
    public string Address { get; }
    public GdapBootstrapPeer() {
        listener.Start();
        Address = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
        _ = Serve();
    }
    async Task Serve() {
        try {
            while (!stop.IsCancellationRequested) {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                using var stream = client.GetStream();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var one = new byte[1];
                var header = new StringBuilder();
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) {
                    if (header.Length > 65536 || await stream.ReadAsync(one, timeout.Token) != 1) throw new IOException("Invalid fixture request");
                    header.Append((char)one[0]);
                }
                int length = 0;
                foreach (var line in header.ToString().Split("\r\n"))
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15).Trim());
                if (length < 0 || length > 1048576) throw new IOException("Oversized fixture request");
                var buffer = new byte[4096];
                while (length > 0) {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(length, buffer.Length)), timeout.Token);
                    if (read == 0) throw new IOException("Incomplete fixture request");
                    length -= read;
                }
                var response = Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, timeout.Token);
            }
        } catch (OperationCanceledException) {} catch (ObjectDisposedException) {} catch (SocketException) {}
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); }
}
'@
$peer = [GdapBootstrapPeer]::new()
try {
$module = Import-Module $ModulePath -PassThru
& $module {
    param($CookieMode, $TraceHttp, $LoopbackAddress)
    $script:gdapLoopbackAddress = $LoopbackAddress
    $script:gdapRequireHttpLimits = $TraceHttp
    $script:gdapTransportProbe = @{ CookieMode = $CookieMode; Requests = 0; BootstrapReads = 0; PostLandingReads = 0; Navigations = 0; InvitationReads = 0; ApprovalWrites = 0; HeaderFailures = 0; Stopped = $false }
    $script:gdapTransportPage = 'https://admin.cloud.microsoft/'
    function script:Resolve-M365BrowserPath { @{ Name = 'Microsoft Edge'; Path = 'synthetic-browser' } }
    function script:Get-M365BrowserFreeTcpPort { 37101 }
    function script:Resolve-M365BrowserProfileConfiguration { @{ ProfilePath = 'synthetic-profile'; CleanupProfileOnExit = $false } }
    function script:Test-M365BrowserProcessOutputSuppression { $false }
    function script:Start-M365BrowserProcess {
        $process = [pscustomobject]@{ HasExited = $false }
        $process | Add-Member -MemberType ScriptMethod -Name Refresh -Value { }
        $process
    }
    function script:Stop-M365BrowserProcess { $script:gdapTransportProbe.Stopped = $true }
    function script:Remove-M365BrowserProcessRedirectFiles { }
    function script:Start-Sleep { }
    function script:Get-M365BrowserCdpVersion { @{ webSocketDebuggerUrl = 'ws://synthetic.invalid' } }
    function script:Get-M365BrowserPreferredTargetContext { @{ Url = $script:gdapTransportPage; WebSocketUrl = 'ws://synthetic.invalid' } }
    function script:Invoke-M365BrowserCdpCommand {
        param($WebSocketUrl, $Method, $Params)
        if ($Method -eq 'Page.navigate') {
            if (-not $script:m365PortalConnection.Validated) { throw 'Navigation before tenant validation' }
            $script:gdapTransportProbe.Navigations++
            $script:gdapTransportPage = $Params.url
            return [pscustomobject]@{ frameId = 'synthetic' }
        }
        if ($Method -ne 'Runtime.evaluate' -or $Params.expression -cne 'JSON.stringify({url:location.href,ready:document.readyState})') { throw 'Unexpected CDP request' }
        @{ result = @{ value = (@{ url = $script:gdapTransportPage; ready = 'complete' } | ConvertTo-Json -Compress) } }
    }
    function script:Get-M365BrowserCookieJar {
        foreach ($name in @('RootAuthToken', 'SPAAuthCookie', 'OIDCAuthCookie', 's.AjaxSessionKey', 'x-portal-routekey')) {
            @{ name = $name; value = 'synthetic-only'; domain = 'admin.cloud.microsoft' }
        }
        @{ name = 'UserLoginRef'; value = '%2Fhomepage'; domain = 'admin.cloud.microsoft' }
        if ($script:gdapTransportProbe.CookieMode -eq 'Present') {
            @{ name = 's.UserTenantId'; value = '11111111-1111-1111-1111-111111111111'; domain = 'admin.cloud.microsoft' }
        }
    }
    function script:Set-M365Cache { }
    # Do NOT replace login, post-landing, connection validation, context headers,
    # or Invoke-M365PortalRequest. Exercise real Invoke-WebRequest header parsing
    # AND its mutations to WebRequestSession, with only synthetic credentials.
    function script:Invoke-WebRequest {
        [CmdletBinding()]
        param($Uri, $WebSession, [string]$UserAgent, $Headers, $Method = 'Get', $MaximumRedirection, $ContentType, $Body, $ConnectionTimeoutSeconds, $OperationTimeoutSeconds, $MaximumRetryCount)
        if ($script:gdapRequireHttpLimits -and ($ConnectionTimeoutSeconds -ne 30 -or $OperationTimeoutSeconds -ne 30 -or $MaximumRetryCount -ne 0)) { throw 'A real entry-point HTTP call escaped the diagnostic timeout/no-retry policy' }
        $transport = @{}
        foreach ($key in $PSBoundParameters.Keys) { $transport[$key] = $PSBoundParameters[$key] }
        # Never contact Microsoft. Require an actual local response after real
        # header construction; a timeout is a failure, not an allowed outcome.
        $transport.Uri = $script:gdapLoopbackAddress
        $transport.NoProxy = $true
        $transport.ConnectionTimeoutSeconds = 10
        $transport.OperationTimeoutSeconds = 10
        $transport.ErrorAction = 'Stop'
        try {
            $response = Microsoft.PowerShell.Utility\Invoke-WebRequest @transport
            if ($response.StatusCode -ne 204) { throw 'Loopback fixture did not return the expected response' }
        } catch {
            $script:gdapTransportProbe.HeaderFailures++; throw
        }
        if ($WebSession.UserAgent -cne (Get-M365DefaultUserAgent)) { throw 'Request changed the browser User-Agent' }
        $script:gdapTransportProbe.Requests++
        $content = '{"TID":"11111111-1111-1111-1111-111111111111"}'
        $contentTypeValue = 'application/json'
        if ($Uri -eq 'https://admin.cloud.microsoft/') {
            $content = "var loginURL = 'https://login.microsoftonline.com/common/oauth2/authorize?client_id=synthetic';"
            $contentTypeValue = 'text/html'
        } elseif ($Uri -like 'https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/oauth2/authorize?*') {
            $content = '$Config={};'
            $contentTypeValue = 'text/html'
        } elseif ($Uri -like 'https://admin.cloud.microsoft/fd/commerceMgmt2/partnermanage/gdapInvitations/*') {
            if ($script:gdapTransportProbe.Navigations -ne 1) { throw 'Invitation read before navigation' }
            $script:gdapTransportProbe.InvitationReads++
            $content = @{ partner = @{ tenantId = '22222222-2222-2222-2222-222222222222' }
                relationship = @{ partnerGdapRelationshipId = 'transport-test'; status = 'approvalPending'; etag = 'e1'
                duration = 730; autoExtendDuration = 'P180D'
                roles = @('33333333-3333-3333-3333-333333333333')
            } } | ConvertTo-Json -Depth 10
        } elseif ($Uri -like 'https://admin.cloud.microsoft/*') {
            if ($Uri -like '*/GranularAdminRelationships/*/UpdateStatus') { $script:gdapTransportProbe.ApprovalWrites++; throw 'Unexpected approval under WhatIf' }
            if ($Method -eq 'Post' -and $Uri -ne 'https://admin.cloud.microsoft/api/instrument/logclient') { throw 'Unexpected portal write' }
            if ($Uri -like '*ClassicModernAdminDataStream*') { $script:gdapTransportProbe.BootstrapReads++ }
            if ($Uri -eq 'https://admin.cloud.microsoft/adminportal?ref=/homepage') { $script:gdapTransportProbe.PostLandingReads++ }
        } else { throw 'Unexpected request destination' }
        [pscustomobject]@{ StatusCode = 200; Content = $content; Headers = @{ 'Content-Type' = $contentTypeValue } }
    }
} $TenantCookie ([bool]$PortalRequestDiagnostics) $peer.Address
$null = & $ApprovalScript -RelationshipId 'transport-test' -ExpectedTenantId '11111111-1111-1111-1111-111111111111' -ExpectedPartnerTenantId '22222222-2222-2222-2222-222222222222' -WhatIf -PortalRequestDiagnostics:$PortalRequestDiagnostics
$probe = & $module { $script:gdapTransportProbe }
if ($probe.HeaderFailures -ne 0 -or $probe.InvitationReads -ne 1 -or $probe.ApprovalWrites -ne 0 -or $probe.BootstrapReads -lt 3 -or -not $probe.Stopped) { throw 'Portal transport or approval safety regression' }
if ($TenantCookie -eq 'Missing' -and $probe.PostLandingReads -lt 2) { throw 'Missing-cookie bootstrap path was not exercised' }
Write-Output "PASS: $TenantCookie tenant cookie; real bootstrap/validation/request chain preserves User-Agent; WhatIf approval writes=0"
} finally { $peer.Dispose() }
