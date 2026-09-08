[CmdletBinding()]
param([Parameter(Mandatory)][string]$RelationshipId, [Parameter(Mandatory)][guid]$ExpectedTenantId, [Parameter(Mandatory)][guid]$ExpectedPartnerTenantId)
$ErrorActionPreference = 'Stop'
try {
    if ($PSVersionTable.PSVersion -lt [version]'7.6') { throw 'PowerShell 7.6 LTS or newer is required.' }
    $result = & "$PSScriptRoot/Approve-GdapRelationship.ps1" -RelationshipId $RelationshipId -ExpectedTenantId $ExpectedTenantId -ExpectedPartnerTenantId $ExpectedPartnerTenantId -Confirm
    if (-not $result -or $result.relationship.status -ne 'active') { exit 2 }
    exit 0
} catch {
    # Never propagate raw portal response/error bodies to diagnostic exports.
    Write-Host 'Acceptance stopped. Verify the expected tenant and partner, then inspect the Microsoft invitation.'
    exit 1
}
