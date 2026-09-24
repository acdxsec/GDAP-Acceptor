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
    $types = @('Microsoft.ContainerRegistry/registries', 'Microsoft.ManagedIdentity/userAssignedIdentities', 'Microsoft.Authorization/roleAssignments', 'Microsoft.OperationalInsights/workspaces', 'Microsoft.App/managedEnvironments')
    Assert-Contract ($foundation.resources.Count -eq 5) 'Unexpected foundation resource count.'
    foreach ($resource in $foundation.resources) {
        Assert-Contract ($resource.type -in $types) 'Unexpected resource: must not deploy or modify CIPP infrastructure.'
        Assert-Contract ($resource.type -ne 'Microsoft.Authorization/roleAssignments' -or $resource.scope -like '*Microsoft.ContainerRegistry/registries*') 'Pull assignment must be registry scoped.'
    }
    $registry = @($foundation.resources | Where-Object type -eq 'Microsoft.ContainerRegistry/registries')[0]
    Assert-Contract ($registry.properties.adminUserEnabled -eq $false) 'Registry passwords must remain disabled.'
    Assert-Contract ($foundation.variables.acrPullRole -like '*7f951dda-4ed3-4680-a7ca-43fe172d538d*') 'Unexpected registry permission.'
    $environment = @($foundation.resources | Where-Object type -eq 'Microsoft.App/managedEnvironments')[0]
    Assert-Contract ($environment.properties.workloadProfiles[0].workloadProfileType -eq 'Consumption') 'Expected consumption environment.'
    Assert-Contract ($application.resources.Count -eq 1 -and $application.resources[0].type -eq 'Microsoft.App/containerApps') 'Application deployment must write only the companion Container App.'
    Assert-Contract ($application.parameters.cippClientSecret.type -ieq 'securestring') 'CIPP credential must be a secure deployment input.'
    Assert-Contract (-not $application.parameters.cippClientSecret.ContainsKey('defaultValue')) 'No default credential permitted.'
    Assert-Contract ($application.parameters.settings.type -ieq 'secureObject') 'Configuration input must not be stored in deployment history.'
    Assert-Contract ($application.outputs.Count -eq 1 -and $application.outputs.ContainsKey('companionOrigin')) 'No configuration/secret outputs permitted.'
    $properties = $application.resources[0].properties
    Assert-Contract ($properties.configuration.ingress.allowInsecure -eq $false -and $properties.configuration.ingress.targetPort -eq 8080) 'Require TLS at Azure ingress and the existing backend port.'
    Assert-Contract ($properties.configuration.activeRevisionsMode -eq 'Single') 'Multiple active revisions are not supported.'
    Assert-Contract ($properties.template.scale.minReplicas -eq 1 -and $properties.template.scale.maxReplicas -eq 1) 'Only one steady-state replica is supported.'
    Assert-Contract ($properties.configuration.identitySettings[0].lifecycle -eq 'None') 'Image-pull identity must not be exposed to application code.'
    $container = $properties.template.containers[0]
    Assert-Contract ($container.image -like '*gdap-status@*' -and $container.image -like '*imageDigest*') 'Image must be pinned by digest.'
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
    Write-Host 'PASS: Azure templates compile; resource isolation, secure inputs, TLS, digest pinning, managed identity, single-replica limits and secret-file contracts verified. No Azure resources deployed.'
}
finally {
    # Exact temporary directory created by this test; contains compiled templates only.
    [IO.Directory]::Delete($temporary, $true)
}
