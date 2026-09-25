# Azure invitation connector setup

Current source preparation, **not a completed deployment**. Use this document
instead of the historical status-only setup. The old published image lacks the
creation endpoints and the offline journal probe; build/publish a reviewed new
digest before deploying. Do not put placeholders or credentials in chat.

## Exact scope

- Subscription: `7c99b9cd-a6e2-4ca4-8ee3-68ab6ad7b670`.
- Resource group: `CIPP-Resorces` (the existing spelling).
- Region: `northcentralus`.
- Existing environment: `gdap-status-environment`. Do not recreate it or change
  its logging choice.
- Companion Container App: `gdap-status`. Its historical name does not imply
  onboarding polling; creation and browser handoff are the normal workflow.
- Staff tenant: `b618675e-4f91-4bc1-8ab6-3e7bc2c5cfaf`.
- CIPP browser origin: `https://cippabcmq.azurewebsites.net`.
- Partner tenant: `39851031-8246-4fdc-941b-b504fcb5df10`.

No change to CIPP's source, container image, App Service plan, Key Vault, storage
or webhook settings. Authorizing a dedicated CIPP API client is a separate,
explicit CIPP configuration change, not a CIPP software modification.

## Storage proposal and costs — approve before deploying

`journal.bicep` declares four ARM resources: one dedicated Standard_LRS StorageV2
account, its default file service, one classic SMB share and one storage attachment
under the existing Container Apps environment. It does not redeploy that
environment. The deterministic account name starts `gdapj`; there is no parameter
for reusing CIPP's account or supplying its key.

