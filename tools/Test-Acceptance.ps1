[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ModulePath,
    [string]$ApprovalScript = "$PSScriptRoot/../scripts/Approve-GdapRelationship.ps1"
)
$ErrorActionPreference = 'Stop'
$checks = Join-Path $PSScriptRoot '../tests/functions'
$pwsh = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
$count = 0
function Check([string]$Name, [string[]]$Arguments = @()) {
    $output = & $pwsh -NoProfile -NonInteractive -File "$checks/Approve-GdapRelationship.$Name.ps1" @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Failed $Name $($Arguments -join ' ')`n$($output -join "`n")" }
    $script:count++
    Write-Host "PASS $Name $($Arguments -join ' ')"
}
foreach ($name in @('Checks', 'PortalContract.Checks', 'PortalShape.Checks', 'CustomerSelection.Checks')) { Check $name }
$payload = @('-ModulePath', $ModulePath, '-ApprovalScript', $ApprovalScript)
Check 'HttpDiagnostics.Checks' $payload
Check 'Startup.Checks' $payload
Check 'LoginBootstrap.Checks' $payload
foreach ($route in @('PortalRedirect', 'UnsafeRedirect', 'MissingRedirect')) { Check 'LoginBootstrap.Checks' ($payload + @('-LoginRoute', $route)) }
foreach ($cookie in @('Missing', 'Present')) { Check 'PortalBootstrap.Checks' ($payload + @('-TenantCookie', $cookie)) }
foreach ($cookie in @('Missing', 'Present')) { Check 'PortalBootstrap.Checks' ($payload + @('-TenantCookie', $cookie, '-PortalRequestDiagnostics')) }
foreach ($scenario in @('Complete', 'Close', 'Timeout', 'ValidationFailure', 'ProcessHandoff')) { Check 'BrowserCompletion.Checks' ($payload + @('-Scenario', $scenario)) }
foreach ($scenario in @('Ready', 'CompetingTab', 'CdpNoise', 'NoPageState', 'WrongRoute', 'NavigationFailure', 'BrowserClosed', 'ProcessHandoff', 'TenantChanged', 'MissingId')) { Check 'WhatIf.Checks' ($payload + @('-InvitationScenario', $scenario)) }
foreach ($scenario in @('Wrong', 'WrongExpected', 'Missing', 'Conflict', 'HttpError', 'TransportError', 'ShellFallback')) { Check 'WhatIf.Checks' ($payload + @('-TenantCookieFormat', 'Missing', '-LiveTenantScenario', $scenario)) }
foreach ($format in @('Plain', 'Quoted', 'Opaque', 'Missing')) { Check 'WhatIf.Checks' ($payload + @('-TenantCookieFormat', $format)) }
Check 'WhatIf.Checks' ($payload + @('-TenantCookiePath', '/adminportal'))
Check 'WhatIf.Checks' ($payload + @('-SubmitSyntheticApproval'))
foreach ($policy in @('Explicit', 'Default')) { Check 'WhatIf.Checks' ($payload + @('-RequireApprovalConfirmation', '-ConfirmationPolicy', $policy)) }
Check 'WhatIf.Checks' ($payload + @('-DiscoverCustomer', '-SubmitSyntheticApproval'))
Check 'WhatIf.Checks' ($payload + @('-DiscoverCustomer'))
foreach ($choice in @('Cancel', 'Partner')) { Check 'WhatIf.Checks' ($payload + @('-DiscoverCustomer', '-CustomerSelection', $choice)) }
Check 'WhatIf.Checks' ($payload + @('-DiscoverCustomer', '-InvitationScenario', 'TenantChanged'))
Check 'WhatIf.Checks' ($payload + @('-DiscoverCustomer', '-RequireApprovalConfirmation'))
Write-Output "PASS: $count isolated acceptance checks; no live browser, tenant, approval or CIPP changes."
