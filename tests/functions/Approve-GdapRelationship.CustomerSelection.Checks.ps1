# Isolated identity-choice contracts. No browser, token, tenant or network access.
$ErrorActionPreference = 'Stop'
$customer = [guid]'11111111-1111-1111-1111-111111111111'
$partner = [guid]'22222222-2222-2222-2222-222222222222'
. "$PSScriptRoot/../../scripts/Approve-GdapRelationship.ps1" -RelationshipId 'selection-test' -ConfirmAuthenticatedTenant -ExpectedPartnerTenantId $partner
function ExpectFailure([scriptblock]$Action, [string]$Pattern) {
    try { & $Action | Out-Null } catch { if ($_.Exception.Message -like $Pattern) { return }; throw }
    throw "Expected failure: $Pattern"
}
$session = @{ Validated = $true; Source = 'LivePortalBootstrap'; TenantId = $customer.ToString() }
$selected = Confirm-GdapCustomerIdentity -Session $session -PartnerTenantId $partner -ReadConfirmation { 'CUSTOMER' }
if ($selected -ne $customer) { throw 'Wrong customer selected' }
ExpectFailure { Confirm-GdapCustomerIdentity $session $partner -ReadConfirmation { 'customer' } } '*selection cancelled*'
ExpectFailure { Confirm-GdapCustomerIdentity $session $partner -ReadConfirmation { '' } } '*selection cancelled*'
ExpectFailure { Confirm-GdapCustomerIdentity $session $customer -ReadConfirmation { throw 'Must not prompt' } } '*partner tenant*'
foreach ($change in @(@{ Validated = $false }, @{ Source = 'Cached' }, @{ TenantId = '' }, @{ TenantId = [guid]::Empty.ToString() })) {
    $bad = $session.Clone()
    foreach ($key in $change.Keys) { $bad[$key] = $change[$key] }
    ExpectFailure { Confirm-GdapCustomerIdentity $bad $partner -ReadConfirmation { throw 'Must not prompt' } } '*live validated customer*'
}
Write-Output 'PASS: explicit customer selection requires validated live identity and rejects cancellation and the partner tenant'
