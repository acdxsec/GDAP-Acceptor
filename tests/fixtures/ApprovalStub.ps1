# Test fixture only. Never bundled; never loads the real module or uses network.
[CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'ExpectedCustomer')]
param(
    [Parameter(Mandatory)][string]$RelationshipId,
    [Parameter(Mandatory, ParameterSetName = 'ExpectedCustomer')][guid]$ExpectedTenantId,
    [Parameter(Mandatory, ParameterSetName = 'AuthenticatedCustomer')][switch]$ConfirmAuthenticatedTenant,
    [Parameter(Mandatory)][guid]$ExpectedPartnerTenantId,
    [switch]$PortalRequestDiagnostics,
    [System.Collections.IDictionary]$OutcomeState
)
if ($RelationshipId -cne 'synthetic-invitation' -or $ExpectedPartnerTenantId -ne [guid]'22222222-2222-2222-2222-222222222222') { throw 'Forwarded identities changed' }
if (-not $PSBoundParameters['Confirm'] -or $WhatIfPreference -or -not $PortalRequestDiagnostics) { throw 'Approval confirmation or diagnostics policy changed' }
if ($PSCmdlet.ParameterSetName -eq 'ExpectedCustomer' -and $ExpectedTenantId -ne [guid]'11111111-1111-1111-1111-111111111111') { throw 'Explicit customer changed' }
if ($PSCmdlet.ParameterSetName -eq 'AuthenticatedCustomer' -and -not $ConfirmAuthenticatedTenant) { throw 'Customer confirmation was disabled' }
Write-Host "STUB: validated $($PSCmdlet.ParameterSetName) forwarding"
switch ($env:GDAP_TEST_RESULT) {
    'active' { @{ relationship = @{ status = 'active' } } }
    'pending' { @{ relationship = @{ status = 'approvalPending' } } }
    'approved' { @{ relationship = @{ status = 'approved' } } }
    'activating' { @{ relationship = @{ status = 'activating' } } }
    'empty' { }
    'malformed' { @{ status = 'active' } }
    'error' { throw 'SYNTHETIC_SECRET_MUST_NOT_APPEAR' }
    default { throw 'Unknown fixture scenario' }
}
