# Central CIPP status — 0.4.0 development

Target: a **separate Azure Container App**, in the same subscription and resource
group as CIPP. The user superseded the Ubuntu-host plan. Use
[Azure deployment](AZURE-DEPLOYMENT.md), not the Docker-host steps below.
No Azure resources or live staff/CIPP authentication have been verified or
deployed. No custom CIPP build, webhook modification or new onboarding job is
needed. Customer acceptance is unchanged. Azure's generated HTTPS origin is the
initial companion address; `cippapi.fizlian.dev` is optional later.
The lower-cost revision uses HTTP scale-to-zero and a public GHCR image, not
paid ACR. Retained companion logs require an explicit decision; CIPP's existing
onboarding logs are unaffected. Previous five-resource previews are superseded.

## Architecture and access scope

The companion approves GDAP in a fresh customer browser. Microsoft's webhook
triggers existing CIPP onboarding. Separately, the companion authenticates an
authorized **staff** member to the central host. That host reads CIPP with one
dedicated credential and returns only the exact relationship's status,
observation time and configured CIPP/partner identity—not the full table or logs.

One CIPP instance/partner is configured per server. Assigned staff can read
status for **any relationship in that instance**, not only their own approvals.
This is not customer-facing access. Possession of an invitation grants no access.

## 1. Staff identity setup (administrator, once)

Use your staff Entra tenant; this need not be CIPP's authentication tenant.
Register two single-tenant applications. Neither needs a desktop client secret.

**`GDAP Status API` — resource application:**

1. Record its directory tenant ID and application/client ID (`Audience`).
2. Set Application ID URI to `api://<central-api-app-id>` and expose the delegated
   scope **`Status.Read`**, with admin consent only.
3. Set manifest `api.requestedAccessTokenVersion` to **2**.
4. Add enabled app role **`Onboarding.Read`**, allowed member type **Users/Groups**.
   Do not define it as an application-only permission.
5. In this application's Enterprise Application, require assignment. Assign
   authorized technicians, or an eligible licensed staff group, to `Onboarding.Read`.
   Do not assign all users or customer accounts by default.

**`GDAP Acceptor Desktop` — public client:**

1. Record its application/client ID (`DesktopClientId`). Do not create a secret.
2. Add the **Mobile and desktop applications** platform and redirect URI
   **`http://localhost`**. MSAL uses a workstation loopback port; this is not the
   public server URL.
3. Add delegated permission `GDAP Status API / Status.Read`; grant admin consent.
4. Apply the appropriate staff Conditional Access policy to the central API
   resource. This code does not bypass MFA or device requirements.

The server validates signature, issuer, audience, expiry, tenant, v2 token version,
delegated scope, desktop `azp`, staff `oid` and app role. Proxy identity headers
and CIPP browser cookies are not authentication. Only `/healthz` is anonymous;
it reports process liveness, not CIPP or Entra readiness.

## 2. CIPP identity setup (administrator, once)

Create/import and enable **one dedicated CIPP API client**. Assign a custom read
role requiring `Tenant.Administration.Read`, restrict unnecessary endpoints, and
use Save to Azure as required by CIPP. Do not grant write categories or SuperAdmin.
The read category can authorize other endpoints; validate effective permissions.
The underlying CIPP endpoint returns its whole onboarding table; central projection
does not reduce what the CIPP credential itself can read.

Copy CIPP's displayed API origin, authentication tenant, client ID and scope.
Do not infer these from the portal URL. Only public-cloud Microsoft authentication
is supported. CIPP IP restrictions must match the **central service's actual
outbound public IP**, not workstation IPs or the frontend/DNS address. The basic
Azure template does not provide fixed egress: resolve that requirement before
deployment with a separately reviewed network design. Do not use `96.11.28.184`
for Azure egress.
IP restrictions are optional; authentication/authorization remain mandatory.

## 3. Alternative Docker-host files and secret (not the selected Azure deployment)

From the tested source root, copy `deploy/settings.example.json` to
`deploy/settings.json` and replace every `REPLACE_` value. Confirm the prefilled
CIPP portal and partner. Use lowercase canonical GUIDs. `CippApiOrigin` must be
an HTTPS origin **without `/api`**. The central API app ID and desktop app ID are
different from the CIPP API client ID.

Store the CIPP secret in `deploy/secrets/cipp-client-secret` with a secure local
editor or secret-management tool—not CLI arguments, environment entries, Git,
chat or an image. Both local configuration and secrets are gitignored. The Docker
build context contains only `server/` and `shared/` sources.

Compose mounts it read-only at `/run/secrets/cipp-client-secret`. The app runs
as UID/GID **1654**, which must be able to read the mounted file. With rootful
Docker, one option is a root-owned secret directory mode `0700`, with its file
owned by `root:1654`, mode `0440`; check host group membership. Rootless Docker
needs permissions matching its user mapping. File-backed Compose secrets are
protected host files/bind mounts, **not encrypted storage**. Root/Docker
administrators can access them. Protect backups accordingly.

