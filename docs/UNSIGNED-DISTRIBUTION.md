# Unsigned development distribution

The user chose not to purchase a signing service. The current working baseline
is 0.1.5, with matching Windows and Linux source. These are unsigned development
packages, not a claim of production signing or certification. CIPP remains
unmodified; the companion does not manage CIPP's queue.

## Choose one package for your desktop

| Desktop | Installer | Portable alternative |
| --- | --- | --- |
| Windows x64 | `gdap-acceptor-0.1.5-x64.msi` | `gdap-acceptor-0.1.5-win-x64.zip` |
| Linux x64, Debian/Ubuntu family | `gdap-acceptor_0.1.5_amd64.deb` | `gdap-acceptor-0.1.5-linux-x64.tar.gz` |

PowerShell 7.6+ and an installed Edge, Chrome or Chromium browser are prerequisites.
A graphical desktop is required. The .NET runtime and reviewed M365Internals module
are bundled; users do not need a source checkout or developer SDK.

On Windows, install the MSI and open GDAP Acceptor from Start, or extract the ZIP
into a fresh folder and open `gdap-acceptor.exe`.

On Linux, from the folder containing the Debian package:

```bash
sudo apt install ./gdap-acceptor_0.1.5_amd64.deb
```

Then open GDAP Acceptor from the application menu. Run the launcher as your normal
desktop user, never with sudo. Alternatively, extract the portable tar archive
into a fresh directory and run `./gdap-acceptor` from its `gdap-acceptor` folder.
Keep the whole payload together. These instructions do not bypass desktop,
execution-policy or application-control restrictions on unsigned software.

## Normal workflow

Paste a CIPP-generated invitation URL. On first use, independently enter and
trust your CIPP origin and partner tenant. Complete fresh customer-admin sign-in,
confirm the live customer tenant, and review/confirm approval in the terminal.
On active GDAP, the companion opens CIPP's normal onboarding page. Existing CIPP
automation owns the subsequent work; opening that page does not verify completion.
This is the normal approval-capable workflow, not a dry-run helper.

## Manual updates and recovery

Stop the existing launcher and its private browser session before changing its
files. Install a higher-version package or extract a new portable version into a
separate directory; do not mix old and new runtime or script files. Do not install
the CI-only 0.1.6 upgrade fixture. Keep the prior working portable folder until
the new one has been checked. No silent updater or dependency download is enabled.

Enrollment and queue state live outside the program folder under your user
profile. Updates must preserve them. If an earlier approval was interrupted,
inspect its outcome before using `gdap-acceptor queue resolve`; do not clear state
or replay uncertain approvals to test an update. A completed invitation does not
need another approval.

## Verification and provenance

The 0.1.5 source head is `1412f99c29ee640e0d1e84d7172f923f5a516125`.
[CI run 35758177011](https://github.com/acdxsec/GDAP-Acceptor/actions/runs/35758177011)
used merge commit `ba23c02b89dc58e7658369b158868aa8e5ed1eb3` with the same
source tree and passed Windows/Linux verification, packaging and Windows installer
lifecycle checks. Later documentation edits do not alter those binaries.

The handoff directory supplies `SHA256SUMS`. Compare the exact file against its
recorded hash before installation. A checksum detects corruption or a mismatch;
an unsigned checksum file is not publisher authentication. The user subsequently
approved the public [v0.1.5 prerelease](https://github.com/acdxsec/GDAP-Acceptor/releases/tag/v0.1.5),
which contains these exact packages and a release-specific checksum manifest.
No APT repository or paid signing service was created.
