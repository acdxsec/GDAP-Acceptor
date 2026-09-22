# Layout and equality evidence came from the user's live diagnostic. Values,
# numeric duration, and status below are synthetic, not a live response capture.
$ErrorActionPreference = 'Stop'
$tenant = [guid]'11111111-1111-1111-1111-111111111111'
$partner = [guid]'22222222-2222-2222-2222-222222222222'
$id = 'portal-contract-test'
. "$PSScriptRoot/../../scripts/Approve-GdapRelationship.ps1" -RelationshipId $id -ExpectedTenantId $tenant -ExpectedPartnerTenantId $partner
function Assert($Value, $Message) { if (-not $Value) { throw $Message } }
function NewPortalInvitation {
    @{ partner = @{ tenantId = $partner.ToString(); name = 'Synthetic partner' }; relationship = @{
        partnerGdapRelationshipId = $id; roles = @('33333333-3333-3333-3333-333333333333'); duration = 730
        autoExtendDuration = 'P180D'; status = 'approvalPending'; etag = 'synthetic-etag'; name = 'Synthetic relationship'
    } }
}
$session = @{ Validated = $true; TenantId = $tenant.ToString() }
$fixture = NewPortalInvitation
$evidence = Assert-GdapEvidence $fixture $id $tenant $partner $session
Assert ($evidence.Summary.Contains('33333333-3333-3333-3333-333333333333')) 'Portal role GUID missing from consent'
Assert ($evidence.Summary.Contains('730 (as returned by portal)')) 'Numeric duration was lost or converted'
$json = $fixture | ConvertTo-Json -Depth 10 | ConvertFrom-Json
Assert ((Assert-GdapEvidence $json $id $tenant $partner $session).Consent -ceq $evidence.Consent) 'Parsed JSON changed consent'
$script:posts = 0
$script:reads = 0
$script:mutation = ''
$script:writeFails = $false
$request = {
    param($p)
    if ($p.Method -ne 'Get') {
        $script:posts++
        Assert ($p.Method -eq 'Post' -and $script:reads -eq 2) 'Approval preceded consent recheck'
        Assert ($p.Path -ceq "/fd/GdapPartnerManage/CustomerServiceAdminApi/Web/v1/GranularAdminRelationships/$id/UpdateStatus") 'Wrong approval path'
        Assert ($p.Headers['If-Match'] -ceq 'synthetic-etag') 'Approval omitted ETag'
        Assert ($p.Body.Count -eq 1 -and $p.Body.status -ceq 'approved') 'Approval changed request terms'
        if ($script:writeFails) { throw 'Synthetic ambiguous transport failure' }
        return
    }
    $script:reads++
    $value = NewPortalInvitation
    if ($script:posts -gt 0) { $value.relationship.status = 'active' }
    if ($script:reads -eq 2) {
        switch ($script:mutation) {
            'duration' { $value.relationship.duration = 365 }
            'extension' { $value.relationship.autoExtendDuration = 'P0D' }
            'roles' { $value.relationship.roles = @('44444444-4444-4444-4444-444444444444') }
            'etag' { $value.relationship.etag = 'changed-etag' }
            'partner' { $value.partner.tenantId = $tenant.ToString() }
        }
    }
    $value
}
$params = @{ RelationshipId=$id; ExpectedTenantId=$tenant; ExpectedPartnerTenantId=$partner; Request=$request; GetSession={ $session }; Confirm=$false }
$null = Approve-GdapRelationship @params -WhatIf
Assert ($script:posts -eq 0) 'Portal preview submitted approval'
$script:reads = 0
$result = Approve-GdapRelationship @params
Assert ($result.relationship.status -eq 'active' -and $script:posts -eq 1 -and $script:reads -eq 3) 'Portal acceptance did not submit once and observe active'
foreach ($mutation in @('duration', 'extension', 'roles', 'etag', 'partner')) {
    $script:reads = 0; $script:posts = 0; $script:mutation = $mutation
    $message = $null
    try { $null = Approve-GdapRelationship @params } catch { $message = $_.Exception.Message }
    Assert ($null -ne $message -and $script:posts -eq 0) "Approval accepted changed consent: $mutation"
}
$script:reads = 0; $script:posts = 0; $script:mutation = ''; $script:writeFails = $true
$message = $null
try { $null = Approve-GdapRelationship @params } catch { $message = $_.Exception.Message }
Assert ($message -like 'Approval outcome is unknown*' -and $script:posts -eq 1) 'Ambiguous portal approval was retried'
foreach ($case in @('id','partner','late-role','empty-roles','role-object','boolean-duration','zero-duration','infinite-duration','negative-duration','partner-array','etag-array','conflicting-id','conflicting-partner','conflicting-roles','customer')) {
    $bad = NewPortalInvitation
    switch ($case) {
        'id' { $bad.relationship.partnerGdapRelationshipId = 'different' }
        'partner' { $bad.partner.tenantId = $tenant.ToString() }
        'late-role' { $bad.relationship.roles = @('33333333-3333-3333-3333-333333333333') * 14 + @('invalid') }
        'empty-roles' { $bad.relationship.roles = @() }
        'role-object' { $bad.relationship.roles = @(@{ roleDefinitionId = '33333333-3333-3333-3333-333333333333' }) }
        'boolean-duration' { $bad.relationship.duration = $true }
        'zero-duration' { $bad.relationship.duration = 0 }
        'infinite-duration' { $bad.relationship.duration = [double]::PositiveInfinity }
        'negative-duration' { $bad.relationship.duration = -1 }
        'partner-array' { $bad.partner.tenantId = @($partner.ToString()) }
        'etag-array' { $bad.relationship.etag = @('synthetic-etag') }
        'conflicting-id' { $bad.relationship.id = 'different' }
        'conflicting-partner' { $bad.relationship.partner = @{ tenantId = $tenant.ToString() } }
        'conflicting-roles' { $bad.relationship.accessDetails = @{ unifiedRoles = @() } }
        'customer' { $bad.relationship.customer = @{ tenantId = $partner.ToString() } }
    }
    $failed = $false
    try { $null = Assert-GdapEvidence $bad $id $tenant $partner $session } catch { $failed = $true }
    Assert $failed "Unsafe portal evidence accepted: $case"
}
Write-Output 'PASS: portal approval preserves request terms, submits once, observes active, and rejects consent changes; WhatIf and ambiguity checks preserved'
