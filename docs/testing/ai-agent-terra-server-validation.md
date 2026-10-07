# Terra server fault retests — 21 September 2026

Branch: `feature/ai-agent-activities`. This repeats original cases 7, 9 and 10
after their incomplete Luna investigations in
[the cache follow-up](ai-agent-cache-validation.md). The user explicitly requested
Terra for these three cases. No product code, skill or diagnostic instructions are
changed for this comparison.

## Protocol

The same five-member team uses `gpt-5.6-terra` for supervisor, client/server specialists
and client/server reviewers. Budgets remain 100 model calls, 500 tool calls,
20 delegations and 30 minutes; the per-call output ceiling is 250,000 tokens.
The global active profile remains Luna. Each case receives a fresh workflow and
sessions, the neutral task, target/task-sequence/package/deployment identities and
UTC investigation start. It receives no fault description, expected diagnosis or
controller oracle. Agents only use the existing read-only tools and skill 1.0.21.

The isolated task sequence CHQ0000B and package CHQ0000A are retained test assets.
New deployment CHQ2000D targets collection CHQ00015 containing only CLIENT1. Initial
content version is 3. A controller clears only that test package's cache entry to
force actual DP access. A fresh healthy download and complete task sequence succeeded
at 07:04:56 UTC before the first injection.

Case 7 temporarily moves the exclusively test-owned physical FileLib payload into
recovery storage. Case 9 changes one byte of that payload and retains the original
byte for restoration. Case 10 changes only the test package source to an absent
directory and attempts its distribution. Guard checks, recovery tasks, fault-presence
snapshots, restoration and healthy references are separate controller operations.
The test runner never treats a workflow's Succeeded status as diagnostic acceptance.

Raw scripts, traces and verification are in `.runlogs/agent-terra-server-validation/`.
Prior Luna and Terra cache traces remain unchanged in their original directory.

## Results

### Case 7 — missing physical DP file

67 model calls, 185 tool calls, 6 delegations, 463.9 seconds. **Substantially improved,
but not a complete evidence chain.** The team identifies the exact payload GET with
404, successful listing/manifest requests, and the physical FileLib directory with
only INI/SIG metadata and no payload object. It proposes scoped, supported validation
or redistribution of only the affected package on CM1. The current failure is
correlated across client and server; generic authentication/HTTPS failure is rejected.

The server reviewer catches an incorrect claim that the package-level DataLib INI
contains the payload hash. The team withdraws that claim but does not finish the
per-file metadata mapping, retaining physical absence as a strong indication rather
than completing its own demanded association proof. This is the remaining gap, not
a tool permission or budget failure: no tool call failed and 33 model calls remained.
Review also updates the now-terminal client status and separates the action timeout
from a proven hash mismatch. The report identifies the started TS action and timeout;
it does not establish successful execution of the intended hash checker.

Restoring the original physical object alone was followed by a fresh successful real
task-sequence run at 07:15:22 UTC. No IIS/authentication change was required.

