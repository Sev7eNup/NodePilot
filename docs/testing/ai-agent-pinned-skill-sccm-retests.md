# SCCM repeats after skill delivery correction

Date: 2026-09-22. Seven previously incomplete SCCM/WSUS/content cases on
CLIENT1 and CM1. The previously diagnosed maintenance-window case and the
non-SCCM synthetic incomplete-source case are excluded.

All diagnostic members use `gpt-5.6-luna` and selected `sccm-troubleshooting`
1.0.0, SHA-256 `592e6b8a66751c4b5508f53092ff1cb99a7f1c91ebfb7be8f33d58d6a83fff17`.
The corrected runtime pins complete main instructions, returns bounded complete
reference pages and excludes skill reading from fresh target evidence.
Diagnostic agents remain read-only. The controller separately injects, reproduces
and restores each fault. Symptom briefs do not reveal the injected change.

Comparison: [previous skill-assisted round](ai-agent-sccm-skill-retests.md).
Successful workflow execution alone does not mean a causal diagnosis passed.

| Case | Acceptance | Result |
|---|---|---|
| Advanced 4 — WsusPool | Effective memory/unit, native recycling linkage, scoped remedy | Partial: 50,000 KB and WAS 5117 linked; remedy explicitly offers 0 or a suitable higher limit but no justified definite target. 50 model / 100 tool calls, 372.9 s. |
| Advanced 8 — application detection | Actual incorrect detection path/view versus installed marker | Partial evidence; runtime failed with incomplete client review. Exact detection path found, but no comparison with installer/actual marker; installer versus detection remains unresolved. 99 model / 144 tool calls, 653.1 s. |
| Advanced 7 — StateSys | Exact stale CI, queued installed message, stopped worker and remedy | Partial: stopped worker, exact stale CI and targeted remedy identified. Reads `statmgr.box/statmsgs` instead of `auth/statesys.box/incoming`, missing the queued message. Runtime completed with partial outcome. 81 model / 128 tool calls, 585.2 s. |
| Advanced 6 — publisher trust | Exact update/signature failure and missing signer trust | Partial evidence; runtime exhausted model budget. Correct CAB/update and signature phase; current server hashes/signature verified, but missing TrustedPublisher trust not established. Historical-copy uncertainty dominates. 99 model / 173 tool calls, 810.2 s. |
| TS 5 — missing cache file | Complete matching-version comparison identifies required.txt | Cause identified: `required.txt` absent; other two cache files match expected SHA256. Report still fails clean acceptance: historic MissingSource path promoted to a current blocker, with an unnecessary source-repair recommendation. 80 model / 130 tool calls, 503.9 s. |
| TS 6 — modified cache manifest | Concrete manifest.sha256 deviation distinguished from payload | Partial: native package hash mismatch found; concrete manifest deviation not isolated. Runtime reports completed despite this gap. 63 model / 125 tool calls, 416.4 s. |
| TS 9 — corrupted DP bytes | Physical FileLib payload hash mismatch and actual request mapping | Partial: physical FileLib objects located, but no `Get-FileHash` attempt. Agent incorrectly claims no approved hash path is available. Corrupted payload not isolated. 79 model / 130 tool calls, 537.0 s. |

Artifacts: `.runlogs/agent-skill-pinned-retests/` and
`.runlogs/agent-skill-pinned-cache/`. Connection setup's first probe failed while
the temporary NIC became reachable; the subsequent NodePilot probes to both targets
succeeded before fault injection. Existing administrator budgets and model settings
remain unchanged. No feature-code changes were made during this comparison.

## Interpretation

The skill delivery correction works, but this round does not establish complete
acceptance of any of the seven previously incomplete reports. TS5 now identifies
the exact missing file; its additional repair advice is wrong. StateSys identifies
the injected worker stop and a targeted remedy, but misses the actual inbox.
Application detection gets further than the previous round by reading the exact
deployment-type XML. The publisher case now verifies actual server hashes and
signature. TS6 and TS9 still stop short of the decisive file comparison.

These are single runs, not a controlled model comparison. They do not establish
that every remaining gap is caused solely by Luna. Runtime/tool usability also
needs attention: 22 rejected updates to already closed investigation checks,
schema-limit/required-field mistakes and repeated review handoffs consume calls.
The application run ends with `Team review incomplete`; publisher ends with
`Model call budget exhausted`. TS5 and TS6 demonstrate that runtime `completed`
does not certify a fully correct causal report.

