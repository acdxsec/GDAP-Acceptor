# GDAP Acceptor

Independent desktop companion for an internal MSP operator using custom CIPP.
The native launcher validates the versioned URI, queues requests per OS user,
requires the expected customer tenant before authentication, and invokes a
bundled PowerShell payload with a fresh browser profile. The partner identity is
read from explicitly enrolled local configuration, never trusted from the URI.

This is a development implementation. Production promotion is blocked on live
portal-schema verification, Windows/Kubuntu desktop testing, and signed packages.
No production release or unattended installation is available yet.
CI builds unsigned development MSI/Debian artifacts and exercises the Windows
installer on disposable runners. These are not approved workstation installers.

## Enrollment

After installing a built package, obtain the instance ID and partner tenant ID
from your authenticated CIPP administrator. Enroll each trusted HTTPS origin:

```text
gdap-acceptor instance add <instance-id> https://cipp.example <partner-tenant-id>
gdap-acceptor instance remove <instance-id>
```

The protocol is `gdap-acceptor://v1/accept/<instance-id>/<relationship-id>`.
Identifiers are opaque, bounded path segments and may contain composite GUIDs.
There is no arbitrary callback, command, credential, or script path in the URI.
Enrollment is an explicit local trust decision; the URI does not prove origin.

PowerShell 7.6 LTS and Chrome, Chromium, or Edge are prerequisites. The .NET 10
launcher is published self-contained. Authentication uses isolated Chromium;
the return to CIPP uses the operating system's default browser. Windows uses
per-user installation; Linux installs application files through a Debian package
and registers the handler in each user's desktop session.

Sanitized diagnostics retain seven days of state changes in the user's local
application-data directory. No authentication state is persisted by the launcher.
Export these records with `gdap-acceptor diagnostics export <new-output-file>`.
Queued entries expire after ten minutes and still require fresh authentication
and confirmation. A stopped run must not be treated as an accepted invitation.

## Build and verification

Run `dotnet run --project src -- self-test` and the approval regression checks.
Use `tools/Build-Package.ps1` to assemble a self-contained Windows or Linux build
with the pinned MIT-licensed M365Internals sources. Users do not need a module
installation or repository checkout. Third-party notices must remain in packages.
Signing must happen in a managed release environment; never bypass signature or
APT verification to promote a package. See `docs/RELEASE-GATES.md`.

On Linux, after assembling both runtime payloads, run
`pwsh -File tools/Build-Installers.ps1` with `wixl`, `msiinfo` (GNOME msitools),
and `dpkg-deb` installed. Then run
`pwsh -File tools/Test-Installers.ps1 -Directory dist/installers`.
The builder refuses existing output files; use a fresh output directory or version.
MSI authoring uses HKCU and `%LocalAppData%`; Debian installs under `/opt` and ships
a desktop entry without modifying any user's handler preference. Handler selection
must happen in the intended user's desktop session, never via root `xdg-mime`.

The workflow also builds a 0.1.1 upgrade fixture from the same payload solely for
lifecycle testing. Neither version is a published release. Windows CI checks
handler collision refusal, per-user-only context, installation, upgrade, downgrade
rejection, removal and enrollment retention. Target-desktop tests, failure rollback,
multi-user isolation, signing and a signed APT repository remain release gates.
