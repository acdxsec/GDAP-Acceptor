targetScope = 'resourceGroup'

@minLength(3)
@maxLength(20)
param namePrefix string = 'gdap-status'
param location string = resourceGroup().location

@description('Digest of the tested gdap-status image built in this deployment registry (sha256: plus 64 hex characters).')
@minLength(71)
@maxLength(71)
param imageDigest string

@description('Contents of deploy/settings.example.json with every placeholder replaced. Never add the CIPP secret here.')
@secure()
param settings object

@description('Dedicated read-only CIPP API client secret. Supply through a protected parameter file or secure deployment input; never a CLI value.')
@secure()
@minLength(1)
@maxLength(1024)
param cippClientSecret string

@description('Change on secret/settings rotation to force a new revision; not a secret.')
param configurationVersion string = '1'

resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: 'gdap${uniqueString(resourceGroup().id, namePrefix)}'
}
resource imageReader 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: '${namePrefix}-image-reader'
}
resource environment 'Microsoft.App/managedEnvironments@2025-01-01' existing = {
  name: '${namePrefix}-environment'
}

resource app 'Microsoft.App/containerApps@2025-01-01' = {
  name: namePrefix
  location: location
  tags: { application: 'gdap-acceptor-status', deployment: 'companion-only' }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${imageReader.id}': {} }
  }
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      maxInactiveRevisions: 3
      // Managed identity is used by the platform for image pulls, not exposed
      // to application code as a general-purpose Azure credential.
      identitySettings: [{ identity: imageReader.id, lifecycle: 'None' }]
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
        traffic: [{ latestRevision: true, weight: 100 }]
      }
      registries: [{ server: registry.properties.loginServer, identity: imageReader.id }]
      secrets: [
        // Public configuration identifiers. secureObject protects deployment
        // input, and this conversion preserves JSON in the mounted file.
        #disable-next-line use-secure-value-for-secure-inputs
        { name: 'settings-json', value: string(settings) }
        { name: 'cipp-client-secret', value: cippClientSecret }
      ]
    }
    template: {
      containers: [
        {
          name: 'status'
          image: '${registry.properties.loginServer}/gdap-status@${imageDigest}'
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [{ name: 'GDAP_CONFIGURATION_VERSION', value: configurationVersion }]
          volumeMounts: [
            { volumeName: 'settings', mountPath: '/config' }
            { volumeName: 'credential', mountPath: '/run/secrets' }
          ]
          // Liveness only; a CIPP outage must not cause restart storms.
          probes: [for probe in ['Startup', 'Readiness', 'Liveness']: {
            type: probe
            httpGet: { path: '/healthz', port: 8080, scheme: 'HTTP' }
            periodSeconds: 10
            timeoutSeconds: 3
            failureThreshold: probe == 'Startup' ? 30 : 3
          }]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 }
      volumes: [
        {
          name: 'settings'
          storageType: 'Secret'
          secrets: [{ secretRef: 'settings-json', path: 'settings.json' }]
        }
        {
          name: 'credential'
          storageType: 'Secret'
          secrets: [{ secretRef: 'cipp-client-secret', path: 'cipp-client-secret' }]
        }
      ]
    }
  }
}

output companionOrigin string = 'https://${app.properties.configuration.ingress.fqdn}'
