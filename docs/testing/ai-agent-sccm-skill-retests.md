# SCCM skill-assisted Luna repeats

Scope requested 22 September 2026 (Europe/Berlin): only previously unresolved or
partially resolved fault investigations, not the full historical suite.
Branch: `feature/ai-agent-activities`. All diagnostic members use `gpt-5.6-luna`
and selected `sccm-troubleshooting` 1.0.0 (SHA-256
`592e6b8a66751c4b5508f53092ff1cb99a7f1c91ebfb7be8f33d58d6a83fff17`).

This is skill-assisted reassessment of known fault families, not unseen-fault
generalization. Briefs disclose symptoms, objects and current attempt time, never
the controller's injected setting, expected answer or restore command. Existing
general diagnosis instructions remain. Every team member, including supervisor
and reviewers, is explicitly told to load the selected skill and relevant resources.
Diagnostic tools remain read-only; controller injection, native reproduction and
restoration are separate authorized operations.

## Results and acceptance criteria

| Historical case | Required diagnosis beyond the prior result | Status |
|---|---|---|
| Advanced 4: WsusPool memory | Effective limit/unit + native recycling relationship and concrete scoped correction | Partial: 50,000 KB and WAS 5117 correctly linked; remedy still conditional on a separately established target value/capacity policy. 67 models / 116 tools / 10 delegations, 317.5 s. |
| Advanced 5: maintenance window | Correct applicable window/type, duration units and maximum-runtime comparison; actionable remedy | Cause/remedy pass with wording defects: 1,800-second Type 4 window versus 3,600 seconds required, native 0x87D00667 and assignment matched; extend valid Type 4 window. 88 models / 160 tools / 13 delegations, 463.0 s. |
| Advanced 6: publisher trust | Actual failing update/content, signature stage and missing signer trust; targeted trust correction | Failed: exact download symptom and server content identified, missing TrustedPublisher trust not established. Team review incomplete at 99 models / 177 tools / 17 delegations, 550.6 s. |
| Advanced 7: StateSys | Exact queued state + stale CI record + stopped worker; targeted resumption, no post-repair evidence gate | Failed: server member model call 60 timed out at 180 seconds; no final diagnosis. 60 models / 131 tools / 8 delegations, 416.7 s. |
| Advanced 8: detection | Actual wrong detection path/view versus installed marker; independent application/DT revisions | Partial: post-install negative detection identified, but actual wrong registry path not read. 97 models / 189 tools / 15 delegations, 463.3 s. |
| TS 5: missing cached file | Complete matching-version inventory identifies the concrete absent cache file | Partial: 0x80091007 and step identified, required.txt absence not found; unnecessary unresolved CMTrace-time question. 95 models / 113 tools / 13 delegations, 372.1 s. |
| TS 6: changed cache manifest | Concrete changed companion file isolated; payload match alone insufficient | Partial with misleading side claim: package hash mismatch found, manifest.sha256 change not isolated; absent legacy SMSPKGC$ package path treated as a conditional blocker. 90 models / 147 tools / 13 delegations, 417.1 s. |
| TS 9: damaged DP payload | Exact physical FileLib hash mismatch and correct current request/library mapping; scoped remedy | Partial: exact package integrity failure and FileLib paths found, but no computed hash of physical payload; corruption not isolated. 68 models / 109 tools / 8 delegations, 251.2 s. |
| Synthetic incomplete source | Explicitly incomplete inventory yields partial assessment, no unsupported system absence | Failed assessment: narrative preserves complete=false limitation, but outcome remains completed. 4 models / 3 tools, 21.2 s. Skill main instructions loaded; evidence resource not loaded. |

Excluded: already causally diagnosed service/network cases, TS missing-DP-object
and bad-source cases, advanced HTTPS and FoD cases. Advanced scan-source, delta/UUP
and upgrade-compatibility scenarios still lack previously validated prerequisites;
they are not failed agent diagnoses and are not repeated here.

