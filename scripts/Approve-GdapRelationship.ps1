<#
.SYNOPSIS
    Accept a GDAP invitation with explicit customer and partner checks.
.DESCRIPTION
    The portal adapter is undocumented. Schema drift, missing identity evidence,
    changed consent, or ambiguous writes stop automation. No POST is retried.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'ExpectedCustomer')]
param (
    [Parameter(Mandatory, Position = 0)] [Alias('InvitationUri')] [string]$RelationshipId,
    [Parameter(Mandatory, ParameterSetName = 'ExpectedCustomer')] [Alias('TenantId')] [guid]$ExpectedTenantId = [guid]::Empty,
    [Parameter(Mandatory, ParameterSetName = 'AuthenticatedCustomer')] [switch]$ConfirmAuthenticatedTenant,
    [Parameter(Mandatory)] [guid]$ExpectedPartnerTenantId,
    [string]$Username,
    [ValidateRange(30, 1800)] [int]$AuthenticationTimeoutSeconds = 300,
    [ValidateRange(5, 300)] [int]$InvitationTimeoutSeconds = 60,
    [ValidateRange(0, 1800)] [int]$ActivationTimeoutSeconds = 180,
    [ValidateRange(1, 30)] [int]$PollIntervalSeconds = 3,
    [string]$BrowserPath,
    [switch]$PortalRequestDiagnostics,
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
    param($Object, [string]$Name, [switch]$NoEnumerate)
    if ($null -eq $Object) { return $null }
    if ($Object -is [System.Collections.IDictionary]) {
        if ($NoEnumerate) { return ,$Object[$Name] }
        return $Object[$Name]
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($property) {
        if ($NoEnumerate) { return ,$property.Value }
        return $property.Value
    }
    return $null
}
function ConvertTo-GdapDisplayText {
    param([AllowNull()]$Value)
    [regex]::Replace([string]$Value, '[\p{Cc}\p{Cf}]', '')
}
function Confirm-GdapCustomerIdentity {
    param(
        [Parameter(Mandatory)]$Session,
        [Parameter(Mandatory)][guid]$PartnerTenantId,
        [scriptblock]$ReadConfirmation = { Read-Host 'Inspect the customer account in the browser. Type CUSTOMER to use this tenant, or anything else to cancel' }
    )
    $tenant = [guid]::Empty
    if ((Get-GdapField $Session 'Validated') -ne $true -or
        (Get-GdapField $Session 'Source') -cne 'LivePortalBootstrap' -or
        -not [guid]::TryParseExact([string](Get-GdapField $Session 'TenantId'), 'D', [ref]$tenant) -or
        $tenant -eq [guid]::Empty) {
        throw 'A live validated customer tenant is required before opening the invitation.'
    }
    if ($tenant -eq $PartnerTenantId) { throw 'You signed into the partner tenant. Sign into the customer tenant instead. Invitation was not opened.' }
    Write-Host "Authenticated CUSTOMER tenant: $tenant"
    Write-Host 'This selection binds the invitation to the tenant you just signed into. No invitation has been opened or approved yet.'
    if ((& $ReadConfirmation) -cne 'CUSTOMER') { throw 'Customer selection cancelled. Invitation was not opened.' }
    return $tenant
}
function Get-GdapBootstrapTenantId {
    param([AllowEmptyString()][string]$Content)
    # The same bootstrap identity representations recognized by the pinned
    # module's Set-M365PortalConnectionSettings, but reject conflicting IDs.
    $ids = @(
        foreach ($pattern in @(
            'O365\.TID=\\"(?<value>[0-9a-fA-F-]{36})\\"',
            'O365\.TID="(?<value>[0-9a-fA-F-]{36})"',
            '\\"TID\\"\s*:\s*\\"(?<value>[0-9a-fA-F-]{36})\\"',
            '"TID"\s*:\s*"(?<value>[0-9a-fA-F-]{36})"'
        )) {
            foreach ($match in [regex]::Matches($Content, $pattern)) {
                $id = [guid]::Empty
                if (-not [guid]::TryParseExact($match.Groups['value'].Value, 'D', [ref]$id) -or $id -eq [guid]::Empty) {
                    throw 'The live portal identity response contains an invalid tenant ID. Approval stopped.'
                }
                $id.ToString()
            }
        }
    ) | Sort-Object -Unique
    $ids = @($ids)
    if ($ids.Count -gt 1) { throw 'The live portal identity response contains conflicting tenant IDs. Approval stopped.' }
    if ($ids.Count -eq 1) { return $ids[0] }
    return $null
}
function Get-GdapPortalSessionEvidence {
    param($Connection, $WebSession, [scriptblock]$Request)
    if ((Get-GdapField $Connection 'Validated') -ne $true) {
        throw 'Portal session validation is not confirmed. Approval stopped.'
    }
    $connectionTenant = [guid]::Empty
    if (-not [guid]::TryParseExact([string](Get-GdapField $Connection 'TenantId'), 'D', [ref]$connectionTenant) -or $connectionTenant -eq [guid]::Empty) {
        throw 'The portal connection tenant ID is missing or invalid. Approval stopped.'
    }
    if ($null -eq $WebSession -or $null -eq $WebSession.Cookies) {
        throw 'The authenticated portal web session is missing. Approval stopped.'
    }
    if (-not $Request) { throw 'The live portal identity check is unavailable. Approval stopped.' }
    # Cookies are transport details, not a required tenant-ID representation.
    # Read the active tenant using the exact authenticated session, each time
    # evidence is requested (including the pre-write consent recheck).
    foreach ($path in @('/adminportal/home/ClassicModernAdminDataStream?ref=/homepage', '/admin/api/coordinatedbootstrap/shellinfo')) {
        try {
            $response = & $Request @{ Path = $path; Method = 'Get'; RawResponse = $true; WebSession = $WebSession }
        } catch { throw 'The live portal identity request failed. Approval stopped; response details are not displayed.' }
        if ((Get-GdapField $response 'StatusCode') -ne 200) {
            throw 'The live portal identity request did not return HTTP 200. Approval stopped.'
        }
        $liveTenant = Get-GdapBootstrapTenantId -Content ([string](Get-GdapField $response 'Content'))
        if (-not $liveTenant) { continue }
        if ($liveTenant -ne $connectionTenant.ToString()) {
            throw "The active portal tenant changed or disagrees with the validated connection. Connection tenant: $connectionTenant; active tenant: $liveTenant. Approval stopped."
        }
        return @{ TenantId = $liveTenant; Validated = $true; Source = 'LivePortalBootstrap' }
    }
    throw "The live portal responses did not establish an active tenant ID. Connection tenant: $connectionTenant. Approval stopped; response bodies are not displayed."
}
function Get-GdapInvitationShape {
    param($Value)
    # Field names and types only, never field values or a raw response body.
    $parts = @()
    foreach ($entry in @(@{ Name = 'root'; Value = $Value }, @{ Name = 'relationship'; Value = (Get-GdapField $Value 'relationship') })) {
        $object = $entry.Value
        $kind = if ($null -eq $object) { 'null' } elseif ($object -is [string]) { 'string' } elseif ($object -is [array]) { 'array' } else { 'object' }
        $names = if ($object -is [System.Collections.IDictionary]) { @($object.Keys) } elseif ($kind -eq 'object') { @($object.PSObject.Properties | ForEach-Object { $_.Name }) } else { @() }
        $safeNames = @($names | Where-Object { $_ -cmatch '^[A-Za-z][A-Za-z0-9_]{0,63}$' } | Sort-Object | Select-Object -First 24)
        $parts += "$($entry.Name):$kind fields=[$($safeNames -join ',')]"
    }
    $parts -join '; '
}
function Get-GdapInvitationContractDiagnostic {
    param($Invitation, [string]$ExpectedId, [guid]$ExpectedPartner)
    # Diagnostic only: never use these comparisons as approval evidence. The
    # live portal shape differs from our synthetic v1 contract. Collect the
    # remaining structure in one bounded report, without raw response values.
    $lines = [System.Collections.Generic.List[string]]::new()
    $state = @{ Nodes = 0 }
    function Read-ContractField {
        param($Object, [string]$Name)
        # Preserve zero/one-element arrays; PowerShell's normal pipeline
        # enumeration otherwise makes their diagnostic type misleading.
        if ($null -eq $Object) { return $null }
        if ($Object -is [System.Collections.IDictionary]) { return ,$Object[$Name] }
        $property = $Object.PSObject.Properties[$Name]
        if ($property) { return ,$property.Value }
        return $null
    }
    function Add-ContractShape {
        param($Value, [string]$Path, [int]$Depth = 0)
        if ($state.Nodes -ge 48) { return }
        $state.Nodes++
        if ($null -eq $Value) { $lines.Add("${Path}:null"); return }
        if ($Value -is [string]) {
            $guidValue = [guid]::Empty
            $isGuid = [guid]::TryParseExact($Value, 'D', [ref]$guidValue) -and $guidValue -ne [guid]::Empty
            $matchesPartner = $isGuid -and $guidValue -eq $ExpectedPartner
            $lines.Add("${Path}:string nonempty=$(-not [string]::IsNullOrWhiteSpace($Value)) guid=$isGuid matchesExpectedPartner=$matchesPartner")
            return
        }
        if ($Value -is [array]) {
            $lines.Add("${Path}:array count=$($Value.Count)")
            if ($Depth -lt 3) {
                for ($index = 0; $index -lt [Math]::Min(3, $Value.Count); $index++) {
                    Add-ContractShape $Value[$index] "$Path[$index]" ($Depth + 1)
                }
            }
            return
        }
        if ($Value -is [bool]) { $lines.Add("${Path}:boolean"); return }
        if ($Value -is [ValueType]) { $lines.Add("${Path}:scalar"); return }
        $names = if ($Value -is [System.Collections.IDictionary]) { @($Value.Keys) } else { @($Value.PSObject.Properties | ForEach-Object { $_.Name }) }
        $names = @($names | Where-Object { $_ -is [string] -and $_ -cmatch '^[A-Za-z][A-Za-z0-9_]{0,63}$' } | Sort-Object | Select-Object -First 16)
        $lines.Add("${Path}:object fields=[$($names -join ',')]")
        if ($Depth -lt 3) {
            foreach ($name in $names) { Add-ContractShape (Read-ContractField $Value $name) "$Path.$name" ($Depth + 1) }
        }
    }
    $relationship = Read-ContractField $Invitation 'relationship'
    $returnedId = Read-ContractField $relationship 'partnerGdapRelationshipId'
    $idMatches = $returnedId -is [string] -and $returnedId -ceq $ExpectedId
    $lines.Add("partnerGdapRelationshipIdMatchesExpected=$idMatches")
    Add-ContractShape (Read-ContractField $Invitation 'partner') 'partner'
    Add-ContractShape (Read-ContractField $relationship 'roles') 'relationship.roles'
    foreach ($field in @('status', 'duration', 'autoExtendDuration', 'etag', 'partnerGdapRelationshipId')) {
        Add-ContractShape (Read-ContractField $relationship $field) "relationship.$field"
    }
    $lines.Add('Structure only; values omitted; arrays sampled up to 3 items; depth/node limits apply. Approval mapping remains unverified.')
    $lines -join '; '
}
function Assert-GdapEvidence {
    param($Invitation, [string]$Id, [guid]$Tenant, [guid]$Partner, $Session)
    if ($Tenant -eq [guid]::Empty -or $Partner -eq [guid]::Empty) { throw 'Expected identities cannot be empty.' }
    if ((Get-GdapField $Session 'Validated') -ne $true) {
        throw 'Portal session validation is not confirmed. Approval stopped.'
    }
    $observedTenant = [guid]::Empty
    if (-not [guid]::TryParseExact([string](Get-GdapField $Session 'TenantId'), 'D', [ref]$observedTenant) -or $observedTenant -eq [guid]::Empty) {
        throw 'The authenticated portal tenant ID is missing or invalid. Approval stopped.'
    }
    if ($observedTenant -ne $Tenant) {
        throw "Customer tenant mismatch. Expected: $Tenant; authenticated: $observedTenant. Approval stopped."
    }
    $relationship = Get-GdapField $Invitation 'relationship'
    $portalShape = $null -ne (Get-GdapField $relationship 'partnerGdapRelationshipId') -or $null -ne (Get-GdapField $Invitation 'partner')
    $idField = if ($portalShape) { 'partnerGdapRelationshipId' } else { 'id' }
    $returnedId = Get-GdapField $relationship $idField -NoEnumerate
    if ($returnedId -isnot [string] -or $returnedId -cne $Id) {
        throw "The invitation response did not match the expected relationship ID. Response shape: $(Get-GdapInvitationShape $Invitation). Contract diagnostic: $(Get-GdapInvitationContractDiagnostic $Invitation $Id $Partner). Response validation stopped. Opening the page is not proof of acceptance."
    }
    # The portal mapping is based on the user's live structural/equality report.
    # Keep it separate from the earlier synthetic v1 layout; never combine fields
    # from both layouts to assemble apparently valid consent.
    if ($portalShape -and ($null -ne (Get-GdapField $relationship 'id') -or
        $null -ne (Get-GdapField $relationship 'partner') -or $null -ne (Get-GdapField $relationship 'accessDetails'))) {
        throw 'The invitation mixes portal and legacy field layouts. Approval stopped.'
    }
    $partnerObject = if ($portalShape) { Get-GdapField $Invitation 'partner' } else { Get-GdapField $relationship 'partner' }
    $observedPartner = Get-GdapField $partnerObject 'tenantId' -NoEnumerate
    if ($observedPartner -isnot [string] -or $observedPartner -ine $Partner.ToString()) { throw 'Partner identity is missing or does not match the expected partner.' }
    $boundTenant = Get-GdapField (Get-GdapField $relationship 'customer') 'tenantId'
    if ($boundTenant -and [string]$boundTenant -ine $Tenant.ToString()) { throw 'Invitation is bound to a different customer tenant.' }
    if ($portalShape) {
        $roles = Get-GdapField $relationship 'roles' -NoEnumerate
        if ($roles -isnot [array]) { throw 'The portal role list is not an array. Approval stopped.' }
    } else {
        $roles = @(Get-GdapField (Get-GdapField $relationship 'accessDetails') 'unifiedRoles')
    }
    if ($roles.Count -eq 0) { throw 'Requested roles are missing.' }
    $roleIds = @($roles | ForEach-Object {
        if ($portalShape -and $_ -isnot [string]) { throw 'Invalid portal role evidence: expected a GUID string.' }
        $roleId = if ($portalShape) { $_ } else { [string](Get-GdapField $_ 'roleDefinitionId') }
        $parsedRole = [guid]::Empty
        if (-not [guid]::TryParseExact($roleId, 'D', [ref]$parsedRole) -or $parsedRole -eq [guid]::Empty) { throw 'Invalid requested role evidence.' }
        $parsedRole.ToString()
    } | Sort-Object -Unique)
    $rawDuration = Get-GdapField $relationship 'duration' -NoEnumerate
    if ($portalShape) {
        $numeric = $rawDuration -is [byte] -or $rawDuration -is [int16] -or $rawDuration -is [int32] -or
            $rawDuration -is [int64] -or $rawDuration -is [uint16] -or $rawDuration -is [uint32] -or
            $rawDuration -is [uint64] -or $rawDuration -is [decimal] -or $rawDuration -is [double] -or $rawDuration -is [single]
        if (-not $numeric -or [double]::IsInfinity([double]$rawDuration) -or [double]::IsNaN([double]$rawDuration) -or $rawDuration -le 0) {
            throw 'The portal duration must be a positive finite numeric value. Approval stopped.'
        }
        $duration = $rawDuration.ToString([Globalization.CultureInfo]::InvariantCulture)
        $durationDisplay = "$duration (as returned by portal)"
    } else {
        $duration = [string]$rawDuration
        $durationDisplay = $duration
    }
    $extension = [string](Get-GdapField $relationship 'autoExtendDuration')
    if ($portalShape) {
        foreach ($textField in @('autoExtendDuration', 'status', 'etag')) {
            $textValue = Get-GdapField $relationship $textField -NoEnumerate
            if ($textValue -isnot [string] -or [string]::IsNullOrWhiteSpace($textValue)) { throw "The portal $textField must be a nonempty string. Approval stopped." }
        }
    }
    if (-not $duration -or -not $extension) { throw 'Relationship duration or automatic-extension evidence is missing.' }
    [pscustomobject]@{
        Status = [string](Get-GdapField $relationship 'status')
        ETag = [string](Get-GdapField $relationship 'etag')
        Consent = [string]$portalShape + '|' + ($roleIds -join ',') + '|' + $duration + '|' + $extension
        Summary = "Customer tenant: $Tenant; partner tenant: $Partner; relationship: $Id; roles: $($roleIds -join ', '); duration: $durationDisplay; extension: $extension"
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
    Write-Host (ConvertTo-GdapDisplayText ("Validated invitation: " + $evidence.Summary + '; status: ' + $evidence.Status))
    if ($evidence.Status -eq 'active') { return $invitation }
    if ($evidence.Status -notin @('approvalPending', 'approved', 'activating')) { throw 'The relationship is not in an approvable or activating state.' }
    if ($evidence.Status -eq 'approvalPending') {
        if ($DisableAutomatedApproval) { throw 'Automated approval is disabled. Use the Microsoft invitation link.' }
        if (-not $evidence.ETag) { throw 'The invitation did not supply an ETag.' }
        if (-not $WhatIfPreference -and $ConfirmPreference -ne 'None') { Write-Host 'Review the access summary above. The approval confirmation appears in this PowerShell terminal.' }
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
    Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop
    $RelationshipId = Resolve-GdapRelationshipId $RelationshipId
    if ((-not $ConfirmAuthenticatedTenant -and $ExpectedTenantId -eq [guid]::Empty) -or $ExpectedPartnerTenantId -eq [guid]::Empty) { throw 'Expected identities cannot be empty.' }
    if ($ReuseSession) { throw 'This workflow requires a fresh invitation browser; -ReuseSession cannot perform the invitation-navigation stage.' }
    $modulePath = Join-Path $PSScriptRoot '../M365Internals/M365Internals.psd1'
    $browserWorkflow = Join-Path $PSScriptRoot 'GdapInvitationBrowser.ps1'
    $httpDiagnostics = Join-Path $PSScriptRoot 'GdapHttpDiagnostics.ps1'
    if ($PortalRequestDiagnostics -and -not (Test-Path -LiteralPath $httpDiagnostics)) { throw 'The GDAP HTTP diagnostics helper is missing.' }
    if (-not (Test-Path -LiteralPath $browserWorkflow)) { throw 'The GDAP invitation-browser helper is missing.' }
    $module = Get-Module M365Internals
    if (-not $module) { $module = Import-Module $modulePath -PassThru -ErrorAction Stop }
    $request = {
        param($Parameters)
        & $module {
            param($p)
            if ($p.ContainsKey('RawResponse') -and $p.RawResponse) {
                $p.Headers = Get-M365PortalContextHeaders -Context Homepage
            }
            Invoke-M365PortalRequest @p -SkipAutoHeal -SkipConnectionRefresh
        } $Parameters
    }.GetNewClosure()
    $readSessionEvidence = ${function:Get-GdapPortalSessionEvidence}
    $confirmCustomer = ${function:Confirm-GdapCustomerIdentity}
    $customerSelection = @{ TenantId = $ExpectedTenantId; Confirmed = ($ExpectedTenantId -ne [guid]::Empty) }
    $validateIdentity = {
        param($Identity)
        if (-not $customerSelection.Confirmed) {
            $customerSelection.TenantId = & $confirmCustomer -Session $Identity -PartnerTenantId $ExpectedPartnerTenantId
            $customerSelection.Confirmed = $true
        }
        if ([guid]$Identity.TenantId -ne $customerSelection.TenantId) {
            throw "Customer tenant mismatch before invitation inspection. Expected: $($customerSelection.TenantId); authenticated: $($Identity.TenantId). Approval stopped."
        }
    }.GetNewClosure()
    $getSession = {
        $state = & $module {
            @{ Connection = $script:m365PortalConnection; WebSession = $script:m365PortalSession }
        }
        & $readSessionEvidence -Connection $state.Connection -WebSession $state.WebSession -Request $request
    }.GetNewClosure()
    $approval = @{
        RelationshipId = $RelationshipId; ExpectedTenantId = $ExpectedTenantId; ExpectedPartnerTenantId = $ExpectedPartnerTenantId
        GetSession = $getSession; Request = $request; ActivationTimeoutSeconds = $ActivationTimeoutSeconds
        PollIntervalSeconds = $PollIntervalSeconds; DisableAutomatedApproval = $DisableAutomatedApproval; WhatIf = [bool]$WhatIfPreference
    }
    if ($PSBoundParameters.ContainsKey('Confirm')) { $approval.Confirm = [bool]$PSBoundParameters.Confirm }
    $approveFunction = ${function:Approve-GdapRelationship}
    $preview = [bool]$WhatIfPreference
    $approvalConfirmPreference = $ConfirmPreference
    $continue = {
        $WhatIfPreference = $preview
        $ConfirmPreference = $approvalConfirmPreference
        if (-not $customerSelection.Confirmed) { throw 'Customer selection was not confirmed. Approval stopped.' }
        $approval.ExpectedTenantId = $customerSelection.TenantId
        & $approveFunction @approval
    }.GetNewClosure()
    $parameters = @{ TenantId = $(if ($ConfirmAuthenticatedTenant) { '' } else { $ExpectedTenantId.ToString() }); TimeoutSeconds = $AuthenticationTimeoutSeconds; Username = $Username; BrowserPath = $BrowserPath }
    & $module {
        param($Helper, $AuthenticationParameters, $Id, $ValidateIdentity, $ReadEvidence, $PortalRequest, $Continuation, $PageTimeout, $WhatIfMode, $HttpHelper, $TraceHttp)
        # Suppress setup/navigation/cleanup prompts in this call scope only.
        # The approval continuation restores the caller's confirmation policy.
        $WhatIfPreference = $false
        $ConfirmPreference = 'None'
        $httpState = @{ Sequence = 0; TimedOut = $false }
        if ($TraceHttp) { . $HttpHelper -TimeoutSeconds 30 -State $httpState }
        . $Helper
        $validate = {
            param($web)
            # Optional missing-cookie bootstrap requests forward this argument
            # explicitly. An omitted value can clear the shared session's UA,
            # poisoning the later validation request even after a caught error.
            if ([string]::IsNullOrWhiteSpace($web.UserAgent)) { throw 'The browser portal session has no User-Agent. Approval stopped.' }
            if ($TraceHttp) { Write-Host '[GDAP stage] Registering portal session and checking bootstrap endpoints.' }
            $null = Set-M365PortalConnectionSettings -WebSession $web -AuthSource 'WebSession' -AuthFlow 'BrowserSignIn' -UserAgent $web.UserAgent
            if ($httpState.TimedOut) { throw 'Portal validation timed out. Approval stopped.' }
            if ($TraceHttp) { Write-Host '[GDAP stage] Reading live customer tenant identity.' }
            $identity = & $ReadEvidence -Connection $script:m365PortalConnection -WebSession $web -Request $PortalRequest
            if ($TraceHttp) { Write-Host '[GDAP stage] Tenant identity read. Checking customer selection in this terminal.' }
            $null = & $ValidateIdentity $identity
        }
        Connect-GdapInvitationBrowser -AuthenticationParameters $AuthenticationParameters -RelationshipId $Id -ValidateSession $validate -OnInvitationReady $Continuation -InvitationTimeoutSeconds $PageTimeout -Preview $WhatIfMode
    } $browserWorkflow $parameters $RelationshipId $validateIdentity $readSessionEvidence $request $continue $InvitationTimeoutSeconds $preview $httpDiagnostics ([bool]$PortalRequestDiagnostics)
}
