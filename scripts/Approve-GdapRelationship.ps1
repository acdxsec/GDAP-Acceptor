<#
.SYNOPSIS
    Accept a GDAP invitation with explicit customer and partner checks.
.DESCRIPTION
    The portal adapter is undocumented. Schema drift, missing identity evidence,
    changed consent, or ambiguous writes stop automation. No POST is retried.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param (
    [Parameter(Mandatory, Position = 0)] [Alias('InvitationUri')] [string]$RelationshipId,
    [Parameter(Mandatory)] [Alias('TenantId')] [guid]$ExpectedTenantId,
    [Parameter(Mandatory)] [guid]$ExpectedPartnerTenantId,
    [string]$Username,
    [ValidateRange(30, 1800)] [int]$AuthenticationTimeoutSeconds = 300,
    [ValidateRange(0, 1800)] [int]$ActivationTimeoutSeconds = 180,
    [ValidateRange(1, 30)] [int]$PollIntervalSeconds = 3,
    [string]$BrowserPath,
    [switch]$ReuseSession,
    [switch]$DisableAutomatedApproval
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-GdapRelationshipId {
    [CmdletBinding()]
    param ([Parameter(Mandatory, ValueFromPipeline)] [string]$InputObject)
    process {
        $value = $InputObject.Trim()
        if ($value.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) {
            if ($value -cnotmatch '^https://admin\.microsoft\.com/AdminPortal/Home#/partners/invitation/granularAdminRelationships/(?<id>[^/?#]+)$') {
                throw 'Unsupported GDAP invitation URL.'
            }
            $value = [uri]::UnescapeDataString($Matches.id)
        }
        # Opaque, case-preserving, bounded segment: never truncate or double decode.
        if ($value -cnotmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,255}$') { throw 'Invalid GDAP relationship identifier.' }
        $value
    }
}
function Get-GdapField {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    if ($Object -is [System.Collections.IDictionary]) { return $Object[$Name] }
    $property = $Object.PSObject.Properties[$Name]
    if ($property) { return $property.Value }
    return $null
}
function ConvertTo-GdapDisplayText {
    param([AllowNull()]$Value)
    [regex]::Replace([string]$Value, '[\p{Cc}\p{Cf}]', '')
}
function Assert-GdapEvidence {
    param($Invitation, [string]$Id, [guid]$Tenant, [guid]$Partner, $Session)
    if ($Tenant -eq [guid]::Empty -or $Partner -eq [guid]::Empty) { throw 'Expected identities cannot be empty.' }
    if ((Get-GdapField $Session 'Validated') -ne $true -or
        [string](Get-GdapField $Session 'TenantId') -ine $Tenant.ToString()) {
        throw 'The authenticated portal tenant does not match the expected customer tenant, or the session is not validated.'
    }
    $relationship = Get-GdapField $Invitation 'relationship'
    if ([string](Get-GdapField $relationship 'id') -cne $Id) { throw 'The portal returned a missing or different relationship ID.' }
    # Contract v1 must be checked against sanitized live evidence before promotion.
    # Do not infer partner identity from a name or guess paths on schema changes.
    $observedPartner = Get-GdapField (Get-GdapField $relationship 'partner') 'tenantId'
    if ([string]$observedPartner -ine $Partner.ToString()) { throw 'Partner identity is missing or does not match the expected partner.' }
    $boundTenant = Get-GdapField (Get-GdapField $relationship 'customer') 'tenantId'
    if ($boundTenant -and [string]$boundTenant -ine $Tenant.ToString()) { throw 'Invitation is bound to a different customer tenant.' }
    $roles = @(Get-GdapField (Get-GdapField $relationship 'accessDetails') 'unifiedRoles')
    if ($roles.Count -eq 0) { throw 'Requested roles are missing.' }
    $roleIds = @($roles | ForEach-Object {
        $roleId = [string](Get-GdapField $_ 'roleDefinitionId')
        $parsedRole = [guid]::Empty
        if (-not [guid]::TryParseExact($roleId, 'D', [ref]$parsedRole) -or $parsedRole -eq [guid]::Empty) { throw 'Invalid requested role evidence.' }
        $parsedRole.ToString()
    } | Sort-Object -Unique)
    $duration = [string](Get-GdapField $relationship 'duration')
    $extension = [string](Get-GdapField $relationship 'autoExtendDuration')
    if (-not $duration -or -not $extension) { throw 'Relationship duration or automatic-extension evidence is missing.' }
    [pscustomobject]@{
        Status = [string](Get-GdapField $relationship 'status')
        ETag = [string](Get-GdapField $relationship 'etag')
        Consent = ($roleIds -join ',') + '|' + $duration + '|' + $extension
        Summary = "Customer tenant: $Tenant; partner tenant: $Partner; relationship: $Id; roles: $($roleIds -join ', '); duration: $duration; extension: $extension"
    }
}
function Approve-GdapRelationship {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param (
        [Parameter(Mandatory, Position = 0)] [string]$RelationshipId,
        [Parameter(Mandatory)] [guid]$ExpectedTenantId,
        [Parameter(Mandatory)] [guid]$ExpectedPartnerTenantId,
        [Parameter(Mandatory)] [scriptblock]$GetSession,
        [Parameter(Mandatory)] [scriptblock]$Request,
        [ValidateRange(0, 1800)] [int]$ActivationTimeoutSeconds = 180,
        [ValidateRange(1, 30)] [int]$PollIntervalSeconds = 3,
        [switch]$DisableAutomatedApproval
    )
    $id = Resolve-GdapRelationshipId $RelationshipId
    $path = "/fd/commerceMgmt2/partnermanage/gdapInvitations/$($id)?api-version=3.0"
    Write-Host "Microsoft invitation: https://admin.microsoft.com/AdminPortal/Home#/partners/invitation/granularAdminRelationships/$id"
    $invitation = & $Request @{ Path = $path; Method = 'Get' }
    $evidence = Assert-GdapEvidence $invitation $id $ExpectedTenantId $ExpectedPartnerTenantId (& $GetSession)
    if ($evidence.Status -eq 'active') { return $invitation }
    if ($evidence.Status -notin @('approvalPending', 'approved', 'activating')) { throw 'The relationship is not in an approvable or activating state.' }
    if ($evidence.Status -eq 'approvalPending') {
        if ($DisableAutomatedApproval) { throw 'Automated approval is disabled. Use the Microsoft invitation link.' }
        if (-not $evidence.ETag) { throw 'The invitation did not supply an ETag.' }
        if (-not $PSCmdlet.ShouldProcess((ConvertTo-GdapDisplayText $evidence.Summary), 'Approve GDAP access')) { return }
        # Re-read after human confirmation. Changed consent requires another prompt.
        $fresh = & $Request @{ Path = $path; Method = 'Get' }
        $checked = Assert-GdapEvidence $fresh $id $ExpectedTenantId $ExpectedPartnerTenantId (& $GetSession)
        if ($checked.Status -eq 'active') { return $fresh }
        if ($checked.Status -eq 'approvalPending') {
            if ($checked.Consent -cne $evidence.Consent -or $checked.ETag -cne $evidence.ETag) { throw 'Invitation changed during confirmation. Inspect and confirm it again.' }
            try {
                $null = & $Request @{
                    Method = 'Post'
                    Path = "/fd/GdapPartnerManage/CustomerServiceAdminApi/Web/v1/GranularAdminRelationships/$id/UpdateStatus"
                    Headers = @{ 'If-Match' = $checked.ETag; 'x-adminapp-request' = "/partners/invitation/granularAdminRelationships/$id" }
                    Body = @{ status = 'approved' }
                }
            } catch { throw 'Approval outcome is unknown. No write was retried. Inspect the Microsoft invitation before retrying.' }
        } elseif ($checked.Status -notin @('approved', 'activating')) { throw 'Relationship state changed during confirmation; approval stopped.' }
    }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $result = & $Request @{ Path = $path; Method = 'Get' }
        $observed = Assert-GdapEvidence $result $id $ExpectedTenantId $ExpectedPartnerTenantId (& $GetSession)
        if ($observed.Status -eq 'active') { return $result }
        if ($observed.Status -notin @('approvalPending', 'approved', 'activating')) { throw 'Unexpected relationship state; inspect the Microsoft invitation.' }
        if ($timer.Elapsed.TotalSeconds -ge $ActivationTimeoutSeconds) { break }
        Start-Sleep -Seconds $PollIntervalSeconds
    } while ($true)
    throw 'Activation is still pending. Approval was not retried; inspect the relationship before continuing.'
}

