# SCCM troubleshooting skill: coverage and validation

Current source/package: **1.0.1**, adding exact active DeploymentType identity,
separate revision mapping and narrower queries after source truncation.
Artifact: `samples/agent-skills/sccm-troubleshooting-1.0.1.zip` (32,003 bytes).
SHA-256: `a2d871f922bd2287039508a848023d2bb909b7e106774010fbe633f2b4517f06`.
`AgentSkillTests.ShippedSccmRevisionMatchesItsValidatedSourceFiles` validates the
ZIP with the production archive reader and checks every file against source.
The original import/live validation records below concern **1.0.0**. Version 1.0.1
was subsequently deployed on CM1: the physical DP-content and active detection-path
retests both passed with normal completed reports. See
[the live regression results](ai-agent-hardcore-regressions.md#live-retest-on-cm1-2026-10-08-europeberlin).

Skill: `samples/agent-skills/sccm-troubleshooting`, version 1.0.0.
This is a new opt-in instruction package, not a change to the generic runtime or
the existing windows-diagnostics skill. It includes fault descriptions, discriminating
read checks, scoped administrative remedies and post-change success criteria.
No remediation scripts, lab identities, expected test hashes or controller artifacts
are shipped. Skill selection does not grant writes or additional target access.

The latest user request explicitly asks for domain troubleshooting knowledge. The
earlier restriction against hard-coded incident answers remains respected: the package
teaches verifiable mechanisms, not expected answers for numbered lab executions.
Future tests using this package are skill-assisted and must be labelled accordingly;
they are not a blind holdout evaluation of previously unseen fault families.

## Complete fault-family mapping

Numbering belongs to this handbook, not to any earlier test round. Repeated runs
of the same mechanism are grouped. "Live" means the mechanism was exercised, not
that every agent run diagnosed it correctly. "Adjacent" covers observed preparation
failures or differential checks, not a scored injected-fault success.

| ID | Covered mechanism / symptom | Historical coverage | Skill reference |
|---|---|---|---|
| 01 | BITS disabled/stopped blocks applicable transfer | Ten-service round 1 | services |
| 02 | wuauserv disabled/stopped blocks update operation | Ten-service 2; TS 1; initial unsuccessful injection distinguished | services |
| 03 | CcmExec disabled/stopped | Original WinRM test; ten-service 3 | services |
| 04 | SMS_EXECUTIVE disabled/stopped | Ten-service 6 | services |
| 05 | WsusService disabled/stopped | Ten-service 10 | services |
| 06 | Incomplete client installation versus missing field/access error | Diagnosis fixtures 1, 3, 5; actual false installation-health claims | services |
| 07 | DNS/hosts override to wrong address | Ten-service 7 and follow-up | network |
| 08 | Dead/misconfigured WinHTTP proxy | Ten-service 8 and follow-up | network |
| 09 | Active outbound firewall block / route distinction | Five-case scan/policy; TS 4; route-read follow-ups | network |
| 10 | Stopped MP website/application | Ten-service 5 and follow-up | network |
| 11 | Client/SUP URL/port versus actual binding/listener mismatch | Ten-service 9; TS 3 and repeats | wsus-iis |
| 12 | Stopped WsusPool | Ten-service 4; TS 2 and repeats | wsus-iis |
| 13 | WsusPool memory limit causes recycling and 503 | Advanced 4 and repeat | wsus-iis |
| 14 | Erroneous HTTPS client-certificate requirement; TLS differential | Advanced 3 and repeat | wsus-iis |
| 15 | Failed software-update scan and concrete underlying layer | Five-case 1; Unknown follow-ups | updates |
| 16 | Per-update-class scan-source policy conflict | Advanced 1: prepared, effect not proved, not scored | updates |
| 17 | Unknown compliance / absent scan or report mapping | Five-case 2; real-update and Unknown repeats | updates |
| 18 | Required update waiting for future deadline/availability | Real-update Required run and time corrections | updates |
| 19 | Wrong/missing/insufficient maintenance window versus maximum runtime | Advanced 5 and repeats | updates |
| 20 | Applicability, target assignment, expiration/supersedence distinctions | Update baseline controls and CI lifecycle follow-ups | updates |
| 21 | Installed locally, stale server compliance; stopped StateSys | Advanced 7 and repeats | state-policy |
| 22 | Removed/missing package distribution | Five-case 4; server-content; combined test; TS 8 | distribution |
| 23 | Boundary/group/eligible-DP mismatch differential | Server-content checks; not a separate injected boundary fault | distribution |
| 24 | Invalid/missing/inaccessible source path | TS 10; read/identity and model repeats | distribution |
| 25 | Missing required client-cache file | TS 5; cache/inventory/Luna/Terra repeats | content-integrity |
| 26 | Altered cached manifest or companion file | TS 6; cache/inventory/Luna/Terra repeats | content-integrity |
| 27 | Missing physical DP FileLib object | TS 7; context/object and model repeats | content-integrity |
| 28 | Corrupted physical DP FileLib payload | TS 9; Luna/Terra and physical hash repeats | content-integrity |
| 29 | Transfer/native hash versus script hash; stale request/fallback | TS content phases; five-case stale handle; healthy fallback controls | content-integrity |
| 30 | Delta/UUP-only transfer failure | Advanced 2: missing applicable baseline, not live-tested | content-integrity |
| 31 | Missing/stale machine policies | Five-case 5; healthy follow-ups | state-policy |
| 32 | Installer exit success but wrong registry detection | Advanced 8 and lazy-property/revision repeats | application |
| 33 | Task-sequence prerequisite/content/custom-step failure | Full six-step test TS, ten faults and healthy reference runs | application |
| 34 | Third-party update signing/publisher trust failure | Advanced 6, native restored download control and repeats | application |
| 35 | Missing/incompatible servicing/FoD source | Advanced 10; FoD variant live, not every CBS repair mode | servicing |
| 36 | Feature-upgrade compatibility blocker | Advanced 9: suitable media absent, not live-tested | servicing |
| 37 | Incomplete/cancelled metadata sync / unusable catalog | Five-case preparation, WSUS/SUP imports and applicability controls | servicing |
| 38 | SQL/WID memory competition / RESOURCE_SEMAPHORE | Observed lab preparation obstacle and correction | servicing |
| 39 | Gateway/DNS outage or evaluation-triggered VM shutdown | Observed infrastructure preparation failures | servicing |
| 40 | Historical error versus current healthy same-scope outcome | Healthy scan/policy/content/TS controls; diagnosis fixtures 6–10 | evidence |

All skill references are directly linked from SKILL.md. The three unscored advanced
scenarios remain marked as such here; including guidance does not turn them into passes.

## Cross-cutting test lessons included

| Test distinction | Instruction |
|---|---|
| Missing projected version vs actual installation damage | Schema/alternate source and required-file contract, services 06 |
| Disabled service vs installation damage; unknown actor | Mechanism, identity and attribution separated |
| Denied/failed/truncated query vs missing/corrupt system | Evidence limitations preserved |
| Current endpoint mismatch vs old DB errors | Same-operation/object/time correlation |
| Later relevant success vs unrelated success | Healthy-state scope, evidence 40 |
| Empty transient TCP snapshot | Not proof of connection failure |
| Filtered empty result vs successful same-scope full query | Schema and bounded cross-check |
| Unknown timezone, explicit offsets, DST overlap/gap, legacy dates | Raw timestamps and uncertainty retained |
| 250 MB, UTF encodings, long lines, append/rotation/truncation | Bounded collection/search, source recovery and continuity limits |
| Complete inventory with absent item vs reordered equal inventory | Set comparison by stable key |
| Incomplete or inaccessible source | Partial/blocked, not invented absence or global health |
| Missing cache companion vs matching large payload | Complete file membership and individual hashes |
| Package aggregate vs per-file hash; metadata vs physical bytes | No invalid hash comparison |
| Lazy property, unsupported class, wrong target | Instance retrieval/schema/explicit target binding |
| Application revision vs DeploymentType revision | Separate object versions |
| Review loops and closed investigation edits | Discriminating reads; bounded conclusion; no post-repair gate |
| Script blocked by execution policy; Win32_Product side effects | Instruction-only skill; no bypass/MSI queries |
| Technical Succeeded vs complete correct diagnosis | Explicit conclusion category and unverified success checks |

## Historical source inventory

- [Original WinRM acceptance](ai-agent-winrm-acceptance.md), [remote skill tests](ai-agent-skill-acceptance.md).
- [Five SCCM scenarios](ai-agent-sccm-five-scenarios.md), [cause follow-ups](ai-agent-cause-followup.md).
- [Real update validation](ai-agent-update-validation.md), [Unknown follow-ups](ai-agent-unknown-followup.md), [review/time correction](ai-agent-review-time-fix.md).
- [Server distribution](ai-agent-server-content.md), [combined content and 250 MB logs](ai-agent-content-and-remote-logs.md).
- [Ten service/network scenarios](ai-agent-ten-blind-scenarios.md), [their follow-ups](ai-agent-ten-blind-followup.md).
- [Ten task-sequence scenarios](ai-agent-tasksequence-blind-tests.md), [follow-up](ai-agent-tasksequence-followup.md).
- [Cache validation](ai-agent-cache-validation.md), [Terra server comparison](ai-agent-terra-server-validation.md), [case 7 context/object](ai-agent-case7-context-validation.md), [Luna 5/6/9](ai-agent-luna-569-validation.md).
- [Read/budget validation](ai-agent-read-budget-validation.md), [investigation register](ai-agent-investigation-register.md), [Luna investigation](ai-agent-luna-investigation-validation.md).
- [Diagnosis fixtures](ai-agent-diagnosis-quality.md), [time fixtures](ai-agent-time-acceptance.md), [final/source-completeness tests](ai-agent-final-results-validation.md).
- [Advanced ten scenarios](ai-agent-advanced-ten-scenarios.md), [latest diagnostic improvements](ai-agent-advanced-diagnostic-improvements.md).

Authoritative product references are linked next to the relevant instructions in
the package. Version-specific behavior must still be checked on the actual target.

## Package validation

Validated against the Agent Skills frontmatter/directory conventions and imported
through NodePilot's actual AgentSkillArchive-backed API. The package contains 13
Markdown files: 88-line SKILL.md and 12 directly linked references. Every resource
is below 8,192 UTF-8 bytes (largest: 6,731 bytes), all local links resolve, and IDs
01–40 each occur exactly once as a fault-family heading. No lab identifiers or
credentials are included. Archive size: 31,340 bytes.

Artifact: `samples/agent-skills/sccm-troubleshooting-1.0.0.zip`.
SHA-256: `592e6b8a66751c4b5508f53092ff1cb99a7f1c91ebfb7be8f33d58d6a83fff17`.
Dev registry skill ID: `029cca00-170d-445f-ad83-050667e4ae24`, enabled.
No existing workflow skill selection was changed.

Two instruction-only aiAgent runs used gpt-5.6-luna, no target binding, no service
identity and no external/system tools:

| Probe | Result |
|---|---|
| Read the entire handbook and summarize all 40 patterns | All 12 references and SKILL.md were read without tool errors. Final model call 11 exceeded the 180-second call limit; execution Failed after 224.9 seconds, 18 tools. No automatic retry. |
| Focused maintenance-window/revision guidance | Succeeded in 19.0 seconds, 8 model calls / 13 tools. Correctly distinguishes Type 1/4/5/6, seconds versus minutes, and independent Application/DeploymentType revisions. It redundantly reread the two references; this remains a model-efficiency limitation. |

Executions: `e8963116-144b-41a3-8f05-2db9303ffaeb` (full-handbook timeout),
`3000ddec-50f3-4796-814d-a21abef1dc65` (focused pass). Both workflows are disabled
and executions terminal. The successful probe's complete answer cites
references/updates.md and references/application.md. Local verification artifacts
are under `.runlogs/sccm-skill/`.

These checks establish import/resource availability and a bounded instruction-use
example, not correctness of every future diagnosis or a replay of the lab suite.
Neither CLIENT1 nor CM1 was accessed or modified for this skill validation. Existing
read-only host enforcement and generic agent runtime remain unchanged.
