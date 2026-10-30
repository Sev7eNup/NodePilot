# NodePilot Prometheus Metrics — Validated Inventory

**As of:** 2026-10-22. Standard instrumentation and `RAW_METRICS_SAMPLE.txt` reconciled against a live scrape of the Windows dev API: OpenTelemetry SDK 1.19.1, Process instrumentation 1.19.0-rc.1; `process_cpu_time_seconds_total` carries `cpu_mode`. Workflow names/IDs, instance ID, and external host/DNS addresses in the sample are pseudonymized; measured values and other labels come from the scrape. The sample shows the series that arose on this instance, not every possible execution path. Note: tags set via `TelemetryConstants.Attributes.*` constants (`nodepilot.*`) export as a label with a `nodepilot_` prefix (e.g. `nodepilot_llm_kind`); plain string tags (`result`, `status`, `source`, `operation`, …) do not.

## Naming Rule (OpenTelemetry .NET → Prometheus)

| Instrument-Name (Source) | Unit | Prometheus-Export |
|---|---|---|
| `nodepilot.executions.started` | `1` (counter) | `nodepilot_executions_started_total` |
| `nodepilot.execution.duration` | `ms` (histogram) | `nodepilot_execution_duration_milliseconds_{bucket,sum,count}` |
| `nodepilot.execution.nodes_executed` | `1` (histogram) | `nodepilot_execution_nodes_executed_{bucket,sum,count}` |
| `nodepilot.executions.active` | `1` (UpDownCounter) | `nodepilot_executions_active` |

**Important:** The unit `ms` is appended to the metric name as `_milliseconds`. The old dashboards used `nodepilot_execution_duration_bucket` — that does not exist.

---

## NodePilot Engine Metrics (`NodePilot.Engine` Meter)

### Counters (all with `_total` suffix)

| Prometheus name | Tags | Description |
|---|---|---|
| `nodepilot_executions_started_total` | `workflow_id`, `workflow_name`, `trigger_type` | Workflow runs started |
| `nodepilot_executions_completed_total` | `workflow_id`, `workflow_name`, `status` (Succeeded/Failed/Cancelled) | Workflow runs in terminal state |
| `nodepilot_execution_cancellations_total` | `reason`=`user_or_token`: `workflow_id`, `workflow_name`, `reason` | Execution cancelled by user/token (counts executions) |
| `nodepilot_execution_cancellations_total` | `reason`=`junction_race`: only `reason`, **no** workflow tags | counts a **step** (not the run) that is skipped after a waitAny/waitNofM junction is satisfied; the run continues. To get cancel rates, filter by `reason` (the dashboards use `reason!="junction_race"`) |
| `nodepilot_executions_rejected_total` | `reason` (global_cap/per_user_cap) | Capacity cap rejections |
| `nodepilot_steps_executed_total` | `activity_type`, `status` | Step executions |
| `nodepilot_step_retry_attempts_total` | `activity_type` | Retry attempts (>0) |
| `nodepilot_db_save_changes_total` | `operation`, `status` | EF SaveChanges in the engine path |
| `nodepilot_audit_writes_total` | `result`, `error_class` (only with `result="failure"`) | Audit log writes |
| `nodepilot_output_redaction_hits_total` | `pattern_kind` | OutputRedactor hits |
| `nodepilot_debug_resume_commands_total` | `mode` (Continue/StepOver/Stop) | Debug resume calls |
| `nodepilot_subworkflow_invocations_total` | `wait_mode`, `depth_bucket` | Sub-workflow invocations |
| `nodepilot_subworkflow_depth_exceeded_total` | — | Max-depth violations |
| `nodepilot_support_events_dropped_total` | `reason` | Support events dropped (buffer overflow etc.) |
| `nodepilot_support_events_written_total` | — | Support events written to DB |

### Histograms

| Prometheus name | Tags | Unit |
|---|---|---|
| `nodepilot_execution_duration_milliseconds_*` | `workflow_id`, `workflow_name`, `status` | ms |
| `nodepilot_execution_nodes_executed_*` | `workflow_id`, `workflow_name` | count |
| `nodepilot_execution_nodes_skipped_*` | `workflow_id`, `workflow_name` | count |
| `nodepilot_step_duration_milliseconds_*` | `activity_type`, `status` | ms |
| `nodepilot_step_retry_backoff_duration_milliseconds_*` | `backoff_kind` | ms |
| `nodepilot_db_save_changes_duration_milliseconds_*` | `operation`, `status` | ms |
| `nodepilot_db_save_changes_rows_*` | `operation` | count |
| `nodepilot_audit_write_duration_milliseconds_*` | — | ms |
| `nodepilot_debug_pause_duration_milliseconds_*` | `outcome` | ms |

