# Lower-cost Azure companion deployment

Target: the existing CIPP subscription and **CIPP-Resorces** resource group,
North Central US, but **separate compute**. The B2 CIPP plan's operator-supplied
seven-day metrics showed CPU averaging 6.74% (highest minute 82%) and memory
averaging 69.91% (highest minute 95%). Sharing that plan is not the selected
design. These templates do not resize it or change CIPP.

This revision supersedes the always-running ACA/paid-ACR proposal. **Do not use
the earlier downloaded foundation template or creation command.** No Azure
deployment, public image or package visibility change has been performed by
preparing these files. See [cost research](research/AZURE-HOSTING-COST.md).

## Resources and logging decision

Foundation: one Consumption Container Apps environment, plus an optional
Log Analytics workspace. Application: one Container App. The full design has
**two resources without retained logs, or three with retained logs**. No Azure
Container Registry, registry-pull identity, role assignment, new App Service
plan, VNet, NAT gateway or Caddy host is created.

The retainLogs boolean is required, with no default:

- true: dedicated workspace with 30-day retention; ingestion and retention can
  incur charges. Restrict access to staff object IDs in audit records.
- false: logging disabled with a JSON null destination (not the string `"none"`);
  live streaming remains available, but no historical
  companion access/console logs are retained by this deployment.
  Existing CIPP onboarding logs are not disabled or changed.

Choose deliberately. When updating an environment, review its existing logging
requirement before disabling retention. Incremental mode does not remove old
registry/identity/workspace resources removed from the template. If the earlier
foundation was deployed, inventory exact resources and obtain explicit cleanup
approval; the new template does not stop their charges. Never delete the shared
CIPP resource group.

The public, code-only image is ghcr.io/acdxsec/gdap-acceptor-status, pinned by
an approved SHA-256 digest. A public repository does not make its GHCR package
public automatically. **Do not deploy until anonymous digest pull works.**
The image contains server/shared source output, not configuration, credentials
or customer browser state. The CIPP credential stays in Azure secrets.

## Runtime and cost boundaries

The app uses 0.25 vCPU / 0.5 GiB, HTTP ingress, minimum zero, maximum one and
Single revision mode. Its HTTP rule wakes it on demand. It does not reuse CIPP
memory. Platform maintenance or revision replacement may briefly overlap
processes; caches/cooldowns/rate limits remain process-local, not distributed.

Azure supplies a generated HTTPS origin; custom DNS is not needed initially.
cippapi.fizlian.dev is optional later. The server still validates staff tokens.
Anonymous /healthz reports process liveness only. The image remains non-root;
platform isolation is not identical to Compose read-only-root/cap-drop settings.

Zero replicas incur no app resource-consumption charge; startup, processing
and scale-in delays consume resources. External monitoring or unauthorized
traffic can keep it warm. An application rate limiter does not stop ingress
from starting the container and is not a spending cap. Do not add keep-alive
polling just to hide cold starts. GHCR storage/bandwidth is currently free;
builds, retained logs, networking and offer terms can still affect total cost.
Verify Sponsorship eligibility/credits; no $0/month guarantee is made.

The updated desktop allows two minutes per status HTTP request, within its
existing five-minute setup/check and twenty-minute watch deadlines. A timeout
or gateway error fails clearly: it never retries approval, resubmits a CIPP job
or changes successful acceptance. A synthetic 45-second response tests the old
timeout regression, not actual Azure cold-start latency. Live validation is
still required. Process restarts discard cached CIPP tokens/results and limits.

## Invitation-creation pivot

The status-only image and templates in this runbook do not enable the new
[invitation creation workflow](INVITATION-WORKFLOW.md). The hosting environment
can be reused, but a durable shared creation journal, Invitations.Create staff
scope/role and Tenant.Relationship.ReadWrite CIPP access are required. Do not
deploy the earlier image or create status-only permissions for the new workflow.
Persistent-storage wiring and its cost/security review remain a deployment gate.

## Remaining inputs (historical status-only deployment)

- Staff API/desktop Entra registrations, assigned Onboarding.Read role and
  dedicated read-only CIPP API client: [identity setup](CIPP-STATUS.md).
- CIPP API origin/authentication tenant/client/scope copied from CIPP, not
  inferred from the portal URL.
- Explicit logging choice and an approved public image digest.
- CIPP API IP restrictions. This minimal design has no fixed egress address.
  Mandatory allowlisting needs a separately priced network design. Neither
  the old Ubuntu IP nor Azure ingress IP is an egress promise.

Deployment permissions cover these resources and deployments; no registry-build
or role-assignment write permission is needed by these templates. Register
Microsoft.App and, for retained logging, Microsoft.OperationalInsights. Merely
registering ACR/ManagedIdentity earlier did not create resources. Check regional
quota and Azure policy.

## Build and publish (separate approval)

