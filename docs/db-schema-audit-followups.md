# DB-Schema-Audit — offene Follow-ups

Ergebnis des DB-Schema-Audits vom **2026-07-14**. Die umgesetzten Findings **F1** (CompletedAt-Index),
**F2** (committete Secrets entfernt), **F4** (Orphan-Tabellen-Cleanup-Migration) und **F6** (bounded
Startup-Recovery) sind über **PR #153** auf `main`. Dieses Dokument hält die **nicht validierten,
zurückgestellten** Findings fest.

Die drei Findings unten sind **measure-first**: Sie wurden statisch gefunden, aber **nicht gegen eine
Live-DB validiert** (Audit lief static-only). Erst messen, dann entscheiden — jeweils mit Messquery,
Provider, Schwellenwert und Wiederaufnahmebedingung.

> **Voraussetzung für alle drei:** Observability aktivieren — `pg_stat_statements` (PostgreSQL) bzw.
> Query Store (SQL Server) — und mindestens ~30 Tage repräsentativen Produktions-Traffic sammeln, bevor
> eine Index-/Schema-Änderung abgeleitet wird. Ohne echte Statistik bleibt jede dieser Änderungen Spekulation.

---

## F3 — SupportEvents-Freitextsuche ist ein Full-Scan · Medium

- **Evidenz:** `DiagnosticsController.cs:209` `LOWER(Message) LIKE '%q%'` auf ungeindextem
  `nvarchar(8000)`; `:199` `LOWER(UserName)` (kein Index); `:191` `LOWER(WorkflowName)` (Index durch
  `LOWER()` + führendes `%` neutralisiert). SupportEvents ist die Tabelle mit der höchsten Insert-Rate
  (90 Tage Retention).
- **Messquery (PostgreSQL):**
  ```sql
  SELECT calls, mean_exec_time, max_exec_time, rows
  FROM pg_stat_statements
  WHERE query ILIKE '%SupportEvents%' AND query ILIKE '%LIKE%'
  ORDER BY mean_exec_time DESC;
  SELECT reltuples::bigint AS approx_rows FROM pg_class WHERE relname = 'SupportEvents';
  ```
  **SQL Server:** Query Store → gleiche Query nach `avg_duration`/`execution_count`; Zeilen via
  `sys.dm_db_partition_stats`.
- **Schwellenwert (handeln, wenn):** `?q=`-Diagnose-Query p95 > ~500 ms **oder** SupportEvents > ~5 Mio.
  Zeilen im Retention-Fenster, **und** die Suche wird real genutzt (`calls` nicht ~0).
- **Fix (providerspezifisch):** **PostgreSQL** — `pg_trgm`-GIN auf `lower("Message")` / `lower("UserName")`
  (bedient `LIKE '%…%'` echt). **SQL Server** — echte Full-Text-Suche (`CONTAINS`/`FREETEXT`, **braucht
  Code-Änderung**, andere Query-Semantik); eine berechnete Lowercase-Spalte + Index hilft beim
  **führenden** `%` **nicht**. Günstigste Zwischenlösung: verpflichtender Zeitfilter und/oder Mindestlänge
  für `q`, damit ein nackter unbounded Scan unmöglich ist.
- **Wiederaufnahme:** Sobald SupportEvents-Zeilenzahl/Query-Latenz die Schwelle reißt oder ein Nutzer
  langsame Diagnose-Suche meldet.

## F7 — AuditLog: 6 Indizes, teils vermutlich ungenutzt · Low (provisional)

- **Evidenz:** `NodePilotDbContext.cs:277-309` — 6 Indizes; `(IpAddress,Timestamp)` und
  `(Username,Timestamp)` sind plausibel ungenutzt und kosten Write-Amplification (AuditLog schreibt
  synchron pro mutierender Aktion — das ist gewollte Haltbarkeit, **kein** Defekt).
- **Messquery (PostgreSQL):**
  ```sql
  SELECT indexrelname, idx_scan, idx_tup_read
  FROM pg_stat_user_indexes
  WHERE relname = 'AuditLog'
  ORDER BY idx_scan;
  ```
  **SQL Server:** `sys.dm_db_index_usage_stats` (Joins auf `sys.indexes`) nach `user_seeks + user_scans`
  je AuditLog-Index.
