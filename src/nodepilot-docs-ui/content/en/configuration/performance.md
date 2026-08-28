# Performance sizing

NodePilot derives its concurrency limits at startup from the detected hardware. `Performance:ManualTuning` switches between that automatic sizing and the values configured verbatim.

| Key | Default | Effect |
|---|---|---|
| `Performance:ManualTuning` | `false` | `false` = derive the values from the detected CPU + RAM. `true` = take `Engine:Runspace:*`, `Engine:MaxConcurrentSteps`, `Threading:*` and `ExecutionDispatch:*` unchanged from the configuration. **Requires a restart.** |

The switch is deliberately not hot-reloadable: the runspace pool and dispatch worker pool are created once at boot. The plan is therefore built exactly once (`PerformancePlanFactory`) and read by every consumer — otherwise the hot-reloadable ThreadPool would run in one mode while boot-fixed consumers were still in the other.

## What the switch covers — and what it does not

Sized automatically:

`Engine:Runspace:MinRunspaces` · `Engine:Runspace:MaxRunspaces` · `Engine:MaxConcurrentSteps` · `Threading:MinWorkerThreads` · `Threading:MinIoCompletionThreads` · `ExecutionDispatch:WorkerCount`

**Excluded is `Engine:MaxConcurrentExecutions:*`** (`Global` / `PerUser`). That is a safety cap against pathological cases — trigger loops, sub-workflow cascades — not a throughput lever. Whoever sets a value means it; deriving it from the hardware would defuse precisely the barrier that was configured. The cap therefore applies in **both** modes.

With `ManualTuning` set to `false`, the numbers in the sections named above are an **inert preset** in `appsettings.json`: they remain readable but have no effect.

## What is actually in force

Because the configuration file does not tell the truth in automatic mode, there is a dedicated endpoint for this. For every value it also reports the **constraint** that produced it:

```
GET /api/admin/settings/effective-sizing
```

```powershell
np settings effective-sizing
```

| Constraint | Meaning |
|---|---|
| `Cpu` | The CPU formula was the smallest candidate |
| `Ram` | The memory sub-budget was the smallest candidate |
| `Floor` | The result was below the minimum sensible value |
| `Ceiling` | The result was above the range covered by measurements |
| `Manual` | Taken verbatim from the configuration (`ManualTuning: true`) |

The response also names the booted mode **and** the stored one. If they differ, the switch was flipped after startup and only takes effect after a restart.

## The model

**The CPU dimension.** `MaxRunspaces` = cores × 4, `MaxConcurrentSteps` = cores × 32, `Threading:*` = max(200, cores × 16), `ExecutionDispatch:WorkerCount` = cores × 3.

**The memory dimension.** A fixed base requirement of 512 MB is subtracted from the detected memory (runtime, EF model, caches, telemetry — measured idle footprint 383–444 MB, rounded up). Of the remainder, NodePilot claims **60 %** in server mode and **25 %** with `Deployment:Mode=Desktop`, because the desktop package shares the machine with Postgres, the Electron shell and the user's applications. That application budget is divided as **one household** — runspaces 50 %, steps 25 %, the rest is deliberate headroom for GC spikes. Computing each value separately against the full budget would spend the same memory several times over. Pending dispatch work lives in the database outbox and therefore has no in-memory queue-capacity setting.

The **smaller** of the two candidates wins, after which floors and ceilings apply. The memory dimension can therefore only shrink a plan, never grow it.

**Detection failed.** If the platform reports less than 1 GB, that counts as a failed measurement rather than a small machine — no supported host runs below 1 GB. Sizing then falls back to the pure CPU formula, and `effective-sizing` reports memory as not detected.

**Limits.** Floors keep a 2-core/4 GB machine usable; ceilings stop the extrapolation at the edge of the measured range.

| Value | Floor | Ceiling |
|---|---|---|
| `MinRunspaces` | 1 | 8 |
| `MaxRunspaces` | 8 | 64 |
| `MaxConcurrentSteps` | 32 | 600 |
| `Threading:*` | 64 | 768 |
| `ExecutionDispatch:WorkerCount` | 20 | 200 |

`MinRunspaces` always stays at **1** in automatic mode: `RunspacePool.Open()` materializes the minimum immediately, and eager warm-up is a measured anti-pattern (28 % regression). The pool grows under real load anyway.

## When the switch is worth using

The automatic sizing provides a **safe, monotonically scaling default with bounded resource risk** — not a universal optimum. The optimum additionally depends on the number of workflows, the activity mix, step runtime, remote latency and the database provider; none of that is known at boot. Automatic sizing therefore targets light to moderate load.

The measured high-load profile — 768 runspaces for 500 concurrent workflows on 20 cores — is deliberately reserved for manual mode, so that automatic sizing never silently scales it down. Anyone running that profile sets `Performance:ManualTuning: true` and takes the values from the load-profile table in [`docs/performance-improvements.md`](https://github.com/Sev7eNup/NodePilot/blob/main/docs/performance-improvements.md).
