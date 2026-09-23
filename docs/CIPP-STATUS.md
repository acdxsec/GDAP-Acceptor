# Read-only CIPP onboarding connector — 0.3.0 development

The companion already approves GDAP through Microsoft; Microsoft's approval event
triggers existing CIPP Automated Onboarding. This connector only observes CIPP's
record. It never calls `ExecOnboardTenant`, starts a job, retries a job, edits a
webhook or modifies CIPP source. Copy/pasting the invitation remains unchanged.

## One-time setup

An authorized administrator must configure a dedicated API client in the existing
CIPP **Integrations > CIPP-API** page. Create/import the client, enable it, assign
the least-privileged custom read role, set appropriate allowed IPs, and use Save
to Azure as required by your deployment. This changes API-client configuration,
not onboarding behavior. The companion does not perform those administrative
actions. No live API client was created or verified during implementation.

The endpoint requires `Tenant.Administration.Read`. That category can authorize
other reads: use CIPP's endpoint restrictions for unnecessary endpoints and
verify the effective role. Do not grant write categories or SuperAdmin for this
connector. The endpoint returns the entire onboarding table; client-side matching
is **not** server-enforced customer isolation.

In the companion choose **3. CIPP connection settings**, then **C. Configure
read-only API access**. Select the enrolled CIPP instance if there is more than one.
Enter the API URL, authentication tenant ID, application/client ID and API scope
from CIPP. The API URL may differ from the portal's browser URL; use the displayed
API origin, optionally ending in `/api`. Other paths, queries and credentials in
URLs are rejected. This implementation targets public-cloud Entra authentication
at `login.microsoftonline.com`; sovereign clouds require a separate reviewed flow.

Review the destinations and type `CONNECT`, then enter the API secret at the
hidden terminal prompt. The secret cannot be supplied in a CLI argument, an
environment variable or redirected input. Escape cancels secret entry. The
companion obtains an OAuth client-credentials token and tests only
`GET /api/ListTenantOnboarding`. It saves the connection only after successful
authentication and a JSON-array response. Failed verification leaves the existing
saved connection intact. A successful read does not prove the credential lacks
write permissions; least privilege must be configured in CIPP.

The normal settings file still contains no API secret. The entire connection
(including its origin/partner binding and secret) lives in the current user's
credential vault: Windows Credential Manager or the desktop Secret Service via
`/usr/bin/secret-tool`. The latter requires `secret-tool` and a working, unlocked
Secret Service backend. There is no plaintext-file fallback. Other software
running as the same user may access that user's vault; this is not protection
against a compromised workstation. Tokens are held in memory only.

Settings **D** removes only the selected local API credential, after typing
`DISCONNECT`. It does not disable or revoke the API client in CIPP. Reconfigure
after rotating its secret. Removing a portal enrollment does not itself delete
its vault entry; disconnect it first. Uninstalling does not intentionally erase
vault entries or local enrollment.

## Operator behavior

- After verified active GDAP, the acceptance reservation is completed and its
  lock released **before** status observation starts. CIPP errors, a timeout or
  cancellation cannot change the successful approval result or retain its lock.
- If configured, observe every 30 seconds for at most 40 reads / 20 minutes.
  Ctrl+C stops this watch only. Individual HTTP requests are limited to 30 seconds.
  OAuth tokens are refreshed in memory when needed; no status HTTP errors are
  automatically retried. Redirects are disabled for both token and CIPP requests.
- Match the exact relationship `RowKey`; reject duplicate matches, conflicting
  relationship identities/partner evidence, unexpected statuses and response shapes.
  No customer identity is inferred from a CIPP row. Customer validation remains
  the independent Microsoft approval engine's responsibility.
- No matching record means **waiting/unconfirmed**. `queued` and `pending` are
  explicitly not running. `running` confirms CIPP reports onboarding in progress;
  `succeeded`, `failed` and `cancelled` are reported as CIPP states. Observation
  ends at a terminal result or at running; CIPP retains ongoing progress/error handling.
- Limit expiry means running was not confirmed, not that GDAP failed. Never
  repeat approval to refresh status. Use **5. Check CIPP onboarding** to check once
  or watch the same invitation later, without customer sign-in or approval.
- Errors distinguish authentication, authorization/IP restrictions, rate limiting
  and refused redirects. Rate limiting stops the watch and displays a wait period.
  Response bodies, bearer tokens, client secrets and CIPP log contents are not printed
  or included in diagnostic exports. The response is bounded to 4 MiB; larger tables
  fail safely and may require a future supported paginated endpoint.

CLI equivalents: `cipp configure`, `cipp disconnect`, `cipp status <invitation-url>`,
and `cipp watch <invitation-url>`. Status/watch return zero only for running or
succeeded. Successful approval still returns zero even if subsequent CIPP status
cannot be confirmed; its separate console result is authoritative for that check.

## Verification and limitations

Launcher contracts exercise configuration, matching, status separation, bounded
watching, cancellation, errors, destination validation, enrollment binding and
credential-vault failure with synthetic HTTP and credential-store adapters. On
Windows the same launcher tests also round-trip an isolated synthetic credential
through the real native vault, with cleanup. No customer or CIPP credentials are
used. Linux's actual desktop Secret Service integration still requires validation
on a configured desktop; the normal approval workflow does not depend on it.

Live verification of the deployed CIPP client/role and response remains required.
Source/API evidence and the separately documented, unimplemented MCP/public-PKCE
alternative are in [authentication research](research/CIPP-STATUS-AUTH.md).
The connector does not promise that a previously authenticated CIPP browser session
can be reused. It uses its own explicitly configured API credential.

Sources: [CIPP setup/authentication](https://docs.cipp.app/api-documentation/setup-and-authentication),
[API client management](https://docs.cipp.app/user-documentation/cipp/integrations/cipp-api),
[onboarding read endpoint](https://github.com/KelvinTegelaar/CIPP-API/blob/c04bde0f4b53280c1ed21d838ba2c4bbcfc8a600/Modules/CIPPHTTP/Public/Entrypoints/HTTP%20Functions/Tenant/Administration/Tenant/Invoke-ListTenantOnboarding.ps1),
[Windows credential API](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credwritew),
[Secret Service command](https://manpages.debian.org/testing/libsecret-tools/secret-tool.1.en.html).
