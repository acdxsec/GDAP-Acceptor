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
  process's result as its own. Each execution still requires fresh authentication
  and explicit confirmation of the live customer identity; queued requests are not approvals.
- Queued work captures the enrolled origin/partner. Removing and re-enrolling an
  instance cannot silently retarget work that is already queued.
- Active acceptance has a durable reservation and an OS execution lock. Active
  work never expires with pending work. Disposing a handle or killing the launcher
  does not clear the reservation: a child PowerShell/browser may still be running.
- Normal completion (preflight stopped or child exited) clears the exact matching
  reservation. Exceptions leave it for review. Explicit `queue resolve` archives
  reviewed interrupted work; it never authenticates or replays the invitation.
  There is no automatic expiry of active work or automatic approval retry.

## Known handled-error limitation (0.1.5)

A PowerShell child that catches an approval error and exits normally with a
nonzero exit code currently clears its reservation, including when a submitted
approval has an unknown outcome. Its generic error output also loses the specific
unknown-outcome classification. Consequently an empty queue is not proof that no
approval was submitted, and another explicit launch is not forced through
`queue resolve`. There is still no automatic retry, and a new launch still checks
customer/partner/access/state and requires applicable confirmation.

After **any approval error**, inspect the Microsoft relationship outcome before
launching the invitation again, even if the queue is empty. Preserving a distinct
uncertain outcome across the wrapper/native boundary is tracked in
[issue #2](https://github.com/acdxsec/GDAP-Acceptor/issues/2). Process-death and
unhandled native-exception reservations remain protected as described above.

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

Then run `gdap-acceptor queue resolve`. It displays the reserved relationship,
origin and partner and requires literal `RESOLVED` to attest that the prior
processes are stopped and the outcome has been reviewed. It refuses recovery if
the execution lock is held or the reservation changed while the operator read
the prompt. A free lock alone does not prove an orphaned child stopped; the
operator must establish that before confirming.

The command archives the reservation to `reviewed-<token>.json` before removing
active state and any duplicate pending entry for that invitation. It preserves
enrollment, other pending work and diagnostics. The archive records local review,
not a claim of Microsoft acceptance or CIPP success. It never automatically
starts another invitation. Cancellation leaves the reservation untouched. Never
remove the entire application-data directory or enrollment to clear one request.

## Verification limits

Run `dotnet run --project tests/StateContracts` on Windows and Linux. The contracts
cover concurrent enrollment, bounded concurrent admission, pending expiration,
active serialization, process death/restart, enrollment rebinding and legacy input.
The tests create isolated temporary directories and report their location.
`tests/LauncherContracts` also covers recovery cancellation, archival without
replay, refusal while the execution lock is held, and a reservation changing
after it was selected for review.
They do not prove real multi-user desktop isolation, browser-process cleanup,
filesystem power-loss durability, or live approval behavior; those remain gates.