Invalid required configuration or unreadable secrets stop startup. To rotate,
replace the protected file and recreate/restart `status` to discard its memory
token; revoke the old CIPP credential after verification.

## 4. Alternative Docker-host launch (not the selected Azure deployment)

No server or DNS changes were performed during development. Before starting:

- Check listeners on the Ubuntu host: `sudo ss -ltnp '( sport = :80 or sport = :443 or sport = :5080 )'`.
- Check containers: `docker ps --format 'table {{.Names}}\t{{.Ports}}'`.
- If ports are already used, do not stop those services; use the existing-proxy
  option below or choose a reviewed configuration.
- Point the hostname's A record at the server. Set AAAA only if IPv6 works.
  Make 80/443 reachable for Caddy's certificate handling. Keep backend 5080/8080
  off the public internet. No ACME requests occur until the proxy is deployed.

**Included HTTPS proxy:** from the source root run
`docker compose -f deploy/compose.yml --profile https up -d --build`.
Caddy serves `cippapi.fizlian.dev`; certificate state is in persistent named
volumes. Do not delete those volumes during upgrades.

**Existing reverse proxy:** run
`docker compose -f deploy/compose.yml up -d --build status`.
A host-based proxy can forward to `http://127.0.0.1:5080`. A containerized proxy
must join `gdap-status_default` and forward to `http://status:8080`; its own
localhost is not the host. Pass Authorization unchanged, disable response caching
and do not log bearer headers or invitation paths at a proxy/CDN layer.

The app is non-root, read-only, capability-dropped and resource-limited. It has no
Docker socket and needs outbound Microsoft discovery/token and CIPP connectivity.
Run **one replica**: cache, cooldown and rate limits are in-memory/process-local.
Microsoft .NET 10/Caddy 2 image tags receive updates; pin approved digests for
production and rebuild/test intentionally. No public container image is published.

## 5. Companion and first live check

Install matching companion 0.4.0. Choose **3 → C**. Enter
the Azure deployment's `companionOrigin` (replace the old custom-host default),
staff tenant ID, desktop app ID and central
API app ID. Review the expected CIPP/partner and type `CONNECT`. Sign in as staff
in the default browser. Setup verifies staff access and server binding before
saving non-secret identifiers; it does not test upstream CIPP until a status read.

Choose **5. Check CIPP onboarding** and paste an **already-approved invitation**.
No customer sign-in, repeat approval or new onboarding task is required. Verify
an authorized staff member succeeds and an unassigned account is denied. Future
approved invitations automatically watch when configured. Without this server,
acceptance and CIPP's webhook onboarding still work.

Staff tokens are memory-only. The selected account can be used silently within
the same process; initial interactive sign-in offers account selection. Restart
the companion to discard tokens/change staff accounts. This is separate from
fresh customer sign-in. Live Windows/Linux browser sign-in remains a deployment
validation step; neither desktop requires a CIPP API secret.

## Failure behavior and limits

The central host reads CIPP at most once per 30 seconds across callers. Only
status projections are retained; response caching is disabled. Missing records
are waiting, not success. Queued/pending are not running. Watching ends at running
or terminal status, or at 40 reads/20 minutes. Ctrl+C cancels only observation.
Status failure never retries approval or retains a successful acceptance's lock.

Requests to CIPP are bounded to 30 seconds/4 MiB. Failure clears cached evidence
and starts a 60-second cooldown; rate limiting may extend it up to one hour.
No stale success or raw upstream error is returned. The server's shared inbound
limit is 120 requests/minute with a burst of 120. Responses use 503/Retry-After
for upstream cooldown and 429 for inbound rate limiting. Clients reject wrong
identity, malformed, future or >2-minute-old evidence; keep clocks synchronized.
Multi-replica coordination and large-table pagination are not implemented.

Audit logs contain HTTP result and staff object ID, not secrets/tokens, customer
logs or relationship IDs. Protect staff identifiers with log access/retention
controls. An application health check is not an upstream readiness check.

## Migration

Old 0.3.0 vault entries are never read, used or uploaded. **3 → L**, then `REMOVE`,
deletes only the selected enrollment's old local credential, under the same OS
user/state directory. Repeat on previously configured workstations. Revoke any
distributed credential separately in CIPP; deleting a local entry is not revocation.
**3 → D** removes only non-secret central settings, not server credentials or roles.

## References

- [CIPP API setup](https://docs.cipp.app/user-documentation/cipp/integrations/cipp-api)
- [JWT validation](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0)
- [Desktop authentication](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-acquire-token-interactive)
- [Compose secrets](https://docs.docker.com/compose/how-tos/use-secrets/)
- [Caddy HTTPS prerequisites](https://caddyserver.com/docs/automatic-https)