[Case 7 Terra workflow](http://localhost:5173/workflows/f24568e7-d7d2-4473-8441-bae5fb621ae7)
— execution `9102a312-f747-48b3-885c-b81d916033f2`.

### Case 9 — corrupted physical DP file

70 model calls, 111 tool calls, 6 delegations, 471.5 seconds. **Incomplete causal
diagnosis.** Terra correctly correlates the current failed attempt, expected versus
downloaded aggregate ContentHash and `0x80091007`. Review resolves the CMTrace time
offset from independent original observations and avoids claiming the package's
own hash checker produced this failure. The proposed action is scoped validation
and conditional redistribution of CHQ0000A on CM1.

However, the team does not hash the actual corrupted physical FileLib payload.
It stops at uncertainty about DP representation versus delivery despite 30 model
calls remaining. Source/per-file metadata comparisons do not establish the bytes
of the stored DP object. This therefore does not identify the injected cause.

Three calls failed: two listings of absent client paths and a read-only CIM query
for `SMS_Advertisement` rejected by the class/namespace policy. That rejection is
a real tool-coverage limitation, but it does not explain the omitted physical-file
hash comparison; no such comparison was attempted and blocked. Do not attribute
the entire outcome exclusively to the model or exclusively to permissions.

[Case 9 Terra workflow](http://localhost:5173/workflows/b348220c-04fd-4e7a-ae51-99fd44f33ad2)
— execution `0a39cb18-1d17-4cb3-92a3-4722338d2537`.

Restoration and a fresh download were followed by a successful real task sequence
at 07:23:48 UTC.

### Case 10 — invalid package source

52 model calls, 142 tool calls, 7 delegations, 401.0 seconds. **Partially diagnosed.**
Terra identifies the exact bad source path and version-4 distribution failure,
with a scoped proposal to correct the source and redistribute. It correctly
classifies the new client attempt as WaitingContent before step execution and
excludes the previous successful task sequence from the current investigation.

The test setup matters: it removes the test package's DP distribution before
changing the source, and the client initially requests version 3 while the new
provider version is 4. The reviewers correctly refuse to treat the v4 source
failure as proof of the precise v3 location-selection decision. This is a compound
fixture, not a clean experiment showing that a source-path change alone removes
a previously healthy v3 location. Its outstanding causal linkage must not be
attributed entirely to model weakness.

Both server specialist and reviewer attempt a UNC Test-Path; both are rejected by
the local-path-only read policy. Neither establishes share-to-local-path mapping
and tests the corresponding local directory. Consequently, the report's phrase
"missing source path" is stronger than its independent observations: the observed
distmgr message says absent **or inaccessible**, with Win32 error 2. The exact
configured bad path is found, but absence versus access is not fully resolved by
the agents. A third failed call lists an absent client work directory.

[Case 10 Terra workflow](http://localhost:5173/workflows/5dae8559-e586-4a5a-9e9e-64cd17a59f39)
— execution `ba35caed-9923-4e1b-8bc2-736be9c7f2a0`.

## Comparison and safety audit

| Case | Terra diagnosis | Strict acceptance |
|---|---|---|
| 7: absent DP payload | Physical absence found; package-to-object proof left incomplete | Partial; improvement over Luna |
| 9: corrupted DP payload | Aggregate hash conflict found; physical DP bytes not hashed | Incomplete root cause |
| 10: bad source | Exact source and failed distribution found; absence/access and v3/v4 linkage unresolved | Partial; fixture caveat applies |

All three five-member runs used only `gpt-5.6-terra`; the global profile stayed
`gpt-5.6-luna`. All finished and their test workflows are disabled. No model or
tool budget was exhausted. These results do not support a claim that changing
models alone solves all remaining cases.

The 145 recorded PowerShell calls contain only read/query/format operations;
the native command is `netsh winhttp show proxy`. AST inspection finds no dynamic
command invocation, invoked member methods or parse errors. This includes rejected
calls, rather than silently excluding them. In all six machine/execution windows
(three runs on CLIENT1 and CM1), MsiInstaller events and WindowsUpdateClient events
19/20/21 are absent. This is specific observed test evidence, not a universal
guarantee about every possible future tool call.

Remaining general issues: complete physical-object comparisons before closing
diagnoses, usable read-only UNC/share access and SMS_Advertisement coverage, and
cleanly separated fault fixtures/version transitions. No error-specific product
rules were added in this comparison.

## Restoration and final state

The first case-10 reference remained attached to the existing WaitingContent
request and produced no fresh task sequence within 100 seconds. A second machine
policy retrieval and fresh reference succeeded at **07:37:48 UTC**. This controller
follow-up is recorded separately as `ts-restored10-policy-*`; the initial failed
reference is preserved. All three faults have a successful real post-restore run.

At 07:38 UTC cleanup verified the original source and physical payload SHA-256,
DP state 0 (now version 4), WsusPool started, WSUS/MP HTTP 200, original cache quota
20,480 MiB and WUServer port 8530. BITS/wuauserv are Manual/Stopped and CcmExec is
Automatic/Running. Recovery tasks/backups and fault rules are absent. Deployment
CHQ2000D is removed, the test task sequence disabled, and the retained test assets
remain available for inspection. The temporary CLIENT1 adapter/firewall are removed
and TrustedHosts restored to `localhost`.

No NodePilot execution remains active. API `/healthz/ready` and designer return
HTTP 200. Branch remains `feature/ai-agent-activities`; no installed-service restart,
product/skill change, commit or push was performed. Cleanup evidence is in
`cleanup-client.json`, `cleanup-server.json`, and `cleanup-host.json`.
