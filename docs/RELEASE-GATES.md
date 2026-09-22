# Release gates and known limitations

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

## Existing Windows CI evidence

[GitHub Actions run 34390399881](https://github.com/acdxsec/GDAP-Acceptor/actions/runs/34390399881)
passed on September 9, 2026, for commit
`24193a5779946a49280003891ff4fd65d533ce33`. It includes Windows native contracts,
payload assembly and per-user MSI install, collision/context guards, upgrade,
downgrade rejection, uninstall and enrollment retention.
That run predates the current uncommitted browser/launcher/recovery changes.
It must not be presented as Windows validation of development build 0.1.4.
The updated source needs a new Windows CI run after publication to its branch;
re-running the old commit would not test these fixes. The September 22 validation
update adds installed CLI/wrapper checks, Start Menu shortcut checks, real MSI
repair and preservation of interrupted reservations, plus isolated Windows Edge
navigation. These additions require a successful run against their own commit;
they are not yet Windows pass evidence merely because the checks exist.

The bundled approval adapter now includes the tested browser-navigation flow,
the observed portal response contract, live tenant validation, User-Agent fixes
and correct WhatIf/confirmation scoping. Numeric duration is displayed as returned,
not used to change the invitation's terms.

## Remaining gates

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
- Exercise the new paste-in launcher and explicit authenticated-customer choice
  on Windows 11. The user reports the normal launcher works on their Linux
  workstation. Offline contracts cover the selection path,
  cancellation, partner-account rejection and tenant changes; they are not
  live browser/MFA tests.
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

Nine native workflow checks exercise pasted invitations, first-use trust, saved
enrollment, multiple-instance selection, cancellation, child outcomes and durable
interrupted-work protection, including explicit reviewed recovery and refusal of
busy or changed reservations. They use an internal acceptance seam and isolated
temporary state; production does not expose a payload or state override.
Fourteen real PowerShell subprocess checks run the production wrapper against a
stub approval script. They verify customer-discovery/explicit-customer forwarding,
required approval confirmation, diagnostics and rejection of every non-active
result. Raw synthetic exceptions remain suppressed. These checks complement the
48 browser/identity/approval checks; they complement the user's Linux launcher
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
