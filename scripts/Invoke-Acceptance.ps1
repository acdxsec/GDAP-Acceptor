[CmdletBinding(DefaultParameterSetName = 'ExpectedCustomer')]
param(
    [Parameter(Mandatory)][string]$RelationshipId,
    [Parameter(Mandatory, ParameterSetName = 'ExpectedCustomer')][guid]$ExpectedTenantId,
    [Parameter(Mandatory, ParameterSetName = 'AuthenticatedCustomer')][switch]$ConfirmAuthenticatedTenant,
    [Parameter(Mandatory)][guid]$ExpectedPartnerTenantId
)
$ErrorActionPreference = 'Stop'
try {
    if ($PSVersionTable.PSVersion -lt [version]'7.6') { throw 'PowerShell 7.6 LTS or newer is required.' }
    $parameters = @{ RelationshipId = $RelationshipId; ExpectedPartnerTenantId = $ExpectedPartnerTenantId; Confirm = $true; PortalRequestDiagnostics = $true }
    if ($ConfirmAuthenticatedTenant) { $parameters.ConfirmAuthenticatedTenant = $true }
    else { $parameters.ExpectedTenantId = $ExpectedTenantId }
    $result = & "$PSScriptRoot/Approve-GdapRelationship.ps1" @parameters
    if (-not $result -or $result.relationship.status -ne 'active') { exit 2 }
    exit 0
} catch {
    # Never propagate raw portal response/error bodies to diagnostic exports.
    Write-Host 'Acceptance stopped. Verify the expected tenant and partner, then inspect the Microsoft invitation.'
    Write-Host 'The last [GDAP HTTP] or [GDAP stage] line identifies how far the run progressed. No automatic approval retry was performed.'
    exit 1
}
