---
name: windows-diagnostics
description: >-
  Investigate Windows Update, component servicing and Configuration Manager failures
  using bounded log excerpts and explicit source evidence.
---

# Windows diagnosis

For software update compliance, scans or deployment timing, read
`resources/software-updates.md` for the distinct client/server keys and evidence sources.

For an assigned Configuration Manager server, read `resources/server-content.md`
when investigating content distribution. Client-only members ask the supervisor for
an explicitly bound server member instead of changing their own target.
For a content discrepancy, use that resource's representation map to exchange
comparable inventories between server and client. The client establishes the exact
cache location from its current records/logs, lists that directory's actual files,
and compares them with the deployed version's expected inventory. Status records
alone are not a file inspection. Do not choose a cause before this comparison.

For endpoint, name resolution, proxy or web-server failures, read
`resources/windows-http.md`. It separates client configuration, service state and
IIS runtime state instead of treating a running Windows service as web health.

Use this skill when the assigned task concerns Windows Update, CBS/component servicing,
or Microsoft Configuration Manager. It supplies a method; permissions come exclusively
from the agent's selected tools and target identity.

1. Establish the symptom, failing operation, time range, machine and relevant error code.
   Read `resources/diagnostic-evidence.md` before choosing queries or interpreting missing
   fields. Use the smallest read-only check that separates plausible explanations.
2. If a service baseline is needed, `scripts/inspect.ps1` reads Windows Update service
   states and `scripts/inspect-sccm.ps1` reads the ConfigMgr service. Their CIM State
   and StartMode fields are text, not ServiceController enum numbers. A missing
   service is an observation, not proof of a damaged installation. These scripts use the
   supported literal read language. If target execution policy blocks a script, report
   the limitation; do not retry its contents inline or through another shell.
3. Read `resources/log-locations.md`. Use target-side `files_search` to narrow the time
   window and error. Collect only needed logs using `logs_collect`; share artifact IDs
   with a supervisor/reviewer. All members share the 250 MB collection quota.
4. Use `logs_search` or `files_read` for bounded evidence. Correlate timestamps and error
   codes across sources. A large count of errors alone is not proof of the root cause.
   Check current counterevidence before promoting a hypothesis. A version lookup or a
   running service is only that observation, not proof of complete client health.
5. Explain findings with file paths and line references. Distinguish observed evidence,
   hypotheses, missing access and recommended next steps. Do not claim a repair occurred
   without a successful tool result. All agent actions are currently read-only, even
   when the task requests a repair. Report proposed repairs without executing them.

If a specialist lacks a necessary task detail, return `needs_input` and a precise question
to the supervisor. Preserve contradictions and unresolved checks in that handoff. If access
or tools are missing, state the limitation. Never request or
print credential secrets, and do not alter tool selection, target or credentials.
