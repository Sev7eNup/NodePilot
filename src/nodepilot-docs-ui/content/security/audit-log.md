# Audit-Log

Jede sicherheitsrelevante Aktion wird auditiert. Codes folgen dem Muster `VERB_NOMEN`. Passwörter/Secrets werden **nie** in Details geschrieben.

## Schreiben

`IAuditWriter` injizieren, dann **nach** `SaveChanges`. Audit-Codes werden **immer** als Konstante aus `NodePilot.Core.Audit.AuditActions` referenziert — nie als rohes String-Literal am Call-Site (der Guard-Test `AuditActionsCatalogTests` erzwingt das):

```csharp
await _audit.LogAsync(AuditActions.WorkflowPublished, "Workflow", resourceId, detailsJson, ct);
```

Schreibfehler darf eine normale Mutation **nie** abbrechen. Ausnahme: Beliebiges DB-Admin-Write-SQL läuft fail-closed und wird ohne vorab persistiertes `DBADMIN_SQL_WRITE_ATTEMPTED` nicht ausgeführt.

## Audit-Codes

Der autoritative Katalog lebt in `NodePilot.Core.Audit.AuditActions` — die Guard-Tests halten ihn vollständig und stale-frei. Codes folgen dem Muster `VERB_NOMEN`:

| Bereich | Codes |
|---|---|
| Workflow | `WORKFLOW_CREATED\|UPDATED\|DELETED\|DUPLICATED\|ROLLED_BACK\|CANCEL_ALL\|STEP_TESTED` |
| Edit-Lock | `WORKFLOW_LOCKED\|UNLOCKED\|PUBLISHED\|FORCE_UNLOCKED` |
| Machine | `MACHINE_CREATED\|UPDATED\|DELETED\|CONNECTION_TEST_FAILED` |
| Credential | `CREDENTIAL_CREATED\|UPDATED\|DELETED\|DECRYPTED\|DECRYPT_FAILED` |
| Globals | `GLOBAL_VARIABLE_CREATED\|UPDATED\|DELETED\|MOVED` |
| Global-Variable-Ordner | `GLOBAL_VARIABLE_FOLDER_CREATED\|UPDATED\|MOVED\|DELETED` |
| Login | `LOGIN_SUCCESS\|FAILED\|LOCKED`, `BREAK_GLASS_LOGIN_SUCCESS`, `LOGOUT`, `TOKEN_REFRESHED`, `USER_CREATED_BOOTSTRAP` |
| User | `USER_CREATED\|ACTIVATED\|DEACTIVATED\|DELETED\|ROLE_CHANGED\|PASSWORD_RESET\|BREAK_GLASS_CHANGED` |
| AD SSO Preview | `USER_{LDAP\|WINDOWS}_JIT_CREATED\|JIT_UPDATED\|REFUSED_COLLISION\|REFUSED_BOOTSTRAP\|REFUSED_LAST_ADMIN`, `USER_DIRECTORY_ACCESS_REFUSED\|SYNCED\|DEPROVISIONED`, `USER_AUTHORIZATION_STALE` |
| SCIM | `USER_SCIM_PROVISIONED\|UPDATED\|DEPROVISIONED`, `SCIM_GROUP_PROVISIONED\|UPDATED\|DEPROVISIONED` |
| Execution / HA | `EXECUTION_STARTED\|CANCELLED\|RETRIED\|RECOVERED_FAILOVER`, `CLUSTER_LEADERSHIP_ACQUIRED` |
| Maintenance | `MAINTENANCE_WINDOW_CREATED\|UPDATED\|DELETED\|OVERRIDDEN`, `EXECUTION_BLOCKED_MAINTENANCE_WINDOW` |
| Trigger | `WEBHOOK_TRIGGERED`, `EXTERNAL_TRIGGER_FIRED` |
| Import/Export | `WORKFLOW_EXPORTED\|EXPORTED_BULK\|IMPORTED\|IMPORTED_SCORCH`, `CUSTOM_ACTIVITY_EXPORTED`, `AUDIT_LOG_EXPORTED`, `SUPPORT_EVENTS_EXPORTED`, `SUPPORT_LOG_DOWNLOADED` |
| Folders | `FOLDER_CREATED\|UPDATED\|MOVED\|DELETED`, `WORKFLOW_MOVED`, `FOLDER_PERMISSION_UPDATED\|REVOKED` |
| AI | `AI_SCRIPT_GENERATED\|AI_WORKFLOW_GENERATED\|AI_WORKFLOW_EXPLAINED\|AI_PROPOSAL_APPLIED` |
| Alerting | `ALERT_RULE_CREATED\|UPDATED\|DELETED\|TEST_FIRED`, `SYSTEM_ALERT_POLICY_CREATED\|UPDATED\|DELETED\|ENABLED\|DISABLED\|TEST_FIRED` |
| Custom Activities | `CUSTOM_ACTIVITY_CREATED\|UPDATED\|DELETED\|IMPORTED\|EXPORTED\|ROLLED_BACK` |
| DB Admin | `DBADMIN_ROWS_VIEWED\|ROW_UPDATED\|ROW_DELETED\|SQL_EXECUTED\|SQL_WRITE_ATTEMPTED\|SQL_WRITE` |
| Secrets | `SECRETS_REENCRYPTED` |
| Backup | `BACKUP_EXPORTED\|RESTORED` |
| Settings | `SETTINGS_{SMTP\|LLM\|RETENTION\|AUTHENTICATION\|LOGGING\|OPENTELEMETRY\|STATS\|DBADMIN}_UPDATED`, `SETTINGS_SMTP_TESTED`, `SETTINGS_LLM_TESTED`, `SETTINGS_AUTHENTICATION_TESTED` |

## Pipeline

Jeder Audit-Write fließt durch `IAuditStager` (in `NodePilot.Core/Audit/`). HTTP-Controller nutzen `IAuditWriter` (`NodePilot.Api/Audit/AuditWriter.cs`), der den Stager mit `HttpContextAccessor`-Actor-Auflösung + ECS-Log-Forwarding + Support-Log-Whitelist-Check wrappt. Redaction und 4 KiB-Cap gelten überall einheitlich.

## Archive-Integrität

`AuditLogRetentionService.ArchiveAsync` schreibt gzip-komprimierte `audit-{date}-{ticks}-{rand}.ndjson.gz` plus SHA-256-Sidecar. Ein periodischer Verify-Pass (default täglich) rechnet Hashes neu und warnt via Metric `nodepilot.audit_archive.hash_drift` bei Drift.

## Zugriff

Admin-only, Cursor-Pagination, Export als CSV/NDJSON (`GET /api/audit/export?format=csv|ndjson`). Der Export selbst schreibt `AUDIT_LOG_EXPORTED` mit Filtern und tatsächlicher Zeilenanzahl.

## SIEM-Forwarding

Bei `Logging:Format=ecs-json` wird jeder erfolgreiche Audit-Row zusätzlich als strukturiertes ECS-Event über Serilog emittiert — siehe [SIEM-Logging](../enterprise/siem-logging).
