# Self-contained regression checks. All requests are synthetic; no module or tenant access.
$ErrorActionPreference = 'Stop'
$tenant = [guid]'11111111-1111-1111-1111-111111111111'
$partner = [guid]'22222222-2222-2222-2222-222222222222'
$id = '5d027261-d21f-4aa9-b7db-7fa1f56fb163-8777b240-c6f0-4469-9e98-a3205431b836'
. (Join-Path $PSScriptRoot '../../scripts/Approve-GdapRelationship.ps1') -RelationshipId $id -ExpectedTenantId $tenant -ExpectedPartnerTenantId $partner
function Assert($Condition, $Message) { if (-not $Condition) { throw $Message } }
function ExpectFailure([scriptblock]$Action, [string]$Pattern) {
    try { & $Action | Out-Null } catch { if ($_.Exception.Message -like $Pattern) { return }; throw }
    throw "Expected failure: $Pattern"
}
function NewInvitation([string]$State = 'approvalPending') {
    @{ relationship = @{ id = $id; status = $State; etag = 'e1'; partner = @{ tenantId = $partner.ToString() }
        customer = @{}; duration = 'P730D'; autoExtendDuration = 'P180D'
        accessDetails = @{ unifiedRoles = @(@{ roleDefinitionId = '33333333-3333-3333-3333-333333333333' }) } } }
}
function ResetScenario {
    $script:reads = 0; $script:posts = 0; $script:sessions = 0
    $script:initial = 'approvalPending'; $script:final = 'active'; $script:change = ''; $script:writeFails = $false
}
$request = {
    param($p)
    if ($p.Method -eq 'Post') {
        $script:posts++
        Assert ($p.Headers['If-Match'] -eq 'e1') 'ETag not forwarded'
        Assert ($p.Path.Contains($id)) 'Identifier truncated in POST'
        if ($script:writeFails) { throw 'synthetic transport error with private details' }
        return
    }
    $script:reads++
    $state = if ($script:reads -le 2 -and $script:initial -eq 'approvalPending') { $script:initial } elseif ($script:reads -eq 1) { $script:initial } else { $script:final }
    $value = NewInvitation $state
    switch ($script:change) {
        'partner' { $value.relationship.partner.tenantId = $tenant.ToString() }
        'customer' { $value.relationship.customer.tenantId = $partner.ToString() }
        'identity' { $value.relationship.id = 'different' }
        'roles' { $value.relationship.accessDetails.unifiedRoles = @() }
        'etag' { if ($script:reads -eq 2) { $value.relationship.etag = 'e2' } }
        'consent' { if ($script:reads -eq 2) { $value.relationship.duration = 'P900D' } }
    }
    $value
}
$session = {
    $script:sessions++
    $observed = if ($script:change -eq 'session' -or ($script:change -eq 'switch' -and $script:sessions -gt 1)) { $partner } else { $tenant }
    @{ Validated = $true; TenantId = $observed.ToString() }
}
$argsForApproval = @{ RelationshipId=$id; ExpectedTenantId=$tenant; ExpectedPartnerTenantId=$partner; Request=$request; GetSession=$session; Confirm=$false; ActivationTimeoutSeconds=0 }
Assert ((Resolve-GdapRelationshipId $id) -ceq $id) 'Composite ID changed'
Assert ((Resolve-GdapRelationshipId "https://admin.microsoft.com/AdminPortal/Home#/partners/invitation/granularAdminRelationships/$id") -ceq $id) 'Invitation parsing failed'
Assert ((Get-GdapInvitationShape ([pscustomobject]@{})) -ceq 'root:object fields=[]; relationship:null fields=[]') 'Empty response shape diagnostic failed'
$shape = Get-GdapInvitationShape @{ relationship = @{ id = 'SECRET_SENTINEL'; status = 'SECRET_SENTINEL' }; data = 'SECRET_SENTINEL' }
Assert ($shape -ceq 'root:object fields=[data,relationship]; relationship:object fields=[id,status]') 'Response shape included values or omitted field names'
foreach ($bad in @('text with 11111111-1111-1111-1111-111111111111', '../bad', 'a%2fb', 'a?b', 'a#b', 'https://evil.example/id', ('a' * 257))) {
    ExpectFailure { Resolve-GdapRelationshipId $bad } '*'
}
ResetScenario
$result = Approve-GdapRelationship @argsForApproval
Assert ($result.relationship.status -eq 'active' -and $script:posts -eq 1) 'Normal acceptance failed'
foreach ($state in @('active', 'approved', 'activating')) {
    ResetScenario; $script:initial = $state
    $null = Approve-GdapRelationship @argsForApproval
    Assert ($script:posts -eq 0) "Resuming $state posted again"
}
foreach ($mutation in @('session','switch','partner','customer','identity','roles','etag','consent')) {
    ResetScenario; $script:change = $mutation
    ExpectFailure { Approve-GdapRelationship @argsForApproval } '*'
    Assert ($script:posts -eq 0) "Unsafe approval after $mutation mismatch"
}
ResetScenario; $script:final = 'activating'
ExpectFailure { Approve-GdapRelationship @argsForApproval } 'Activation is still pending*'
Assert ($script:posts -eq 1) 'Activation timeout retried approval'
ResetScenario; $script:writeFails = $true
ExpectFailure { Approve-GdapRelationship @argsForApproval } 'Approval outcome is unknown*'
Assert ($script:posts -eq 1) 'Ambiguous write retried'
ResetScenario
$null = Approve-GdapRelationship @argsForApproval -WhatIf
Assert ($script:posts -eq 0) 'WhatIf wrote'
ResetScenario
ExpectFailure { Approve-GdapRelationship @argsForApproval -DisableAutomatedApproval } 'Automated approval is disabled*'
Assert ($script:posts -eq 0) 'Emergency disable wrote'
ResetScenario; $script:initial = 'expired'
ExpectFailure { Approve-GdapRelationship @argsForApproval } '*not in an approvable*'
Assert ($script:posts -eq 0) 'Expired invite wrote'
Assert ((ConvertTo-GdapDisplayText "hello$([char]27)[31m$([char]10)world") -notmatch '[\p{Cc}\p{Cf}]') 'Terminal controls retained'
Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop
Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop
$connection = [pscustomobject]@{ TenantId = $tenant.ToString(); Validated = $true }
$identityWeb = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
$script:identityReads = 0
$script:identityContent = '{"TID":"' + $tenant.ToString() + '"}'
$identityRequest = {
    param($p)
    Assert ($p.Method -eq 'Get' -and $p.RawResponse) 'Identity check was not a raw GET'
    Assert ([object]::ReferenceEquals($p.WebSession, $identityWeb)) 'Identity check used another session'
    Assert ($p.Path -in @('/adminportal/home/ClassicModernAdminDataStream?ref=/homepage', '/admin/api/coordinatedbootstrap/shellinfo')) 'Unexpected identity path'
    $script:identityReads++
    @{ StatusCode = 200; Content = $script:identityContent }
}
$identity = Get-GdapPortalSessionEvidence $connection $identityWeb $identityRequest
Assert ($identity.Validated -and $identity.TenantId -eq $tenant.ToString()) 'Cookie-less live identity rejected'
Assert ($script:identityReads -eq 1) 'Live identity was not read'
$script:identityContent = '{"TID":"' + $partner.ToString() + '"}'
ExpectFailure { Get-GdapPortalSessionEvidence $connection $identityWeb $identityRequest } '*active portal tenant changed*'
Assert ($script:identityReads -eq 2) 'Identity was cached instead of rechecked'
ExpectFailure { Get-GdapPortalSessionEvidence $connection $null $identityRequest } '*web session is missing*'
ExpectFailure { Get-GdapPortalSessionEvidence $null $identityWeb $identityRequest } '*session validation is not confirmed*'
ExpectFailure { Get-GdapPortalSessionEvidence @{ Validated = $false; TenantId = $tenant } $identityWeb $identityRequest } '*session validation is not confirmed*'
ExpectFailure { Get-GdapPortalSessionEvidence @{ Validated = $true; TenantId = 'not-a-guid' } $identityWeb $identityRequest } '*connection tenant ID is missing or invalid*'
ExpectFailure { Get-GdapPortalSessionEvidence $connection $identityWeb } '*identity check is unavailable*'
foreach ($content in @(
    ('{"TID":"' + $tenant.ToString() + '"}'),
    ('{"TID": "' + $tenant.ToString() + '"}'),
    ('\"TID\":\"' + $tenant.ToString() + '\"'),
    ('O365.TID="' + $tenant.ToString() + '"'),
    ('O365.TID=\"' + $tenant.ToString() + '\"')
)) {
    Assert ((Get-GdapBootstrapTenantId $content) -eq $tenant.ToString()) 'Module-supported bootstrap format rejected'
}
Assert ($null -eq (Get-GdapBootstrapTenantId '<html>Sign in</html>')) 'HTML shell accepted as tenant identity'
Assert ($null -eq (Get-GdapBootstrapTenantId '{"unrelatedId":"11111111-1111-1111-1111-111111111111"}')) 'Unrelated GUID accepted as tenant identity'
ExpectFailure { Get-GdapBootstrapTenantId '{"TID":"00000000-0000-0000-0000-000000000000"}' } '*invalid tenant ID*'
ExpectFailure { Get-GdapBootstrapTenantId '{"TID":"11111111-1111-1111-1111-111111111111","other":{"TID":"22222222-2222-2222-2222-222222222222"}}' } '*conflicting tenant IDs*'
# The public approval function must not write if the live tenant changes
# between initial inspection and the pre-write consent recheck.
ResetScenario
$script:identityReads = 0
$changingIdentityRequest = {
    param($p)
    $script:identityReads++
    $live = if ($script:identityReads -eq 1) { $tenant } else { $partner }
    @{ StatusCode = 200; Content = '{"TID":"' + $live.ToString() + '"}' }
}
$changingSession = { Get-GdapPortalSessionEvidence $connection $identityWeb $changingIdentityRequest }
$changingApproval = $argsForApproval.Clone()
$changingApproval.GetSession = $changingSession
ExpectFailure { Approve-GdapRelationship @changingApproval } '*active portal tenant changed*'
Assert ($script:posts -eq 0 -and $script:identityReads -eq 2) 'Tenant switch reached approval or was not rechecked'
Write-Output 'PASS: identifier, consent, live identity, resume, timeout, ambiguous-write, WhatIf, disable, and display regression checks'
