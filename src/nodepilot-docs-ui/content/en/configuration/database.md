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

## Migrations

- **One shared migration set**, provider-agnostic (without `type:` strings). Bootstrapped via `db.Database.Migrate()`.
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
