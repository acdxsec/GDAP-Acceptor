[CmdletBinding()]
param([string]$BicepPath = 'bicep')

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('gdap-azure-contracts-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $temporary
function Assert-Contract([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
try {
    foreach ($name in @('foundation', 'application', 'journal')) {
        & $BicepPath build (Join-Path $root "deploy/azure/$name.bicep") --outfile (Join-Path $temporary "$name.json")
        if ($LASTEXITCODE -ne 0) { throw "Bicep compilation failed: $name" }
    }
    $foundation = Get-Content (Join-Path $temporary 'foundation.json') -Raw | ConvertFrom-Json -AsHashtable
    $application = Get-Content (Join-Path $temporary 'application.json') -Raw | ConvertFrom-Json -AsHashtable
    $journal = Get-Content (Join-Path $temporary 'journal.json') -Raw | ConvertFrom-Json -AsHashtable
    $types = @('Microsoft.OperationalInsights/workspaces', 'Microsoft.App/managedEnvironments')
    Assert-Contract ($foundation.resources.Count -eq 2) 'Only the environment and optional workspace may be declared.'
    Assert-Contract ($foundation.parameters.retainLogs.type -eq 'bool' -and -not $foundation.parameters.retainLogs.ContainsKey('defaultValue')) 'Retained audit logging must be an explicit operator choice.'
    foreach ($resource in $foundation.resources) {
        Assert-Contract ($resource.type -in $types) 'Unexpected resource: must not deploy or modify CIPP infrastructure.'
    }
    $logs = @($foundation.resources | Where-Object type -eq 'Microsoft.OperationalInsights/workspaces')[0]
    Assert-Contract ($logs.condition -eq "[parameters('retainLogs')]") 'Paid logging must be optional.'
    $environment = @($foundation.resources | Where-Object type -eq 'Microsoft.App/managedEnvironments')[0]
    # Azure CLI maps --logs-destination none to null, not the rejected string "none".
    $loggingExpression = $environment.properties.appLogsConfiguration
    Assert-Contract ($loggingExpression.StartsWith("[if(parameters('retainLogs'), ") -and $loggingExpression.EndsWith("createObject('destination', null(), 'logAnalyticsConfiguration', null()))]")) 'Disabled retained logging must send null destination and null workspace configuration, not the unsupported string none.'
    Assert-Contract ($environment.properties.workloadProfiles[0].workloadProfileType -eq 'Consumption') 'Expected consumption environment.'
    Assert-Contract ($application.resources.Count -eq 1 -and $application.resources[0].type -eq 'Microsoft.App/containerApps') 'Application deployment must write only the companion Container App.'
    Assert-Contract ($application.parameters.cippClientSecret.type -ieq 'securestring') 'CIPP credential must be a secure deployment input.'
    Assert-Contract (-not $application.parameters.cippClientSecret.ContainsKey('defaultValue')) 'No default credential permitted.'
    Assert-Contract ($application.parameters.settings.type -ieq 'secureObject') 'Configuration input must not be stored in deployment history.'
    Assert-Contract ($application.outputs.Count -eq 1 -and $application.outputs.ContainsKey('companionOrigin')) 'No configuration/secret outputs permitted.'
    $properties = $application.resources[0].properties
    Assert-Contract ($properties.configuration.ingress.allowInsecure -eq $false -and $properties.configuration.ingress.targetPort -eq 8080) 'Require TLS at Azure ingress and the existing backend port.'
    Assert-Contract ($properties.configuration.activeRevisionsMode -eq 'Single') 'Multiple active revisions are not supported.'
    Assert-Contract ($properties.template.scale.minReplicas -eq 0 -and $properties.template.scale.maxReplicas -eq 1) 'Must scale to zero with at most one steady-state replica.'
    Assert-Contract ($properties.template.scale.rules[0].http.metadata.concurrentRequests -eq '10') 'HTTP must wake the zero-replica app.'
    Assert-Contract (-not $application.resources[0].ContainsKey('identity') -and -not $properties.configuration.ContainsKey('registries')) 'Public GHCR image must not need a registry identity or password.'
    $container = $properties.template.containers[0]
    Assert-Contract ($container.image -like '*ghcr.io/acdxsec/gdap-acceptor-status@*' -and $container.image -like '*imageDigest*') 'Approved GHCR image must be pinned by digest.'
    Assert-Contract ($application.parameters.mountInvitationJournal.defaultValue -eq $false -and $application.parameters.enableInvitationCreation.defaultValue -eq $false) 'Neither mounting nor invitation creation may be enabled by default.'
    $expectedEnvironment = "[concat(createArray(createObject('name', 'GDAP_CONFIGURATION_VERSION', 'value', parameters('configurationVersion'))), if(and(parameters('mountInvitationJournal'), parameters('enableInvitationCreation')), createArray(createObject('name', 'GDAP_INVITATION_JOURNAL_DIR', 'value', '/var/lib/gdap-journal')), createArray()))]"
    Assert-Contract ($container.env -eq $expectedEnvironment) 'Only configuration version and a gated journal path may be exposed as environment variables.'
    $expectedMounts = "[concat(createArray(createObject('volumeName', 'settings', 'mountPath', '/config'), createObject('volumeName', 'credential', 'mountPath', '/run/secrets')), if(parameters('mountInvitationJournal'), createArray(createObject('volumeName', 'journal', 'mountPath', '/var/lib/gdap-journal')), createArray()))]"
    Assert-Contract ($container.volumeMounts -eq $expectedMounts) 'Secret mounts must stay unchanged; journal must use the dedicated fixed path and explicit mount gate.'
    $expectedVolumes = "[concat(createArray(createObject('name', 'settings', 'storageType', 'Secret', 'secrets', createArray(createObject('secretRef', 'settings-json', 'path', 'settings.json'))), createObject('name', 'credential', 'storageType', 'Secret', 'secrets', createArray(createObject('secretRef', 'cipp-client-secret', 'path', 'cipp-client-secret')))), if(parameters('mountInvitationJournal'), createArray(createObject('name', 'journal', 'storageType', 'AzureFile', 'storageName', 'invitation-journal', 'mountOptions', 'uid=1654,gid=1654,file_mode=0600,dir_mode=0700,cache=strict,actimeo=0')), createArray()))]"
    Assert-Contract ($properties.template.volumes -eq $expectedVolumes) 'Require exact secret files, restricted non-root SMB mount and locking/cache options; no ephemeral fallback.'
    $expectedJournalTypes = @('Microsoft.Storage/storageAccounts', 'Microsoft.Storage/storageAccounts/fileServices', 'Microsoft.Storage/storageAccounts/fileServices/shares', 'Microsoft.App/managedEnvironments/storages')
    Assert-Contract ((($journal.resources.type | Sort-Object) -join ',') -eq (($expectedJournalTypes | Sort-Object) -join ',')) 'Journal template may only create companion storage and the attachment, not change the environment or CIPP.'
    $account = @($journal.resources | Where-Object type -eq 'Microsoft.Storage/storageAccounts')[0]
    Assert-Contract ($account.kind -eq 'StorageV2' -and $account.sku.name -eq 'Standard_LRS') 'Journal must use standard pay-as-you-go storage, not provisioned premium storage.'
    Assert-Contract ($journal.variables.accountName -eq "[format('gdapj{0}', uniqueString(resourceGroup().id, parameters('namePrefix')))]") 'Journal account must be isolated; do not accept a CIPP storage account name/key.'
    Assert-Contract ($account.properties.allowBlobPublicAccess -eq $false -and $account.properties.minimumTlsVersion -eq 'TLS1_2' -and $account.properties.supportsHttpsTrafficOnly -eq $true) 'Require authenticated access and encrypted transport.'
    $share = @($journal.resources | Where-Object type -eq 'Microsoft.Storage/storageAccounts/fileServices/shares')[0]
    Assert-Contract ($share.properties.shareQuota -eq 1 -and $share.properties.enabledProtocols -eq 'SMB' -and $share.properties.accessTier -eq 'TransactionOptimized') 'Require a bounded classic SMB share, not an unsupported top-level file-share resource.'
    $attachment = @($journal.resources | Where-Object type -eq 'Microsoft.App/managedEnvironments/storages')[0]
    Assert-Contract ($attachment.properties.azureFile.accessMode -eq 'ReadWrite' -and $attachment.properties.azureFile.accountKey -like '*listKeys*') 'Account key must flow directly from ARM into the environment storage attachment.'
    Assert-Contract ((($journal.outputs.Keys | Sort-Object) -join ',') -eq 'journalAccountName,journalStorageName') 'No keys, settings or secret outputs permitted.'
    Assert-Contract ($container.copy[0].input.httpGet.path -eq '/healthz') 'Probes must not invoke CIPP or authenticated routes.'
    $example = Get-Content (Join-Path $root 'deploy/azure/application.parameters.example.json') -Raw | ConvertFrom-Json -AsHashtable
    $expected = Get-Content (Join-Path $root 'deploy/settings.example.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert-Contract ($example.parameters.mountInvitationJournal.value -eq $true -and $example.parameters.enableInvitationCreation.value -eq $false) 'Example must mount for validation while leaving creation disabled.'
    Assert-Contract ((($example.parameters.settings.value.Keys | Sort-Object) -join ',') -eq (($expected.Keys | Sort-Object) -join ',')) 'Azure settings must match the existing service configuration contract.'
    $publisher = Get-Content (Join-Path $root '.github/workflows/publish-status-image.yml') -Raw
    Assert-Contract ($publisher -match '(?m)^  workflow_dispatch:' -and $publisher -notmatch '(?m)^  (push|pull_request|workflow_run|schedule):') 'Image publication must remain manual, not triggered by ordinary CI.'
    Assert-Contract ($publisher -match "(?m)^    if: github\.ref == 'refs/heads/main' && inputs\.publish_approved$") 'Publication must require approved main-branch dispatch.'
    Assert-Contract ($publisher -match 'default: false' -and $publisher -notmatch 'az deployment|az containerapp|az acr') 'Publisher must not deploy Azure or default to approval.'
    Write-Host 'PASS: Azure templates compile; isolated pay-as-you-go journal, non-root SMB mount, separate creation gate, unchanged CIPP/environment, scale-to-zero HTTP, secure inputs and GHCR digest contracts verified. No Azure resources deployed.'
}
finally {
    # Exact temporary directory created by this test; contains compiled templates only.
    [IO.Directory]::Delete($temporary, $true)
}
