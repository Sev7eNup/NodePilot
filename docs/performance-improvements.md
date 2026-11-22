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
| `Engine:MaxScriptProcesses` | `cores * 4`; memory: 40% of the detected memory at 80 MB per process (15% in desktop operation) | 8 | `Engine:MaxConcurrentSteps` |
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

## Raising the limits by hand

Manual tuning lets more work run at once. It does not make the server faster. Measured with 500 parallel runs of a workflow with 118 steps on a server with 8 cores and 8 GB that also runs the database, in experimental mode:

| | Automatic sizing (256 steps, 24 runs at once) | By hand (600 steps, 600 runs at once) |
|---|---|---|
| Time until all 500 runs ended | 344 to 370 s | 330 to 354 s |
| Steps that ran into their 30-second timeout | none | 568 |
| Memory of the service at the peak | 1.6 GB | 4.1 GB, and 9.0 GB without step timeouts |
| Memory of the service five minutes later | 0.8 to 1.0 GB | 2.6 to 5.1 GB |
| Lowest memory left to SQL Server | 1.7 GB | 0.3 GB |

With experimental mode off the picture is the same at a slower pace: 1354 to 1380 s with automatic sizing and 1323 to 1384 s by hand, with a peak of 1.2 GB against 4.3 to 4.5 GB.

What this shows:

- **More steps than the CPU can run do not finish sooner.** Both settings needed about the same time.
- **A step's timeout counts the time on the clock.** Steps that run inside the service (file, registry, service and WMI activities) normally take milliseconds. With up to 600 steps sharing 8 cores they took so long that 568 of them ran into their `timeoutSeconds`, although nothing was wrong with them.
- **Every open run keeps the outputs of its steps in memory until it ends.** `ExecutionDispatch:WorkerCount` decides how many runs are open at once. The memory needed is about the number of open runs times the size of their outputs; a step with a large output, such as a directory listing or a query result, counts once for every open run. Without step timeouts the service wanted more memory than the server has.
- **The memory comes back with a delay.** Five minutes after the load the service still held 2.6 to 5.1 GB.
- **A database server on the same machine is pushed out.** While the service grew, Windows took SQL Server's memory away, from 2.9 to 0.3 GB. In the run without step timeouts NodePilot then paused its work 320 times because the database did not answer in time, and six runs ended early.

**The warning.** When `Engine:MaxConcurrentSteps` is set by hand to more than twice the value automatic sizing would use, NodePilot writes a warning to the server log at startup, and the admin settings show it at the field. The measurements above were taken at 2.3 times the automatic value.

**What to do.**

- Stay with automatic sizing unless a measurement shows that the limits hold the server back.
- Look at the CPU first. If it is at 100 % under the load, a higher limit brings nothing. Workflows that mostly wait, for remote machines, delays or conditions, are the ones that gain from higher limits.
- Raise the values in steps and compare the time for the same load.
- Plan memory for the open runs, and give a database server on the same machine a fixed minimum or a machine of its own.
- A time limit that has to hold under load belongs on the run, not on the single step.

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

