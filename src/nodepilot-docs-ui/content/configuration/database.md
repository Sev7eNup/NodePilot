# Datenbank-Provider

Zwei Provider, umschaltbar über `Database:Provider`:

| Provider | Wert | ConnectionString-Key |
|---|---|---|
| PostgreSQL (Default) | `"postgres"` | `ConnectionStrings:Postgres` |
| SQL Server | `"sqlserver"` | `ConnectionStrings:DefaultConnection` |

SQLite nur als Test-In-Memory-Backend.

## Migrationen

- **Ein gemeinsames Migration-Set**, provider-agnostisch (ohne `type:`-Strings). Bootstrap via `db.Database.Migrate()`.
- Neue Migration:
  ```bash
  dotnet ef migrations add <Name> \
    --project src/NodePilot.Data \
    --startup-project src/NodePilot.Api \
    --context NodePilotDbContext
  ```
- **Pflicht-Postprocessing:** alle `type: "..."`-Annotations entfernen.
- Schema-Änderungen **immer** per EF-Migration. Kein DDL-Hotpatching.

## Credentials

Credentials werden mit DPAPI verschlüsselt (`Credentials:DpapiScope`). Im Cluster muss stattdessen AES-GCM verwendet werden — siehe [Secret-Provider](../enterprise/secrets-providers).

## Produktion

- **SQL Server 2022** (trusted connection) oder **PostgreSQL 16+** (User/Password).
- Das gMSA-Login / die Postgres-Role braucht DDL-Rechte (für `Migrate()`).