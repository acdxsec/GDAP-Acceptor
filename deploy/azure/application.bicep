targetScope = 'resourceGroup'

@minLength(3)
@maxLength(20)
param namePrefix string = 'gdap-status'
param location string = resourceGroup().location

@description('Digest of an approved, anonymously pullable ghcr.io/acdxsec/gdap-acceptor-status image (sha256: plus 64 hex characters).')
@minLength(71)
@maxLength(71)
param imageDigest string

@description('Contents of deploy/settings.example.json with every placeholder replaced. Never add the CIPP secret here.')
@secure()
param settings object

@description('Dedicated CIPP API client secret. Invitation creation requires Tenant.Relationship.ReadWrite. Supply through protected input; never a CLI value.')
@secure()
@minLength(1)
@maxLength(1024)
param cippClientSecret string

@description('Change on secret/settings rotation to force a new revision; not a secret.')
param configurationVersion string = '1'

@description('Mount the companion-only storage prepared by journal.bicep. Does not itself enable invitation creation.')
param mountInvitationJournal bool = false

@description('Enable creation only after staff permissions and live persistent-mount checks pass. Also requires mountInvitationJournal=true.')
param enableInvitationCreation bool = false

resource environment 'Microsoft.App/managedEnvironments@2025-01-01' existing = {
  name: '${namePrefix}-environment'
}

resource app 'Microsoft.App/containerApps@2025-01-01' = {
  name: namePrefix
  location: location
  tags: { application: 'gdap-acceptor-status', deployment: 'companion-only' }
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      maxInactiveRevisions: 3
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
        traffic: [{ latestRevision: true, weight: 100 }]
      }
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
          image: 'ghcr.io/acdxsec/gdap-acceptor-status@${imageDigest}'
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: concat([{ name: 'GDAP_CONFIGURATION_VERSION', value: configurationVersion }], mountInvitationJournal && enableInvitationCreation ? [
            { name: 'GDAP_INVITATION_JOURNAL_DIR', value: '/var/lib/gdap-journal' }
          ] : [])
          volumeMounts: concat([
            { volumeName: 'settings', mountPath: '/config' }
            { volumeName: 'credential', mountPath: '/run/secrets' }
          ], mountInvitationJournal ? [{ volumeName: 'journal', mountPath: '/var/lib/gdap-journal' }] : [])
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
      scale: {
        minReplicas: 0
        maxReplicas: 1
        rules: [{ name: 'http', http: { metadata: { concurrentRequests: '10' } } }]
      }
      volumes: concat([
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
      ], mountInvitationJournal ? [{
        name: 'journal'
        storageType: 'AzureFile'
        storageName: 'invitation-journal'
        // Official .NET Linux app UID/GID. Preserve SMB locking (no nobrl),
        // restrict local file access and avoid stale metadata between replicas.
        mountOptions: 'uid=1654,gid=1654,file_mode=0600,dir_mode=0700,cache=strict,actimeo=0'
      }] : [])
    }
  }
}

output companionOrigin string = 'https://${app.properties.configuration.ingress.fqdn}'
