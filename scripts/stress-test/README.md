# NodePilot Stress-Test Workflow

Artificial load workflow: fans out from `log-start` into **11 parallel branches** and collects them again with `junction-all` (`waitAll`). Uses only engine-local activities + `runScript` with `targetMachineId: "localhost"` (localhost bypass) — **no WinRM target needed**.

## Branches (all parallel)

| # | Activity | Workload |
|---|---|---|
| 1 | `runScript` | Prime sieve up to 2·10⁶ |
| 2 | `runScript` | Σ√i for i=1..5·10⁶ per pass |
| 3 | `runScript` | SHA-256 chained, in blocks of 50,000 |
| 4 | `runScript` | Regex matches on generated log lines, 25,000 per pass |
| 5 | `runScript` | 200×200 matrix multiplication |
| 6 | `runScript` | Sort 10⁶ integers |
| 7 | `jsonQuery` | `$.items[?(@.val > 50)].id` on a 20-item array |
| 8 | `jsonQuery` | `$..name` on a nested org structure |
| 9 | `xmlQuery` | `//host[@up='y']/@name` |
| 10 | `xmlQuery` | `sum(//host[@up='y']/@cpu)` (XPath aggregate) |
| 11 | `delay` | 55 s |

All 6 `runScript` branches run locally on the API host (`engine: auto` → one Windows PowerShell 5.1 process each) → 6 concurrent `powershell.exe` processes that keep ~all cores busy for a while.

## Import

The file is a plain workflow definition (not the export envelope). The launcher
signs in, creates or locks the workflow and publishes the definition
before it starts the parallel executions. Foreign edit locks and ambiguous
workflow names abort the run.

```powershell
./scripts/stress-test/launch-40x.ps1 -BaseUrl http://localhost:5000 -User admin
# PowerShell prompts for the required password.
```

Alternatively: `python scripts/stress-test/launch-40x.py`. The Python clients use
`NODEPILOT_URL` (default `http://localhost:5000`), `NODEPILOT_USER` (default `admin`)
and `NODEPILOT_PASSWORD`; without the password variable they prompt interactively.
`launch-50-master.py` starts an already published workflow by means of
`NODEPILOT_STRESS_WORKFLOW`. The launchers return an error exit code if a start
or an execution fails or if no success is established by the deadline.

The six CPU branches and the delay are designed for 55 seconds;
the total runtime is typically about 55–60 seconds. Live progress via SignalR.

## Warning

The 6 CPU-burn branches **put the API host CPU under full load**. On a dev machine that is harmless, but in production first keep an eye on at least the `Engine:Debug:*` limits and `Retention:*` services — and a running stress test does not block any other workflow execute, but backend response latencies rise visibly.