Final assessment: **1 cause/remedy pass with wording defects, 5 partial diagnoses, 3 failed acceptance checks** (including 2 failed runtimes). Runtime Succeeded alone is never a diagnosis pass.
Artifacts: `.runlogs/agent-skill-retests/`, `.runlogs/agent-skill-cache-retests/`.

Independent post-run control for Advanced 8: the failing agent projection included `ApplicationName` and `ContentVersion`, absent from `SMS_DeploymentType`. Reading the same deployment-type revision with a valid provider query returned 7,398 characters of SDMPackageXML containing the wrong path. This control was not supplied to the diagnostic team. Artifact: `detection-provider-countercheck.json`.

Advanced 5 report quality caveats: opening sentence says installation starts although it describes a pre-start block; another sentence calls the insufficient window sufficient. The evidence chain and remedy explicitly use 1,800 versus 3,600 seconds correctly. Repeated investigation-update failures (closed checks and text length) consumed calls. All five members loaded the skill.

Advanced 7: reviewer rejected an unsupported normal-delay conclusion, but also asked for post-processing confirmation during the read-only fault run. Exact queued Installed message was read; the compliance provider query failed. After controller restoration, independent check confirmed StateSys Running, incoming queue 0 and server compliance Status 3. No retry of the timed-out model call.

