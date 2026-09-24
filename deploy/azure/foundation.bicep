targetScope = 'resourceGroup'

@description('New companion-only name prefix. Do not use the existing CIPP app name.')
@minLength(3)
@maxLength(20)
param namePrefix string = 'gdap-status'

@description('An Azure Container Apps-supported region; the resource group is not moved.')
param location string = resourceGroup().location

var tags = {
  application: 'gdap-acceptor-status'
  deployment: 'companion-only'
}

resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: 'gdap${uniqueString(resourceGroup().id, namePrefix)}'
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: 'Enabled'
    policies: {
      azureADAuthenticationAsArmPolicy: { status: 'enabled' }
    }
  }
}

resource imageReader 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-image-reader'
  location: location
  tags: tags
}

// Registry-scoped pull only. No identity assignment or role change on CIPP.
var acrPullRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
resource imagePull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, imageReader.id, acrPullRole)
  scope: registry
  properties: {
    principalId: imageReader.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: acrPullRole
  }
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${namePrefix}-logs'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    features: { enableLogAccessUsingOnlyResourcePermissions: true }
  }
}

resource environment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: '${namePrefix}-environment'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [
      { name: 'Consumption', workloadProfileType: 'Consumption' }
    ]
  }
}

output registryName string = registry.name
output registryServer string = registry.properties.loginServer
output appName string = namePrefix
output location string = location