- **Schwellenwert (handeln, wenn):** `idx_scan = 0` (bzw. `user_seeks+user_scans = 0`) über ≥30 Tage
  echten Traffic für einen konkreten Composite. Stats-Reset-Zeitpunkt notieren (`pg_stat_reset` /
  Server-Neustart setzen die Zähler zurück).
- **Fix:** Nur den **bestätigt** ungenutzten Composite droppen (einzelne Migration). Nichts droppen, was
  Audit-Export-Filter (`GET /api/audit?ipAddress=…&username=…`) bedient.
- **Wiederaufnahme:** Nach ≥30 Tagen Prod-Traffic mit aktivierten Usage-Stats.

## F9 — Zufalls-GUID-PKs & Clustered-Index-Fragmentierung · Low (provisional, nur SQL Server)

- **Evidenz:** Alle Hot-Insert-Tabellen (StepExecution, WorkflowExecution, AuditLog, SupportEvent) nutzen
  zufällige `Guid.NewGuid()`-PKs. Auf **SQL Server** ist der PK per Default der **Clustered Index** →
  zufällige Inserts an zufälligen Positionen → Page-Splits/Fragmentierung. **PostgreSQL**: PK ist
  non-clustered (Heap) → nur Index-Bloat, deutlich milder → für PG **n/a**.
- **Messquery (nur SQL Server):**
  ```sql
  SELECT OBJECT_NAME(ips.object_id) AS table_name, i.name AS index_name,
         ips.avg_fragmentation_in_percent, ips.page_count
  FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'SAMPLED') ips
  JOIN sys.indexes i ON i.object_id = ips.object_id AND i.index_id = ips.index_id
  WHERE OBJECT_NAME(ips.object_id) IN
        ('StepExecutions','WorkflowExecutions','AuditLog','SupportEvents')
    AND ips.index_id = 1;  -- clustered
  ```
  Zusätzlich Page-Splits via `sys.dm_db_index_operational_stats(...).leaf_allocation_count`.
- **Schwellenwert (handeln, wenn):** anhaltend `avg_fragmentation_in_percent` > ~30 % auf den Hot-Tabellen
  **trotz** regelmäßigem Index-Rebuild, plus messbare Insert-Latenz-Regression.
- **Fix:** Sequentielle GUIDs (`NEWSEQUENTIALID()`-Default) **oder** PK non-clustered lassen und auf einen
  monotonen Schlüssel clustern. Provider-gated: **nur** SQL Server anfassen.
- **Wiederaufnahme:** Nur auf einem **SQL-Server**-Deployment mit gemessener materieller Fragmentierung
  (auf dem Default-PostgreSQL irrelevant).

---

## Weiteres

- **F2-Nachtrag — Passwort rotieren:** Das dev-Postgres-Passwort wurde aus `HEAD` entfernt (PR #153), steht
  aber weiterhin in der **Git-Historie**. Rotation macht das alte Secret unwirksam; eine History-Bereinigung
  (`git filter-repo`/BFG) wäre ein separater, koordinierter Vorgang, falls das Repo geteilt wird.

- **Dev-Secret-Hygiene — `.claude/settings.local.json` statt `settings.json`:** Maschinenspezifische Befehle
  mit Credentials (z. B. `PGPASSWORD=… psql …`) gehören in die **bereits gitignored**
  `.claude/settings.local.json`, **nicht** in die getrackte `.claude/settings.json`. Für API-Aufrufe in
  Allowlist-Befehlen keine fest eingebetteten Bearer-Tokens verwenden, sondern dynamischen Login bzw. eine
  `$TOKEN`-Variable — eingebettete JWTs bleiben lesbar (Claims) und lassen Secret-Scanner anschlagen,
  selbst wenn sie abgelaufen sind.

- **Zugehöriger Guardrail (Top-Empfehlung des Audits):** Eine CI-Matrix, die den Migrations-Pfad
  (Fresh-Install **und** Upgrade) tatsächlich gegen echte PostgreSQL- **und** SQL-Server-Container **ausführt**
  — nicht nur `GenerateScript`. Die vorhandenen `MigrationDriftTests` generieren nur Skripte und fangen daher
  keine reinen Laufzeitfehler (z. B. ein ungeschütztes `DropTable` auf einer divergierten DB — die F4-Klasse).
