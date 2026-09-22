[CmdletBinding()]
param(
    [string]$ApprovalScript = "$PSScriptRoot/../../scripts/Approve-GdapRelationship.ps1",
    [string]$ModulePath = "$PSScriptRoot/../../M365Internals/M365Internals.psd1",
    [ValidateSet('/', '/adminportal')][string]$TenantCookiePath = '/',
    [ValidateSet('Plain', 'Quoted', 'Opaque', 'Missing')][string]$TenantCookieFormat = 'Plain',
    [ValidateSet('Match', 'Wrong', 'WrongExpected', 'Missing', 'Conflict', 'HttpError', 'TransportError', 'ShellFallback')][string]$LiveTenantScenario = 'Match',
    [ValidateSet('Ready', 'CompetingTab', 'CdpNoise', 'NoPageState', 'WrongRoute', 'NavigationFailure', 'BrowserClosed', 'ProcessHandoff', 'TenantChanged', 'MissingId')][string]$InvitationScenario = 'Ready',
    [switch]$SubmitSyntheticApproval,
    [switch]$RequireApprovalConfirmation,
    [ValidateSet('Explicit', 'Default')][string]$ConfirmationPolicy = 'Explicit',
    [switch]$DiscoverCustomer,
    [ValidateSet('Accept', 'Cancel', 'Partner')][string]$CustomerSelection = 'Accept'
)
$ErrorActionPreference = 'Stop'
if ($DiscoverCustomer) {
    $global:gdapCustomerTest = @{ Choice = $CustomerSelection; Prompts = 0 }
    function global:Read-Host {
        param($Prompt)
        if ($Prompt -notlike 'Inspect the customer account*') { throw 'Unexpected interactive prompt in isolated test' }
        $global:gdapCustomerTest.Prompts++
        if ($global:gdapCustomerTest.Choice -eq 'Cancel') { return 'CANCEL' }
        return 'CUSTOMER'
    }
}
if ($SubmitSyntheticApproval -and ($InvitationScenario -ne 'Ready' -or $LiveTenantScenario -ne 'Match')) { throw 'Synthetic write test requires the matching ready scenario' }
if ($RequireApprovalConfirmation -and ($SubmitSyntheticApproval -or $InvitationScenario -ne 'Ready' -or $LiveTenantScenario -ne 'Match')) { throw 'Confirmation test requires the matching ready scenario without synthetic writes' }
# Match a top-level -File ... -WhatIf host, including the module's caller scope.
$WhatIfPreference = $true
$originalConfirmPreference = $ConfirmPreference
# Run this file with pwsh -NoProfile. Use the real connection/browser helpers,
# replacing only OS process and network boundaries. No browser or tenant access.
$module = Import-Module $ModulePath -PassThru
& $module {
    param($CookiePath, $CookieFormat, $LiveScenario, $PageScenario, $AllowSyntheticApproval, $RequireConfirmation)
    $script:gdapAllowSyntheticApproval = $AllowSyntheticApproval
    $script:gdapPreviewMode = -not ($AllowSyntheticApproval -or $RequireConfirmation)
    # Reproduce the effective Low preference shown by the user's setup prompts.
    # Pre-imported test modules otherwise conceal this preference propagation.
    if ($RequireConfirmation) { $script:ConfirmPreference = 'Low' }
    $script:gdapTenantCookiePath = $CookiePath
    $script:gdapTenantCookieFormat = $CookieFormat
    $script:gdapLiveScenario = $LiveScenario
    $script:gdapPageScenario = $PageScenario
    $script:gdapProbe = @{ Launched = $false; Profile = $null; Reads = 0; IdentityReads = 0; Posts = 0; Stopped = $false; Navigations = 0 }
    function script:Resolve-M365BrowserPath { @{ Name = 'Microsoft Edge'; Path = 'gdap-test-browser' } }
    function script:Get-M365BrowserFreeTcpPort { 37101 }
    function script:Get-M365BrowserInteractiveStartUrl { param($TenantId) "https://login.microsoftonline.com/$(if ($TenantId) { $TenantId } else { 'common' })/oauth2/authorize?client_id=synthetic" }
    function script:Test-M365BrowserProcessOutputSuppression { $false }
    function script:Start-Process {
        [CmdletBinding(SupportsShouldProcess)]
        param($FilePath, $ArgumentList, [switch]$PassThru)
        if (($ArgumentList -join ' ') -notmatch '--user-data-dir=(?:"(?<quoted>[^"]+)"|(?<plain>[^ ]+))') { throw 'Missing private profile argument' }
        $script:gdapProbe.Profile = if ($Matches.quoted) { $Matches.quoted } else { $Matches.plain }
        if ($PSCmdlet.ShouldProcess($FilePath, 'Start-Process')) {
            $script:gdapProbe.Launched = $true
            $script:gdapFakeProcess = [pscustomobject]@{ HasExited = $false }
            $script:gdapFakeProcess | Add-Member -MemberType ScriptMethod -Name Refresh -Value { }
            return $script:gdapFakeProcess
        }
    }
    function script:Get-M365BrowserCdpVersion {
        if (-not $script:gdapProbe.Launched -or -not [IO.Directory]::Exists($script:gdapProbe.Profile)) {
            throw 'WhatIf suppressed private profile creation or Start-Process'
        }
        @{ webSocketDebuggerUrl = 'ws://test.invalid' }
    }
    function script:Get-M365BrowserPreferredTargetContext { throw 'GDAP_WHATIF_BROWSER_BOUNDARY' }
    # Stop at the first polling sleep, before touching CDP or acquiring cookies.
    function script:Start-Sleep {
        param($Seconds, $Milliseconds)
        if ($Seconds) { throw 'GDAP_WHATIF_BROWSER_BOUNDARY' }
    }
    function script:Stop-M365BrowserProcess { $script:gdapProbe.Stopped = $true }
    function script:Set-M365Cache { }
    function script:Invoke-M365PortalPostLandingBootstrap { }
    function script:Invoke-WebRequest {
        param($Uri)
        if ($script:gdapTenantCookieFormat -eq 'Missing' -and $Uri -in @(
            'https://admin.cloud.microsoft/admin/api/tenant/datalocationandcommitments',
            'https://admin.cloud.microsoft/adminportal/home/ClassicModernAdminDataStream?ref=/homepage')) {
            return [pscustomobject]@{ StatusCode = 200; Content = '{"TID":"11111111-1111-1111-1111-111111111111"}' }
        }
        throw 'Unexpected network boundary in isolated test'
    }
    function script:Invoke-M365PortalRequest {
        param($Path, $Method = 'Get', $Headers, $WebSession, [switch]$RawResponse, [switch]$SkipAutoHeal, [switch]$SkipConnectionRefresh)
        if ($Method -ne 'Get') {
            if (-not $script:gdapAllowSyntheticApproval -or $Method -ne 'Post' -or
                $script:gdapProbe.Navigations -ne 1 -or $script:gdapProbe.Reads -ne 2 -or $script:gdapProbe.Stopped -or
                $Path -cne '/fd/GdapPartnerManage/CustomerServiceAdminApi/Web/v1/GranularAdminRelationships/whatif-test/UpdateStatus') {
                throw 'Unsafe or unexpected synthetic approval request'
            }
            $script:gdapProbe.Posts++
            return
        }
        if ($Path -in @('/adminportal/home/ClassicModernAdminDataStream?ref=/homepage',
            '/admin/api/coordinatedbootstrap/shellinfo', '/admin/api/navigation', '/admin/api/features/all')) {
            if ($script:gdapProbe.Navigations -gt 0 -and $script:gdapPageScenario -eq 'TenantChanged') {
                return @{ StatusCode = 200; Content = '{"TID":"22222222-2222-2222-2222-222222222222"}' }
            }
            if ($script:gdapProbe.Reads -gt 0) {
                $script:gdapProbe.IdentityReads++
                if (-not [object]::ReferenceEquals($WebSession, $script:m365PortalSession) -or -not $RawResponse -or -not $SkipAutoHeal -or -not $SkipConnectionRefresh) {
                    throw 'Live identity probe did not use the authenticated session and no-heal request flags'
                }
                switch ($script:gdapLiveScenario) {
                    'Wrong' { return @{ StatusCode = 200; Content = '{"TID":"22222222-2222-2222-2222-222222222222"}' } }
                    'Missing' { return @{ StatusCode = 200; Content = '<html>SECRET_SENTINEL</html>' } }
                    'Conflict' { return @{ StatusCode = 200; Content = '{"TID":"11111111-1111-1111-1111-111111111111","other":{"TID":"22222222-2222-2222-2222-222222222222"}}' } }
                    'HttpError' { return @{ StatusCode = 403; Content = 'SECRET_SENTINEL' } }
                    'TransportError' { throw 'SECRET_SENTINEL' }
                    'ShellFallback' {
                        if ($Path -like '*ClassicModern*') { return @{ StatusCode = 200; Content = '{}' } }
                    }
                }
            }
            return [pscustomobject]@{ StatusCode = 200; Content = '{"TID":"11111111-1111-1111-1111-111111111111"}'; Headers = @{} }
        }
        if ($Path -ne '/fd/commerceMgmt2/partnermanage/gdapInvitations/whatif-test?api-version=3.0') { throw 'Unexpected request path' }
        if ($script:gdapProbe.Navigations -ne 1 -or $script:gdapProbe.Stopped) { throw 'Invitation inspection did not run in the retained, navigated browser lifetime' }
        if ([bool]$WhatIfPreference -ne [bool]$script:gdapPreviewMode) { throw 'Inspection WhatIf scope was not restored' }
        $script:gdapProbe.Reads++
        if ($script:gdapPageScenario -eq 'MissingId') { return @{ data = @{ privateValue = 'SECRET_SENTINEL' }; status = 'unknown' } }
        $state = if ($script:gdapProbe.Posts -gt 0) { 'active' } else { 'approvalPending' }
        @{ partner = @{ tenantId = '22222222-2222-2222-2222-222222222222' }
            relationship = @{ partnerGdapRelationshipId = 'whatif-test'; status = $state; etag = 'e1'
            duration = 730; autoExtendDuration = 'P180D'
            roles = @('33333333-3333-3333-3333-333333333333')
        } }
    }
} $TenantCookiePath $TenantCookieFormat $LiveTenantScenario $InvitationScenario ([bool]$SubmitSyntheticApproval) ([bool]$RequireApprovalConfirmation)
$arguments = @{ RelationshipId = 'whatif-test'; ExpectedTenantId = '11111111-1111-1111-1111-111111111111'
    ExpectedPartnerTenantId = '22222222-2222-2222-2222-222222222222'; WhatIf = (-not ($SubmitSyntheticApproval -or $RequireApprovalConfirmation)); InvitationTimeoutSeconds = 5 }
