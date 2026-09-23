# Test-only entry point: exercise the real approval function with synthetic
# portal requests. No browser, module, credentials or network is used.
[CmdletBinding(SupportsShouldProcess)]
param([string]$RelationshipId, [switch]$ConfirmAuthenticatedTenant, [guid]$ExpectedPartnerTenantId, [switch]$PortalRequestDiagnostics, [System.Collections.IDictionary]$OutcomeState)
$ErrorActionPreference = 'Stop'
$tenant = [guid]'11111111-1111-1111-1111-111111111111'
$id = $RelationshipId
$partner = $ExpectedPartnerTenantId
. "$PSScriptRoot/ApprovalCore.ps1" -RelationshipId $id -ExpectedTenantId $tenant -ExpectedPartnerTenantId $partner -OutcomeState $OutcomeState
$script:posts = 0
$scenario = $env:GDAP_TEST_RESULT
if ($scenario -eq 'abnormal-exit') { exit 17 }
$request = {
    param($p)
    if ($scenario -eq 'preflight-error') { throw 'SYNTHETIC_SECRET_MUST_NOT_APPEAR' }
    if ($p.Method -eq 'Post') {
        $script:posts++
        Write-Host 'SYNTHETIC_POST'
        if ($scenario -eq 'post-timeout') { throw [TimeoutException]::new('SYNTHETIC_SECRET_MUST_NOT_APPEAR') }
        return
    }
    if ($script:posts -gt 0 -and $scenario -eq 'readback-error') { throw 'SYNTHETIC_SECRET_MUST_NOT_APPEAR' }
    $status = if ($scenario -eq 'already-active' -or ($script:posts -gt 0 -and $scenario -in @('success', 'cleanup-error'))) { 'active' }
        elseif ($scenario -eq 'already-approved') { 'approved' }
        elseif ($scenario -eq 'already-activating' -or $script:posts -gt 0) { 'activating' }
        else { 'approvalPending' }
    @{ relationship = @{ id = $id; status = $status; etag = 'e1'; partner = @{ tenantId = $partner.ToString() }
        customer = @{}; duration = 'P730D'; autoExtendDuration = 'P180D'
        accessDetails = @{ unifiedRoles = @(@{ roleDefinitionId = '33333333-3333-3333-3333-333333333333' }) } } }
}
$session = { @{ Validated = $true; TenantId = $(if ($script:posts -gt 0 -and $scenario -eq 'readback-identity') { $partner.ToString() } else { $tenant.ToString() }) } }
try {
    Approve-GdapRelationship -RelationshipId $id -ExpectedTenantId $tenant -ExpectedPartnerTenantId $partner -Request $request -GetSession $session -ActivationTimeoutSeconds 0 -Confirm:$false -WhatIf:($scenario -eq 'cancelled') -OutcomeState $OutcomeState
} finally {
    if ($scenario -eq 'cleanup-error') { throw 'SYNTHETIC_SECRET_MUST_NOT_APPEAR' }
}
