# Datenbank-Provider

NodePilot unterstützt PostgreSQL und SQL Server. `Database:Provider` wählt den aktiven Provider beim Start aus.

| Provider | Wert | ConnectionString-Key |
|---|---|---|
| PostgreSQL (Default) | `"postgres"` | `ConnectionStrings:Postgres` |
| SQL Server | `"sqlserver"` | `ConnectionStrings:DefaultConnection` |

SQLite wird ausschließlich als In-Memory-Backend in Tests verwendet.

## Ausfall im laufenden Betrieb

Fällt die Datenbank nach dem Start aus oder hängt sie, bleibt NodePilot erreichbar: Datenbankabhängige
Requests antworten schnell mit `503 DATABASE_UNAVAILABLE`, die UI zeigt den Ausfall und laufende
Workflows warten an dauerhaften Schrittgrenzen. Eine separate Sonde prüft `SELECT 1`; nach erfolgreicher
Erholung laufen Requests und Hintergrunddienste ohne Neustart weiter.

`RejectedByServer` bedeutet dagegen fehlerhafte Zugangsdaten, Datenbankauswahl oder TLS-Konfiguration.
Dieser Zustand benötigt einen Administrator und ist nicht durch Warten behebbar. Nach serverseitiger
Korrektur greift die Sonde automatisch; geänderte NodePilot-Verbindungsdaten benötigen einen Neustart.
Einzelne langsame Abfragen liefern `DATABASE_TIMEOUT` und öffnen den globalen Ausfall-Breaker nicht sofort.

| Einstellung | Default | Zweck |
|---|---:|---|
| `Database:ConnectTimeoutSeconds` | `5` | Verbindungsbudget der Anwendung |
| `Database:AuthReadTimeoutSeconds` / `ReadinessProbeTimeoutSeconds` | `3` / `5` | Kurze Budgets für Authentifizierung und Readiness |
| `Database:Probe:ConnectTimeoutSeconds` / `CommandTimeoutSeconds` | `2` / `2` | Harte Budgets der Recovery-Sonde |
| `Database:Probe:IdleIntervalSeconds` / `OutageIntervalSeconds` | `5` / `5` | Prüfintervall im Normal- und Ausfallzustand |
| `Database:Probe:SuccessesToRecover` / `FailureThreshold` | `2` / `2` | Bestätigung vor Recovery beziehungsweise Breaker-Öffnung |

Alle Werte sind positive, restart-pflichtige Boot-Konfiguration. `0` wird abgelehnt, weil Provider
damit teilweise einen unbegrenzten Timeout aktivieren. Status liefern `/healthz/ready` (Traffic-Gate)
und `/healthz/database` (immer HTTP 200, Zustand im Body).

## Migrationen

- **Ein gemeinsames Migration-Set**, provider-agnostisch (ohne `type:`-Strings). Bootstrap via `db.Database.Migrate()`.
- Neue Migration:
  ```bash
  dotnet ef migrations add <Name> \
    --project src/NodePilot.Data \
    --startup-project src/NodePilot.Api \
    --context NodePilotDbContext
  ```
- **Pflicht-Postprocessing — zwei Schritte:**
  1. In der Migration (`<Name>.cs`): alle `type: "..."`-Annotations entfernen.
  2. In der Designer-Datei (`<Name>.Designer.cs`): `MigrationModelPortability.UseActiveProviderStoreTypes(modelBuilder);` als letzte Zeile vor `#pragma warning restore 612, 618` in `BuildTargetModel` ergänzen. Der `ModelSnapshot` bekommt den Aufruf bewusst **nicht** — er ist Diff-Basis, kein Migration-Target-Model.

  Beide Schritte sind durch `MigrationDriftTests` abgesichert.
- Schema-Änderungen **immer** per EF-Migration. Kein DDL-Hotpatching.

## Credentials

Credentials werden mit DPAPI verschlüsselt (`Credentials:DpapiScope`). Im Cluster muss stattdessen AES-GCM verwendet werden — siehe [Secret-Provider](../enterprise/secrets-providers).

## Produktion

- **SQL Server 2022 ab CU1** (trusted connection, Build ≥ 16.0.4003.1) oder **PostgreSQL 16+** (User/Password). Die Produktions-Verbindung nutzt `Encrypt=Strict` (TDS 8.0) — SQL Server 2019 kann das nicht, und 2022 RTM bricht parametrisierte Abfragen mit TDS-Error 8005 ab (behoben ab CU1).
- Das gMSA-Login / die Postgres-Role braucht DDL-Rechte (für `Migrate()`).
