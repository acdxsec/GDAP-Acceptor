[CmdletBinding(DefaultParameterSetName = 'ExpectedCustomer')]
param(
    [Parameter(Mandatory)][string]$RelationshipId,
    [Parameter(Mandatory, ParameterSetName = 'ExpectedCustomer')][guid]$ExpectedTenantId,
    [Parameter(Mandatory, ParameterSetName = 'AuthenticatedCustomer')][switch]$ConfirmAuthenticatedTenant,
    [Parameter(Mandatory)][guid]$ExpectedPartnerTenantId
)
$ErrorActionPreference = 'Stop'
$outcomeState = @{ RequiresReview = $false }
try {
    if ($PSVersionTable.PSVersion -lt [version]'7.6') { throw 'PowerShell 7.6 LTS or newer is required.' }
    $parameters = @{ RelationshipId = $RelationshipId; ExpectedPartnerTenantId = $ExpectedPartnerTenantId; Confirm = $true; PortalRequestDiagnostics = $true; OutcomeState = $outcomeState }
    if ($ConfirmAuthenticatedTenant) { $parameters.ConfirmAuthenticatedTenant = $true }
    else { $parameters.ExpectedTenantId = $ExpectedTenantId }
    $result = & "$PSScriptRoot/Approve-GdapRelationship.ps1" @parameters
    if ($result -and $result.relationship.status -eq 'active') { exit 0 }
    if ($outcomeState.RequiresReview -or $result) {
        Write-Host 'Approval outcome requires review. Inspect Microsoft/CIPP before using queue resolve. Do not retry approval.'
        exit 3
    }
    # Empty result with no submission means confirmation was declined.
    exit 2
} catch {
    # Never propagate raw portal response/error bodies to diagnostic exports.
    Write-Host 'Acceptance stopped. Verify the expected tenant and partner, then inspect the Microsoft invitation.'
    Write-Host 'The last [GDAP HTTP] or [GDAP stage] line identifies how far the run progressed. No automatic approval retry was performed.'
    if ($outcomeState.RequiresReview) {
        Write-Host 'Approval may have been submitted or is still activating. Inspect Microsoft/CIPP before using queue resolve. Do not retry approval.'
        exit 3
    }
    exit 2
}
