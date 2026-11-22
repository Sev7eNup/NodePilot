# Database providers

NodePilot supports PostgreSQL and SQL Server. `Database:Provider` selects the active provider at startup.

| Provider | Value | Connection-string key |
|---|---|---|
| PostgreSQL (default) | `"postgres"` | `ConnectionStrings:Postgres` |
| SQL Server | `"sqlserver"` | `ConnectionStrings:DefaultConnection` |

SQLite is used exclusively as an in-memory backend in tests.

## An outage during operation

If the database fails or hangs after startup, NodePilot stays reachable: database-dependent requests
answer quickly with `503 DATABASE_UNAVAILABLE`, the UI shows the outage, and running workflows wait at
durable step boundaries. A separate probe checks `SELECT 1`; after a successful recovery, requests and
background services continue without a restart.

`RejectedByServer`, by contrast, means incorrect credentials, database selection or TLS configuration.
That state needs an administrator and cannot be resolved by waiting. After a server-side correction the
probe picks it up automatically; changed NodePilot connection details need a restart.
Individual slow queries return `DATABASE_TIMEOUT` and do not immediately open the global outage breaker.

## A busy database

A database that answers but has nothing free right now is not an outage: every connection of the
pool is in use (`Max Pool Size`, 800 as installed), or the server reports that it is out of memory,
locks or log space. NodePilot does not show an outage for this. A running workflow that has to
save the state of a step or of the run waits one second and tries again, until the write gets
through or the service stops. The run continues afterwards; nothing is lost.

The server log has one of these lines for each write that had to wait:

```
Lifecycle write for step <id> found the database busy; retrying until it gets through.
Terminal write for execution <id> found the database busy; retrying
```

They mean that the database could not keep up with the load at that moment. If they appear often,
the database server needs more memory or a machine of its own, or fewer steps should run at once;
see "Raising the limits by hand" under Performance sizing. Up to version 1.5.4 a run ended as
failed when such a write found no free connection.

| Setting | Default | Purpose |
|---|---:|---|
| `Database:ConnectTimeoutSeconds` | `5` | The application's connection budget |
| `Database:AuthReadTimeoutSeconds` / `ReadinessProbeTimeoutSeconds` | `3` / `5` | Short budgets for authentication and readiness |
| `Database:Probe:ConnectTimeoutSeconds` / `CommandTimeoutSeconds` | `2` / `2` | Hard budgets for the recovery probe |
| `Database:Probe:IdleIntervalSeconds` / `OutageIntervalSeconds` | `5` / `5` | Check interval in the normal and the outage state |
| `Database:Probe:SuccessesToRecover` / `FailureThreshold` | `2` / `2` | Confirmations before recovery and before opening the breaker |

All values are positive, restart-required boot configuration. `0` is rejected, because some providers
treat it as an unlimited timeout. Status is available from `/healthz/ready` (the traffic gate) and
`/healthz/database` (always HTTP 200, with the state in the body).

## Database load during operation

Machine selectors in the designer load configuration through `GET /api/machines/options`.
The machine list and detail page share step statistics for ten seconds; concurrent requests
trigger a single refresh. Activity counts are reused for up to ten seconds after a refresh
and are not a second-by-second live display.

Accepted workflow starts wait durably in the dispatch outbox. Only one idle worker per process
checks the queue, waking immediately for new requests and falling back to a one-second interval.
`ExecutionDispatch:WorkerCount` still limits concurrently running workflows. Increasing the
worker count does not increase the query rate of an empty queue. If the response to a reservation
is lost, the request becomes available again when its 60-second lease expires; execution starts
only after a confirmed reservation.

Reservations run one at a time. This reduces database load but can increase wait times when many
starts arrive together; higher database latency increases this effect. Adding workers does not
remove this reservation-rate limit.

The metrics `nodepilot.dispatch.claims` and `nodepilot.dispatch.claim.duration` report reservation
attempt counts and duration, grouped by `success`, `empty` and `error`.

## Migrations

- **One shared migration set**, provider-agnostic (without `type:` strings). Bootstrapped via `db.Database.Migrate()`.
- **Initial baseline:** `20260915180058_InitialBaseline` contains the complete current schema, including the Running-step index. It replaces the development history before the first production deployment. Existing development databases from that older history need a fresh database or an explicit data transfer; this baseline does not upgrade them. Startup never resets them automatically. Later schema changes append migrations to this baseline.
- A new migration:
  ```bash
  dotnet ef migrations add <Name> \
    --project src/NodePilot.Data \
    --startup-project src/NodePilot.Api \
    --context NodePilotDbContext
  ```
- **Mandatory post-processing — two steps:**
  1. In the migration (`<Name>.cs`): remove all `type: "..."` annotations.
  2. In the designer file (`<Name>.Designer.cs`): add `MigrationModelPortability.UseActiveProviderStoreTypes(modelBuilder);` as the last line before `#pragma warning restore 612, 618` in `BuildTargetModel`. The `ModelSnapshot` deliberately does **not** get that call — it is the diff basis, not a migration target model.

  Both steps are covered by `MigrationDriftTests`.
- Schema changes **always** through an EF migration. No DDL hot-patching.

## Credentials

Credentials are encrypted with DPAPI (`Credentials:DpapiScope`). In a cluster, AES-GCM has to be used instead — see [Secret providers](../enterprise/secrets-providers).

## Production

- **SQL Server 2022 CU1 or later** (trusted connection, build ≥ 16.0.4003.1) or **PostgreSQL 16+** (user/password). The production connection uses `Encrypt=Strict` (TDS 8.0) — SQL Server 2019 cannot do that, and 2022 RTM aborts parameterized queries with TDS error 8005 (fixed from CU1).
- The gMSA login / the Postgres role needs DDL permissions (for `Migrate()`).
- The database server needs a TLS certificate that NodePilot can verify: [Database certificates](../deployment/database-tls).
