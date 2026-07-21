# NodePilot Prometheus Metrics — Validated Inventory

**Stand:** 2026-05-07, Label-/Instrument-Abgleich gegen Source nachgezogen 2026-06-23. Validiert gegen Live-`/metrics`-Output (siehe `RAW_METRICS_SAMPLE.txt`) und Source. Hinweis: Tags, die über `TelemetryConstants.Attributes.*`-Konstanten (`nodepilot.*`) gesetzt werden, exportieren mit `nodepilot_`-Präfix als Label (z.B. `nodepilot_llm_kind`); reine String-Tags (`result`, `status`, `source`, `operation`, …) nicht.

## Naming-Regel (OpenTelemetry .NET → Prometheus)

| Instrument-Name (Source) | Unit | Prometheus-Export |
|---|---|---|
| `nodepilot.executions.started` | `1` (counter) | `nodepilot_executions_started_total` |
| `nodepilot.execution.duration` | `ms` (histogram) | `nodepilot_execution_duration_milliseconds_{bucket,sum,count}` |
| `nodepilot.execution.nodes_executed` | `1` (histogram) | `nodepilot_execution_nodes_executed_{bucket,sum,count}` |
| `nodepilot.executions.active` | `1` (UpDownCounter) | `nodepilot_executions_active` |

**Wichtig:** Einheit `ms` wird als `_milliseconds` an den Metric-Namen angehängt. Die alten Dashboards verwendeten `nodepilot_execution_duration_bucket` — das existiert nicht.

---

## NodePilot Engine Metrics (`NodePilot.Engine` Meter)

### Counters (alle mit `_total` Suffix)

| Prometheus name | Tags | Beschreibung |
|---|---|---|
| `nodepilot_executions_started_total` | `workflow_id`, `workflow_name`, `trigger_type` | Workflow-Runs gestartet |
| `nodepilot_executions_completed_total` | `workflow_id`, `workflow_name`, `status` (Succeeded/Failed/Cancelled) | Workflow-Runs in Terminal-State |
| `nodepilot_execution_cancellations_total` | `workflow_id`, `workflow_name`, `reason` (user_or_token/junction_race) | Manuell gecancelt |
| `nodepilot_executions_rejected_total` | `reason` (global_cap/per_user_cap) | Capacity-Cap-Rejections |
| `nodepilot_steps_executed_total` | `activity_type`, `status` | Step-Executions |
| `nodepilot_step_retry_attempts_total` | `activity_type` | Retry-Attempts (>0) |
| `nodepilot_db_save_changes_total` | `operation`, `status` | EF SaveChanges in Engine-Pfad |
| `nodepilot_audit_writes_total` | `result`, `error_class` (nur bei `result="failure"`) | Audit-Log-Writes |
| `nodepilot_output_redaction_hits_total` | `pattern_kind` | OutputRedactor-Treffer |
| `nodepilot_debug_resume_commands_total` | `mode` (continue/stepOver/stop) | Debug-Resume-Calls |
| `nodepilot_subworkflow_invocations_total` | `wait_mode`, `depth_bucket` | Sub-Workflow-Aufrufe |
| `nodepilot_subworkflow_depth_exceeded_total` | — | Max-Depth-Verletzungen |
| `nodepilot_support_events_dropped_total` | `reason` | Support-Events verworfen (Buffer-Overflow etc.) |
| `nodepilot_support_events_written_total` | — | Support-Events in DB geschrieben |

### Histograms

| Prometheus name | Tags | Unit |
|---|---|---|
| `nodepilot_execution_duration_milliseconds_*` | `workflow_id`, `workflow_name`, `status` | ms |
| `nodepilot_execution_nodes_executed_*` | `workflow_id` | count |
| `nodepilot_execution_nodes_skipped_*` | `workflow_id` | count |
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
| `nodepilot_security_revoked_tokens_deleted_total` | — |
| `nodepilot_machine_test_connections_total` | `result` |
| `nodepilot_maintenance_window_crud_total` | `operation`, `result` |
| `nodepilot_maintenance_window_api_blocks_total` | `source`, `scope` |
| `nodepilot_maintenance_window_overrides_total` | — |