The share uses HDD pay-as-you-go, TransactionOptimized and a 1 GiB quota. Billing
is for used storage, transactions and applicable transfer, not the quota. This
avoids provisioned-capacity billing for a tiny intermittent journal, but is not a
fixed price or a spending cap. Confirm the subscription's North Central US rates
before creation. Container compute still scales to zero; no paid registry, extra
plan, Log Analytics workspace, NAT gateway or VNet is introduced.
([Azure Files billing](https://learn.microsoft.com/en-us/azure/storage/files/understanding-billing),
[pricing](https://azure.microsoft.com/en-us/pricing/details/storage/files/))

The account has an authenticated public network endpoint because the existing
environment has no private-network integration. Blob public access is disabled;
secure transfer is required. If policy requires private endpoints or fixed egress,
stop and redesign networking; do not relax policy or guess IP allowlists.

Azure mounts SMB with an account key, so isolation avoids exposing CIPP storage
through an account-wide key. ARM passes the key directly to the environment
attachment; it is not a template output or a shell argument. Limit who can read
keys, edit the environment attachment or run commands in the container. Account
key rotation requires refreshing the attachment and verifying mounts.
([Container Apps storage](https://learn.microsoft.com/en-us/azure/container-apps/storage-mounts),
[attachment schema](https://learn.microsoft.com/en-us/azure/templates/microsoft.app/2025-01-01/managedenvironments/storages))

Share-level soft deletion is enabled for 14 days; this is not file-level backup.
There is no automatic journal cleanup. Do not delete unresolved records or restore
an older journal over current state. If capacity or storage access fails, leave
creation disabled while investigating, rather than clearing records.

## Identity setup inputs

Two companion registrations have different jobs: the desktop identifies the
public client signing in staff; the connector registration is the protected
resource staff are allowed to call. Neither needs a client secret. CIPP's own
dedicated API client is separate and is the only secret-bearing identity here.
Do not assume any of these registrations already exists.

For a new connector registration in the confirmed staff tenant:

- Use a single-tenant registration, recommended name `GDAP Invitation Connector`.
- Set its Application ID URI to `api://<connector-client-id>` and the manifest's
  `api.requestedAccessTokenVersion` to `2`.
- Expose enabled, admin-consent-only delegated scope `Invitations.Create`.
- Add enabled app role `Invitations.Create`, allowed member type Users/Groups.
- In its Enterprise Application, require assignment and assign only the approved
  staff user(s)/group(s) to that role. Do not grant everyone access. Keep MFA and
  Conditional Access intact.

For the desktop registration, recommended name `GDAP Acceptor Desktop`:

- Single tenant; Authentication → Add a platform → Mobile and desktop applications,
  with system-browser callback `http://localhost`. Do not create a secret.
- Grant delegated `api://<connector-client-id>/Invitations.Create` and admin consent.
- Record both client IDs; application client IDs, not application object IDs.

If reusing prior companion registrations, inspect their IDs, assignments and
existing permissions first; add the new role/scope without replacing unrelated
settings. The launcher uses PKCE, not password, embedded browser or client-secret
authentication. Resource access requires both the role and delegated scope.
([Expose scopes and roles](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-configure-app-expose-web-apis),
[desktop registration](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-app-configuration))

In CIPP → Integrations → CIPP-API, use Actions → Create New Client (or deliberately
import an identified existing dedicated client), enable it, assign a custom role
with `Tenant.Relationship.ReadWrite`, and save to Azure. MCP is not needed.
Copy the actual API origin, authentication tenant/token endpoint, client ID and
scope from CIPP's configuration; do not infer them from the portal URL. Store the
secret privately. This permission is broader than creation, so keep it dedicated;
the connector only exposes template reads, invitation creation and recovery.
Do not silently disable existing IP restrictions: the selected compute has no
fixed egress, and private/fixed-egress requirements need a separate decision.
([CIPP client setup](https://docs.cipp.app/user-documentation/cipp/integrations/cipp-api))

## Preview and staged deployment

Run from the reviewed source checkout in authenticated Azure Cloud Shell. Register
Microsoft.Storage if necessary; Microsoft.App was handled during foundation setup.
All commands below are single lines. Never enable CLI debug or echo secret files.

1. Preview the journal only. Expect the four companion resources above; stop if
   the preview targets CIPP resources. A what-if success is not runtime verification.

```bash
az deployment group what-if --subscription 7c99b9cd-a6e2-4ca4-8ee3-68ab6ad7b670 --resource-group CIPP-Resorces --template-file deploy/azure/journal.bicep --parameters namePrefix=gdap-status location=northcentralus --mode Incremental
```

2. Only after cost/network/preview approval, deploy that journal template in
   Incremental mode. Record only its non-secret output names.

```bash
az deployment group create --subscription 7c99b9cd-a6e2-4ca4-8ee3-68ab6ad7b670 --resource-group CIPP-Resorces --name gdap-invitation-journal --template-file deploy/azure/journal.bicep --parameters namePrefix=gdap-status location=northcentralus --mode Incremental --query properties.outputs
```

3. Prepare a protected `deploy/azure/application.parameters.local.json` from the
   example (0600 permissions, private directory/editor, excluded from Git). Supply
   the approved newly built image digest and actual identity/CIPP inputs. Set
   `mountInvitationJournal=true`, `enableInvitationCreation=false`. The CIPP secret
   belongs only in this protected file/approved secret store, never command text.
   `Audience` is the connector client GUID; `DesktopClientId` is the desktop GUID.

4. Preview, inspect, then separately approve the app deployment. The app template
   only changes the companion Container App. Both feature flags default false;
   creation requires both to be true. Mounting alone does not authorize creation.

```bash
az deployment group what-if --subscription 7c99b9cd-a6e2-4ca4-8ee3-68ab6ad7b670 --resource-group CIPP-Resorces --template-file deploy/azure/application.bicep --parameters @deploy/azure/application.parameters.local.json --mode Incremental
```

```bash
az deployment group create --subscription 7c99b9cd-a6e2-4ca4-8ee3-68ab6ad7b670 --resource-group CIPP-Resorces --name gdap-invitation-application --template-file deploy/azure/application.bicep --parameters @deploy/azure/application.parameters.local.json --mode Incremental --query properties.outputs.companionOrigin.value --output tsv
```

## Mount and authorization checks before enabling creation

Use `az containerapp exec`/the Azure container console in the running companion
revision. The probe command runs as the image's non-root user and requires no
settings, credentials or network. It uses only a `.probe-<guid>` subdirectory of
`/var/lib/gdap-journal`; no real operation or invitation is changed or removed.

Generate a fresh non-secret probe GUID locally and retain it. In the container:

```text
dotnet GdapStatusServer.dll journal-probe prepare <probe-guid>
```

Expect PROBE PREPARED. It tests 16 simultaneous reservations, durable flush,
readback and replay denial through the production journal implementation. Failure
must stop setup; do not change ownership to world-writable or use ephemeral disk.
The mount maps to UID/GID 1654 with 0600 files, 0700 directories, strict caching
and fresh metadata; it does not disable SMB locks.

Increment `configurationVersion` in the protected parameter file and redeploy
with creation still disabled. In the replacement revision run:

```text
dotnet GdapStatusServer.dll journal-probe verify <same-probe-guid>
```

Expect PROBE VERIFIED. Also verify after scaling down/up. This confirms readback
across replacements; the prepare race is in-process. Local/container CI is not
proof of Azure SMB behavior under cross-revision races or storage faults; verify
that operationally before claiming distributed exactly-once semantics.

Verify anonymous creation/template endpoints return 401; assigned staff can
complete connector setup and unassigned staff cannot. While disabled, template
and creation endpoints must return 503 for authorized staff. This separates
staff sign-in verification from authorization to create a GDAP invitation.

After those checks and explicit live-use approval, set
`enableInvitationCreation=true`, increment `configurationVersion`, preview and
deploy again. The first authorized live flow selects a known CIPP template,
generates one invitation, approves in the customer session and opens CIPP's exact
onboarding page. CIPP's existing automation owns onboarding; do not submit another
job to compensate for webhook delay.

To suspend creation, redeploy with `enableInvitationCreation=false`; keep the
storage mount and all journal files. Do not roll back to an image without durable
creation safeguards. Remove protected local credential files only after saving
credentials in the approved secure store. No cleanup may target the shared group.