A local script runs in a PowerShell process of its own; these processes are not part of the
memory budget above. In experimental mode NodePilot limits how many exist at once to four per core, and to what 40%
of the detected memory holds at 80 MB per process (15% in desktop operation); the smaller value
wins, with a lower bound of 8 and never more than `Engine:MaxConcurrentSteps`.
`Engine:MaxScriptProcesses` sets the limit by hand and, unlike the other values in the table
above, applies with and without manual tuning; the shipped `0` keeps the derived value.
Without a memory reading only the cores count. See
[When the script process limit is reached](#when-the-script-process-limit-is-reached).

The in-process runspace pool does not load implicit Windows PowerShell compatibility.
Eager pre-warm and shared FormatTables were rejected. The
[roadmap](roadmap.md) holds the binding blocking notes for tuning approaches
that are proposed again.

## Experimental mode

`Engine:ExperimentalMode` (admin settings under Performance, default off, takes effect after a restart) changes how local scripts run. Off, they run as they did up to version 1.5.4. On, two things change together:

- **The same command line for every script.** NodePilot starts one PowerShell process per local script. Up to 1.5.4 the start command carries the path of the script file, so every start has a command line of its own. Antivirus products look up each new command line, and the starts queue behind one another. In experimental mode the command line is always the same and the path travels in the environment variable `NODEPILOT_SCRIPT_PATH`.
- **A limit on script processes.** Because the starts no longer queue, the number of PowerShell processes that exist at once is limited; see *Automatic Sizing* above and *When the script process limit is reached* below.

Measured on one server with 8 cores and 8 GB that also runs the database, with Microsoft Defender and automatic sizing:

| | Off (as 1.5.4) | Experimental mode |
|---|---|---|
| 50 short scripts at once | 31 s | 5 s |
| 50 parallel runs of a workflow with 118 steps | 136 s | 37 s |
| 500 parallel runs of that workflow | 1259 s | 342 s |
| CPU load during the 500 runs, average | 24 % | 78 % |
| Execution list during the 500 runs, median / 95 % of the requests | 0.06 s / 0.08 s | 0.12 s / 1.2 s |
| Built-in file or registry step during the 500 runs, median | 0.09 s | 3 s |
| Script step during the 500 runs, median | 15.5 s | 1.9 s |

What this means:

- **Scripts finish three to six times sooner** as soon as several are due at once. On a server without internet access the difference is larger, because the antivirus lookup then takes about two seconds per start.
- **The mode uses the CPU that 1.5.4 leaves idle.** While a burst of scripts runs, the CPU is full, and the web interface, the built-in activities and a database on the same machine answer more slowly. No request and no step failed because of it in these tests. With few scripts in parallel the CPU is not full and nothing changes.
- **Rules of security products can match differently.** A rule that looks for the path of the script file in the decoded start command no longer finds it, and a product that judges command lines one by one judges one line for all scripts.
- **The switch is meant for a transition period** and will be removed in a later version.

**Keeping the web interface fast during bursts.** Set `Engine:MaxScriptProcesses` to the number of cores minus two. In the test with 500 runs the execution list then answered within 0.4 s for 95 % of the requests, and the runs took no longer (290 s). The price: scripts that mostly wait, for a remote call or another system, hold one of the few places while they wait. 100 scripts that each waited 30 seconds took 517 s instead of 153 s. Leave the derived value where scripts mostly wait.

## When the script process limit is reached

This section applies in experimental mode only.

NodePilot starts one PowerShell process for every local script: a `runScript` step with engine `auto`, `powershell` or `pwsh`, a custom activity, a script check of `waitForCondition`, `startProgram` with `useShellExecute`, and a local tool call of an AI agent. When as many of these processes run as the limit allows, the next script waits until one ends. Nothing fails because of the wait itself. Steps take longer, and that has these effects:

| What | Behaviour while a script waits for a process |
|---|---|
| Script step (`runScript`, custom activity) | Shows `Running` and has no output yet. Its own `timeoutSeconds` starts only when its process starts, so the step can take longer than that value. |
| `waitForCondition` in script mode | **Can run longer than its `timeoutSeconds`.** The wait before the first check does not count towards the timeout, so the step takes longer by as long as it waited for a process. From the first check on the timeout runs. A later check that gets no process before the timeout is over ends the step as timed out. The typed modes (`pathExists`, `serviceRunning`, `portOpen`, `httpOk`) need no process and are not affected. |
| Enclosing timeouts | Keep running during the wait: the timeout of the run, of a `startWorkflow` call that waits for its child workflow, and `timeoutSecondsPerItem` of `forEach`. A run with many waiting scripts can reach one of them. |
| Other steps | **A waiting script step keeps its place in `Engine:MaxConcurrentSteps`.** When many scripts wait, steps that need no script process can start later, also in other workflows, because the places are taken. This is deliberate: the waiting steps hold back the load on the database and on the in-process PowerShell pool. Giving the places up was measured and led to database connection timeouts under a burst. |

**How to recognise it.** The server log has the line `every script process is in use; the script waits for a free one` for each wait. `np settings effective-sizing` and the admin settings under Performance show the limit in force.

**What to do.** If the server has CPU and memory to spare, raise `Engine:MaxScriptProcesses`. Scripts that mostly wait for something else, such as a remote call, need little CPU, so a higher value suits them. If the server is at its limit, spread the load over time, set a concurrency limit on the workflows that start the bursts, or give the server more cores and memory. A time limit that has to hold under load belongs on the run, not on the single step.

## Measurement Evidence

Dated load tests, rejected variants, and root-cause analyses are in the
[archive of performance measurements](archived/performance-measurements.md).
The configurations there apply to the respective measurement state.
New comparisons need the same workload, comparable data volumes, and
multiple runs. A limited heap excerpt alone does not prove a memory cause.
