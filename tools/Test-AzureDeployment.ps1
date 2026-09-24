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
    foreach ($name in @('foundation', 'application')) {
        & $BicepPath build (Join-Path $root "deploy/azure/$name.bicep") --outfile (Join-Path $temporary "$name.json")
        if ($LASTEXITCODE -ne 0) { throw "Bicep compilation failed: $name" }
    }
    $foundation = Get-Content (Join-Path $temporary 'foundation.json') -Raw | ConvertFrom-Json -AsHashtable
    $application = Get-Content (Join-Path $temporary 'application.json') -Raw | ConvertFrom-Json -AsHashtable
    $types = @('Microsoft.OperationalInsights/workspaces', 'Microsoft.App/managedEnvironments')
    Assert-Contract ($foundation.resources.Count -eq 2) 'Only the environment and optional workspace may be declared.'
    Assert-Contract ($foundation.parameters.retainLogs.type -eq 'bool' -and -not $foundation.parameters.retainLogs.ContainsKey('defaultValue')) 'Retained audit logging must be an explicit operator choice.'
    foreach ($resource in $foundation.resources) {
        Assert-Contract ($resource.type -in $types) 'Unexpected resource: must not deploy or modify CIPP infrastructure.'
    }
    $logs = @($foundation.resources | Where-Object type -eq 'Microsoft.OperationalInsights/workspaces')[0]
    Assert-Contract ($logs.condition -eq "[parameters('retainLogs')]") 'Paid logging must be optional.'
    $environment = @($foundation.resources | Where-Object type -eq 'Microsoft.App/managedEnvironments')[0]
    Assert-Contract ($environment.properties.appLogsConfiguration -like "*if(parameters('retainLogs')*" -and $environment.properties.appLogsConfiguration -like "*'destination', 'none'*") 'Disabled retained logging must not use a workspace.'
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
    Assert-Contract ($container.env.Count -eq 1 -and $container.env[0].name -eq 'GDAP_CONFIGURATION_VERSION') 'No credential/config values in environment variables.'
    $mounts = @{}
    foreach ($mount in $container.volumeMounts) { $mounts[$mount.volumeName] = $mount.mountPath }
    $files = @{}
    foreach ($volume in $properties.template.volumes) {
        Assert-Contract ($volume.storageType -eq 'Secret' -and $volume.secrets.Count -eq 1) 'Only explicitly selected secret-file mounts permitted.'
        $files[$volume.secrets[0].secretRef] = $mounts[$volume.name] + '/' + $volume.secrets[0].path
    }
    Assert-Contract ($files['settings-json'] -eq '/config/settings.json') 'Settings mount does not match ServiceSettings.Load.'
    Assert-Contract ($files['cipp-client-secret'] -eq '/run/secrets/cipp-client-secret') 'Credential mount does not match ServiceSettings.ReadSecret.'
    Assert-Contract ($container.copy[0].input.httpGet.path -eq '/healthz') 'Probes must not invoke CIPP or authenticated routes.'
    $example = Get-Content (Join-Path $root 'deploy/azure/application.parameters.example.json') -Raw | ConvertFrom-Json -AsHashtable
    $expected = Get-Content (Join-Path $root 'deploy/settings.example.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert-Contract ((($example.parameters.settings.value.Keys | Sort-Object) -join ',') -eq (($expected.Keys | Sort-Object) -join ',')) 'Azure settings must match the existing service configuration contract.'
    $publisher = Get-Content (Join-Path $root '.github/workflows/publish-status-image.yml') -Raw
    Assert-Contract ($publisher -match '(?m)^  workflow_dispatch:' -and $publisher -notmatch '(?m)^  (push|pull_request|workflow_run|schedule):') 'Image publication must remain manual, not triggered by ordinary CI.'
    Assert-Contract ($publisher -match "(?m)^    if: github\.ref == 'refs/heads/main' && inputs\.publish_approved$") 'Publication must require approved main-branch dispatch.'
    Assert-Contract ($publisher -match 'default: false' -and $publisher -notmatch 'az deployment|az containerapp|az acr') 'Publisher must not deploy Azure or default to approval.'
    Write-Host 'PASS: Azure templates compile; optional retained logs, no ACR/identity/role assignment, scale-to-zero HTTP, secure inputs, TLS, GHCR digest pinning and secret-file contracts verified. No Azure resources deployed.'
}
finally {
    # Exact temporary directory created by this test; contains compiled templates only.
    [IO.Directory]::Delete($temporary, $true)
}
