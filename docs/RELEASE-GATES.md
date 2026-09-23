# Release gates and known limitations

## Current distribution decision

Continue with unsigned development builds; the user declined paid signing.
The user separately approved the public
[0.1.5 prerelease](https://github.com/acdxsec/GDAP-Acceptor/releases/tag/v0.1.5),
published from tested source `1412f99c29ee640e0d1e84d7172f923f5a516125`.
No signing subscription, automatic updater or APT repository was created.
Existing desktop policies still apply.
Windows and Linux 0.1.5 artifacts come from the same tested source tree. Signing
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
Uncertain/post-submission outcomes and abnormal child exits retain the local
reservation until explicit review. The wrapper distinguishes these from known
preflight cancellation and verified active success. Tests use real approval and
wrapper code with synthetic portal boundaries, including a second-launch refusal
before authentication. No live approval is required for these regression checks.
The 0.1.5 release remains immutable and does not contain this correction.
The 0.1.7 package produced by the current workflow is an upgrade-test fixture only.

## Remaining gates

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

No CIPP modification is required. The launcher opens the stock onboarding page,
does not call a custom callback or manually enqueue onboarding, and does not claim
to verify CIPP success. CIPP owns onward tasks, delays and failure logs.

The expected partner is enrolled independently of the invitation. The launcher
selects the customer from a freshly validated live portal session, explicitly
asks the operator to confirm it before opening the invitation, then pins that
customer for subsequent identity checks. This is operator-confirmed selection,
not an independent assertion that a generic invitation was originally intended
for that customer. The original script's explicit -ExpectedTenantId mode remains
available for workflows requiring an independently supplied customer ID.
