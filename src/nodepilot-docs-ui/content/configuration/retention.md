# Retention-Services

Retention-Services trimmen historische Daten. Alle sind opt-out via `Retention:*:Enabled: false`. **Idempotency-Keys** (24 h, fixe TTL) laufen **immer** — nicht abschaltbar.

## Übersicht

| Service | Zweck | Default | Gating |
|---|---|---|---|
| `ExecutionRetentionService` | Trimt `WorkflowExecutions` | 30 d | `Retention:Executions:Enabled` |
| `AuditLogRetentionService` | Trimt `AuditLogs` + gzip/SHA-256-Archive | 365 d | `Retention:AuditLog:Enabled` |
| `WorkflowVersionsRetentionService` | Behält N Versionen pro Workflow | 50 | `Retention:WorkflowVersions:Enabled` |
| `SupportEventRetentionService` | Trimt `SupportEvents` | 90 d | `Retention:SupportEvents:Enabled`, leader-only |
| `NotificationRetentionService` | Trimt terminale `NotificationDeliveryAttempt` + verwaiste `NotificationSuppressionState` | 90 d | `Retention:Notifications:Enabled`, leader-only |
| `IdempotencyKeyCleanupService` | Trimt Idempotency-Keys nach 24 h TTL | 24 h | immer an (nicht disablebar) |

## Andere Background-Services

| Service | Zweck | Gating |
|---|---|---|
| `TriggerOrchestrator` + Quartz | Trigger-Scan (5 s) + Quartz-Cron für `scheduleTrigger` | leader-only (im Cluster) |
| `ExecutionDispatchWorker` | Channel-basierter Dispatch von `Pending` Executions | immer an |
| `MaintenanceWindowSnapshotService` | Hält Maintenance-Window-Snapshot pro Node aktuell | immer an |
| `WorkflowStatsRefresher` | Berechnet `WorkflowStats`-Aggregate | immer an |
| `RevokedTokensCleanupService` | Daily Sweep von `RevokedTokens` | immer an |
| `HubRevocationSweeper` | Schließt SignalR-Connections bei Logout/Deactivation | immer an |
| `SupportEventFlushService` | Gepufferter Flush von Support-Events in DB | immer an (wenn DB-Projektion an) |
| `ClusterLeaderService` / `ClusterFencingHost` / `ClusterFailoverRecoveryHost` | Leader-Lease, Fencing, Failover-Recovery | nur `Cluster:Enabled` |

## Stats-Aggregate

Dashboard und Workflow-Listen lesen ein **precomputed** `WorkflowStats`-Aggregate statt `WorkflowExecutions` pro Request zu scannen. Refresh durch `WorkflowStatsRefresher`.

| Key | Default | Effect |
|---|---|---|
| `Stats:RefreshIntervalMinutes` | `5` | Aggregate-Refresh-Interval |
| `Stats:WindowDays` | `7` | Zeitfenster der aggregierten KPIs |

`GET /api/stats/dashboard` liefert den letzten Refresh-Stand, keine Live-Zahlen. Settings-Mutationen schreiben `SETTINGS_STATS_UPDATED` ins Audit-Log.