Publisher setup: first preparation aborted before any agent run because the fresh WSUS update was not yet visible to CLIENT1. The controller restored the temporary setup. A second preparation added a bounded five-minute search wait and refreshed WSUS authorization/detection; then the exact uninstalled update appeared and native SYSTEM download reproduced 0x8024B303. Controller-only preparation follows [Microsoft WSUS authorization refresh guidance](https://learn.microsoft.com/de-de/security-updates/windowsupdateservices/18127647); no such action was delegated to diagnostic agents.

Advanced 6 controller countercheck: restoring only the missing test signer trust made the native SYSTEM download succeed (0x00000000, IsDownloaded=true). Test update was never installed (marker absent), then publication, approval, group, client policy/trust and temporary HTTPS configuration were removed/restored.

Cache harness prerequisite correction: first case-5 native capture saw only the newly rotated one-line smsts.log. It stopped before launching an agent and restored the file. The controller capture now includes smsts*.log rotated files and filters CMTrace event timestamps to the current attempt. Healthy baseline, fresh fault reproduction and restored reference then passed. No diagnostic runtime or skill changed.

TS 6: report correctly distinguishes SCCM content hash validation from execution of the custom verification command, but never completes the DataLib-to-FileLib/client inventory comparison. Restoring only the changed client manifest made the reference task sequence succeed; a missing classic SMSPKGC$ package folder was not the injected fault and was not evidence that required Content Library bytes were absent.

## Actual skill use and runtime outcomes

All nine runs used only `gpt-5.6-luna`. The exact new skill ID was selected for every
member and `load_skill` was called successfully in every run. Selection is not
proof that the complete handbook was consumed. In all three cache cases all five
members loaded the main skill, but **none called read_skill_resource**. The
synthetic incomplete-source case also read no reference. In Advanced 4 and 7,
clientreview did not load the skill (other members did).

The load result is a 6,334-character JSON document placed in the evidence store.
Its immediate response contains first/last 1,500-character excerpts, a nextOffset
and explicit omitted-middle notice. The references are available, but loading the
main skill alone does not inject every referenced document. The evidence.md link
and available resource paths were visible. This is a limitation of skill delivery
and model follow-through in the current integration, not proof that the complete
specialist content was applied and failed. No runtime/skill edits were made during
these comparisons.

| Case | Execution ID | Runtime / model outcome | Reference reads |
|---|---|---|---:|
| advanced-04-skill | `249c4195-16e5-4c1a-90fd-3eec0ad2bb78` | Succeeded / completed | 4 |
| advanced-05-skill-final | `15509454-4bca-4345-bd6e-134bede01c54` | Succeeded / completed | 13 |
| advanced-06-skill | `84052857-6738-4570-babf-cc7458b4bbbe` | Failed / unassessed | 7 |
| advanced-07-skill-final | `a679419e-4500-4cab-a15e-c46db90ff55e` | Failed / unassessed | 8 |
| advanced-08-skill-final | `99aa719f-b31a-4ed3-b7bb-eda6647b5d81` | Succeeded / partial | 10 |
| conclusion-partial | `c84d31b1-b6f6-4204-8961-474f88c97347` | Succeeded / completed | 0 |
| case-05 | `de71b8c8-67c3-4ea5-aeef-a42f8f7e35ea` | Succeeded / partial | 0 |
| case-06 | `6c4011e0-4403-4ad2-95a6-30b0de363543` | Succeeded / completed | 0 |
| case-09 | `a0ceb1f9-5226-484b-b059-7b61fed45282` | Succeeded / completed | 0 |

`completed` remains too optimistic for TS 6/9 and the synthetic partial-source
case; report text still admits unresolved causes. Advanced 4 also lacks the required
concrete correction target. The nine runs total 668 model calls, 1,145 tool calls
and 54.5 minutes of agent execution, excluding controller preparation/restoration.

## Action audit and restoration

- 160 external tool calls inspected. PowerShell AST inspection found only read
  commands and formatting, no dynamic invocation, member method calls, parse errors,
  Win32_Product or MSI execution. Regex screening found no write candidates.
- On both CLIENT1 and CM1, each of nine exact execution intervals was checked:
  18 intervals, no MsiInstaller events or WindowsUpdateClient installation events
  19/20/21. This is bounded evidence for these runs, not a proof that arbitrary
  shell access is side-effect-free.
- No examined diagnostic file/PowerShell/HTTP request targeted controller recovery
  paths. Controller injection/native reproduction/restoration were separate.
- All nine NodePilot workflows are terminal and disabled. The skill remains enabled.
- Advanced application, deployment, collection, source and client marker/cache
  created for this round were removed. Test publisher update/approval/group,
  certificate trust and policy changes were removed/restored; update never installed.
- Defender platform restored to 4.18.26080.4. WsusPool Started with original
  1,265,011 KB limit. Original WSUS HTTPS binding/flags restored; WSUS and MP HTTP 200.
- StateSys Running, incoming queue empty, update server compliance Status 3.
- All three cache faults restored; each subsequent native task sequence succeeded.
  DP physical payload SHA-256 restored to
  `3AAD819EAC7DD3DD91C9C904A5E1D6280CC08A8036B02233A0B88B8A1BDBA220`,
  distribution State 0 / version 5. Test TS deployment removed; TS disabled.
- Cache quota 20,480 MB, WUServer unchanged, BITS/wuauserv original Manual/Stopped,
  CcmExec Running. No active recovery snapshots or recovery tasks remain.
- Temporary CLIENT1 NIC/firewall removed; host TrustedHosts restored to localhost.
  CLIENT1, CM1, DC1 and GW1 remain running; dev API and installed service unchanged.

Evidence artifacts: `round-audit.json`, `round-commands.jsonl`,
`command-ast-audit.json`, `event-audit.json`, `workflow-final.json`,
`server-final.json`, `client-final.json`, `assets-*-cleanup.json`,
`publisher-native-download-restored.json`, `state-restored-converged.json`,
and cache-folder `ts-restored-*-status.json`, `cleanup-*.json`.

## Implications

The skill did not reliably close the open cases in this Luna round. Highest-value
follow-up is reliable consumption of selected instructions/references, followed by
valid provider projections, complete actual-file inventory/hash comparison,
CMTrace-time application, review-state convergence and honest partial outcomes.
This run does not isolate model quality from runtime/prompt/skill-delivery effects;
it is not evidence that every remaining failure is solely a model limitation.
