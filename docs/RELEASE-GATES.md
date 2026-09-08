# Release gates and known limitations

The development implementation must not be represented as a production signed
installer or a Microsoft-supported approval API. The following gates remain:

- Verify sanitized pre-approval portal fixtures for partner tenant ID, customer
  binding (including generic invitations), canonical relationship ID, roles,
  duration, auto-extension, and ETag. The strict adapter currently requires the
  documented-in-source v1 field shape and stops when evidence is missing. No
  alternative field names or identities may be guessed.
- Exercise MFA, passkeys, PIM, Conditional Access, browser-management policies,
  wrong tenant, identity changes during confirmation, and interrupted requests
  in an authorized test partner/customer environment on both target platforms.
- Verify Windows 11 native activation and Kubuntu 26.04 desktop activation,
  temporary-profile cleanup after cancellation/crash, multi-user queue isolation,
  visible terminal lifetime, upgrade, removal, and configuration rollback.
- Produce and sign a per-user MSI with a managed signing identity. The registry
  helper is development-only. Research suggesting a per-machine MSI is not the
  product decision. Never change the installation context during an upgrade.
- Verify package dependencies on a clean Kubuntu 26.04 VM; publish the Debian
  package through a scoped signed APT repository. Do not run xdg-mime as root.
- Bind production updates to signed immutable assets. Release notification is
  still required; no silent update is permitted.
- Preserve upstream MIT attribution in bundled source and packages. The CIPP
  frontend/backend overlay has its own upstream license and release lifecycle.

The launcher requires explicit enrollment of instance ID, HTTPS origin, and
partner tenant ID. Enrollment does not authenticate the invoking website. It
does ensure invocation data cannot choose a different partner or callback URL.
The expected customer tenant is supplied locally before authentication and
validated independently against the returned portal session.

Raw portal response schemas and signing credentials are not present in this
repository. Supplying fabricated fixtures or unsigned production packages would
not satisfy these gates.