### Histograms

| Prometheus name | Tags |
|---|---|
| `nodepilot_llm_call_duration_milliseconds_*` | `nodepilot_llm_kind`, `nodepilot_llm_model` |
| `nodepilot_import_export_duration_milliseconds_*` | `nodepilot_import_export_operation` |
| `nodepilot_machine_test_connection_duration_milliseconds_*` | — |
| `nodepilot_security_revoked_tokens_sweep_duration_milliseconds_*` | — |

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
| `nodepilot_trigger_orchestrator_sync_changes_total` | `change`, `trigger_type` |
| `nodepilot_trigger_orchestrator_sync_failures_total` | — |
| `nodepilot_trigger_registration_failures_total` | `trigger_type` |
| `nodepilot_trigger_poll_errors_total` | `trigger_type`, `error_class` |
| `nodepilot_trigger_events_total` | `trigger_type`, `event_kind` |
| `nodepilot_retention_rows_deleted_total` | `nodepilot_retention_service` |
| `nodepilot_retention_sweep_errors_total` | `nodepilot_retention_service` |
| `nodepilot_maintenance_window_blocks_total` | `trigger_type` | Trigger-Fire wegen Maintenance-Window unterdrückt |
| `nodepilot_maintenance_window_snapshot_refresh_errors_total` | — | Fehler beim Snapshot-Refresh |
| `nodepilot_audit_archive_verified_total` | — | Audit-Archiv-Segmente verifiziert (Hash OK) |
| `nodepilot_audit_archive_hash_drift_total` | — | Audit-Archiv: SHA-256 weicht ab |
| `nodepilot_audit_archive_sidecar_missing_total` | — | Audit-Archiv: Sidecar-Hashdatei fehlt |

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
| `nodepilot_credential_crypto_legacy_reads_total` | counter | `provider` (Provider-Rotation: Reads über den Legacy-Protector) |

---

## Standard OTel-Instrumentation (immer da)

### .NET Runtime (`OpenTelemetry.Instrumentation.Runtime`)

| Prometheus name | Type | Notes |
|---|---|---|
| `dotnet_gc_collections_total` | counter | Label `gc_heap_generation` ∈ {gen0,gen1,gen2} |
| `dotnet_gc_heap_total_allocated_bytes_total` | counter | Allocations seit Start |
| `dotnet_gc_pause_time_seconds_total` | counter | Akkumulierte GC-Pause-Zeit |
| `dotnet_gc_last_collection_heap_size_bytes` | gauge | Heap-Size bei letzter Collection |
| `dotnet_gc_last_collection_heap_fragmentation_size_bytes` | gauge | Fragmentation |
| `dotnet_gc_last_collection_memory_committed_size_bytes` | gauge | Commit-Size |
| `dotnet_jit_compiled_methods_total` | counter | JIT-Methoden-Count |
| `dotnet_jit_compilation_time_seconds_total` | counter | JIT-Compile-Zeit |
| `dotnet_jit_compiled_il_size_bytes_total` | counter | JIT-IL-Größe |
| `dotnet_thread_pool_thread_count_total` | counter (delta sample) | TP-Worker-Threads |
| `dotnet_thread_pool_queue_length_total` | counter | TP-Queue-Items pro Sample |
| `dotnet_thread_pool_work_item_count_total` | counter | Abgearbeitete TP-Items |
| `dotnet_monitor_lock_contentions_total` | counter | Lock-Contention |
| `dotnet_exceptions_total` | counter | Geworfene Exceptions |
| `dotnet_assembly_count` | gauge | Geladene Assemblies |
| `dotnet_timer_count` | gauge | Aktive Timers |

### Process (`OpenTelemetry.Instrumentation.Process`)

