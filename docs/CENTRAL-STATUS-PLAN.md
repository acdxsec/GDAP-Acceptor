# Central onboarding status — implementation plan

Status: implemented in 0.4.0 development source; deployment and live authentication pending.
No resources, Entra registrations, credentials or CIPP settings have been changed.
The 0.3.0 workstation connector is not this implementation.

## Responsibilities

- Workstation: retain the verified Microsoft invitation opening, customer
  sign-in/MFA, tenant and partner validation, and explicit approval flow.
- Microsoft and CIPP: retain the existing webhook and automated onboarding.
- Central status module: hold one dedicated CIPP read credential, authenticate
  technicians and return a minimal status for one exact relationship.
- Workstation status module: use technician sign-in, not a CIPP client secret.
  Its failure must not invalidate approval, retain the acceptance reservation,
  or trigger another approval/onboarding request.

## Hosting and access

User selected an existing Ubuntu Docker server and public hostname
`cippapi.fizlian.dev`, independent of CIPP's source, image and deployment.
The user does not believe a reverse proxy is already installed; include optional
Caddy HTTPS handling, but check existing listeners before deployment.
Use single-tenant Entra authentication; authorize an explicitly assigned staff
group through an application role. Authorized staff are allowed to inspect
all relationships for the configured partner, not only their own approvals.
The administrator must deliberately assign staff to that permission scope.

Use a public desktop client with authorization-code PKCE and a system-browser
callback for technician sign-in. Keep it independent of the fresh customer
approval browser. No shared desktop secret, anonymous status endpoint or trust
in caller-supplied identity headers. Validate token signature, issuer, audience,
expiry, delegated scope, role and authorized desktop client at the central host.

Store the CIPP client secret centrally in a protected, read-only Docker secret
file. Keep CIPP API origin, authentication tenant, client ID, scope and partner
binding in administrator-controlled configuration, never in request parameters.
No Azure hosting, managed identity or Key Vault is required for this deployment.
File-backed Compose secrets are not encrypted storage; protect the host and backups.

Workstation IP allowlisting is not required for CIPP. If CIPP IP restrictions
are enabled, allow the Ubuntu host's actual public egress address. Do not infer
outbound identity from DNS or provision any Azure network resources.

## Implementation slices and test interfaces

1. Central authenticated HTTP endpoint: reject anonymous, wrong-tenant,
   wrong-audience and unassigned callers before contacting CIPP. Accept only an
   exact relationship identifier for the single server-configured CIPP instance;
   do not accept URLs, credentials or arbitrary CIPP operations from callers.
2. Read CIPP's onboarding table centrally and project the matching status plus
   observation time and configured partner identity. Preserve waiting, pending,
   queued, running, succeeded, failed and cancelled distinctions. Never return
   the raw table, customer logs, upstream errors or credentials. Bound upstream
   requests and rate-limit/coalesce reads to avoid one full-table poll per user.
3. Companion command interface: replace local API-secret setup with central
   connection configuration and technician sign-in. Preserve bounded watching,
   cancellation, exact relationship correlation and independent approval results.
4. Migration: stop reading old local API credentials; provide explicit local
   credential removal. Do not upload them to the host or silently delete them.
   Previously distributed credentials require administrator review/revocation.
5. Prepare deployment files and operator setup; test locally and in CI with
   synthetic identities and upstream responses before deployment.
6. After separately approved deployment, verify real staff authorization,
   denied access, CIPP read permissions and one already-approved invitation.
   No repeat customer approval is required for this check.

Tests exercise companion commands and the real authenticated central HTTP pipeline,
with synthetic signing keys and upstream responses. Container smoke tests check
non-root/read-only startup and anonymous rejection on the packaged image.

## Primary references

- [Desktop interactive authentication](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-acquire-token-interactive)
- [Docker deployment and setup](CIPP-STATUS.md)
- [CIPP authentication evidence](research/CIPP-STATUS-AUTH.md)
