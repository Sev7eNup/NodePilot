# Retention services

Retention services delete or archive historical data after a retention period. Individual services can be disabled with `Retention:*:Enabled=false`. Idempotency keys always have a fixed lifetime of 24 hours.

## Overview

| Service | Purpose | Default | Gating |
|---|---|---|---|
| `ExecutionRetentionService` | Trims `WorkflowExecutions` | 30 d | `Retention:Executions:Enabled` |
| `AuditLogRetentionService` | Trims `AuditLogs` + gzip/SHA-256 archives | 365 d | `Retention:AuditLog:Enabled` |
| `WorkflowVersionsRetentionService` | Keeps N versions per workflow | 50 | `Retention:WorkflowVersions:Enabled` |
| `SupportEventRetentionService` | Trims `SupportEvents` | 90 d | `Retention:SupportEvents:Enabled`, leader only |
| `NotificationRetentionService` | Trims terminal `NotificationDeliveryAttempt` records + orphaned `NotificationSuppressionState` | 90 d | `Retention:Notifications:Enabled`, leader only |
| `TriggerReceiptRetentionService` | Trims `TriggerDeliveryReceipts` — one row per observed trigger signal, so the fastest-growing table here. `TriggerDeliveryCheckpoints` is never swept: one row per trigger node, updated in place | 7 d | `Retention:TriggerReceipts:Enabled`, leader only |
| `IdempotencyKeyCleanupService` | Trims idempotency keys after a 24 h TTL | 24 h | Always on (cannot be disabled) |

## Other background services

| Service | Purpose | Gating |
|---|---|---|
| `TriggerOrchestrator` + Quartz | Trigger scan (5 s) + Quartz cron for `scheduleTrigger` | Leader only (in a cluster) |
| `ExecutionDispatchWorker` | Leased dispatch of persisted `Pending` executions from the database outbox | Leader-only (in a cluster) |
| `MaintenanceWindowSnapshotService` | Keeps the maintenance-window snapshot per node current | Always on |
| `WorkflowStatsRefresher` | Computes the `WorkflowStats` aggregates | Always on |
| `RevokedTokensCleanupService` | Daily sweep of `RevokedTokens` | Always on |
| `HubRevocationSweeper` | Closes SignalR connections on logout/deactivation | Always on |
| `SupportEventFlushService` | Buffered flush of support events into the database | Always on (when the DB projection is enabled) |
| `ClusterLeaderService` / `ClusterFencingHost` / `ClusterFailoverRecoveryHost` | Leader lease, fencing, failover recovery | Only with `Cluster:Enabled` |

## Statistics aggregates

The dashboard and the workflow lists read a **precomputed** `WorkflowStats` aggregate instead of scanning `WorkflowExecutions` per request. It is refreshed by `WorkflowStatsRefresher`.

| Key | Default | Effect |
|---|---|---|
| `Stats:RefreshIntervalMinutes` | `5` | The aggregate refresh interval |
| `Stats:WindowDays` | `7` | The time window of the aggregated KPIs |

`GET /api/stats/dashboard` returns the state as of the last refresh, not live numbers. Settings mutations write `SETTINGS_STATS_UPDATED` to the audit log.