if ($MyInvocation.InvocationName -ne '.') {
    $RelationshipId = Resolve-GdapRelationshipId $RelationshipId
    if ($ExpectedTenantId -eq [guid]::Empty -or $ExpectedPartnerTenantId -eq [guid]::Empty) { throw 'Expected identities cannot be empty.' }
    $modulePath = Join-Path $PSScriptRoot '../M365Internals/M365Internals.psd1'
    $module = Get-Module M365Internals
    if (-not $module) { $module = Import-Module $modulePath -PassThru -ErrorAction Stop }
    if (-not $ReuseSession) {
        $parameters = @{ TenantId = $ExpectedTenantId.ToString(); PrivateSession = $true; TimeoutSeconds = $AuthenticationTimeoutSeconds }
        if ($Username) { $parameters.Username = $Username }
        if ($BrowserPath) { $parameters.BrowserPath = $BrowserPath }
        $null = Connect-M365PortalByBrowser @parameters
    }
    $getSession = {
        & $module {
            $connection = $script:m365PortalConnection
            $cookieTenant = $script:m365PortalSession.Cookies.GetCookies('https://admin.cloud.microsoft/') | Where-Object Name -eq 's.UserTenantId' | Select-Object -First 1
            @{ TenantId = [string]$cookieTenant.Value; Validated = ($connection.Validated -eq $true -and $connection.TenantId -ieq $cookieTenant.Value) }
        }
    }.GetNewClosure()
    $request = {
        param($Parameters)
        # Suppress the portal helper's general authentication healing/retry path.
        & $module { param($p) Invoke-M365PortalRequest @p -SkipAutoHeal -SkipConnectionRefresh } $Parameters
    }.GetNewClosure()
    $approval = @{
        RelationshipId = $RelationshipId; ExpectedTenantId = $ExpectedTenantId; ExpectedPartnerTenantId = $ExpectedPartnerTenantId
        GetSession = $getSession; Request = $request; ActivationTimeoutSeconds = $ActivationTimeoutSeconds
        PollIntervalSeconds = $PollIntervalSeconds; DisableAutomatedApproval = $DisableAutomatedApproval; WhatIf = [bool]$WhatIfPreference
    }
    if ($PSBoundParameters.ContainsKey('Confirm')) { $approval.Confirm = [bool]$PSBoundParameters.Confirm }
    Approve-GdapRelationship @approval
}