### UpDown / Gauges

| Prometheus name | Tags |
|---|---|
| `nodepilot_executions_active` | `workflow_id`, `workflow_name` |
| `nodepilot_debug_sessions_active` | — |

---

## NodePilot API Metrics (`NodePilot.Api` Meter)

### Counters

| Prometheus name | Tags |
|---|---|
| `nodepilot_signalr_messages_sent_total` | `event_type` |
| `nodepilot_webhook_requests_total` | `result`, `reason` |
| `nodepilot_auth_login_attempts_total` | `result`, `reason` |
| `nodepilot_auth_token_revocations_total` | `reason` |
| `nodepilot_auth_lockouts_total` | — |
| `nodepilot_idempotency_replays_total` | `result` (cached/fresh) |
| `nodepilot_external_trigger_auth_failures_total` | — |
| `nodepilot_llm_calls_total` | `nodepilot_llm_kind`, `result` |
| `nodepilot_llm_errors_total` | `nodepilot_llm_kind`, `nodepilot_llm_error_kind` |
| `nodepilot_llm_tokens_total` | `nodepilot_llm_kind`, `nodepilot_llm_model`, `token_type` |
| `nodepilot_rate_limit_rejections_total` | `nodepilot_rate_limit_policy`, `source` |
| `nodepilot_workflow_operations_total` | `nodepilot_workflow_operation`, `result` |
| `nodepilot_import_export_operations_total` | `nodepilot_import_export_operation`, `result` |
| `nodepilot_credential_crud_total` | `operation`, `result` |
| `nodepilot_global_variable_crud_total` | `operation`, `result` |
| `nodepilot_dispatch_items_processed_total` | `result` |
| `nodepilot_dispatch_claims_total` | `result` (`success`/`empty`, …) |
| `nodepilot_security_revoked_tokens_deleted_total` | — |
| `nodepilot_machine_test_connections_total` | `result`, `source` (`exception` only) |
| `nodepilot_maintenance_window_crud_total` | `operation`, `result` |
| `nodepilot_maintenance_window_api_blocks_total` | `source`, `scope` |
| `nodepilot_maintenance_window_overrides_total` | — |
| `nodepilot_dispatch_workflow_concurrency_deferrals_total` | `source` |
| `nodepilot_database_requests_rejected_total` | — |
| `nodepilot_database_outages_total` | — |
| `nodepilot_database_probe_cleanup_timeouts_total` | `provider`, `operation` |

### Histograms

| Prometheus name | Tags |
|---|---|
| `nodepilot_llm_call_duration_milliseconds_*` | `nodepilot_llm_kind`, `nodepilot_llm_model` |
| `nodepilot_import_export_duration_milliseconds_*` | `nodepilot_import_export_operation` |
| `nodepilot_machine_test_connection_duration_milliseconds_*` | — |
| `nodepilot_security_revoked_tokens_sweep_duration_milliseconds_*` | — |
| `nodepilot_dispatch_claim_duration_milliseconds_*` | `result` |

### UpDown / Gauges

| Prometheus name | Tags |
|---|---|
| `nodepilot_signalr_connections_active` | — |

---

## NodePilot Scheduler Metrics

### Counters

| Prometheus name | Tags |
|---|---|
| `nodepilot_triggers_fired_total` | `trigger_type`, `workflow_id` |
| `nodepilot_trigger_orchestrator_sync_changes_total` | `change` (add/update/remove/evict-unhealthy), `trigger_type` |
| `nodepilot_trigger_orchestrator_sync_failures_total` | — |
| `nodepilot_trigger_registration_failures_total` | `trigger_type` |
| `nodepilot_trigger_poll_errors_total` | `trigger_type`, `error_class` |
| `nodepilot_trigger_events_total` | `trigger_type`, `event_kind` |
| `nodepilot_retention_rows_deleted_total` | `nodepilot_retention_service` |
| `nodepilot_retention_sweep_errors_total` | `nodepilot_retention_service` |
| `nodepilot_maintenance_window_blocks_total` | `trigger_type` | Trigger fire suppressed due to maintenance window |
| `nodepilot_maintenance_window_snapshot_refresh_errors_total` | — | Errors during snapshot refresh |
| `nodepilot_audit_archive_verified_total` | — | Audit archive segments verified (hash OK) |
| `nodepilot_audit_archive_hash_drift_total` | — | Audit archive: SHA-256 differs |
| `nodepilot_audit_archive_sidecar_missing_total` | — | Audit archive: sidecar hash file missing |
| `nodepilot_scheduler_triggers_admission_deferred_total` | `trigger_type` | Trigger admission deferred (persistence temporarily unavailable) |
| `nodepilot_scheduler_triggers_fires_skipped_total` | `trigger_type` | Trigger signals skipped after restart/failover |

