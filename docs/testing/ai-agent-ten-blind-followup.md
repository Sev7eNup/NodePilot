# Follow-up: ten blind diagnoses after targeted corrections

Date: 2026-09-20. Branch: `feature/ai-agent-activities`.

Result: **10/10 injected configuration causes identified**, each with a scoped
proposed remedy and verification. All runs completed; lab restoration and the
20-window MSI/update-event audit passed.

Baseline: [ten blind scenarios](ai-agent-ten-blind-scenarios.md): five identified,
one partial, four missed causes. Technical workflow success was not counted as
diagnostic success.

## Changes under test

- Existing PowerShell tool: local IIS website, binding and application-pool state
  queries; fixed-executable `netsh winhttp show proxy` in PowerShell and CMD.
  Neither write operations nor alternate remote targets are authorized.
- Inline Get-Service Status/StartType are normalized to names before projection,
  filtering and JSON. Original packaged example scripts instead read textual
  Win32_Service State/StartMode; no script rewriting or execution-policy bypass.
- Runtime evidence instructions distinguish later blocking state from earlier
  successful probes. Reviewer instructions require checking decisive raw values.
- Existing optional Windows diagnostic skill, version 1.0.16, gains a general
  Windows HTTP resource: hosts/cache/DNS, system proxy, required services,
  IIS site/pool/binding/listener, scope, chronology and minimal proposed remedy.
  No per-fault skill or SCCM-specific technical agent role was introduced.

The initial regression run reproduced five failures: three IIS reads and the
WinHTTP read were rejected, and service JSON used numeric enums. The corrected
Windows PowerShell integration test checks actual JSON strings; negative tests
reject proxy writes, command chaining, output redirection and target overrides.
The full Engine agent test namespace passes **276 tests**; the AI project passes
**555 tests**. The API build passes without warnings/errors. Native IIS cmdlets were
also exercised on CM1 over WinRM before the diagnostic runs.

A further regression reproduces the misleading Access denied from searching a
directory. The final file-search implementation reports that a file is required and
directs the caller to files_list. This message-only correction was tested after
the live batch had started; recorded live errors retain the old wording.

## Live method

Evidence directory: ignored `.runlogs/agent-ten-fixed/`. Each fresh workflow has
the same five-member structure, symptom and 40-model/500-tool/20-delegation budgets
as the baseline. Specialists and reviewers now select the updated skill and can
load its resources. The active model remains `gpt-5.6-luna`, with the existing
250,000-character context ceiling. Original diagnostic-evidence
guidance remains in the member instructions as in the baseline.

The controller separately injects one real fault at a time and preserves the original
restore command plus a recovery watchdog. Agents receive the symptom and investigation
window, not the injected setting, restore command or expected diagnosis. No synthetic
log lines, forced scans, downloads or installations are used to create evidence.
For the changed-port case, the controller waits for listener propagation before
starting the workflow, avoiding a known setup timing ambiguity. It verifies the
fault still exists at completion, then restores and verifies the original state.

Old product log entries are deliberately retained. This is a repeat on the same lab
faults with improved software and knowledge, not a held-out benchmark or proof of
universal diagnostic accuracy. Skills improve question selection; they cannot replace
missing tool capabilities or make uncertain evidence conclusive.

The installed live-test package remains immutable at 1.0.16. The final 1.0.17 package
clarifies that DnsOnly controls protocol fallback (not the separate NoHostsFile
option), and identifies ItemXPath/Value as the useful pool-state fields plus the
single-name shape of Get-Website. The inlined DNS guidance received the corresponding
wording correction before cases 08–10. No oracle or case-specific remedy was added.

## Results