For TS5, the server reviewer uses an old `distmgr.log` MissingSource entry and a
current absence check of that old path without checking the current package
configuration. Independent controller observation at 19:20:37Z confirms
`PkgSourcePath=\\CM1.corp.contoso.com\Packages$\NP-TaskSequence-20260920`, version 5.
Final cleanup confirms the same source. This is a false current blocker, not an
additional lab fault. The observation was not supplied to the diagnosing team.

TS9 makes no hash call and receives no hash-policy denial. PowerShell is selected,
and `AgentPermissionPolicy` explicitly permits `Get-FileHash`. Do not describe its
unsupported-access claim as an established permissions restriction. The only two
host read-policy rejections in this round are WsusPool client event queries using
`-LogName` together with a `-FilterHashtable` lacking its own `LogName`; the
diagnostic error says a log name is required. Query construction/error recovery
needs improvement; this is not evidence that all event reads are unavailable.

## Delivery, actions and restoration audit

- All 35 member contexts pin the selected skill and use `gpt-5.6-luna`.
- All 59 completed reference reads are complete JSON pages from offset 0 with
  `hasMore=false` and the expected package SHA256. Unlike the previous round,
  cache cases read references (5 / 8 / 6 reads for TS5 / TS6 / TS9).
- Event sequences are contiguous for every run. No logged native/file tool call
  targets controller recovery paths or local test-orchestration artifacts.
- 164 native tool requests were inspected. PowerShell ASTs contain read/query and
  formatting commands only, with no invoked methods, dynamic commands or parse
  errors. No `Win32_Product`, MSI, repair or write request was found.
- Exact execution-window event checks on both machines (14 windows) contain no
  MsiInstaller events and no WindowsUpdateClient installation events 19/20/21.
  This supports the observed read-only behavior; it is not a general proof of
  absence of every possible side effect.
- WsusPool restored to Started / 1,265,011 KB; StateSys Running, inbox empty,
  exact compliance status 3; Defender platform restored to 4.18.26080.4.
- Publisher trust restoration makes the native SYSTEM download succeed with
  `0x00000000`, `downloaded=true`; no installation marker. Temporary publication,
  trust/policy and HTTPS changes removed/restored. Test application, collection,
  source, registry marker and owned application cache removed.
- Healthy baseline and all three restored task-sequence runs succeed. All three
  injected runs fail freshly with native `80091007`. Original DP payload SHA256
  restored; distribution state 0 / source version 5. MP and WSUS HTTP checks 200.
- Task-sequence deployment removed; sequence disabled. Temporary CLIENT1 NIC and
  firewall access removed; host TrustedHosts restored to `localhost`. BITS and
  wuauserv restored to Manual/Stopped, CcmExec Automatic/Running.
- All seven NodePilot test workflows disabled and executions terminal. Lab VMs,
  installed NodePilot service and development API remain running.

Audit files: `round-audit.json`, `delivery-and-failure-audit.json`,
`round-commands.jsonl`, `command-ast-audit.json`, `event-audit.json`,
`server-final.json`, `client-final.json` in the primary artifact directory;
`current-package-source.json`, `ts-*-status.json`, `cleanup-*.json` in the cache
artifact directory. Fault proofs, restoration proofs, reports and full event
journals remain alongside them.

## Run identifiers

| Case | Execution ID | Agent run ID |
|---|---|---|
| Advanced 4 | `575fa2d3-2279-45f1-83bc-b9feece0a9f5` | `9606fb34-35d2-4e17-b124-45447dd52859` |
| Advanced 8 | `bc34cef0-a2cd-4f10-b7b2-ce1e4368c49c` | `691e12c7-8cbd-4440-8af9-41b464304692` |
| Advanced 7 | `ab3822ae-b259-4462-8135-d7fd7e423aee` | `8a041473-d211-447b-afdf-359988b543cd` |
| Advanced 6 | `42737ddb-e432-429d-a9f7-9a88132f5517` | `ef9807dc-c703-42ad-890b-ca23f5645d0b` |
| TS 5 | `ca92c5d2-9e76-41cc-abf9-17d56fe94210` | `7a596541-c957-4276-ae92-853bd1e2a4aa` |
| TS 6 | `59063b3b-2c99-42ba-b7cf-1a0e55c834f4` | `711e9eed-c6ab-4c55-9f2c-3b7ac2a99cef` |
| TS 9 | `408ab4ac-cc82-46ff-9f25-d48805f165ae` | `77b35323-25d9-456c-86db-b81fead18843` |
