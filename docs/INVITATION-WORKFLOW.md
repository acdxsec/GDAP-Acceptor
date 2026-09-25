# CIPP invitation creation → customer acceptance → CIPP handoff

This is the current requested workflow, superseding the status-only deployment
plan. Source implementation is under development; it is not in the published
desktop installers or the previously published status image. Do not deploy that
image expecting invitation creation.

## Operator flow

1. Configure a trusted CIPP origin and partner as before. Pasting an existing
   invitation still works without a connector or staff app registration.
2. For creation, configure the authenticated invitation connector in **3 → C**.
   Its address is the deployed Azure HTTPS origin, not a workstation or CIPP
   browser session. No CIPP credential is stored on the desktop.
3. Choose **6. Create/resume invitation through CIPP, then accept**. Sign in as
   authorized staff. Select an existing CIPP role template, review its roles and
   group mappings, optionally enter a client/ticket reference, and type CREATE.
4. The connector fetches the authoritative template again, rejects changed
   mappings, records the attempt durably and calls CIPP to create one invitation.
5. The desktop validates the returned partner/relationship and runs the existing
   customer sign-in, tenant confirmation, invitation inspection and approval.
6. Only a verified active GDAP result clears the approval reservation and opens
   CIPP's `/tenant/gdap-management/onboarding/start?id=<relationship>` page.
   Existing CIPP webhook automation owns onboarding. No ExecOnboardTenant call,
   extra job, retry or status watch is submitted by the companion.

If the webhook has not arrived, refresh the CIPP page later; do not click Start
to create a second job. Opening the page is not proof of onboarding completion.
Menu **5** opens this same relationship-specific page for a pasted invitation.

## Permissions and configuration

Use the user-confirmed staff tenant
`b618675e-4f91-4bc1-8ab6-3e7bc2c5cfaf`. Hosting remains in the existing sponsorship
subscription; partner binding remains `39851031-8246-4fdc-941b-b504fcb5df10`.

- Connector resource registration: single tenant, v2 access tokens,
  `api://<app-id>`, admin-consent delegated scope **Invitations.Create** and an
  enabled user/group app role **Invitations.Create**. Require assignment and
  assign only explicitly authorized staff. Keep existing Conditional Access.
- Desktop registration: single tenant, public client, `http://localhost`
  callback, delegated Invitations.Create permission with admin consent. No secret.
- CIPP API client: dedicated centrally held credential, CIPP's
  **Tenant.Relationship.ReadWrite** category. Read-only credentials are insufficient.
  This category is broader than creation alone; CIPP also guards template editing
  and invite update/delete with it. The connector does NOT expose those operations.
  It exposes no arbitrary CIPP URL, action, roles, groups or onboarding mutation.
- Existing ServiceSettings file fields and CIPP secret mount remain unchanged.
  Both authentication tenant and CIPP API origin/scope must come from the actual
  CIPP configuration, not an assumption based on the portal URL.

Legacy status endpoints and CLI commands remain for compatibility, but are not
in the normal menu or completion path. Status.Read/Onboarding.Read cannot create
invitations; invitation staff do not need those old permissions.

## Durable creation journal — deployment gate

CIPP has no creation idempotency key. An in-memory cache cannot prevent a second
relationship after an ACA restart. The connector therefore requires
`GDAP_INVITATION_JOURNAL_DIR` pointing to a dedicated **persistent shared filesystem**
that preserves atomic create-new and durable flush semantics across every revision
and replica. With this variable absent, POST creation is disabled (HTTP 503).
The directory must already exist and be writable by the container's non-root UID.

**The Azure templates now offer a dedicated SMB journal mount, with creation
disabled by default.** Follow [Azure invitation setup](AZURE-INVITATIONS.md).
Cost/network approval, deployment and live restart/concurrency verification remain
required. Do not point this setting
at `/tmp`, the container's writable layer or per-replica ephemeral storage merely
to bypass the gate. No storage resources have been created by this code change.

The journal contains operation IDs, staff IDs, client references, role mappings
and invitation links, but no tokens or credentials. Restrict access and protect
backups. Do not expire/delete unresolved entries or restore an older journal
over current state: either can invalidate duplicate prevention.

Before transmitting creation, the desktop saves a non-secret operation record.
The server exclusively creates a durable attempt file before POSTing to CIPP.
That operation is bound to its staff member, CIPP/partner, request and role mapping
fingerprint. Repeated requests with the same ID do not POST again. A new operation
ID is a new authorization to create; this is not cross-operation deduplication.

On uncertain results, desktop resume sends **GET only**. The connector searches
ListGDAPInvite for the exact `[GDAP-Acceptor:<operation-id>]` reference marker,
validates partner, ID and mappings, then saves the recovered result. Multiple or
conflicting matches fail closed. No match stays uncertain, even when the original
request may never have reached CIPP. There is no automatic reset/expiry.

The operator must review unresolved cases in CIPP with the server administrator.
If a request never reached the connector, recovery has no server record and remains
blocked; don't delete the desktop record or generate a replacement without review.
A self-service reviewed-abandon/replacement flow is not implemented in this slice.
An uncertain GDAP approval retains the existing separate approval reservation.

## Verified contracts and remaining gates

Automated synthetic contracts cover authoritative template selection, changed
template rejection, strict/size-bounded request input, staff-only creation policy,
read-only caller rejection, durable replay suppression after server recreation,
concurrent reservation, response loss, absent/conflicting evidence, wrong partner,
desktop resume, no-secret persistence and the exact browser handoff. Existing
PowerShell approval/wrapper/launcher outcome contracts remain in place.

Before live use: approve and provision the dedicated persistent mount, configure Entra/CIPP
permissions, build/test new matched desktop and connector artifacts, validate
against the installed CIPP version, and perform one authorized live creation and
acceptance. Existing CIPP source, image and webhook behavior must stay unchanged.

## Source contracts inspected

- [CIPP invitation handler](https://github.com/acdxsec/CIPP-API/blob/master/Modules/CIPPHTTP/Public/Entrypoints/HTTP%20Functions/Tenant/GDAP/Invoke-ExecGDAPInvite.ps1): Action=Create, roleMappings, Reference, Invite.RowKey/InviteUrl/RoleMappings.
- [CIPP role template handler](https://github.com/acdxsec/CIPP-API/blob/master/Modules/CIPPHTTP/Public/Entrypoints/HTTP%20Functions/Tenant/GDAP/Invoke-ExecGDAPRoleTemplate.ps1): Results, TemplateId, RoleMappings.
- [CIPP relationship onboarding page](https://github.com/acdxsec/CIPP/blob/b1238941d1f14a0fc16416e8df5161afa0b17950/src/pages/tenant/gdap-management/onboarding/start.js): query `id` selects the relationship and existing onboarding; job submission is a separate user action.
