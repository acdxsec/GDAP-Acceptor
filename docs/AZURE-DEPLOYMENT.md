# Azure companion deployment

Selected target: **the same Azure subscription and existing resource group as
CIPP, but separate resources**. This supersedes the Ubuntu-server plan. These
templates have not been deployed. Nothing here moves, rebuilds or changes CIPP.

## What is created

`deploy/azure/foundation.bicep` creates a Basic Azure Container Registry (admin
passwords disabled), registry-scoped AcrPull managed identity, Consumption
Container Apps environment, and a separate 30-day Log Analytics workspace.
`deploy/azure/application.bicep` creates the companion Container App after the
image exists. Only the companion is changed by later application deployments.

Use **Incremental**, never Complete, deployment mode in the shared resource
group. Review what-if and name collisions before creation. Default names start
with `gdap-status`; the registry name is deterministically derived from the
resource group/prefix. Do not repurpose an existing resource with those names.
Never delete the CIPP resource group to uninstall this service.

Azure terminates HTTPS on its generated `*.azurecontainerapps.io` address. No
Ubuntu machine, SSH, Caddy, ports on your workstation, custom DNS or manual copy
to a server is needed. `cippapi.fizlian.dev` can be bound later, after the Azure
endpoint works. Desktop setup accepts the Azure origin instead of its old default.

The existing non-root server image and file configuration are unchanged. Settings
and the dedicated CIPP credential are mounted from Container Apps secrets at
`/config/settings.json` and `/run/secrets/cipp-client-secret`. The credential is
a secure ARM input, never an output, environment variable, image layer or desktop
setting. Limit Azure roles that can list secrets or change containers/identities.
Registry credentials are replaced by managed identity, not the CIPP API credential.

The app uses 0.25 vCPU / 0.5 GiB and one minimum/maximum replica, Single revision
mode. Rolling revision replacement can temporarily overlap processes; the
in-memory cache/rate limits are not distributed. Do not scale out. Platform
isolation is not the same as Compose's read-only-root/cap-drop configuration.
Health probes measure only process liveness, not CIPP permissions/readiness.

These are **additional billable resources**, not use of CIPP's App Service plan.
Estimate regional registry, running app, log ingestion/retention and build costs
before deployment. No fixed-price or free-tier assumption is made. Keep staff
object IDs in audit logs access-controlled. No ingress/access-log diagnostic
settings are enabled by these templates; review any organization-wide policies.

## Inputs still required

- CIPP's Azure subscription ID, existing resource group, and supported deployment
  region. Resource-group location is the default, not proof of CIPP app location.