### Histograms

| Prometheus name | Tags |
|---|---|
| `nodepilot_trigger_orchestrator_sync_duration_milliseconds_*` | — |
| `nodepilot_trigger_poll_duration_milliseconds_*` | `trigger_type` |
| `nodepilot_retention_sweep_duration_milliseconds_*` | `nodepilot_retention_service` |

---

## NodePilot Remote (WinRM) Metrics

| Prometheus name | Type | Tags |
|---|---|---|
| `nodepilot_winrm_sessions_opened_total` | counter | `result`, `auth` |
| `nodepilot_winrm_session_open_duration_milliseconds_*` | histogram | `result`, `auth` |
| `nodepilot_winrm_sessions_active` | up-down | — |
| `nodepilot_winrm_script_duration_milliseconds_*` | histogram | `result` |
| `nodepilot_winrm_script_timeouts_total` | counter | — |
| `nodepilot_winrm_auth_failures_total` | counter | `auth`, `reason` |

---

## NodePilot Data Metrics

| Prometheus name | Type | Tags |
|---|---|---|
| `nodepilot_credential_crypto_calls_total` | counter | `operation`, `result`, `provider` (Dpapi/AesGcm) |
| `nodepilot_credential_crypto_duration_milliseconds_*` | histogram | `operation`, `provider` |
| `nodepilot_credential_crypto_legacy_reads_total` | counter | `legacy_provider` (provider rotation: reads via the legacy protector) |

---

## Standard OTel Instrumentation (always present)

### .NET Runtime (`OpenTelemetry.Instrumentation.Runtime`)

| Prometheus name | Type | Notes |
|---|---|---|
| `dotnet_gc_collections_total` | counter | Label `gc_heap_generation` ∈ {gen0,gen1,gen2} |
| `dotnet_gc_heap_total_allocated_bytes_total` | counter | Allocations since start |
| `dotnet_gc_pause_time_seconds_total` | counter | Accumulated GC pause time |
| `dotnet_gc_last_collection_heap_size_bytes` | gauge | Heap size at last collection; label `gc_heap_generation` ∈ {gen0,gen1,gen2,loh,poh} |
| `dotnet_gc_last_collection_heap_fragmentation_size_bytes` | gauge | Fragmentation; label `gc_heap_generation` as above |
| `dotnet_gc_last_collection_memory_committed_size_bytes` | gauge | Commit size |
| `dotnet_jit_compiled_methods_total` | counter | JIT method count |
| `dotnet_jit_compilation_time_seconds_total` | counter | JIT compile time |
| `dotnet_jit_compiled_il_size_bytes_total` | counter | JIT IL size |
| `dotnet_thread_pool_thread_count_total` | counter (delta sample) | TP worker threads |
| `dotnet_thread_pool_queue_length_total` | counter | TP queue items per sample |
| `dotnet_thread_pool_work_item_count_total` | counter | Completed TP items |
| `dotnet_monitor_lock_contentions_total` | counter | Lock contention |
| `dotnet_exceptions_total` | counter | Exceptions thrown |
| `dotnet_assembly_count` | gauge | Loaded assemblies |
| `dotnet_timer_count` | gauge | Active timers |
| `dotnet_process_memory_working_set_bytes` | gauge | Working set |
| `dotnet_process_cpu_time_seconds_total` | counter | Label `cpu_mode` for user/system |
| `dotnet_process_cpu_count` | gauge | Available processors |

### Process (`OpenTelemetry.Instrumentation.Process`)

| Prometheus name | Type |
|---|---|
| `process_cpu_time_seconds_total` | counter (label `cpu_mode` for user/system) |
| `process_memory_usage_bytes` | gauge (RSS/working set) |
| `process_memory_virtual_bytes` | gauge |
| `process_thread_count` | gauge |
| `process_uptime_seconds` | gauge |
| `process_windows_handle_count` | gauge (Windows only) |

The runtime instrumentation additionally provides `dotnet_process_memory_working_set_bytes`, `dotnet_process_cpu_time_seconds_total` (label `cpu_mode`), and `dotnet_process_cpu_count` (see above); there is no `process_cpu_count`.

### ASP.NET Core (`OpenTelemetry.Instrumentation.AspNetCore`)

