# Deployment-specific desktop packages

Build-Package.ps1 accepts an optional `-DeploymentProfile` pointing to a reviewed,
non-secret JSON file kept outside the public repository. The public release is a
generic build with no organization-specific profile. The profile is embedded in the launcher assembly,
not loaded from an adjacent editable configuration file. Sign the final EXE and
launcher DLL after building; profile changes require a new build and signature.

On a new workstation, **6. Create/resume** offers the packaged CIPP connection for
explicit TRUST confirmation, then shows the packaged connector settings for CONNECT
confirmation and staff sign-in. **3 → C** also uses these settings automatically.
No URL, tenant ID or app ID needs to be typed for this deployment. Existing saved
connections take priority and are never silently replaced by a newer package.
A profile is used only for its exact CIPP-origin/partner pair. Generic builds and
other enrolled CIPP instances retain manual setup. **3 → A** remains manual so an
operator can deliberately add a different trusted CIPP instance.

The profile schema permits only version, CIPP origin, partner tenant ID, connector
origin, staff tenant ID and the one companion registration's client/API IDs.
No customer tenant, password, client secret or token belongs in a profile. Staff
sign-in and the authenticated connector response must verify the CIPP/partner
binding before settings are saved. Customer sign-in and approval remain separate.

Invitation failures show the stage, HTTP status and an allowlisted explanation.
No arbitrary HTTP body or raw exception text is displayed. A failure before
submission is distinguished from a durably saved attempt whose outcome may be
uncertain. Saved attempts are never deleted or resubmitted automatically.

`persistent_journal_required` (HTTP 503) means creation is disabled or the journal
is unavailable; it is not proof of a temporary cold start. An administrator must
check the deployment setting and persistent mount before enabling creation.
Desktop setup does not change Azure or CIPP settings.
