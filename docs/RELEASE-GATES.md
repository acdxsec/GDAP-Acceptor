# Release gates and known limitations

## Current distribution decision

Continue with unsigned development builds; the user declined paid signing.
The user separately approved the public
[0.1.6 prerelease](https://github.com/acdxsec/GDAP-Acceptor/releases/tag/v0.1.6),
published from tested source `7938ce0a99f0511371c398eb98caad09abf68be6`.
No signing subscription, automatic updater or APT repository was created.
Existing desktop policies still apply.
Windows and Linux 0.1.6 artifacts come from the same tested source tree. Signing
items below describe future production-release work, not a requirement to keep
using the working unsigned tool. See [unsigned distribution](UNSIGNED-DISTRIBUTION.md).

## Live evidence already obtained

On September 18, 2026, the user's Linux PowerShell acceptance run was followed by
CIPP automatic onboarding. The exported CIPP onboarding log confirms invitation
acceptance, required roles, successful group mapping and SAM-user group checks.
CIPP then deliberately rescheduled for Microsoft propagation. These observations
establish the acceptance-to-existing-CIPP handoff; they do not establish final
onboarding completion or live validation of the new native launcher.

On September 21, the user also completed the packaged Linux script's explicit-
customer path: fresh sign-in, same-tab invitation navigation, live tenant and
access validation, confirmed approval and an active relationship readback. The
user then confirmed that CIPP automated onboarding started. This establishes
the repaired packaged acceptance path and handoff, not final CIPP completion.
The customer-specific `Test-Gdap.ps1` runner used for that check is not shipped.

The user subsequently reported that the normal Linux launcher also appeared to
work after using the explicit reservation-recovery command. This is operator-
reported live launcher evidence, not an automated assertion of every downstream
onboarding step or of the remaining desktop/release gates.

On September 22, the user completed Windows test revision 2: private Edge sign-in,
explicit CUSTOMER selection, same-tab invitation navigation, tenant revalidation,
partner and requested-access validation, and the final WhatIf preview. The output
confirmed an exited launch process with a still-live private browser session.
This validates the process-handoff fix on that desktop. No approval was submitted;
the normal Windows launcher and installed Start Menu path remain separate checks.

After receiving the normal Windows 0.1.5 launcher and installer, the user reported
"ok that worked". This establishes operator-reported success for the normal
Windows workflow. It does not distinguish installer/Start Menu use from portable
launch, nor separately establish final CIPP onboarding completion.

## Existing Windows CI evidence

[GitHub Actions run 34390399881](https://github.com/acdxsec/GDAP-Acceptor/actions/runs/34390399881)
passed on September 9, 2026, for commit
`24193a5779946a49280003891ff4fd65d533ce33`. It includes Windows native contracts,
payload assembly and per-user MSI install, collision/context guards, upgrade,
downgrade rejection, uninstall and enrollment retention.
That historical run predates the browser/launcher/recovery changes and is not
validation of those changes.

[GitHub Actions run 35752406930](https://github.com/acdxsec/GDAP-Acceptor/actions/runs/35752406930)
passed all four jobs for commit `d86536ec8cb350c7658a70b6659272d11c3b1449`,
including the process-handoff fix. It covers Windows/Linux native and packaged
checks, 50 acceptance scenarios, wrapper subprocess checks, isolated Windows
Edge navigation and unsigned packaging. MSI lifecycle checks cover per-user
installation, collision/context guards, installed CLI/wrapper and Start Menu
metadata, repair, upgrade/downgrade, uninstall and enrollment/reservation retention.
These were disposable-runner checks, not live Microsoft authentication.

The distributed 0.1.5 artifacts passed all four jobs in
[run 35758177011](https://github.com/acdxsec/GDAP-Acceptor/actions/runs/35758177011).
Source head `1412f99c29ee640e0d1e84d7172f923f5a516125` and PR merge build commit
`ba23c02b89dc58e7658369b158868aa8e5ed1eb3` have the same source tree. All 315
extracted MSI files were hash-compared to the tested portable Windows payload.
The CI-only 0.1.6 upgrade package is a test fixture, not a release.
The parallel push run initially timed out acquiring a state lock during concurrent
enrollment; its unchanged retry passed all jobs. The locking safeguards were not
weakened, and the timing cause is not proven.

The bundled approval adapter now includes the tested browser-navigation flow,
the observed portal response contract, live tenant validation, User-Agent fixes
and correct WhatIf/confirmation scoping. Numeric duration is displayed as returned,
not used to change the invitation's terms.

## 0.1.6 outcome-review correction

Source/build 0.1.6 implements [issue #2](https://github.com/acdxsec/GDAP-Acceptor/issues/2).
Its published packages are the unchanged tested artifacts from
[run 35862592526](https://github.com/acdxsec/GDAP-Acceptor/actions/runs/35862592526),
which passed all four Windows/Linux verification, packaging and installer jobs.
Uncertain/post-submission outcomes and abnormal child exits retain the local
reservation until explicit review. The wrapper distinguishes these from known
preflight cancellation and verified active success. Tests use real approval and
wrapper code with synthetic portal boundaries, including a second-launch refusal
before authentication. No live approval is required for these regression checks.
The 0.1.5 release remains immutable and does not contain this correction.
The 0.1.7 package produced by that workflow was an upgrade-test fixture only.

## 0.2.0 guided workspace

Development source adds a scrolling terminal menu for invitation acceptance,
queue/recovery, trusted connections and diagnostic export. The existing approval
path now prints separate access-review fields and role IDs. All consent and
identity checks remain in place; uncertain outcomes still retain their reservation.
See [operator workspace](OPERATOR-WORKSPACE.md) for scope and acceptance criteria.
That workflow's 0.2.1 installer was an upgrade-test fixture, not a release.
The published 0.1.6 assets are unchanged. New Windows/Linux CI and desktop evidence
must be distinguished from the historical acceptance evidence above.

The user subsequently reported that the new menu appears to work. This is
operator-reported menu evidence, not validation of the later CIPP connector.

## 0.3.0 read-only onboarding-status connector

Historical implementation, superseded by 0.4.0's central credential model below.
Its setup is retained in [historical 0.3.0 documentation](CIPP-STATUS-0.3.0.md).

Development source adds dedicated API-client configuration, OS-vault storage,
exact-relationship status reads and a bounded watch after verified acceptance.
It does not change CIPP or submit onboarding tasks. A status error or cancelled
watch cannot change a successful acceptance or retain its reservation.
See [CIPP status](CIPP-STATUS.md) for the operator setup and detailed constraints.
Native launcher tests cover the connector with synthetic HTTP and vault adapters;
Windows CI also round-trips an isolated synthetic credential through Credential
Manager. Live API configuration/permissions/response validation remains outstanding.
Linux's real desktop Secret Service integration also remains a desktop check;
there is no plaintext fallback when it is unavailable. The current workflow's
0.3.1 installer is an upgrade-test fixture, not a release. Published assets remain
unchanged; new development packages must be identified separately.

## Remaining gates

### 0.4.0 central status deployment

Development source replaces per-workstation CIPP secrets with public-client staff
sign-in to an independent central service. The user confirmed Azure in CIPP's
existing subscription/resource group, superseding the earlier Ubuntu/Docker plan.
Separate scale-to-zero Container Apps/GHCR deployment templates are prepared; CIPP remains
unchanged. No public host/DNS/firewall, Azure resource or CIPP configuration was
changed. No image or new public release was published.

Both Azure Bicep templates compile locally with compiler 0.47.16; offline
deployment-contract checks and all four existing server HTTP contract groups
pass. CI now includes the template checks, but this change has not been run in
remote CI. Target-subscription what-if/policy/quota checks, remote image build,
Azure runtime mounts/TLS and real staff/CIPP authentication remain unverified.

The later cost revision removes ACR and its pull identity/role, makes historical
logging an explicit choice and changes the replica range to zero through one.
The user's B2 plan has one instance but peaked at 95% memory over seven days;
sharing CIPP compute is not selected. A manual/main-only GHCR publishing workflow
is prepared but not executed; image publication and public visibility require
approval. The desktop status timeout is now two minutes, and a 45-second mock
response succeeds without retry. This does not establish live Azure cold-start
latency. Existing public desktop releases are not changed by source edits.

Local companion regression and central HTTP tests pass with synthetic credentials,
real JWT middleware and isolated upstream responses. The image builds, runs non-root
with a read-only filesystem, and rejects anonymous/spoofed identity requests. This
does not verify live Entra app registrations, staff browser sign-in, role assignment,
CIPP credentials/permissions, actual onboarding response or public DNS/TLS.
See [Azure deployment](AZURE-DEPLOYMENT.md) and [identity setup](CIPP-STATUS.md).
The 0.4.1 MSI is only an upgrade-test fixture.
Legacy credentials are never read/uploaded; removal is explicit and remote
revocation is a separate administrator action. Staff tokens are memory-only.

The server currently supports one partner, one replica, a 4-MiB CIPP table and
process-local 30-second read coalescing/cooldowns. Assigned staff can inspect all
relationships in that instance. Review those limits and host secret-file permissions
before production use. The old desktop-vault live-setup gate is superseded, not
completed; the new host and interactive sign-in gates remain open.

- In the published 0.1.5, a handled child-process approval error can clear its reservation even when the
  write outcome is uncertain. No automatic retry occurs, but the next explicit
  launch is not forced through outcome-review recovery. Inspect the Microsoft
  outcome after any approval error, even if the queue is empty. Preserving this
  review gate is implemented in source/build 0.1.6; use the newer build for this protection.
- Navigation now pins the validated browser tab. A competing admin-centre tab
  reproduced the reported readiness timeout offline; that test now passes.
  Actual Edge on an isolated blank page also passed the production page-state
  reader, including extra async-result objects from the pinned CDP helper. This
  is not live proof that competing tabs caused the user's particular timeout.
  Subsequent user evidence confirms successful live navigation with this fix.
- Explicit expected-tenant sign-in now handles a portal `/login` redirect as well
  as a direct Entra authorization URL. Offline launch-boundary tests verify the
  targeted authority, original OAuth parameters and rejection of unsafe/missing
  final redirects. The user's subsequent run passed targeted sign-in and live
  identity checks after they corrected the supplied customer ID. This fixes a
  reproduced targeting gap, not proof that every reported mismatch has that cause.
- Normal-launcher success is operator-reported on both Windows and Linux. Windows
  customer selection and invitation inspection also have detailed live dry-run
  evidence. Offline contracts cover cancellation, partner-account rejection and
  tenant changes; they do not prove every live browser/MFA or policy variant.
- Test passkeys, PIM, Conditional Access and browser-management policies in
  authorized customer/partner environments.
- Confirm application-menu/Start Menu launch, temporary-profile cleanup on crash,
  multi-user isolation and installer upgrade/removal/rollback on target desktops.
- Sign production MSI releases and publish Debian packages through a scoped signed
  APT repository. Local unsigned packages are development artifacts only.
- Bind future updates to signed immutable assets. Do not silently update.
- Preserve the upstream MIT attribution and source pin.
- The portal adapter uses undocumented Microsoft admin-portal endpoints. Schema
  drift fails closed; no ambiguous approval POST is automatically retried.
- Browser polling limits do not provide hard cancellation of every underlying CDP
  transport call. Process interruption can require manual local-state review.
- The launcher now traces fixed HTTP endpoint labels with 30-second connect/read
  limits and no retries. A loopback stalled-response test verifies the timeout;
  the particular request behind the reported live validation stall is not yet
  identified. The later live run completed validation; this does not establish
  the cause of the earlier stall.

## Launcher handoff checks

Native workflow checks exercise pasted invitations, first-use trust, saved
enrollment, multiple-instance selection, cancellation, child outcomes and durable
interrupted-work protection, including explicit reviewed recovery and refusal of
busy or changed reservations. They use an internal acceptance seam and isolated
temporary state; production does not expose a payload or state override.
Fourteen real PowerShell subprocess checks run the production wrapper against a
stub approval script. They verify customer-discovery/explicit-customer forwarding,
required approval confirmation, diagnostics and rejection of every non-active
result. Raw synthetic exceptions remain suppressed. These checks complement the
50 browser/identity/approval checks; they complement the user's Linux launcher
report and do not replace Windows desktop testing.

## Scope and identity

No CIPP source modification is required. The launcher opens the stock onboarding
page and does not call a custom callback or manually enqueue onboarding. Version
0.3.0 can independently report CIPP's recorded status after API-client configuration;
approval alone is never evidence of CIPP success. CIPP owns onward tasks, delays
and failure logs.

The expected partner is enrolled independently of the invitation. The launcher
selects the customer from a freshly validated live portal session, explicitly
asks the operator to confirm it before opening the invitation, then pins that
customer for subsequent identity checks. This is operator-confirmed selection,
not an independent assertion that a generic invitation was originally intended
for that customer. The original script's explicit -ExpectedTenantId mode remains
available for workflows requiring an independently supplied customer ID.
