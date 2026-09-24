# GDAP Acceptor

Standalone Windows/Linux launcher for an MSP operator accepting a Microsoft GDAP
invitation created by **unmodified CIPP**. No CIPP custom page, custom image,
custom API, or locally installed M365Internals checkout is needed.

Download the [unsigned 0.1.6 prerelease](https://github.com/acdxsec/GDAP-Acceptor/releases/tag/v0.1.6)
for Windows/Linux installers, portable packages, checksums and installation instructions.

Development version **0.4.0** moves optional status reporting to a separate
authenticated Azure Container App in CIPP's existing subscription/resource group. CIPP API
credentials stay on the server; workstations use staff sign-in. This replaces
the 0.3.0 per-workstation API credential model. It is not in the published 0.1.6
download and is not deployed. See [Azure deployment](docs/AZURE-DEPLOYMENT.md)
and [staff/CIPP authentication setup](docs/CIPP-STATUS.md). Azure's generated
HTTPS address is sufficient initially; the custom hostname is optional.
The revised Azure deployment scales to zero when idle and pulls an approved
GHCR image; it creates no paid Azure registry or registry-pull identity. Retained
companion logs are an explicit choice. The earlier five-resource foundation
preview is superseded; do not deploy its old downloaded template.

Version **0.1.6** preserves the local reservation after uncertain
approval outcomes, failed readback and abnormal child exits. Before retrying,
inspect the relationship in Microsoft/CIPP and run `gdap-acceptor queue resolve`.
Known preflight cancellation and verified active results still clear the reservation.
The published 0.1.5 files remain unchanged; they do not contain this fix.

Development package **0.1.5** includes the Windows browser process-handoff fix:
an exited launch process no longer stops sign-in while its private browser session
is still responding. The user completed a Windows interactive dry run with this
fix, including customer selection and invitation validation. No approval was
submitted by that test.
After receiving the normal 0.1.5 Windows launcher/installer, the user also reported
that it worked. This is operator-reported Windows launcher evidence, not a separate
confirmation of final CIPP onboarding completion or every installer launch path.

## Run

Install PowerShell 7.6 or newer and Microsoft Edge, Chrome or Chromium first.
The package bundles the .NET runtime and the pinned M365Internals source payload.
Use a graphical desktop; a terminal-only SSH session cannot perform browser MFA.

Launch **GDAP Acceptor** from the application menu, double-click the Windows
executable, or run the executable from its extracted package:

```text
./gdap-acceptor
```

1. Choose **1. Accept invitation**, then paste the full Microsoft invitation URL
   copied from CIPP. Direct pasting at the home prompt is also supported.
2. On first use, enter your CIPP HTTPS origin and **partner** tenant ID and type
   TRUST to save them locally. This is not a CIPP instance ID or API credential.
   Use `gdap-acceptor configure` to enroll another origin/partner later.
3. Sign in as the **customer administrator** in the fresh private browser.
   Complete MFA, inspect the signed-in customer account in the browser, and type
   CUSTOMER in the terminal to confirm the live tenant. No customer GUID input
   is required. Selecting the partner tenant is rejected.
4. The same browser opens the invitation. Review the tenant, partner and requested
   access in the terminal and confirm approval. The script rechecks identity and
   terms before submitting and waits for the relationship to become active.
5. The launcher opens CIPP's normal GDAP Onboarding page in your default browser.
   Existing CIPP Automated Onboarding handles the Microsoft approval event.
   **Active GDAP is not a claim that CIPP onboarding started or completed.**
6. If central status access is configured, the companion watches the exact relationship's
   CIPP record for up to 20 minutes. Queued is reported separately from running.
   Ctrl+C stops only the status watch; CIPP continues independently. A failed
   status check never changes a successful GDAP approval or submits another job.

After the action, the workspace returns to its home menu. Choose **2** to inspect
the local queue and explicitly review an interrupted reservation, **3** to view
or add trusted CIPP connections, **4** to export local diagnostics to a new file,
**5** to check/watch CIPP onboarding without repeating acceptance, or **0** to
exit. In settings, **C** connects central status, **D** removes local connection
settings, and **L** removes an old 0.3.0 local API credential with explicit consent.
These actions do not authenticate to a customer or
start CIPP onboarding. Approval and CUSTOMER confirmation remain in this terminal.

Staff sign-in uses the default browser and a public desktop Entra application;
only non-secret connection identifiers are stored locally. Staff tokens stay in
memory and are separate from customer approval cookies. The central host holds
one read-only CIPP client credential in a mounted secret file, never in the image
or workstation package. Server deployment and Entra setup must be completed
first; see [central setup and limitations](docs/CIPP-STATUS.md).

You can also pass the full URL as a single quoted command-line argument.
Cancellation or a tenant change stops approval. Authentication uses an isolated
temporary profile, not a previous customer session.

When `-ExpectedTenantId` is supplied to the bundled script, the browser start
URL is explicitly tenant-targeted. Portal `/login` redirects are resolved to a
Microsoft Entra authorization URL before targeting its tenant path. The portal's
OAuth query parameters are retained; unexpected redirect targets stop before
browser launch. The live tenant check still runs after sign-in and must match.
Discovery mode (`-ConfirmAuthenticatedTenant`) remains an operator-confirmed
customer choice; it cannot target a tenant before that tenant has been selected.

Invitation navigation stays on the validated tab rather than selecting another
admin-centre tab while polling. `[GDAP navigation]` output reports only a route
category and document readiness, not raw page URLs or page contents. A timeout
includes the last safe observation. Missing or unexpected page state still stops
before invitation inspection or approval.

CIPP Automated Onboarding must already be enabled. This tool neither changes its
configuration nor starts a manual onboarding job. CIPP's webhook schedule and
Microsoft propagation may delay the visible onboarding record and later steps.

## State and diagnostics

The launcher prints `[GDAP stage]` and `[GDAP HTTP #N]` progress while validating
the portal and inspecting the invitation. HTTP requests have 30-second connection
and response-read timeouts and no transport retries. A timed-out request blocks
further HTTP calls in that run. These are per-request transport limits, not an
overall deadline for browser CDP or the whole onboarding workflow. Output uses
fixed endpoint labels and elapsed times; it does not print request URLs, headers,
cookies, tokens, response bodies or raw HTTP errors. A write timeout is reported
as an unknown outcome and must not be followed by an automatic approval retry.

For an approval-free diagnostic pass, stop the previous process, then run the
bundled `scripts/Approve-GdapRelationship.ps1` with the invitation,
`-ConfirmAuthenticatedTenant`, `-ExpectedPartnerTenantId`,
`-PortalRequestDiagnostics` and `-WhatIf`. This does not clear queue reservations
or submit GDAP approval. HTTP progress is terminal-only, not a persisted log.

Origin and partner enrollment is an explicit local trust decision. Neither the
pasted URL nor a custom protocol invocation can replace an enrolled partner.
Multiple enrolled instances require an explicit selection. The existing
`gdap-acceptor://v1/accept/<instance-id>/<relationship-id>` handler remains for
compatibility but is not required for the paste-in workflow.

```text
gdap-acceptor queue status
gdap-acceptor queue resolve
gdap-acceptor diagnostics export <new-output-file>
```

A durable per-user queue prevents overlapping approvals and ambiguous retries.
Pending entries expire after ten minutes; interrupted active reservations require
review and are never automatically replayed. After stopping the prior processes
and verifying the outcome, `queue resolve` asks for `RESOLVED` and archives that
specific reservation without retrying it. Held locks and changed reservations
prevent recovery. Diagnostics contain identifiers and
state transitions, not tokens or cookies. See [local state](docs/LOCAL-STATE.md).

## Build and checks

```text
dotnet run --project src -- self-test
dotnet run --project tests/StateContracts
dotnet run --project tests/LauncherContracts
pwsh -File tools/Build-Package.ps1 -Runtime linux-x64 -UpstreamSource <pinned-checkout>
pwsh -File tools/Test-Acceptance.ps1 -ModulePath ./dist/linux-x64/M365Internals/M365Internals.psd1 -ApprovalScript ./dist/linux-x64/scripts/Approve-GdapRelationship.ps1
pwsh -File tools/Test-Wrapper.ps1 -Wrapper ./dist/linux-x64/scripts/Invoke-Acceptance.ps1
```

Repeat the build with `-Runtime win-x64` for Windows. Use a fresh
`-OutputDirectory` when rebuilding; existing payloads are not overwritten.
Upstream source must be clean and pinned to
`21e8728b9491eda1c13e1e05ce03678ca75d64cc`. Attribution is included in packages.

The PowerShell contracts mock browser/process/Microsoft boundaries; an additional
loopback HTTP peer tests real transport timeouts. They do not authenticate to a
real tenant. Launcher checks use temporary state and an acceptance adapter;
wrapper subprocess checks replace only the approval script with a fixture, and
verify that confirmation remains required and only active GDAP is successful.
CI runs native and packaged acceptance checks on
Windows and Linux. Installer metadata/lifecycle checks are also included in CI.

`tools/Build-Installers.ps1` builds unsigned development MSI/Debian packages on
Linux with wixl, msitools and dpkg-deb. Windows installation is per-user and adds a
Start Menu shortcut; Linux exposes an application-menu entry. No installer changes
CIPP or enrolls customer credentials. Runtime/browser prerequisites are not
silently installed.

**These are development builds, not signed production releases.** Windows and
Linux now have operator-reported normal-launcher success. Windows also has detailed
interactive dry-run evidence; Linux has acceptance and CIPP handoff evidence.
The unsigned prerelease is published. Production signing and the remaining
desktop-policy checks are not complete.
Paid signing was declined; continue with the unsigned builds. See
[unsigned distribution](docs/UNSIGNED-DISTRIBUTION.md),
[optional future signing](docs/SIGNING-READINESS.md) and
[release gates](docs/RELEASE-GATES.md).
