targetScope = 'resourceGroup'

@description('Prefix of the EXISTING companion environment, not the CIPP app.')
@minLength(3)
@maxLength(20)
param namePrefix string = 'gdap-status'
param location string = resourceGroup().location

// Deliberately isolated: the SMB mount uses an account-wide key. Never give
// the companion the key to CIPP's storage account merely to reuse a file share.
var accountName = 'gdapj${uniqueString(resourceGroup().id, namePrefix)}'
var shareName = 'invitation-journal'

resource environment 'Microsoft.App/managedEnvironments@2025-01-01' existing = {
  name: '${namePrefix}-environment'
}

resource journal 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: accountName
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  tags: { application: 'gdap-acceptor', purpose: 'invitation-creation-journal' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: true
    allowCrossTenantReplication: false
    // The existing environment has no private-network attachment. Public
    // networking still requires authentication; no anonymous access is granted.
    publicNetworkAccess: 'Enabled'
    networkAcls: { defaultAction: 'Allow', bypass: 'None' }
    encryption: {
      keySource: 'Microsoft.Storage'
      services: { file: { enabled: true, keyType: 'Account' } }
    }
  }
}

resource files 'Microsoft.Storage/storageAccounts/fileServices@2023-05-01' = {
  parent: journal
  name: 'default'
  properties: { shareDeleteRetentionPolicy: { enabled: true, days: 14 } }
}

resource share 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = {
  parent: files
  name: shareName
  properties: {
    enabledProtocols: 'SMB'
    accessTier: 'TransactionOptimized'
    // Pay-as-you-go: capacity ceiling, not provisioned/billed capacity or a
    // transaction spending cap. A full journal fails closed; do not prune it.
    shareQuota: 1
  }
}

resource attachment 'Microsoft.App/managedEnvironments/storages@2025-01-01' = {
  parent: environment
  name: shareName
  properties: {
    azureFile: {
      accountName: journal.name
      accountKey: journal.listKeys().keys[0].value
      shareName: share.name
      accessMode: 'ReadWrite'
    }
  }
}

// Account keys are never outputs or parameters copied through Cloud Shell.
output journalAccountName string = journal.name
output journalStorageName string = attachment.name
