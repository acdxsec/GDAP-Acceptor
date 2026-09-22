# Signing readiness

Research checked 2026-09-22. This is a readiness assessment and proposed workflow, not an implemented signing pipeline.

**Decision: paid signing declined.** The user chose to continue with unsigned
development builds. Do not provision this service or treat signing as the next
required action for the current workflow. The research below is retained only as
a future option if the user reopens that decision.

## Current boundary

The user reports the normal Windows launcher works; Linux was also previously reported working. The available installers remain unsigned development artifacts. The user has said no signing certificate/service is available. No paid resources, identity submission, signing credentials or signing operation have been authorized or created. The user separately approved publication of the unsigned [0.1.5 prerelease](https://github.com/acdxsec/GDAP-Acceptor/releases/tag/v0.1.5); this does not authorize signing services. Signing must not modify CIPP or request customer-tenant credentials.

## Verified service facts

- **Candidate:** Microsoft Azure Artifact Signing, formerly Trusted Signing. Use a **Public Trust** profile for publicly distributed Windows software. Public Trust Test and Private Trust are not substitutes for default public trust. [Microsoft trust models](https://learn.microsoft.com/en-us/azure/artifact-signing/concept-trust-models).
- **Basic cost:** US$9.99 per account/month includes 5,000 signatures; additional signatures are $0.005 each. Confirm the actual Azure billing offer before provisioning. [Microsoft pricing tiers](https://learn.microsoft.com/en-us/azure/artifact-signing/how-to-change-sku).
- **Subscription:** Microsoft excludes free, trial, and sponsored Azure subscriptions; a paid subscription is required. Billing is not prorated according to the FAQ. Do not assume Visual Studio or Partner Launch credits make this service eligible or free. [Microsoft FAQ](https://learn.microsoft.com/en-us/azure/artifact-signing/faq).
- **Eligibility:** The current service quickstart lists organizations in the US, Canada, EU, UK, Australia, New Zealand, Japan, South Korea, Singapore, Switzerland, Norway, and Israel. Individuals must be in the US or Canada. Individual validation also requires an Individual-type Azure billing account whose legal name/address matches the identity evidence. Organization identity validation requires business information; validation is completed through Azure Portal and may take 1–20 business days or longer. [Microsoft setup requirements](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart).
- **Documentation discrepancy:** The Windows app signing overview still lists a narrower organizational geography. Prefer the service-specific quickstart for this assessment, and confirm eligibility in the actual service before spending. [Windows signing overview](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options), [service quickstart](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart).
- **No warning-free promise:** A valid signature establishes publisher identity and integrity, but new files can still trigger SmartScreen reputation warnings. [Microsoft SmartScreen guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation).

## Coverage and automation

Artifact Signing supports SignTool-compatible formats and explicitly offers PowerShell Authenticode integration. The official GitHub action demonstrates EXE/DLL signing; MSI packages support Windows digital signatures. PowerShell checks signatures on `.ps1`, `.psm1`, `.psd1`, and related script formats. Signing only the MSI would not sign the scripts or executables inside it. [Signing integrations](https://learn.microsoft.com/en-us/azure/artifact-signing/how-to-signing-integrations), [official action](https://github.com/Azure/artifact-signing-action), [Windows Installer signatures](https://learn.microsoft.com/en-us/windows/win32/msi/digital-signatures-and-windows-installer), [PowerShell signing](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_signing?view=powershell-7.6).

The official action runs on Windows and recommends GitHub OIDC: an Entra application/service principal, federated credentials, `id-token: write`, and the Artifact Signing Certificate Profile Signer role. This avoids a long-lived client-secret login. Its OIDC guide and README currently show different Azure Login action versions; implementation must review and pin supported action commits rather than copy examples blindly. [Action requirements](https://github.com/Azure/artifact-signing-action), [official OIDC guide](https://github.com/Azure/artifact-signing-action/blob/main/docs/OIDC.md).

## Proposed project workflow — not implemented

1. Obtain explicit approval for the paid service, legal publisher identity/country, and target Azure subscription/region. Complete identity verification without putting identity documents in this repository or chat.
2. After approval, create the account/Public Trust profile and narrowly scoped signing identity. Restrict federation to the approved repository/release environment; keep pull-request jobs unable to sign.
3. Build the reviewed commit and run existing tests. Inventory project-owned outputs separately from bundled upstream/runtime files. Sign staged distribution copies only: preserve the pinned upstream source, MIT license, provenance, and existing valid vendor signatures. Review unsigned bundled scripts before choosing whether to sign distribution copies for the intended execution policy.
4. Sign and timestamp the intended Windows payload files first, verify their signatures, then build the MSI from that payload and sign/timestamp the final MSI. Generate the portable ZIP from the same signed payload. Do not rewrite signed files afterward. Microsoft provides the SHA256/RFC3161 signing integration. [Signing setup](https://learn.microsoft.com/en-us/azure/artifact-signing/how-to-signing-integrations).
5. Test final signed artifacts, including installer lifecycle and script-policy behavior. Record hashes only after signing/packaging. Signing does not override organizational execution policy or remove all publisher prompts. [PowerShell policy/signing behavior](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_signing?view=powershell-7.6).
6. Publish only after separate authorization specifying audience, destination, and release version. Linux repository/package signing is a separate distribution decision, not supplied by Windows Authenticode.

## Decisions only if signing is reopened

Paid signing has been declined. If the user later approves it, identify the legal organization (or individual) and country plus the paid Azure subscription to use. The current unsigned prerelease is published on GitHub; materially different publication destinations or signing expenditure still require approval. Do not provision merely to discover eligibility.