The publish-status-image workflow is manual, main-branch only, and requires
publish_approved=true. It runs server contracts, builds server/Dockerfile,
smoke-tests that exact image, then pushes a unique source/run/attempt tag to
GHCR using a job-scoped GitHub token. The run summary records the image digest.
It does not deploy Azure, create a GitHub release or change package visibility.
Normal push/PR verification does not publish. The Docker build context excludes
settings and secrets.

Before running it, approve publishing the source-only image and merge tested,
reviewed source. The owner must deliberately approve public package visibility
if it is private; visibility is not changed automatically. Verify an anonymous
pull and use the recorded digest in application.parameters.local.json. Never
deploy latest or a mutable tag. Reruns do not move earlier build tags.

## Deployment sequence

These are reference commands, not executed by validation. Use a reviewed source
checkout in Azure Cloud Shell or an authenticated Azure CLI. Replace placeholders.
Keep subscription/group/prefix/region consistent. Always use Incremental mode
and inspect what-if for collisions/unrelated changes. No command contains a secret.

1. Preview the revised foundation with the logging choice. Without retained
   logs, expect one resource to create, not the earlier five.

```bash
az deployment group what-if --subscription '<subscription-id>' --resource-group CIPP-Resorces --template-file deploy/azure/foundation.bicep --parameters namePrefix=gdap-status location=northcentralus retainLogs=<true-or-false> --mode Incremental
```

2. Only after reviewing the preview and cost choice, create the foundation.

```bash
az deployment group create --subscription '<subscription-id>' --resource-group CIPP-Resorces --name gdap-status-foundation --template-file deploy/azure/foundation.bicep --parameters namePrefix=gdap-status location=northcentralus retainLogs=<true-or-false> --mode Incremental --query properties.outputs
```

3. Make a protected copy of application.parameters.example.json named
   application.parameters.local.json in deploy/azure. Restrict file/directory
   access (0600 on Linux; equivalent Windows ACL). In a secure editor supply
   the approved image digest, matching prefix/region, configuration and CIPP
   secret. Never put the credential in chat, CLI arguments, shell variables,
   environment, Git or transcripts. Local parameter files are gitignored but
   plaintext: protect backups too. ARM secure inputs protect deployment history,
   not the local file. Do not enable CLI debug logging.

4. Preview the app. Secret diffs are not a reliable security audit; review
   permissions and configuration too. Do not share raw secret-bearing output.

```bash
az deployment group what-if --subscription '<subscription-id>' --resource-group CIPP-Resorces --template-file deploy/azure/application.bicep --parameters @deploy/azure/application.parameters.local.json --mode Incremental
```

5. After approval, deploy and obtain the generated HTTPS origin.

```bash
az deployment group create --subscription '<subscription-id>' --resource-group CIPP-Resorces --name gdap-status-application --template-file deploy/azure/application.bicep --parameters @deploy/azure/application.parameters.local.json --mode Incremental --query properties.outputs.companionOrigin.value --output tsv
```

The unchanged server file contract mounts settings at /config/settings.json
and the CIPP secret at /run/secrets/cipp-client-secret. Restrict Azure roles
that can list secrets or change containers. Remove the local plaintext parameter
file after storing the credential securely. Never delete the shared group on failure.

## Live checks and upgrades

Verify /healthz returns 200, anonymous /v1/connection returns 401, assigned
staff can connect and unassigned users are denied. Use the generated origin in
desktop settings 3 → C, replacing its old custom-host default. Check an already
approved invitation through menu 5; do not repeat customer approval.
Leave it idle until zero replicas and test an actual cold-start status read.
Confirm cancellation/timeouts leave approval unchanged and inspect actual costs.

Deploy new tested digests without touching CIPP. Increment configurationVersion
when rotating settings/secrets to replace the process and clear cached tokens.
Verify before revoking old credentials. Roll back with the previous approved
digest and compatible settings. No automatic updater is added.

## Verification and primary sources

Test-AzureDeployment.ps1 compiles both templates and checks the absence of ACR/
identity/role assignments, mandatory logging choice, optional workspace, HTTP
scale-to-zero, one-replica limit, GHCR digest pinning, secure inputs and mounts.
These tests do not replace live quota/policy, anonymous image pull, runtime
mount, staff authentication or cold-start validation.

The disabled-logging regression checks the compiled null destination and null
workspace configuration against Azure CLI's payload. The previous string `"none"`
compiled successfully but failed Azure provider preflight; compilation alone
does not prove service acceptance. Run Azure what-if before deploying.

- [Scaling and billing behavior](https://learn.microsoft.com/en-us/azure/container-apps/scale-app)
- [Logging options](https://learn.microsoft.com/en-us/azure/container-apps/log-options)
- [Azure CLI logging payload mapping](https://github.com/Azure/azure-cli/blob/dev/src/azure-cli/azure/cli/command_modules/containerapp/containerapp_env_decorator.py)
- [GHCR access](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry)
- [GitHub package billing](https://docs.github.com/en/billing/concepts/product-billing/github-packages)
- [Azure secret handling](https://learn.microsoft.com/en-us/azure/container-apps/manage-secrets)
