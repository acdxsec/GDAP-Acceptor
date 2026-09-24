targetScope = 'resourceGroup'

@description('New companion-only name prefix. Do not use the existing CIPP app name.')
@minLength(3)
@maxLength(20)
param namePrefix string = 'gdap-status'

@description('An Azure Container Apps-supported region; the resource group is not moved.')
param location string = resourceGroup().location

@description('Explicit choice: true creates paid retained logging; false provides live logs only, without historical companion audit records.')
param retainLogs bool

var tags = {
  application: 'gdap-acceptor-status'
  deployment: 'companion-only'
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = if (retainLogs) {
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
    appLogsConfiguration: retainLogs ? {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs!.properties.customerId
        sharedKey: logs!.listKeys().primarySharedKey
      }
    } : {
      destination: 'none'
    }
    workloadProfiles: [
      { name: 'Consumption', workloadProfileType: 'Consumption' }
    ]
  }
}

output appName string = namePrefix
output location string = location
output retainedLogging bool = retainLogs
