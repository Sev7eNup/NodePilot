# Audit log

The audit log records security-relevant actions. Action codes follow the pattern `VERB_NOUN`. Passwords and secrets must not appear in audit details.

## Writing

Inject `IAuditWriter`, then call it **after** `SaveChanges`. Audit codes are **always** referenced as a constant from `NodePilot.Core.Audit.AuditActions` — never as a raw string literal at the call site (the guard test `AuditActionsCatalogTests` enforces this):

```csharp
await _audit.LogAsync(AuditActions.WorkflowPublished, "Workflow", resourceId, detailsJson, ct);
```

A write failure must **never** abort a normal mutation. The exception: arbitrary database-admin write SQL runs fail-closed and is not executed without a previously persisted `DBADMIN_SQL_WRITE_ATTEMPTED`.

## Audit codes

The authoritative catalog lives in `NodePilot.Core.Audit.AuditActions` — the guard tests keep it complete and free of stale entries. Codes follow the pattern `VERB_NOUN`:

| Area | Codes |
|---|---|
| Workflow | `WORKFLOW_CREATED\|UPDATED\|DELETED\|DUPLICATED\|ROLLED_BACK\|CANCEL_ALL\|STEP_TESTED` |
| Edit lock | `WORKFLOW_LOCKED\|UNLOCKED\|PUBLISHED\|FORCE_UNLOCKED` |
| Machine | `MACHINE_CREATED\|UPDATED\|DELETED\|CONNECTION_TEST_FAILED` |
| Credential | `CREDENTIAL_CREATED\|UPDATED\|DELETED\|DECRYPTED\|DECRYPT_FAILED` |
| Globals | `GLOBAL_VARIABLE_CREATED\|UPDATED\|DELETED\|MOVED` |
| Global-variable folders | `GLOBAL_VARIABLE_FOLDER_CREATED\|UPDATED\|MOVED\|DELETED` |
| Login | `LOGIN_SUCCESS\|FAILED\|LOCKED`, `BREAK_GLASS_LOGIN_SUCCESS`, `LOGOUT`, `TOKEN_REFRESHED`, `USER_CREATED_BOOTSTRAP` |
| User | `USER_CREATED\|ACTIVATED\|DEACTIVATED\|DELETED\|ROLE_CHANGED\|PASSWORD_RESET\|BREAK_GLASS_CHANGED` |
| AD SSO Preview | `USER_{LDAP\|WINDOWS}_JIT_CREATED\|JIT_UPDATED\|REFUSED_COLLISION\|REFUSED_BOOTSTRAP\|REFUSED_LAST_ADMIN`, `USER_DIRECTORY_ACCESS_REFUSED\|SYNCED\|DEPROVISIONED`, `USER_AUTHORIZATION_STALE` |
| SCIM | `USER_SCIM_PROVISIONED\|UPDATED\|DEPROVISIONED`, `SCIM_GROUP_PROVISIONED\|UPDATED\|DEPROVISIONED` |
| Execution / HA | `EXECUTION_STARTED\|CANCELLED\|RETRIED\|RECOVERED_FAILOVER`, `CLUSTER_LEADERSHIP_ACQUIRED` |
| Maintenance | `MAINTENANCE_WINDOW_CREATED\|UPDATED\|DELETED\|OVERRIDDEN`, `EXECUTION_BLOCKED_MAINTENANCE_WINDOW` |
| Triggers | `WEBHOOK_TRIGGERED`, `EXTERNAL_TRIGGER_FIRED` |
| Import/export | `WORKFLOW_EXPORTED\|EXPORTED_BULK\|IMPORTED\|IMPORTED_SCORCH`, `CUSTOM_ACTIVITY_EXPORTED`, `AUDIT_LOG_EXPORTED`, `SUPPORT_EVENTS_EXPORTED`, `SUPPORT_LOG_DOWNLOADED` |
| Folders | `FOLDER_CREATED\|UPDATED\|MOVED\|DELETED`, `WORKFLOW_MOVED`, `FOLDER_PERMISSION_UPDATED\|REVOKED` |
| AI | `AI_SCRIPT_GENERATED\|AI_WORKFLOW_GENERATED\|AI_WORKFLOW_EXPLAINED\|AI_PROPOSAL_APPLIED\|AI_KNOWLEDGE_ASKED` |
| Alerting | `ALERT_RULE_CREATED\|UPDATED\|DELETED\|TEST_FIRED`, `SYSTEM_ALERT_POLICY_CREATED\|UPDATED\|DELETED\|ENABLED\|DISABLED\|TEST_FIRED` |
| Custom activities | `CUSTOM_ACTIVITY_CREATED\|UPDATED\|DELETED\|IMPORTED\|EXPORTED\|ROLLED_BACK` |
| Database admin | `DBADMIN_ROWS_VIEWED\|ROW_UPDATED\|ROW_DELETED\|SQL_EXECUTED\|SQL_WRITE_ATTEMPTED\|SQL_WRITE` |
| Secrets | `SECRETS_REENCRYPTED` |
| Backup | `BACKUP_EXPORTED\|RESTORED` |
| Settings | `SETTINGS_{AIKNOWLEDGE\|AUTHENTICATION\|DBADMIN\|ENGINE\|EXECUTIONDISPATCH\|EXTERNALTRIGGER\|FILESYSTEMOPERATION\|LLM\|LOGGING\|OPENTELEMETRY\|REMOTE\|RESTAPI\|RETENTION\|SECURITY\|SMTP\|SQLACTIVITY\|STARTPROGRAM\|STATS\|THREADING\|WEBHOOK}_UPDATED` (one code per settings section), `SETTINGS_SMTP_TESTED`, `SETTINGS_LLM_TESTED`, `SETTINGS_AUTHENTICATION_TESTED` |

## Pipeline

Every audit write flows through `IAuditStager` (in `NodePilot.Core/Audit/`). HTTP controllers use `IAuditWriter` (`NodePilot.Api/Audit/AuditWriter.cs`), which wraps the stager with `HttpContextAccessor` actor resolution + ECS log forwarding + a support-log allow-list check. Redaction and the 4 KiB cap apply uniformly everywhere.

## Archive integrity

`AuditLogRetentionService.ArchiveAsync` writes gzip-compressed `audit-{date}-{ticks}-{rand}.ndjson.gz` files plus a SHA-256 sidecar. A periodic verify pass (daily by default) recomputes the hashes and warns through the metric `nodepilot.audit_archive.hash_drift` on drift.

## Access

Admin only, cursor pagination, export as CSV/NDJSON (`GET /api/audit/export?format=csv|ndjson`). The export itself writes `AUDIT_LOG_EXPORTED` with the filters and the actual row count.

## SIEM forwarding

With `Logging:Format=ecs-json`, every successful audit row is additionally emitted as a structured ECS event through Serilog — see [SIEM logging](../enterprise/siem-logging).
