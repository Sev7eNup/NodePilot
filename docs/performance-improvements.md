# Performance and Sizing

NodePilot sizes runspaces, parallel steps, ThreadPool, and dispatch workers
at startup based on the detected CPU and available memory.
`Performance:ManualTuning` is `false` by default. With `true`, the stored
values apply unchanged. Switching requires a service restart.

## Effective Settings

The admin UI distinguishes the selected and the running mode. The values actually
in effect and their limiting factor are reported by the process itself:

```powershell
np settings effective-sizing
```

The corresponding endpoint is `GET /api/admin/settings/effective-sizing`.
Under automatic sizing, the stored tuning values are inactive.
`Threading` can only be changed at runtime in manually started mode.

`Engine:MaxConcurrentExecutions:Global` and `PerUser` remain
configured safety limits in both modes. The shipped values are 5000 and 2000,
the code fallbacks for missing keys 500 and 200.

## Automatic Sizing

The CPU formula is bounded by the memory budget as well as lower and upper bounds.
The calculation lives in `PerformanceSizing`, the detection in `PerformancePlanFactory`.

| Setting | CPU formula | Lower bound | Auto upper bound |
|---|---|---:|---:|
| `Engine:Runspace:MaxRunspaces` | `min(64, max(8, cores * 4))` | 8 | 64 |
| `Engine:Runspace:MinRunspaces` | `MaxRunspaces / 8` | 1 | 8 |
| `Engine:MaxConcurrentSteps` | `cores * 32` | 32 | 600 |
| `Threading:MinWorkerThreads` and `MinIoCompletionThreads` | `max(200, cores * 16)` | 64 | 768 |
| `ExecutionDispatch:WorkerCount` | `cores * 3` | 20 | 200 |

The memory budget uses 60% of the detected memory in server operation and 25%
in desktop operation, minus a 512 MB base. Of this, 50% goes to runspaces,
25% to running steps, and 25% to reserve. The cost assumptions are conservative
estimates and not a measurement of the specific workload.

The detection takes process and job limits into account. Values below 1 GB are treated as
failed memory detection and lead to the CPU-based calculation.
The server-side DB pool is not part of this memory budget.

## Manual Tuning

The configured high-load preset was measured for 20 cores and up to 500 parallel
workflows. It is not a general default for smaller systems.
The current defaults are in [appsettings.json](../src/NodePilot.Api/appsettings.json)
and the [deployment templates](../deploy/templates/).

| Setting | Configured preset |
|---|---:|
| Dispatch workers | 600 |
| Parallel steps | 600 |
| Runspaces, minimum / maximum | 256 / 768 |
| Worker and I/O threads, minimum | 768 each |
| DB connection pool, minimum / maximum | 40 / 800 |

CPU, activity mix, remote latency, and database jointly determine throughput.
Before making changes, therefore measure the limiting area. Larger pools can
increase wait times and memory consumption without completing more work.

## Runtime Behavior

Waiting starts are stored durably in the DB outbox. `ExecutionDispatch:Capacity`
has been removed; `WorkerCount` determines the number of concurrently working dispatchers.
Workflow-specific limits are taken into account before the start.

`startWorkflow` and `forEach` share 128 slots for active child workflows by default.
Ancestors that are entirely waiting for children release their slot until they resume.
Active siblings continue to hold it. The global execution cap also counts waiting
ancestors, see [ADR 0018](adr/0018-active-subworkflow-capacity.md).

Dashboard and lists use precomputed statistics. The refresh state can
therefore lag behind live execution. When measuring, distinguish between database query,
API runtime, and rendering.

The in-process runspace pool does not load implicit Windows PowerShell compatibility.
Eager pre-warm and shared FormatTables were rejected. The
[roadmap](roadmap.md) holds the binding blocking notes for tuning approaches
that are proposed again.

## Measurement Evidence

Dated load tests, rejected variants, and root-cause analyses are in the
[archive of performance measurements](archived/performance-measurements.md).
The configurations there apply to the respective measurement state.
New comparisons need the same workload, comparable data volumes, and
multiple runs. A limited heap excerpt alone does not prove a memory cause.
