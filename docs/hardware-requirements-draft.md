# DRAFT: Hardware requirements

Draft section for `src/nodepilot-docs-ui/content/en/deployment/production.md` (under
"Prerequisites", after "Target server") and a short pointer in the README prerequisites.
The "Derivation" part at the end is for review only and is removed before publishing.

---

## Hardware

Two reference configurations. Both assume the automatic sizing (`Performance:ManualTuning: false`,
the default), which derives runspaces, parallel steps and dispatch workers from the detected CPU
and RAM, see [Performance sizing](../configuration/performance).

| | Minimum | Recommended |
|---|---|---|
| Intended load | Evaluation, small team, up to about 10 workflows running at the same time | Production, up to about 50 workflows running at the same time |
| NodePilot server | 4 vCPU, 8 GB RAM | 8 vCPU, 16 GB RAM |
| Database | Same host as the server | Separate host (4 vCPU, 8 GB RAM) or a managed instance |
| Disk (server) | 60 GB SSD | 100 GB SSD |
| Disk (database) | 20 GB, shared with the server disk | 100 GB SSD, sized for your retention settings |
| Network | 1 Gbit/s to the database and to the target machines | same |

Notes:

- **Below the minimum.** A 2 vCPU / 4 GB host is the smallest machine the automatic sizing still
  handles. It is not recommended together with a database on the same host. No host with less than
  1 GB of RAM is supported.
- **Virtual machines.** Reserve the memory instead of using dynamic memory. NodePilot sizes itself
  once at startup from the detected CPU and RAM, so a later change needs a service restart.
- **Disk.** Application logs are capped at about 0.7 GB (7 files of 100 MB) and the support log at
  about 0.9 GB (90 files of 10 MB), see [Logging](../configuration/logging). Database growth depends
  on workflow count, step count and `Retention:Executions` (30 days by default), see
  [Retention services](../configuration/retention). Check the database size after the first weeks
  and adjust the disk.
- **Above the recommended size.** The high-load profile (about 500 workflows at the same time) was
  measured on 20 cores. It needs manual tuning, a dedicated database host and a server with at least
  32 GB of RAM. Take the values from the load-profile table in
  [Performance and Sizing](https://github.com/Sev7eNup/NodePilot/blob/main/docs/performance-improvements.md).
  With PostgreSQL, raise `max_connections` together with the application's DB pool.
- Check what is actually in force with `np settings effective-sizing`.

---

## Derivation (review only, not for publishing)

| Claim in the draft | Source |
|---|---|
| Idle footprint about 0.4 GB | `docs/archived/performance-measurements.md`: measured 383–444 MB, rounded to a 512 MB base. Desktop install flat at 216 MB after 24 h |
| Runspace cost 1.2–1.4 MB empty, about 8 MB under full load | same file, "Memory-Daumenregel RunspacePool" |
| Auto sizing at 4 / 8 / 16 cores | `PerformanceSizing` formulas: 4 cores → 16 runspaces, 128 steps, 20 workers; 8 cores → 32 / 256 / 24; 16 cores → 64 / 512 / 48. The 8-core and 16-core rows match the load-profile table (8-core / 20 workflows, 16-core / 50 workflows) |
| Minimum 4 vCPU / 8 GB | At 4 cores the plan needs roughly 0.4 GB base plus 16 × 8 MB runspaces plus steps, far below the 60 % memory budget of 8 GB. The rest covers the OS and PostgreSQL on the same host. The sizing floors target a 2-core / 4 GB machine (performance.md, "Limits") |
| Recommended 8 vCPU / 16 GB, database separate | 8 cores is the 20-workflow profile; the 16-core / 50-workflow profile still fits the 64-runspace auto ceiling. A shared host gets tight early: PostgreSQL with `max_connections=600` and `shared_buffers=512MB` can reach about 6.5 GB (capacity, not measured steady state), the application peaked at 5.4–6 GB working set at 200–500 workflows with the manual 768-runspace profile |
| 32 GB for the 500-workflow profile | Measured working set 5.4 GB (200 workflows) to about 6 GB (500 workflows) plus up to 6.5 GB PostgreSQL capacity plus OS headroom |
| Log caps 0.7 GB and 0.9 GB | `appsettings.json` (`RetainedFileCountLimit` 7 × `FileSizeLimitBytes` 100 MB; support log 90 × 10 MB) |

Not covered by measurements, so the draft states them as sizing guidance and not as guarantees:

- **Database disk growth.** No measurement of bytes per execution or per step exists in the repo.
  The disk figures are therefore a planning guess. One measurement (for example 500 executions of a
  114-step workflow, then table sizes) would settle it.
- **Small-host load tests.** All load tests ran on a 20-core machine with PostgreSQL. The 8-core and
  16-core rows of the load-profile table are tuning profiles, not separate load-test results, and the
  4-core minimum was not load-tested at all.
- **SQL Server.** Memory and throughput were measured with PostgreSQL. SQL Server by default takes
  as much memory as it is allowed to, so cap `max server memory` on a shared host.
- **Runspace-heavy vs. process-heavy mixes.** `runScript` runs as a Windows PowerShell 5.1 process by
  default (one process start per step), so scripts add per-process memory on top of the runspace
  figures above.
