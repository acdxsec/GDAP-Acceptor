# Field names are from the user's 2026-09-14 report; ALL values and nested
# partner/role examples below are synthetic. They do not validate a live mapping.
$ErrorActionPreference = 'Stop'
$tenant = [guid]'11111111-1111-1111-1111-111111111111'
$partner = [guid]'22222222-2222-2222-2222-222222222222'
$id = 'shape-test'
. "$PSScriptRoot/../../scripts/Approve-GdapRelationship.ps1" -RelationshipId $id -ExpectedTenantId $tenant -ExpectedPartnerTenantId $partner
function Assert($Value, $Message) { if (-not $Value) { throw $Message } }
$invitation = @{ partner = @{ tenantId = $partner.ToString(); displayName = 'SECRET_SENTINEL' }
    relationship = @{ autoExtendDuration = 'P180D'; duration = 'P730D'; etag = 'SECRET_SENTINEL'
        name = 'SECRET_SENTINEL'; partnerGdapRelationshipId = $id
        roles = @(@{ roleDefinitionId = '33333333-3333-3333-3333-333333333333'; displayName = 'SECRET_SENTINEL' })
        status = 'approvalPending'
    }
}
$diagnostic = Get-GdapInvitationContractDiagnostic $invitation $id $partner
$jsonObject = $invitation | ConvertTo-Json -Depth 10 | ConvertFrom-Json
Assert ((Get-GdapInvitationContractDiagnostic $jsonObject $id $partner) -ceq $diagnostic) 'JSON object/array diagnostics differ from dictionary fixture'
Assert ($diagnostic.Contains('partnerGdapRelationshipIdMatchesExpected=True')) 'Missing relationship equality diagnostic'
Assert ($diagnostic.Contains('partner.tenantId:string nonempty=True guid=True matchesExpectedPartner=True')) 'Missing partner equality diagnostic'
Assert ($diagnostic.Contains('relationship.roles:array count=1')) 'Missing role collection kind'
Assert ($diagnostic.Contains('relationship.roles[0].roleDefinitionId:string nonempty=True guid=True')) 'Missing role element field types'
foreach ($privateValue in @('SECRET_SENTINEL', $id, $partner.ToString(), '33333333-3333-3333-3333-333333333333', 'P730D', 'approvalPending')) {
    Assert (-not $diagnostic.Contains($privateValue)) 'Diagnostic exposed a response value'
}
$wrong = Get-GdapInvitationContractDiagnostic $invitation 'different-id' $tenant
Assert ($wrong.Contains('partnerGdapRelationshipIdMatchesExpected=False')) 'Different relationship ID reported as matching'
Assert (-not $wrong.Contains('matchesExpectedPartner=True')) 'Different partner reported as matching'
Assert ((Get-GdapInvitationContractDiagnostic $null $id $partner).Contains('partner:null')) 'Null response diagnostic failed'
$invitation.relationship.roles = @()
Assert ((Get-GdapInvitationContractDiagnostic $invitation $id $partner).Contains('relationship.roles:array count=0')) 'Empty role array collapsed to null'
$invitation.relationship.roles = @('SECRET_SENTINEL', 'SECRET_SENTINEL', 'SECRET_SENTINEL', 'SECRET_SENTINEL')
$sampled = Get-GdapInvitationContractDiagnostic $invitation $id $partner
Assert ($sampled.Contains('relationship.roles:array count=4') -and -not $sampled.Contains('relationship.roles[3]')) 'Role diagnostics are not bounded'
$posts = [Collections.Generic.List[string]]::new()
$invitation.relationship.partnerGdapRelationshipId = 'different-id'
$request = { param($p); if ($p.Method -ne 'Get') { $posts.Add('write'); throw 'Unexpected approval' }; $invitation }.GetNewClosure()
$caught = $null
try {
    $null = Approve-GdapRelationship -RelationshipId $id -ExpectedTenantId $tenant -ExpectedPartnerTenantId $partner -Request $request -GetSession { @{ Validated = $true; TenantId = $tenant.ToString() } } -Confirm:$false
} catch { $caught = $_.Exception.Message }
Assert ($null -ne $caught -and $caught.Contains('Contract diagnostic:') -and $caught.Contains('Approval mapping remains unverified')) 'Unverified portal shape did not stop with diagnostics'
Assert ($posts.Count -eq 0) 'Unverified portal shape allowed approval'
Assert (-not $caught.Contains('SECRET_SENTINEL')) 'Failure exposed a response value'
Write-Output 'PASS: mismatched portal identity stops before approval and reports bounded structure/equality only'