- The two staff Entra applications and assigned staff role from
  [CIPP status setup, section 1](CIPP-STATUS.md#1-staff-identity-setup-administrator-once).
- Dedicated read-only CIPP API client details from section 2 of that guide.
  Confirm API origin/auth tenant/scope from CIPP; do not infer from the portal URL.
- Confirmation of CIPP API IP restrictions. The simple template has **no fixed
  outbound IP**. If restrictions are required, stop and prepare a separate
  VNet/NAT design; do not allow the earlier Ubuntu IP or Azure ingress IP.

Deployment needs resource creation and registry build permissions plus permission
to create the registry-scoped role assignment (Contributor alone is insufficient).
Microsoft.App, Microsoft.ContainerRegistry, Microsoft.ManagedIdentity and
Microsoft.OperationalInsights providers must be registered, with appropriate
regional quota and no conflicting policy. Do not broaden permissions silently.

## Deployment sequence

Use an authenticated Azure CLI environment or Azure Cloud Shell with this source
checkout. Local Docker is unnecessary: **ACR builds the image in Azure** using
the existing `server/Dockerfile`. The `.dockerignore` limits the uploaded build
context to server/shared source, excluding local settings and credentials.
Use a reviewed source revision containing these templates; they are not in
the published 0.1.6 release or the earlier Ubuntu server bundle.

The following are operator reference commands, **not executed by validation**.
Replace angle-bracket values and use the same subscription/group/prefix/region
at every stage. No command contains a secret value.

1. Preview the new foundation. Stop if any existing non-companion resource changes.

```bash
az deployment group what-if --subscription '<subscription-id>' --resource-group '<cipp-resource-group>' --template-file deploy/azure/foundation.bicep --parameters namePrefix=gdap-status location='<azure-region>' --mode Incremental
```

2. After approving costs/changes, create the foundation.

```bash
az deployment group create --subscription '<subscription-id>' --resource-group '<cipp-resource-group>' --name gdap-status-foundation --template-file deploy/azure/foundation.bicep --parameters namePrefix=gdap-status location='<azure-region>' --mode Incremental --query properties.outputs
```

3. Use the returned registryName to build a uniquely tagged source revision. The
   build context is the repository root, not `deploy/azure`. Never reuse a tag
   for a different build. ARM-audience ACR authentication is enabled for managed
   identity image pull. Allow role-assignment propagation before app deployment.

```bash
az acr build --subscription '<subscription-id>' --registry '<registryName>' --image gdap-status:<source-revision> --file server/Dockerfile .
```

4. Resolve the image digest; deployment is pinned to that immutable image.

```bash
az acr repository show --subscription '<subscription-id>' --name '<registryName>' --image gdap-status:<source-revision> --query digest --output tsv
```

5. In a private working directory, make a protected copy of
   `deploy/azure/application.parameters.example.json` named
   `deploy/azure/application.parameters.local.json`. Restrict it to the operator
   (0600 on Linux; equivalent Windows ACL), including its parent directory and
   backups. Complete it with a secure editor: exact digest, matching region/prefix,
   configuration and CIPP secret. Never paste credentials into chat, CLI arguments,
   shell variables/environment, terminal transcripts or source control. Local
   parameter files are gitignored. ARM secure parameters protect deployment
   history, **not this local plaintext file**. Do not enable CLI debug logging.

6. Preview the app. Secret differences are not a reliable what-if audit; inspect
   the template and access permissions too. Do not share raw deployment output.

```bash
az deployment group what-if --subscription '<subscription-id>' --resource-group '<cipp-resource-group>' --template-file deploy/azure/application.bicep --parameters @deploy/azure/application.parameters.local.json --mode Incremental
```

7. Create the companion and obtain its HTTPS origin. CIPP itself is not a resource
   in either template.

```bash
az deployment group create --subscription '<subscription-id>' --resource-group '<cipp-resource-group>' --name gdap-status-application --template-file deploy/azure/application.bicep --parameters @deploy/azure/application.parameters.local.json --mode Incremental --query properties.outputs.companionOrigin.value --output tsv
```

If deployment fails, inspect this deployment/revision, not CIPP. Do not delete the
shared resource group or disable authentication. Preserve the secure credential
in an approved secret store and remove the local parameter copy after use.

## Live gate and upgrades

Check `/healthz` returns 200 and anonymous `/v1/connection` returns 401. Then
connect the 0.4.0 companion (settings **3 → C**) using the returned origin and
staff application IDs. Check an **already-approved invitation** through menu 5.
Verify authorized access, unassigned-user denial and a matching CIPP status.
This does not repeat customer approval or submit another onboarding job.

For updates build a new unique tag, record/test its digest, preview and deploy
only `application.bicep` with the new digest. For settings/credential rotation,
also increment `configurationVersion` to force process replacement and clear
cached tokens; app-level secret changes alone are not sufficient. Verify before
revoking the old CIPP credential. Rollback by deploying the previous approved
digest and compatible configuration, not by modifying CIPP. No automatic
updater, public image, GitHub deployment identity or Azure deployment is created
by these source changes.

## Verification and sources

`tools/Test-AzureDeployment.ps1` compiles both templates and checks resource
scope, secret parameters/mounts, image pinning, HTTPS, identity access and replica
limits. CI runs it without Azure authentication. Compilation is **not** an Azure
what-if, policy/quota check, actual image build, secret-mount runtime check or live
authentication test. Those gates remain pending in the target subscription.

- [Azure Container Apps ingress and TLS](https://learn.microsoft.com/en-us/azure/container-apps/ingress-overview)
- [Managed identity image pulls](https://learn.microsoft.com/en-us/azure/container-apps/managed-identity-image-pull)
- [Secret volumes and rotation](https://learn.microsoft.com/en-us/azure/container-apps/manage-secrets)
- [Build images in ACR without local Docker](https://learn.microsoft.com/en-us/azure/container-registry/container-registry-quickstart-task-cli)
