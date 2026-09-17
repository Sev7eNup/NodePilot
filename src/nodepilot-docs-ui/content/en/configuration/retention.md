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
| `ExecutionStatsRollupService` | Keeps the hourly buckets behind the dashboard history current (`ExecutionHourlyStat`, `FailureCauseHourlyStat`); sweeps every 60 s, initial fill in daily chunks | `Stats:Rollup:Enabled` (on by default), leader only |
| `DashboardAggregateWarmup` | Precomputes the 24 h / 7 d / 30 d dashboard aggregates after startup so the first caller hits the cache too | `Dashboard:Warmup:Enabled` (on by default), per node |
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
| `Stats:Rollup:Enabled` | `true` | Precomputed hourly buckets for the dashboard history. Turned off, the dashboard recomputes every window from the raw rows — correct, but markedly slower on large histories |

The hourly buckets carry the historical dashboard figures (status counts, retry share, duration, failure causes). Live values — running executions, queue depth, heartbeats — are never precomputed. Windows shorter than a day (the 1 h dashboard window) are always computed from the raw executions, because hourly buckets would add up to an hour of runs from before the window. While the initial fill does not yet cover a window, the dashboard keeps serving that window from the live computation. The same happens when the buckets stop being updated — rollup switched off or failing for more than ten minutes — so the dashboard never shows buckets that are missing recent hours. Buckets older than 32 days (the longest dashboard window of 30 days plus a margin) are deleted by the rollup service itself; no separate retention setting exists for them.

`GET /api/stats/dashboard` returns the state as of the last refresh, not live numbers. Settings mutations write `SETTINGS_STATS_UPDATED` to the audit log.