if ($SubmitSyntheticApproval) { $arguments.Confirm = $false }
if ($RequireApprovalConfirmation -and $ConfirmationPolicy -eq 'Explicit') { $arguments.Confirm = $true }
if ($DiscoverCustomer) {
    $arguments.Remove('ExpectedTenantId')
    $arguments.ConfirmAuthenticatedTenant = $true
    if ($CustomerSelection -eq 'Partner') { $arguments.ExpectedPartnerTenantId = '11111111-1111-1111-1111-111111111111' }
}
try {
    & $ApprovalScript @arguments
    throw 'Expected stopped browser boundary'
} catch {
    if ($_.Exception.Message -ne 'GDAP_WHATIF_BROWSER_BOUNDARY') { throw }
}
$probe = & $module { $script:gdapProbe }
if (-not $probe.Launched -or -not $probe.Stopped -or [IO.Directory]::Exists($probe.Profile)) {
    throw 'Private browser launch/cleanup did not execute during WhatIf'
}
# Now simulate completed authentication at the browser boundary, exercising the
# actual script entry point through session identity checks and ShouldProcess.
& $module {
    $script:gdapClock = [datetime]::new(2026, 1, 1)
    $script:gdapPageUrl = 'https://admin.cloud.microsoft/'
    $script:gdapProbe.Stopped = $false
    function script:Get-Date { $script:gdapClock }
    function script:Start-Sleep {
        param($Seconds, $Milliseconds)
        $script:gdapClock = $script:gdapClock.AddSeconds([double]$Seconds)
        if ($script:gdapProbe.Navigations -gt 0 -and $script:gdapPageScenario -in @('BrowserClosed', 'ProcessHandoff')) { $script:gdapFakeProcess.HasExited = $true }
    }
    function script:Get-M365BrowserPreferredTargetContext {
        if ($script:gdapPageScenario -eq 'CompetingTab' -and $script:gdapProbe.Navigations -gt 0) {
            # A second admin.cloud tab wins the upstream preference order after
            # the navigated tab changes to admin.microsoft.com.
            return @{ WebSocketUrl = 'ws://other-tab.invalid'; Url = 'https://admin.cloud.microsoft/' }
        }
        @{ WebSocketUrl = 'ws://test.invalid'; Url = $script:gdapPageUrl }
    }
    function script:Invoke-M365BrowserCdpCommand {
        param($WebSocketUrl, $Method, $Params)
        if ($Method -eq 'Browser.close') {
            if ($WebSocketUrl -ne 'ws://test.invalid') { throw 'Cleanup selected another browser' }
            return
        }
        if ($script:gdapPageScenario -eq 'BrowserClosed' -and $script:gdapFakeProcess.HasExited) { throw 'Synthetic closed CDP endpoint' }
        if ($Method -eq 'Page.navigate') {
            if (-not $script:m365PortalConnection.Validated -or $script:gdapProbe.Stopped) { throw 'Navigation preceded session validation or followed browser closure' }
            if ($Params.url -cne 'https://admin.microsoft.com/AdminPortal/Home#/partners/invitation/granularAdminRelationships/whatif-test') { throw 'Wrong invitation navigation URL' }
            $script:gdapProbe.Navigations++
            if ($script:gdapPageScenario -eq 'NavigationFailure') { return [pscustomobject]@{ errorText = 'SYNTHETIC_FAILURE' } }
            $script:gdapPageUrl = $Params.url
            return [pscustomobject]@{ frameId = 'test-frame' }
        }
        if ($Method -ne 'Runtime.evaluate' -or $Params.expression -cne 'JSON.stringify({url:location.href,ready:document.readyState})') { throw 'Unexpected browser command or page-data collection' }
        if ($script:gdapPageScenario -eq 'NoPageState') { return @{ exceptionDetails = @{ text = 'SECRET_SENTINEL' } } }
        if ($script:gdapPageScenario -eq 'CdpNoise') { [pscustomobject]@{ Unrelated = 'SECRET_SENTINEL' } }
        $url = if ($script:gdapPageScenario -eq 'WrongRoute') { 'https://example.invalid/' } else { $script:gdapPageUrl }
        if ($script:gdapPageScenario -eq 'CompetingTab' -and $WebSocketUrl -eq 'ws://other-tab.invalid') { $url = 'https://admin.cloud.microsoft/' }
        @{ result = @{ value = (@{ url = $url; ready = 'complete' } | ConvertTo-Json -Compress) } }
    }
    function script:Get-M365BrowserCookieJar {
        foreach ($name in @('RootAuthToken', 'SPAAuthCookie', 'OIDCAuthCookie', 's.AjaxSessionKey', 'x-portal-routekey')) {
            @{ name = $name; value = 'synthetic-test-only'; domain = 'admin.cloud.microsoft' }
        }
        @{ name = 'UserLoginRef'; value = '%2Fhomepage'; domain = 'admin.cloud.microsoft' }
        $cookieValue = switch ($script:gdapTenantCookieFormat) {
            'Plain' { '11111111-1111-1111-1111-111111111111' }
            'Quoted' { '"11111111-1111-1111-1111-111111111111"' }
            'Opaque' { 'SECRET_SENTINEL_NOT_A_GUID' }
        }
        if ($script:gdapTenantCookieFormat -ne 'Missing') {
            @{ name = 's.UserTenantId'; value = $cookieValue; domain = 'admin.cloud.microsoft' }
        }
    }
}
if ($LiveTenantScenario -eq 'WrongExpected') { $arguments.ExpectedTenantId = '22222222-2222-2222-2222-222222222222' }
$diagnostic = $null
try { $null = & $ApprovalScript @arguments } catch {
    if (-not $RequireApprovalConfirmation -and $LiveTenantScenario -in @('Match', 'ShellFallback') -and $InvitationScenario -in @('Ready', 'CompetingTab', 'CdpNoise', 'ProcessHandoff') -and (-not $DiscoverCustomer -or $CustomerSelection -eq 'Accept')) { throw }
    $diagnostic = $_.Exception.Message
}
$pageDiagnostic = switch ($InvitationScenario) {
    'NoPageState' { '*Last observation: No readable page state*' }
    'WrongRoute' { '*did not finish navigating*' }
    'NavigationFailure' { '*page could not be opened*' }
    'BrowserClosed' { '*invitation browser was closed*' }
    'TenantChanged' { '*tenant*Approval stopped*' }
    'MissingId' { '*Response shape: root:object fields=[[]data,status[]]*' }
}
$expectedDiagnostic = switch ($LiveTenantScenario) {
    'Wrong' { '*active portal tenant changed*' }
    'WrongExpected' { '*Customer tenant mismatch*' }
    'Missing' { '*did not establish an active tenant ID*' }
    'Conflict' { '*conflicting tenant IDs*' }
    'HttpError' { '*did not return HTTP 200*' }
    'TransportError' { '*identity request failed*' }
}
if ($pageDiagnostic) { $expectedDiagnostic = $pageDiagnostic }
if ($RequireApprovalConfirmation) { $expectedDiagnostic = '*NonInteractive*' }
if ($DiscoverCustomer -and $CustomerSelection -eq 'Cancel') { $expectedDiagnostic = '*Customer selection cancelled*' }
if ($DiscoverCustomer -and $CustomerSelection -eq 'Partner') { $expectedDiagnostic = '*signed into the partner tenant*' }
if ($expectedDiagnostic) {
    if ($diagnostic -notlike $expectedDiagnostic -or $diagnostic -like '*SECRET_SENTINEL*') {
        throw 'Entry-point identity rejection did not safely report the expected cause'
    }
}
$stoppedBeforeInspection = $LiveTenantScenario -eq 'WrongExpected' -or $InvitationScenario -in @('NoPageState', 'WrongRoute', 'NavigationFailure', 'BrowserClosed', 'TenantChanged')
if ($DiscoverCustomer -and $CustomerSelection -ne 'Accept') { $stoppedBeforeInspection = $true }
$maxIdentityReads = if ($SubmitSyntheticApproval) { 3 } else { 2 }
if (-not $stoppedBeforeInspection -and ($probe.IdentityReads -lt 1 -or $probe.IdentityReads -gt $maxIdentityReads)) { throw 'Missing or excessive live identity requests' }
$expectedReads = if ($stoppedBeforeInspection) { 0 } elseif ($SubmitSyntheticApproval) { 3 } else { 1 }
$expectedPosts = if ($SubmitSyntheticApproval -and -not $stoppedBeforeInspection) { 1 } else { 0 }
if ($probe.Reads -ne $expectedReads -or $probe.Posts -ne $expectedPosts) { throw 'Unsafe invitation inspection or approval write' }
if ($LiveTenantScenario -eq 'WrongExpected' -and $probe.Navigations -ne 0) { throw 'Invitation opened in the wrong expected tenant' }
if (-not $probe.Stopped -or [IO.Directory]::Exists($probe.Profile)) { throw 'Retained browser cleanup failed' }
if (-not $WhatIfPreference) { throw 'Script changed caller WhatIf preference' }
if ($ConfirmPreference -ne $originalConfirmPreference) { throw 'Script changed caller Confirm preference' }
if ($RequireApprovalConfirmation -and (& $module { $script:ConfirmPreference }) -ne 'Low') { throw 'Script changed module confirmation preference outside its call scope' }
if ($DiscoverCustomer) {
    $expectedPrompts = if ($CustomerSelection -eq 'Partner') { 0 } else { 1 }
    if ($global:gdapCustomerTest.Prompts -ne $expectedPrompts) { throw 'Customer selection was skipped or repeated' }
    if ($CustomerSelection -ne 'Accept' -and $probe.Navigations -ne 0) { throw 'Invitation navigated before customer selection' }
    Remove-Item Function:\global:Read-Host -WhatIf:$false -Confirm:$false
    Remove-Variable gdapCustomerTest -Scope Global -WhatIf:$false -Confirm:$false
    Write-Output "PASS: authenticated customer selection: $CustomerSelection; customer GUID input not required"
}
if ($RequireApprovalConfirmation) { Write-Output 'PASS: browser setup/cleanup need no confirmation; real approval ShouldProcess still requires a prompt, with zero writes in a noninteractive host' }
Write-Output "PASS: authenticated invitation navigation before inspection, browser lifetime/cleanup, tenant checks; synthetic approval writes=$expectedPosts"