| Prometheus name | Type |
|---|---|
| `process_cpu_time_seconds_total` | counter (Label `cpu_mode` für user/system) |
| `process_cpu_count` | gauge |
| `process_memory_usage_bytes` | gauge (RSS/Working Set) |
| `process_memory_virtual_bytes` | gauge |
| `process_thread_count` | gauge |
| `dotnet_process_memory_working_set_bytes` | gauge (Duplikat aus runtime instrum.) |
| `dotnet_process_cpu_time_seconds_total` | counter (Label `cpu_mode`) |
| `dotnet_process_cpu_count` | gauge |

### ASP.NET Core (`OpenTelemetry.Instrumentation.AspNetCore`)

| Prometheus name | Type | Tags |
|---|---|---|
| `http_server_request_duration_seconds_*` | histogram | `http_request_method`, `http_response_status_code`, `http_route`, `network_protocol_version`, `url_scheme` |
| `http_server_active_requests` | gauge | (route, method) |
| `aspnetcore_routing_match_attempts_total` | counter | — |
| `kestrel_connection_duration_seconds_*` | histogram | (protocol/transport tags) |
| `kestrel_active_connections` | gauge | — |
| `kestrel_queued_connections` | gauge | — |

---

## CRITICAL: Falsche Namen in alten Dashboards

| Falsch (alte Dashboards) | Richtig (real exposed) |
|---|---|
| `nodepilot_execution_duration_bucket` | `nodepilot_execution_duration_milliseconds_bucket` |
| `nodepilot_step_duration_bucket` | `nodepilot_step_duration_milliseconds_bucket` |
| `nodepilot_winrm_session_open_duration_bucket` | `nodepilot_winrm_session_open_duration_milliseconds_bucket` |
| `nodepilot_winrm_script_duration_bucket` | `nodepilot_winrm_script_duration_milliseconds_bucket` |
| `nodepilot_trigger_orchestrator_sync_duration_bucket` | `nodepilot_trigger_orchestrator_sync_duration_milliseconds_bucket` |
| `nodepilot_execution_nodes_executed_bucket` | `nodepilot_execution_nodes_executed_bucket` (KORREKT — unit `1` hängt nichts an) |
| `process_cpu_seconds_total` | `process_cpu_time_seconds_total` |
| `process_working_set_bytes` | `process_memory_usage_bytes` |
| `dotnet_gc_heap_size_bytes` | `dotnet_gc_last_collection_heap_size_bytes` |
| `dotnet_gc_collections_count_total` | `dotnet_gc_collections_total` |
| `dotnet_gc_collections_count_total{generation=...}` | `dotnet_gc_collections_total{gc_heap_generation=...}` |
| `process_runtime_dotnet_thread_pool_queue_length` | `dotnet_thread_pool_queue_length_total` |
| `process_runtime_dotnet_thread_pool_threads_count` | `dotnet_thread_pool_thread_count_total` |
| `process_runtime_dotnet_thread_pool_completed_items_count_total` | `dotnet_thread_pool_work_item_count_total` |

Histogram-Suffix-Regel:
- Source-Unit `ms` → Prometheus-Suffix `_milliseconds`
- Source-Unit `1` oder leer → kein Unit-Suffix
- Source-Unit `s` → `_seconds` (HTTP-Histogramme)
- Source-Unit `By` → `_bytes`

Counter-Suffix:
- Counter immer `_total`
- UpDownCounter NIE `_total` (z.B. `nodepilot_executions_active`, `nodepilot_winrm_sessions_active`)

---

## Lazy Creation

OpenTelemetry erstellt Counter/Histogram-Time-Series erst beim **ersten Record**. Das heißt:
- Frischer API-Start → nur Trigger-Metriken (Orchestrator pollt jede 5s) + Process/Runtime/HTTP sind sofort da
- Engine-/Step-/WinRM-Metriken erscheinen erst nach dem ersten Workflow-Run
- Auth-/LLM-/Audit-Metriken erscheinen erst nach Login bzw. KI-Call

Folge fürs Dashboard: Alle PromQL-Queries die `sum(...)` ohne Default verwenden, sollten `or vector(0)` als Fallback bekommen, damit Stat-Panels nicht "No Data" zeigen.
