# GPT-6 Luna: repeat of unresolved SCCM scenarios

Date: 2026-09-23 (Europe/Berlin). **All seven requested scenarios were rerun through real published NodePilot agent-team workflows. None achieved end-to-end acceptance: six exhausted the model budget, one encountered a journal database error. TS6 and TS9 nevertheless contain newly successful exact cause/remedy findings in member/reviewer reports.**

## Configuration and method

The active development profile is `gpt-6-luna` through `https://api.openai.com/v1/responses`. Effective profile maximum output is 128,000 tokens, matching the [model documentation](https://developers.openai.com/api/docs/models/gpt-6-luna); the separate agent maximum remains 250,000. A real single-agent smoke run succeeded (`bd942353-d4ab-47c9-b722-0b49bb085439`). No model fallback was used.

These seven cases are the unresolved SCCM cases in `ai-agent-pinned-skill-sccm-retests.md`. The synthetic incomplete-source scenario subsequently passed in `ai-agent-runtime-robustness.md`, so it was not repeated here.

Each diagnosis uses a supervisor, CLIENT1/CM1 specialists and CLIENT1/CM1 reviewers. All 35 configured members explicitly select GPT-6 Luna and SCCM troubleshooting skill 1.0.0 (SHA256 `592e6b8a66751c4b5508f53092ff1cb99a7f1c91ebfb7be8f33d58d6a83fff17`). Budgets remain 100 model calls, 500 tools, 20 delegations. Instructions and symptom briefs are copied from the previous comparison, with current timestamps and resource identities. Only controller scripts inject/recover faults; agents remain read-only. No runtime or skill changes were made for this comparison.

Evidence: `.runlogs/agent-gpt6-luna-retests/` and `.runlogs/agent-gpt6-luna-cache/`. Earlier results remain preserved. Total: 682 model calls and 1,249 tool calls (including internal investigation/evidence/delegation tools).

## Results

| Case | Evidence and diagnostic outcome | Acceptance | Model/tool calls | Seconds |
|---|---|---|---:|---:|
| Advanced 4: WsusPool | Exact 50,000 KB limit + WAS 5117 found; remedy still lacks justified sizing. | Partial diagnosis; budget failure | 99/196 | 919.5 |
| Advanced 8: application detection | Actual install.cmd and installed registry marker found; no completed comparison with effective detection rule. | Unresolved; budget failure | 99/122 | 457.8 |
| Advanced 7: StateSys | Worker restarted before server inspection; team correctly found current Installed status. Exact queued message not proved. | Inconclusive comparison; journal database failure | 88/159 | 648.3 |
| Advanced 6: publisher trust | Exact CAB/update and trust-rejection phase found; missing TrustedPublisher entry not proved. | Unresolved; budget failure | 99/194 | 649.1 |
| TS5: missing cache file | Native package/hash failure and healthy server content established; missing required.txt not identified as cause. | Unresolved; budget failure | 99/158 | 525.7 |
| TS6: modified cache manifest | Client reviewer proved exact manifest.sha256 mismatch against version-5 DataLib and physical FileLib; targeted remedy and validation given. | Cause found; overall workflow failed at budget | 99/222 | 682.1 |
| TS9: corrupt DP bytes | Server measured exact corrupt physical FileLib payload versus source/DataLib; client reviewer confirmed causal chain and targeted remedy. | Cause found; overall workflow failed at budget | 99/198 | 470.5 |

Technical execution success, member-level diagnostic success and full team completion are distinct. Only the WsusPool run produced a saved final report; the other failures retain evidence and member reports but no full final report. In particular, TS6 and TS9 must not be advertised as successful overall workflow runs.

## Per-case details

Advanced 4 execution: `56dc2e6e-59a5-4c31-9a20-7dfb7f5c66f4`; run `25c9ac00-3ee4-44b8-bc9d-3e1a3b508492`. Controller restoration confirms WsusPool Started with original privateMemory 1,265,011 KB.

Advanced 8: failed after 99 model / 122 tool calls, 2 delegations, 457.8 s. Client evidence now includes the actual install.cmd and existing HKLM marker (Version=1.0), but no completed comparison/review or final report. Execution `3e50258d-25b7-4428-bc90-4097534d6f22`; run `1a657972-c159-4914-a6ee-70700445cb88`. Test application, collection, source and owned marker/cache removed by controller cleanup.

Advanced 7: technically failed (88 model / 159 tool calls, 7 delegations, 648.3 s). PostgreSQL error 22021: invalid UTF8 byte 0x00 while persisting AgentRunEvents. Execution `b3c8b8b5-7068-44cd-b8bf-09af1bd59c11`; run `9eddb345-c618-44fd-96d8-fa77973b6ed0`. The initial controller proof at 23:05:44Z had StateSys Stopped, three pending files and Status 2. SMS_EXECUTIVE restarted the worker at 23:07:43Z before the server specialist inspected it; the fallback recovery task had never run. A later controller read confirmed Running, empty queue, Status 3. The team correctly found the current resolved status but did not prove the exact queued message. This is not a valid pass/fail comparison of the original persistent backlog scenario; it also exposed a separate journal persistence defect.

Advanced 6: failed at 99 model / 194 tool calls, 5 delegations, 649.1 s. Exact CAB/update and trust-rejection phase found; current server CAB validly signed, but absent CLIENT1 TrustedPublisher entry not established. No completed final report. Execution `dfe7ace7-6e09-484b-bec4-e2e6ef3332e2`; run `c9686be8-347a-4741-98f2-b69f75587efa`. Controller recovery then downloaded the same update successfully (0x00000000, IsDownloaded=true), without installation; temporary publication, certificate trust and policies restored.

TS5: failed at 99 model / 158 tool calls, 3 delegations, 525.7 s, no final report. Current package/hash failure and original server hash established, but missing required.txt not identified as exact cause in the completed member reports. Execution `e97994d6-1bea-4cdd-8da7-bc5eddee566b`; run `90becd5d-a14e-4cad-bde8-1c49336477e1`. Controller restored the file; repeated TS succeeded.

TS6: **exact cause and targeted remedy found by client reviewer**, but full workflow failed at 99 model / 222 tool calls, 4 delegations, 682.1 s; no final report. Reviewer compared the actual manifest.sha256 cache file SHA256 60E05BD1B195AF2F94112FA7197A5C88289058840CE7C6DF9693756BC6250F55 with version-5 DataLib/physical FileLib SHA256 5847BAC69EFCD638869BCFFBD04D6EA12230DAC51B24C90C07CD93D8EAC3B4E2; payload.bin and required.txt matched. Reviewer explicitly corrected the earlier incomplete specialist report and proposed targeted ConfigMgr cache-entry renewal, followed by native hash/TS validation. Execution `c8ffe0f9-e1cf-4bbd-8dc0-9652622025b8`; run `04325fd5-09de-43e1-b4b1-817d0405649f`. Controller restored manifest; repeated TS succeeded. This is diagnostic progress, not end-to-end acceptance.

TS9: exact physical FileLib payload SHA256 E23878D9D9F675ABF5544836648ECD619987594B620CD45BA6E1AD064809EF9C versus expected/source 3AAD819EAC7DD3DD91C9C904A5E1D6280CC08A8036B02233A0B88B8A1BDBA220. Other package files match. Server specialist and client reviewer establish the same concrete fault and propose supported targeted ConfigMgr redistribution plus hash/TS validation. The server review/finalization cannot complete: 99 model / 198 tool calls, 4 delegations, 470.5 s. Execution `05feaa9d-5adc-4bfd-8db6-7380af41d09e`; run `7ba153a4-7b5b-44e3-a143-d9586026a191`. Controller restores payload, clears only the test package cache and verifies successful TS execution.

## Runtime and read-only audit

- All seven workflows plus smoke workflow are disabled; all executions are terminal. All 35 configured team members have the exact Luna model and pinned skill in the run context. This does not imply every member was reached before failure.
- 284 external tool calls. PowerShell AST review finds only read/query/selection/formatting commands, no invoked methods or dynamic commands. One syntactically malformed read was rejected. No Win32_Product or msiexec calls.
- Native event audit on both CLIENT1 and CM1 finds zero MsiInstaller events and zero WindowsUpdateClient installation events 19/20/21 during the agent execution windows. Controller changes outside those windows are separately authorized fault setup/recovery.
- 21 invalid investigation updates; 2 invalid PowerShell query combinations; 13 file-list failures; 6 policy rejections; 3 delegation failures. The six policy rejections concern read-only syntax (system-query pipelines, missing SimpleMatch, one unterminated quote), not attempted writes. These are visible policy/usability limitations; do not attribute every unresolved diagnosis solely to model capability.
- Persisted event sequences are contiguous. The StateSys persistence failure nevertheless loses the rejected batch: PostgreSQL 22021 `invalid byte sequence for encoding UTF8: 0x00` while inserting AgentRunEvents. This is a separate unresolved product defect, not fixed during the model comparison. Evidence: `state-journal-error.txt` and API log around 2026-09-23 01:16:33 +02:00.

## Recovery verified

- WsusPool Started, original privateMemory 1,265,011 KB; StateSys Running, backlog empty, target CI Status 3. Defender platform restored to 4.18.26080.4. No active fault-recovery snapshots/tasks remain.
- Test application, collection, source and owned cache/registry marker removed. Temporary publisher package, trust/policy/HTTPS changes removed. Restored publisher download succeeds (0x00000000, IsDownloaded=true), without installation.
- Each TS fault is followed by a successful restored run. DP payload hash and original package source/version verified. Test deployment removed, TS disabled. BITS/wuauserv returned to Manual/Stopped, CcmExec Automatic/Running.
- Temporary CLIENT1 adapter/firewall removed; host TrustedHosts restored to localhost. Existing installed NodePilot service untouched. Active development model remains GPT-6 Luna as requested.

## Remaining work exposed by this run

1. Keep useful exact findings through budget exhaustion and reserve enough capacity for required reviews/finalization; reduce repeated evidence reads and investigation-update errors.
2. Sanitize/reject PostgreSQL-incompatible null characters at the journal persistence boundary with regression coverage.
3. Stabilize the StateSys fault fixture before a definitive persistent-backlog retest; its worker restarted before inspection in this run.
4. Complete application detection, missing cache file and publisher-trust comparisons; finalize WsusPool remedy. TS6/TS9 need successful full team completion after the newly established exact diagnoses.

These are observations from one rerun per scenario, not a universal model ranking.
