# Local enrollment and queue safety

The launcher and contract tests use the same `LocalState` interface. Tests inject
only the state directory and clock; separate processes use real files and locks.
No test invokes authentication or supplies a production-state override to the CLI.

## Guarantees

- Enrollment read/modify/write operations share a short cross-process lock.
  Writes use a flushed temporary file and same-directory atomic replacement.
  A malformed existing file stops processing; it is not replaced with defaults.
- At most 20 pending requests are admitted, including concurrent producers.
  Duplicate detection happens before the capacity check. Pending entries expire
  after ten minutes; expiration is not a successful acceptance result.
- Each waiting launcher executes only its own request. It cannot report another
  process's result as its own. Each execution still asks for customer identity and
  fresh authentication; queued requests are not approvals.
- Queued work captures the enrolled origin/partner. Removing and re-enrolling an
  instance cannot silently retarget work that is already queued.
- Active acceptance has a durable reservation and an OS execution lock. Active
  work never expires with pending work. Disposing a handle or killing the launcher
  does not clear the reservation: a child PowerShell/browser may still be running.
- Only normal completion (preflight stopped or child exited) clears the exact
  matching reservation. Exceptions leave it for review. No reset/replay command,
  automatic expiry of active work, or automatic approval retry is provided.

Run `gdap-acceptor queue status` for pending and active-or-needs-review identities.
This observation cannot establish whether an approval succeeded, whether a child
process still runs, or whether CIPP started onboarding. Inspect Microsoft and CIPP
separately for those facts. Local queue status does not start work.

## Interrupted work and upgrades

Existing `instances.json` enrollment is preserved. Old development `queue/*.json`
files cannot distinguish waiting requests from possibly active acceptance, so new
queue operations refuse to bypass them. Stop previous launcher versions before
upgrading; mixed-version writers are not a supported operating mode.

If active state remains after a crash, do not repeatedly launch invitations or
delete the reservation. Identify the relationship with queue status, inspect the
Microsoft invitation and CIPP status, and establish that **all** related launcher,
PowerShell and browser processes have stopped. A parent process exiting is not
sufficient evidence. Do not repeat an ambiguous approval POST as a recovery step.

Any local-state repair is a separate operator maintenance decision. Preserve the
state and diagnostics first; never remove the entire application-data directory
or enrollment to clear one request. This development implementation deliberately
does not automate that decision. Lab-test the process before production use.

## Verification limits

Run `dotnet run --project tests/StateContracts` on Windows and Linux. The contracts
cover concurrent enrollment, bounded concurrent admission, pending expiration,
active serialization, process death/restart, enrollment rebinding and legacy input.
The tests create isolated temporary directories and report their location.
They do not prove real multi-user desktop isolation, browser-process cleanup,
filesystem power-loss durability, or live approval behavior; those remain gates.
