# Optional real-browser smoke test. Uses a dedicated headless blank-page profile;
# never opens Microsoft, reads a user's browser profile, signs in or approves.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ModulePath,
    [Parameter(Mandatory)][string]$BrowserPath,
    [string]$ApprovalScript = "$PSScriptRoot/../../scripts/Approve-GdapRelationship.ps1"
)
$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Utility
$module = Import-Module $ModulePath -PassThru
$helper = Join-Path (Split-Path $ApprovalScript) 'GdapInvitationBrowser.ps1'
$profile = Join-Path ([IO.Path]::GetTempPath()) ('gdap-cdp-test-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory $profile
$browser = $null
try {
    $browser = Start-Process $BrowserPath -ArgumentList @('--headless=new','--disable-background-networking','--disable-component-update','--no-first-run','--no-default-browser-check','--remote-debugging-port=0',"--user-data-dir=`"$profile`"",'about:blank') -PassThru -RedirectStandardOutput "$profile/stdout" -RedirectStandardError "$profile/stderr"
    $deadline = [datetime]::UtcNow.AddSeconds(15)
    while (-not (Test-Path "$profile/DevToolsActivePort")) {
        if ($browser.HasExited -or [datetime]::UtcNow -gt $deadline) { throw 'Isolated headless test browser did not start' }
        Start-Sleep -Milliseconds 100
    }
    $port = [int](Get-Content "$profile/DevToolsActivePort" -First 1)
    $page = $null
    do {
        $targets = Invoke-RestMethod "http://127.0.0.1:$port/json/list" -ConnectionTimeoutSeconds 3 -OperationTimeoutSeconds 3
        foreach ($target in $targets) { if ($target.type -eq 'page') { $page = $target; break } }
        if (-not $page) { Start-Sleep -Milliseconds 100 }
    } while (-not $page -and [datetime]::UtcNow -lt $deadline)
    if (-not $page) { throw 'Blank browser page did not become available' }
    & $module {
        param($Socket, $Helper)
        . $Helper
        $null = Invoke-M365BrowserCdpCommand -WebSocketUrl $Socket -Method Page.navigate -Params @{ url = 'about:blank#gdap-probe' }
        $state = Get-GdapBrowserPageState -WebSocketUrl $Socket
        if ($state.url -cne 'about:blank#gdap-probe' -or $state.ready -cne 'complete') { throw 'Production page-state reader did not recognize real browser navigation' }
        'PASS: actual Edge navigation and production page-state reader, including real CDP pipeline output; no Microsoft authentication or approval'
    } $page.webSocketDebuggerUrl $helper
} finally {
    if ($browser -and -not $browser.HasExited) { $browser.Kill($true); $null = $browser.WaitForExit(5000) }
    # Only the newly allocated test profile above, never a user's browser profile.
    Remove-Item -LiteralPath $profile -Recurse -Force -ErrorAction SilentlyContinue
}
