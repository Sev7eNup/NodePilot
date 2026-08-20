# ADR 0011 – Database-Availability Breaker

**Status:** Accepted – 2026-08-07  
**Scope:** The application database failing or hanging during operation. The boot path stays
fail-closed.

## Context

Without a shared failure state, HTTP requests and background services each waited out their own
database timeouts. A hanging server could therefore leave the process unusable for minutes while
liveness still reported 200. At the same time, a single slow query must not mark the whole
installation as failed.

## Decision

A process-wide breaker manages four states:

| State | Meaning | API | Readiness |
|---|---|---|---|
| `Booting` | Migration and startup recovery are running | no pipeline yet | – |
| `Available` | The database is confirmed usable | normal | 200 |
| `Armed` | A timeout was observed; the probe decides | normal | 503 |
| `Unavailable` | The outage is confirmed | 503 | 503 |

After boot, only the recovery probe may publish `Available` again. Interceptors may only degrade
the state.

### Detection and recovery

- Classified connection errors open the breaker. Unknown open errors and command timeouts only set `Armed` at first.
- A separate, unpooled connection probes with `SELECT 1`. Open, command and cleanup each carry hard
  time limits.
- Two successful probes close the breaker. The application pool is cleared exactly once per actual
  outage episode, not already at `Armed`.
- `RejectedByServer` is sticky for the episode and marks credential, database or TLS errors. That
  state needs an administrator; changed NodePilot connection details only take effect after a
  restart.

### HTTP, health and UI

- The availability middleware runs before authentication and terminates `/api`, the hub HTTP
  surface, `/signin-oidc` and protected `/metrics` without touching the database.
- The static SPA shell and its assets stay reachable. The UI shows a banner and a status light,
  suppresses toast storms, and reloads data after recovery.
- `/healthz/live` stays 200. `/healthz/ready` is the traffic gate. `/healthz/database` always
  answers 200 with `ok | armed | unavailable` and a coarse cause.
- HTTP errors use the shared body
  `{code, message, retryAfterSeconds, reason, retryable}`.
  `DATABASE_TIMEOUT` remains the answer for a slow but not confirmed-failed database; the error
  shape is defined in [ADR 0007](0007-api-validation-and-error-contract.md).
- Established WebSockets are protected by a hub filter. Generic I/O or timeout errors are only
  translated into database errors given breaker or database-provider evidence.

### Workflows and background services

- Before an activity runs, the `Running` step is durably stored with a stable GUID.
- After the activity, the terminal step write is a barrier: on failure the engine waits, takes a
  fresh DbContext, and only then enters the next edge.
- The terminal workflow CAS waits as well, until recovery or host shutdown. User cancellation does
  not abort the final persistence attempt.
- The dispatcher only re-queues failures that provably happened before the engine started.
- Database-dependent hosted services park on the shared availability signal. Support events are
  dropped with a counter during the outage and summarised once after recovery.
- Trigger fires that active sources observe during the outage are dropped with a counter and never
  replayed. Leadership loss disposes of the sources; persistence and dispatch check the lease epoch.
- Notification delivery stays at-least-once. Webhooks carry `eventKey` in the JSON body and in
  `X-NodePilot-Event-Key` so recipients can deduplicate.

### Configuration and operation

Every availability value is positive, boot-time configuration and requires a restart:

| Setting | Default |
|---|---:|
| `Database:ConnectTimeoutSeconds` | 5 |
| `Database:AuthReadTimeoutSeconds` | 3 |
| `Database:ReadinessProbeTimeoutSeconds` | 5 |
| `Database:Probe:ConnectTimeoutSeconds` / `CommandTimeoutSeconds` / `CleanupTimeoutSeconds` | 2 / 2 / 2 |
| `Database:Probe:IdleIntervalSeconds` / `OutageIntervalSeconds` | 5 / 5 |
| `Database:Probe:SuccessesToRecover` / `FailureThreshold` | 2 / 2 |

`0` is rejected, because some providers read it as an unbounded timeout.

Signals that matter:

- Metrics: `nodepilot.database.requests_rejected`, `nodepilot.database.outages`,
  `nodepilot.database.probe_cleanup_timeouts` and
  `nodepilot.scheduler.triggers.dropped_db_unavailable`.
- Audit: exactly one `DATABASE_RECOVERED` per process-local recovery episode; no trip audit,
  because the database is not reliably writable at the moment the breaker opens.
- Logs: the opening, real reason changes and the closing of an episode — not every probe tick.

## Consequences and limits

- A cold start without a database stays fail-fast; the Service Control Manager handles the restart.
- Losing the process, or an HA failover during an ambiguous activity, still ends in
  `Interrupted/Cancelled` rather than an automatic retry of external side effects.
- Commands already running and blocked inside the provider are not aborted globally when the
  breaker opens later. New access, by contrast, is gated immediately.
- Triggers deliberately have no catch-up; notification recipients must support deduplication.