| Prometheus name | Type | Tags |
|---|---|---|
| `http_server_request_duration_seconds_*` | histogram | `http_request_method`, `http_response_status_code`, `http_route`, `network_protocol_version`, `url_scheme` |
| `http_server_active_requests` | gauge | `http_request_method`, `url_scheme` |
| `aspnetcore_routing_match_attempts_total` | counter | `http_route`, `aspnetcore_routing_match_status`, `aspnetcore_routing_is_fallback` |
| `aspnetcore_memory_pool_{allocated,rented}_bytes_total`, `aspnetcore_memory_pool_pooled_bytes` | counter / gauge | `aspnetcore_memory_pool_owner` |
| `kestrel_connection_duration_seconds_*` | histogram | `network_protocol_name`, `network_protocol_version`, `network_transport`, `network_type`, `server_address`, `server_port` |
| `kestrel_active_connections` | gauge | `network_transport`, `network_type`, `server_address`, `server_port` |
| `kestrel_queued_connections` | gauge | `network_transport`, `network_type`, `server_address`, `server_port` |

### HTTP Client (`OpenTelemetry.Instrumentation.Http`)

From `AddHttpClientInstrumentation()` (all outgoing `HttpClient` calls of the API process). The series appear with the first outgoing request.

| Prometheus name | Type | Tags |
|---|---|---|
| `http_client_request_duration_seconds_*` | histogram | `http_request_method`, `http_response_status_code`, `network_protocol_version`, `server_address`, `server_port`, `url_scheme` |
| `http_client_request_time_in_queue_seconds_*` | histogram | `http_request_method`, `network_protocol_version`, `server_address`, `server_port`, `url_scheme` |
| `http_client_active_requests` | gauge | `http_request_method`, `server_address`, `server_port`, `url_scheme` |
| `http_client_open_connections` | gauge | `http_connection_state` (idle/active), `network_peer_address`, `network_protocol_version`, `server_address`, `server_port`, `url_scheme` |
| `http_client_connection_duration_seconds_*` | histogram | `network_peer_address`, `network_protocol_version`, `server_address`, `server_port`, `url_scheme` |

---

## CRITICAL: Wrong Names in Old Dashboards

| Wrong (old dashboards) | Correct (actually exposed) |
|---|---|
| `nodepilot_execution_duration_bucket` | `nodepilot_execution_duration_milliseconds_bucket` |
| `nodepilot_step_duration_bucket` | `nodepilot_step_duration_milliseconds_bucket` |
| `nodepilot_winrm_session_open_duration_bucket` | `nodepilot_winrm_session_open_duration_milliseconds_bucket` |
| `nodepilot_winrm_script_duration_bucket` | `nodepilot_winrm_script_duration_milliseconds_bucket` |
| `nodepilot_trigger_orchestrator_sync_duration_bucket` | `nodepilot_trigger_orchestrator_sync_duration_milliseconds_bucket` |
| `nodepilot_execution_nodes_executed_bucket` | `nodepilot_execution_nodes_executed_bucket` (CORRECT — unit `1` appends nothing) |
| `process_cpu_seconds_total` | `process_cpu_time_seconds_total` |
| `process_working_set_bytes` | `process_memory_usage_bytes` |
| `dotnet_gc_heap_size_bytes` | `dotnet_gc_last_collection_heap_size_bytes` |
| `dotnet_gc_collections_count_total` | `dotnet_gc_collections_total` |
| `dotnet_gc_collections_count_total{generation=...}` | `dotnet_gc_collections_total{gc_heap_generation=...}` |
| `process_runtime_dotnet_thread_pool_queue_length` | `dotnet_thread_pool_queue_length_total` |
| `process_runtime_dotnet_thread_pool_threads_count` | `dotnet_thread_pool_thread_count_total` |
| `process_runtime_dotnet_thread_pool_completed_items_count_total` | `dotnet_thread_pool_work_item_count_total` |

Histogram suffix rule:
- Source unit `ms` → Prometheus suffix `_milliseconds`
- Source unit `1` or empty → no unit suffix
- Source unit `s` → `_seconds` (HTTP histograms)
- Source unit `By` → `_bytes`

Counter suffix:
- Counter always `_total`
- UpDownCounter NEVER `_total` (e.g. `nodepilot_executions_active`, `nodepilot_winrm_sessions_active`)

---

## Lazy Creation

OpenTelemetry creates counter/histogram time series only on the **first record**. This means:
- Fresh API start → trigger orchestrator metrics (sync pass every 5 s), on the leader the dispatch claim metrics (`nodepilot_dispatch_claims_total{result="empty"}` per poll), and Process/Runtime are present immediately; HTTP server/Kestrel series from the first request, `http_client_*` from the first outgoing request
- Engine/step/WinRM metrics appear only after the first workflow run
- Auth/LLM/audit metrics appear only after a login or AI call

Consequence for the dashboard: all PromQL queries that use `sum(...)` without a default should get `or vector(0)` as a fallback, so stat panels do not show "No Data".
