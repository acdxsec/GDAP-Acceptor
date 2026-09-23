# Guided operator workspace — 0.2.0 development

This implements the next interface slice of
[CIPP operator experience issue #6](https://github.com/acdxsec/CIPP/issues/6).
It does not complete that entire issue or the whole onboarding project.

## Interface and acceptance criteria

- Launching with no arguments presents a numbered terminal menu, not a new
  authentication session. Exit and end-of-input terminate without authenticating.
- Accept invitation takes the existing Microsoft URL copied from unmodified CIPP.
  Direct paste at the home prompt and existing command-line invocations still work.
  Text entered at the invitation prompt is never interpreted as a menu or CLI command.
- First-use enrollment and multi-instance selection delegate to the existing
  explicit trust flow. Settings list enrolled origins/partners and allow adding
  another connection. Enrollment does not store customer credentials or configure CIPP.
- The acceptance path identifies sign-in/customer selection, access review and
  CIPP handoff stages. Access review lists the customer, partner, relationship,
  duration, extension, every requested role ID and current Microsoft status.
  PowerShell owns the CUSTOMER and approval prompts in the same terminal.
  Existing HTTP diagnostic progress remains available during acceptance.
- The interface delegates approval to the existing engine: fresh private browser,
  live tenant/partner checks, invitation opening, term recheck, confirmation,
  single approval POST and active readback. It never substitutes menu consent
  for approval confirmation or retries an uncertain write.
- Queue/recovery shows pending work and the active-or-needs-review reservation.
  It is explicitly local state, not CIPP job status or proof a process is running.
  Review requires the existing RESOLVED attestation; busy or changed reservations
  cannot be cleared. Nothing is automatically replayed.
- Diagnostic export writes existing sanitized state events to a new file only.
  It does not collect browser cookies, tokens, page content or CIPP logs. Identifiers
  are still present; review before sharing. Existing output files are not overwritten.
- Actions return to the menu. Cancelled input and invalid options do not start
  authentication. Corrupt state is reported rather than reset.

The UI deliberately uses ordinary line input, not raw keystrokes or an alternate
screen that would interfere with the child PowerShell's confirmation prompts.
Tests exercise the real launcher with isolated state and a non-authenticating
acceptance adapter, plus the real approval/wrapper subprocess contracts.

## Remaining CIPP status integration

Verified active GDAP still opens CIPP's normal onboarding list and displays the
relationship ID. It does **not** prove CIPP received the approval event or started
onboarding. Existing CIPP handles that event, scheduling and failure logs.

The inspected stock frontend onboarding list reads `ListTenantOnboarding`. Its
records can be matched by relationship `RowKey`. This is the candidate for a future
read-only status adapter; it is not called by this version. The separate start
page exposes manual Start/Retry actions using `ExecOnboardTenant`; those actions
must not be used as a status probe.

Source inspected: CIPP frontend
[`b123894`](https://github.com/acdxsec/CIPP/blob/b1238941d1f14a0fc16416e8df5161afa0b17950/src/pages/tenant/gdap-management/onboarding/index.js)
and previously pinned API
[`df3738d`](https://github.com/KelvinTegelaar/CIPP-API/blob/df3738d6e60d417f912c1842d9c64f985ae348f8/Modules/CIPPHTTP/Public/Entrypoints/HTTP%20Functions/Tenant/Administration/Tenant/Invoke-ListTenantOnboarding.ps1).
These are source evidence, not verification of the deployed CIPP version.

Next implementation dependency: establish an authorized CIPP authentication
method and verify the deployed read-only response contract. The saved origin and
partner ID are trust configuration, not CIPP credentials. Customer Microsoft
session material must never be reused as CIPP authentication. Until this exists,
the workspace must say CIPP onboarding is unverified rather than infer success
from Microsoft approval, elapsed time or opening a browser page.