| Case | Injected cause and assessment | Seconds | Execution |
|---|---|---:|---|
| 01 | **BITS disabled/stopped**. Identified; restore intended BITS start configuration and verify a new transfer. | 262.1 | `30a1acbc-13c5-4473-acd6-0c7ad6c3d8e5` |
| 02 | **wuauserv disabled/stopped**. Identified; restore intended start/trigger mode and verify a new scan; no blanket Automatic recommendation. | 210.8 | `bcff3225-9061-46a3-a199-744c6c2e7020` |
| 03 | **CcmExec disabled/stopped**. Identified; restore/start CcmExec and verify new policy processing. | 153.5 | `730643db-4c22-4a08-a77c-1c7393399379` |
| 04 | **WsusPool stopped**. Identified despite running services/site/listener; restart only the affected pool subject to intended configuration. | 180.7 | `bd828a19-c579-4699-ab3a-845ab585f261` |
| 05 | **Default Web Site stopped**. Identified with binding present and port 80 absent; start the affected site and verify the original MP endpoint. | 176.3 | `5ae341ac-3877-42ba-9b03-60fe7fbeb3f3` |
| 06 | **SMS_EXECUTIVE disabled/stopped**. Identified; restore/start that service; historical client timeouts kept separate. | 259.0 | `9ac58e8c-2be1-49c0-a17d-36b1b59c13fe` |
| 07 | **CM1 hosts override to 127.0.0.2**. Identified from hosts/cache versus DNS; correct/remove the scoped override and verify the original endpoints. | 252.8 | `2ecdae60-c0e0-4b37-b276-0f1147013052` |
| 08 | **WinHTTP proxy 127.0.0.1:18765 without listener**. Identified current configuration blocker; restore intended direct/proxy configuration. Actual use by historical CM requests remains unproven. | 243.3 | `99392799-b430-4569-9163-3fae1d39c246` |
| 09 | **WSUS binding 18530 instead of 8530**. Identified client/server mismatch and missing 8530 listener; restore a matching 8530 binding subject to intended configuration. | 240.9 | `34ad3e8a-adee-493b-9703-28685689cb80` |
| 10 | **WsusService disabled/stopped**. Identified independently of running IIS; restore/start the service and verify WSUS processing. | 180.8 | `8146d41d-b533-4c09-a7ca-bc9c67554088` |

**10/10 injected configuration causes identified with an appropriate scoped remedy and
verification proposal**, versus 5 identified / 1 partial / 4 missed in the baseline.
This scores the current demonstrated blocking condition, not attribution of every
historical application request or the identity of the person who changed the system.
In particular, case 08 does not claim proof that each historic ConfigMgr request used
the dead proxy. No repair was executed by a diagnostic agent.

All ten runs succeeded and their workflows are disabled with histories retained.
Total agent execution time: **2160.2 seconds (36m 00s)**; **287
model calls**, **1039 tools**, including **60 delegations
and **434 PowerShell calls**. Maximum per-run usage was 37 model calls,
122 tools and 8 delegations, below the unchanged 40/500/20 budgets. No timeout,
hang or budget exhaustion occurred. The baseline took 2,150.8 seconds; this batch
is 9.4 seconds longer, with more direct state/configuration reads.

The logs contain 38 skill loads and 93 resource reads. Both attempts to run packaged
scripts were blocked by CLIENT1's Restricted execution policy before execution;
no policy override was introduced. The diagnostic knowledge remained usable and the
team completed the investigation with the selected native read tools.

## Remaining quality observations

There were **13 tool errors**: six guessed/missing log files, three file-tool path-scope
rejections, two policy-blocked skill scripts, one unsupported CIM namespace/class
combination, and one directory passed to file search. The rejected CIM query was
`root/ccm:SMS_Site`, not Win32_Product; the denial's generic message mentions that
prohibited class. These errors did not prevent the ten cause findings. The directory
message is corrected in the final build and covered by the new regression test.

Case 09 had two rejected attempts to close a review without the host's required new
observation. The team performed more reads and completed within budget. Review scope,
repeated inventory and treatment of uncertain log chronology still sometimes cause
extra work or overly cautious wording. Successful cause detection is not a claim that
every sentence of every long report is perfect. The retained journals expose these
limitations instead of counting a green workflow status as diagnostic proof.

## Restoration and read-behavior audit

The controller verified each injected fault was still present after its diagnostic run,
then restored and checked it. Final checks at **2026-09-20T18:51:36Z** confirmed the
original CM1 service states, started WsusPool/site, WSUS HTTP binding `:8530:`, and
**HTTP 200 from both MP and WSUS**. CLIENT1 service states/start modes, exact hosts
content and direct WinHTTP configuration were restored. Temporary CLIENT1 NIC,
firewall rule and recovery tasks were removed. TrustedHosts is back to `localhost`;
VM power states were preserved.

All **20 machine/execution windows** contain **zero MsiInstaller events and zero
WindowsUpdateClient installation events 19/20/21**. The 434 recorded PowerShell calls
contain no service mutation, scan trigger, CIM-method invocation, Win32_Product or
installation command. No agent read the controller's recovery task. Fault injection,
restoration and the controller's HTTP checks are separate from the diagnostic agents.
This is observed test evidence, not a universal proof of side-effect freedom.

Local artifacts: `metrics.json`, `raw-reports.md`, `shell-calls.json`, `tool-failures.json`,
`event-audit.json`, `fault-still-present-*.json`, `verified-restored-*.json`, and
`cleanup-client.json` / `cleanup-server.json` under `.runlogs/agent-ten-fixed/`.
