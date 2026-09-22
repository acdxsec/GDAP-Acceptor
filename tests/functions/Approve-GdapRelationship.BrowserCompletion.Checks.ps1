[CmdletBinding()]
param(
    [string]$ApprovalScript = "$PSScriptRoot/../../scripts/Approve-GdapRelationship.ps1",
    [string]$ModulePath = "$PSScriptRoot/../../M365Internals/M365Internals.psd1",
    [ValidateSet('Complete', 'Close', 'Timeout', 'ValidationFailure')][string]$Scenario = 'Complete'
)
$ErrorActionPreference = 'Stop'
$WhatIfPreference = $true
$module = Import-Module $ModulePath -PassThru
& $module {
    param($Case)
    $script:gdapBrowserProbe = @{ Scenario = $Case; Seconds = 0; Polls = 0; EstsExchanges = 0; PortalValidations = 0; Stopped = $false; Profile = $null }
    $script:gdapOriginalEstsSelector = ${function:Get-M365BestBrowserEstsCookie}.ToString()
    function script:Get-Date { [datetime]::new(2026, 1, 1).AddSeconds($script:gdapBrowserProbe.Seconds) }
    function script:Resolve-M365BrowserPath { @{ Name = 'Microsoft Edge'; Path = 'gdap-test-browser' } }
    function script:Get-M365BrowserFreeTcpPort { 37101 }
    function script:Get-M365BrowserInteractiveStartUrl { param($TenantId) "https://login.microsoftonline.com/$TenantId/oauth2/authorize?client_id=synthetic" }
    function script:Test-M365BrowserProcessOutputSuppression { $false }
    function script:Start-Process {
        [CmdletBinding(SupportsShouldProcess)]
        param($FilePath, $ArgumentList, [switch]$PassThru)
        if (-not $PSCmdlet.ShouldProcess($FilePath, 'Start-Process')) { throw 'WhatIf suppressed browser launch' }
        if (($ArgumentList -join ' ') -notmatch '--user-data-dir=(?:"(?<quoted>[^"]+)"|(?<plain>[^ ]+))') { throw 'Missing profile path' }
        $script:gdapBrowserProbe.Profile = if ($Matches.quoted) { $Matches.quoted } else { $Matches.plain }
        $script:gdapFakeProcess = [pscustomobject]@{ HasExited = $false }
        $script:gdapFakeProcess | Add-Member -MemberType ScriptMethod -Name Refresh -Value { }
        $script:gdapFakeProcess
    }
    function script:Start-Sleep {
        param($Seconds, $Milliseconds)
        if ($Seconds) {
            # Advance the fake clock beyond the upstream 10-second grace period.
            $script:gdapBrowserProbe.Seconds += 12
            if ($script:gdapBrowserProbe.Scenario -eq 'Close' -and $script:gdapBrowserProbe.Seconds -ge 24) {
                $script:gdapFakeProcess.HasExited = $true
            }
        }
    }
    function script:Get-M365BrowserCdpVersion { @{ webSocketDebuggerUrl = 'ws://test.invalid' } }
    function script:Get-M365BrowserPreferredTargetContext {
        $url = if ($script:gdapBrowserProbe.Seconds -ge 36 -and $script:gdapBrowserProbe.Scenario -in @('Complete', 'ValidationFailure')) { 'https://admin.cloud.microsoft/' } else { 'https://login.microsoftonline.com/' }
        @{ WebSocketUrl = 'ws://test.invalid'; Url = $url; Title = 'Sign in' }
    }
    function script:Get-M365BrowserCookieJar {
        $script:gdapBrowserProbe.Polls++
        [pscustomobject]@{ name = 'ESTSAUTH'; value = 'SYNTHETIC_ESTS'; domain = 'login.microsoftonline.com' }
        if ($script:gdapBrowserProbe.Scenario -in @('Complete', 'ValidationFailure') -and $script:gdapBrowserProbe.Seconds -ge 36) {
            foreach ($name in @('RootAuthToken', 'SPAAuthCookie', 'OIDCAuthCookie', 's.AjaxSessionKey')) {
                [pscustomobject]@{ name = $name; value = 'SYNTHETIC_PORTAL'; domain = 'admin.cloud.microsoft' }
            }
        }
    }
    function script:Stop-M365BrowserProcess { $script:gdapBrowserProbe.Stopped = $true }
    function script:Invoke-WebRequest {
        param($Uri)
        if ($Uri -ne 'https://login.microsoftonline.com/error') { throw 'Unexpected network boundary' }
    }
    function script:Complete-M365AdminPortalSignIn {
        $script:gdapBrowserProbe.EstsExchanges++
        throw 'GDAP_TEST_EARLY_ESTS_EXCHANGE: ConvergedTFA'
    }
    function script:Set-M365PortalConnectionSettings {
        param($WebSession)
        if (-not $WebSession -or $WebSession.Cookies.GetCookies('https://admin.cloud.microsoft/').Count -lt 4) {
            throw 'Portal validation was attempted without portal cookies'
        }
        $script:gdapBrowserProbe.PortalValidations++
        # Stop at the server-validation boundary: no actual authentication.
        if ($script:gdapBrowserProbe.Scenario -eq 'ValidationFailure') { throw 'GDAP_TEST_PORTAL_VALIDATION_FAILED' }
        throw 'GDAP_TEST_PORTAL_VALIDATION_BOUNDARY'
    }
} $Scenario
$message = $null
try {
    & $ApprovalScript -RelationshipId 'browser-test' -ExpectedTenantId '11111111-1111-1111-1111-111111111111' -ExpectedPartnerTenantId '22222222-2222-2222-2222-222222222222' -AuthenticationTimeoutSeconds 60 -WhatIf
} catch { $message = $_.Exception.Message }
$probe = & $module { $script:gdapBrowserProbe }
$expected = switch ($Scenario) {
    'Complete' { '*GDAP_TEST_PORTAL_VALIDATION_BOUNDARY*' }
    'ValidationFailure' { '*GDAP_TEST_PORTAL_VALIDATION_FAILED*' }
    'Close' { '*browser window was closed before*' }
    'Timeout' { '*before the timeout expired*' }
}
if ($message -notlike $expected -or $probe.EstsExchanges -ne 0) {
    throw "Browser completion regression ($Scenario): $message"
}
if ($Scenario -in @('Complete', 'ValidationFailure') -and ($probe.Polls -ne 3 -or $probe.PortalValidations -ne 1)) {
    throw 'Browser did not wait through the MFA interval for portal cookies'
}
if ($Scenario -in @('Close', 'Timeout') -and $probe.PortalValidations -ne 0) { throw 'Incomplete sign-in reached validation' }
if (-not $probe.Stopped -or [IO.Directory]::Exists($probe.Profile)) { throw 'Browser/profile cleanup failed' }
& $module {
    if (${function:Get-M365BestBrowserEstsCookie}.ToString() -cne $script:gdapOriginalEstsSelector) { throw 'GDAP policy changed the upstream selector outside its call scope' }
    $cookie = Get-M365BestBrowserEstsCookie -Cookies @(@{ name = 'ESTSAUTH'; value = 'SYNTHETIC_ESTS' })
    if (-not $cookie) { throw 'Upstream ESTS selection remained disabled after the GDAP call' }
}
if (-not $WhatIfPreference) { throw 'Caller WhatIf preference changed' }
Write-Output "PASS: $Scenario; waits for portal cookies, no ESTS exchange, cleanup and upstream behavior preserved"
