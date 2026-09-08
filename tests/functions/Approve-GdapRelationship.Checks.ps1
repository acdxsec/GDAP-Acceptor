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
Write-Output 'PASS: identifier, consent, identity, resume, timeout, ambiguous-write, WhatIf, disable, and display regression checks'
