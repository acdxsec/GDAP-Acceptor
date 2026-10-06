# GDAP Acceptor 0.4.2

## Changes

- Template retrieval, submission and read-only recovery have separate timeouts.
  Time spent reviewing templates or the CREATE prompt does not consume the
  submission deadline. An already-cancelled submission creates no local attempt.
- Queue and recovery now shows saved creation attempts even when the customer
  approval queue is empty. Option **2 → C** allows explicit reviewed archival.
  It sends no request, retains the original record and preserves enrollment.
- Recovery distinguishes a missing server record from a generic server failure.
  Missing evidence never authorizes automatic creation or approval retries.
- Optional private deployment profiles support preconfigured organization builds.
  The public package contains no such profile, credentials or tenant identifiers.
- Errors display a safe stage-specific explanation, not raw responses or secrets.

## Windows installation or portable update

Use `gdap-acceptor-0.4.2-win-x64.zip` from this release. PowerShell 7.6+ and an
installed Edge, Chrome or Chromium browser are prerequisites; .NET and the pinned
M365Internals module are bundled. No separate M365Internals checkout is required.

Close the old launcher and its private browser session. Verify the archive against
`SHA256SUMS`, extract into a fresh folder, and open `gdap-acceptor.exe`. Keep the
entire extracted folder together. Do not overwrite an older runtime in place or
delete user-profile state. Existing trusted connections and attempts are preserved.
Keep the previous portable folder for rollback; there is no automatic updater.

The generic package does not embed a CIPP address, partner/staff tenant or app ID.
Existing saved settings continue working. On a new workstation, use **3. CIPP
connection settings** to configure the administrator-provided trusted connection.
Organization-configured packages are distributed privately, separately from this
public ZIP. Neither type stores a CIPP client secret on the workstation.

## Signature and trust

The launcher EXE and DLL are SHA-256 Authenticode-signed and RFC3161-timestamped
using the project's existing internal certificate. The ZIP itself and checksum
manifest are not Authenticode-signed. Bundled runtime and upstream files retain
their original contents; signing the launcher does not sign every bundled script.

`GDAP-Acceptor-Internal.cer` contains the public certificate only. Its SHA-256
fingerprint is `4B81E68427403471AF859FD30935E8CA84BC8DA7022EBFFB688B107224BA39C9`.
This is **not public CA trust** and does not guarantee Defender/ASR/SmartScreen
acceptance. Administrators must independently approve the certificate and package
under their existing application-control policy. Do not disable protection or
automatically trust a certificate merely because it accompanied a download.
No private key, signing-service purchase or security-policy change is included.

## Normal workflow and recovery

Choose **6** to load templates, sign in as authorized staff, select the requested
access and confirm CREATE. Then sign in separately as the customer administrator,
confirm the tenant and approve the invitation after reviewing the access summary.
After verified acceptance, the companion opens the matching CIPP onboarding record.
CIPP owns automated onboarding, retries and task logs; the companion does not
enqueue another onboarding job or infer completion from opening the page.

If creation was interrupted, **6** only reads the saved operation. Stop previous
sessions, allow in-flight requests to finish and review its operation reference
in CIPP. If an invitation exists, use its existing URL through **1**; do not create
a replacement. Only after reviewing the outcome use **2 → C → RESOLVED** to archive
the local attempt. A timeout or HTTP 404 alone is not proof that a retry is safe.
Interrupted customer approval uses the separate reservation review in option 2.

## Validation boundaries

The operator confirmed creation, acceptance and exact-record CIPP handoff on
Windows with the configured 0.4.2 build. Public generic packaging intentionally
omits that private profile. Automated contracts cover synthetic authentication,
safe failures, timing, recovery, preserved state and packaged PowerShell behavior.
They do not exercise real customer authentication or every Conditional Access/
endpoint policy. Final onboarding completion and current Linux live creation were
not established by that Windows test. Portal endpoints remain undocumented and
schema drift fails closed. Old release assets remain unchanged.
