# AI agent activities

`aiAgent` runs a general-purpose agent inside one workflow step. `aiAgentTeam` gives
one supervisor a visible team of specialists working in bounded parallel batches. `llmQuery` remains a single
model request; agents can choose tools, inspect their results and continue working.
Architecture and trust boundaries: [ADR 0016](adr/0016-general-ai-agent-activities.md).

## Configure an agent

### Power mode and execution limits

Settings → System → AI Agents offers an administrator-only `Agents:PowerMode`
switch (default `false`). New agent activities snapshot this setting when they start.
Power mode removes model-call, tool-call, delegation and overall agent-time budgets
for both single agents and teams, including activity-level budget overrides. Usage
is still counted. Turning it off restores the saved limits for subsequent runs.
Manual cancellation and the agent kill switch remain effective, as do explicit
workflow timeouts, concurrency limits, permissions, per-request timeouts and
context/output size limits. Power mode does not make a workflow immune to cancellation
or guarantee that a model can finish an analysis. Runtime and API costs may increase.
The Agents section retains its existing restart notice for settings such as gate capacity.

Progress control also applies in Power mode. Each member receives current host review
state on every model turn, including after compaction. An identical review submission
reuses its approval only while its recorded dependencies remain current; changed
submissions and stale reviews still require review. Follow-up assignments must name
the concrete unresolved question or material correction.

After 12 model turns without a previously unseen observation/obligation signature,
the host asks the member to change its approach. After 24 it disables that member's
tools and requests retained findings and limitations. Alternating previously seen
states does not reset the counter. This is a structural progress guard, not a semantic
judgment: new outputs can still be irrelevant, and long analysis of unchanged evidence
may stop early. A stopped investigation cannot receive a `completed` task outcome.

Evidence envelopes distinguish `sourceTruncated` (the original operation discarded
data) from `excerptTruncated` (only the model-facing snapshot excerpt is shortened).
The first requires a narrower source query; paging immutable memory cannot recover
the missing data. These flags do not establish completeness of filters or scope.

The journal updates an intermediate report from member findings, investigation
checks and recent tool results throughout the run. Bounded excerpts and omitted
sections are marked; full observations remain in the journal. Cancellation or timeout
retains this report without generating a final assessment or running further tools.
The result panel separates technical execution from task outcome and labels retained
reports as preliminary. A final model report replaces the checkpoint on success.

The settings registry uses searchable, height-limited lists for skills and MCP servers.
Expand an entry's details to inspect skill descriptions and hashes or server IDs.

Teams have shared `investigation_read` and `investigation_update` working-memory
tools automatically; users need not select or name them. For material investigations,
members record focused questions, owners, hypotheses, evidence/counterevidence IDs,
next checks and conclusions. Up to 20 checks are kept for a run. An open check blocks
completion; resolving it requires original run evidence, and blocking it requires a
specific limitation. Changes invalidate dependent reviews; new checks invalidate all
reviews. Updates appear in the
existing journal and support export and survive model-context compaction.

Updates accept a stable `id` plus changed fields; omitted fields are preserved.
New checks require a question and next check, with the caller as default owner.
An attempted rewrite of a closed check returns `accepted=false`, its current
contents and the exact reopening step. It does not silently modify the conclusion
or invalidate review. Review approval belongs in the member's response.

Exhausting the shared investigation model
budget, including inside a parallel batch, enters the reserved tool-free final report
after started members finish. Unfinished members and missing reviews stay open;
the reserve neither grants approval nor increases the configured call limit.
Context/evidence limits, cancellation and technical failures remain distinct errors.
Finalization also records `coverage` in `run_conclusion`: each requested deliverable
has a requirement, `fulfilled`/`unresolved` status and supporting basis. The host
prevents `completed` when any listed requirement is unresolved: some fulfilled
requirements yield at most `partial`, none yield `blocked`; an explicit blocked
assessment is never upgraded. Final synthesis also receives the actual tool counts
and bounded last-call input/result excerpts, distinguishing plans from execution.
This metadata does not change
the user's output schema. Enumeration and fulfillment still depend on the model;
the list is not a semantic proof checker.

Tool schema errors identify missing fields and length/item limits without echoing
argument values. Generic failures distinguish `invalid_arguments`, `permission_denied`
and `tool_error`; process failures retain their existing detailed codes. A rejected
schema call is not executed. Only a corrected read should be retried; none of these
messages grants additional tools or permission to bypass a denial.

The register is a record of public findings, not private reasoning or an independent
truth checker. It cannot prove that the model registered every necessary question or
that a stated limitation is justified. Notes never become fresh system evidence or
change tool permissions. Reviewers must challenge unsupported closures. Diagnostic
answers should cover findings, causal evidence, counterevidence/uncertainty, the
smallest proposed remedy, verification and remaining checks, while respecting the
user's configured result schema. Unrelated tasks retain their requested format.

For a reusable machine-validated diagnostic result, select JSON output and use
[`samples/agent-diagnosis-result.schema.json`](../samples/agent-diagnosis-result.schema.json).
Its cause status concerns the stated investigation scope, not whole-system health.
Schema validation checks shape and categories; it cannot establish factual truth.

1. Enable AI and select the active LLM profile in Settings. Use a model/provider with
   function calling support. Each agent can override the model name on that profile.
2. Add AI Agent or AI Agent Team in the designer. Enter the task, role and instructions.
   In a team, use **Team lead (supervisor)** to choose who receives the task and coordinates
   the others. Select a member separately to edit its role, instructions and tools.
   Describe the work in ordinary language, using distinct role names: “Have the Planner
   draft a proposal, the Reviewer check it, then ask the Planner to revise it.” NodePilot
   supplies tool schemas, each member's responsibilities and capabilities, delegation
   instructions and the specialist response protocol;
   users do not need tool names, member IDs or JSON response instructions in their prompts.
3. Select each agent's tools and optional skill versions. File tools require allowed
   absolute paths; HTTP host restrictions are optional; workflow execution requires selected
   published workflow IDs. MCP tools refer to fixed administrator registrations.
4. For file or shell tools, select one machine and credential per agent. No machine means
   the NodePilot server. Service identity must be explicitly selected and enabled by an
   administrator; it is never inherited implicitly. Team members have separate bindings.
5. Choose text or JSON output. JSON needs a local JSON Schema. Set lower budgets if needed,
   save and publish. Publishing authorizes autonomous selected actions within the current
   read-only policy. There is no per-call approval dialog.

The supervisor uses `delegate({assignments: [{memberId, task, reason}]})`, including a short user-facing
explanation of the assignment and the open question it addresses. Specialists return JSON containing
`status` (`completed`, `needs_input`, `failed`) and string `content`. A follow-up reuses
that member's session. Questions go to the supervisor, not to an interactive user.

### Parallel assignments and live team board

Independent questions go to different members in one batch; dependent work stays in
separate batches. The effective limit is the minimum of `maxParallelMembers`, the
administrator's `TeamMaxParallelMembers` (default 3), and non-supervisor member count.
Set the workflow limit to 1 for sequential execution. Duplicate members, mixed
reviewer/specialist batches and oversized batches are rejected before any budget is
charged. Each accepted assignment consumes one tool call and each started member one
delegation. Results return in `results[]` in input order, with batch/delegation IDs and
per-answer truncation flags. Reviewers receive a stable snapshot after specialist work;
material changes during review require reviewing the new revision.

Running members receive bounded pointers to new peer evidence and shared checks before
their next tool-enabled model call. These untrusted team-board pointers do not count as
observations: use `evidence_read` and `investigation_read` for the originals. The board
is ephemeral and bounded; omitted notices are counted, and delivery IDs appear as
`team_board` events. The collaboration view groups interleaved events by delegation ID
and marks parallel batches. Model/transport failure cancels and awaits siblings before
finishing the run. Individual assignment failures remain visible failed results.
Concurrent calls can increase provider rate-limit errors (429). A transient model
request may be retried once within the shared budgets; persistent failure terminates
the run. See [ADR 0017](adr/0017-parallel-team-delegation.md).

### Follow a team run

The result summary appears before inputs and the journal in live step details,
execution history and the large agent view. It shows the final task assessment and
its recorded deliverables, with unresolved work before fulfilled work. The full
report is expandable directly below it. These are the agent's recorded assessments,
not independently verified facts. Running or interrupted runs do not show a final
checklist; missing assessment data is explicitly identified. Member filters only
affect the journal, not the overall result summary.

Open the agent step in the live execution details or execution history. **Open large
view** provides more space for the same journal. The collaboration view groups each
supervisor-to-member assignment with its rationale, the member's tool arguments and
results, model-call durations, and the answer or question returned to the supervisor.
Follow-up assignments remain separate entries. Reviewer approvals, requests for
evidence or revision, host completion rejections and interrupted calls are distinguished.
The displayed rationale is the supervisor's stated decision summary; tool results and
host statuses separately show what actually happened.

Member names, functions and models are captured in `run_context` when the run starts,
so later workflow edits do not relabel old evidence. Runs predating this snapshot use
their recorded member IDs; missing rationales are not invented. Filter by a member or
switch to the technical journal for original event payloads, sequence numbers and UTC
timestamps (shown on hover; displayed clock times use the browser's timezone).

**Export full journal** downloads the run, member snapshot and all loaded events as
JSON, regardless of the member filter or visible row limit. REST pagination catches up
before export is enabled; a running export is marked `complete: false`. The original
workflow read permissions and server-side secret redaction apply. Exports contain
diagnostic content and should be handled as support evidence.

Other workflow nodes communicate through normal outputs/input variables; only the outer
team node participates in workflow edges. Members do not become independent steps.
The requested order is model guidance, not a deterministic workflow transition.
A complete example is available in [the team sample](../samples/ai-agent-team.workflow.json).

## Tools, packages and storage

### Team completion and reviewers

Members have an optional technical reviewer function (`isReviewer: true`), separate
from their freely named role. A supervisor cannot also be a reviewer. Existing
teams retain their configuration; a member merely named Reviewer is not silently
converted. Select the function in the member editor when a required review is wanted.

The host records every member's `needs_input` or `failed` response. Only a later
`completed` response from that same member clears it. Giving another member an
assignment or claiming success in the final answer does not clear an open question.
Reviewer completion additionally requires `verdict: "approved"` and an empty
`openChecks` array. A finished review with `needs_work` or remaining checks returns
to the supervisor as `needs_input`. These protocol details are supplied by the host;
users do not write them in their task. In teams with tools, closing an objection about
missing evidence requires a new original tool observation from any team member,
including another reviewer. The reviewer who raised the objection must still reassess
and approve it; another member's approval never closes it automatically. Identical
target/tool/input/result observations are deduplicated across roles. Recalling a stored
snapshot or generating working notes does not create a new observation. The host checks
this protocol, while the reviewer judges whether the new evidence addresses the question.
For a correction to interpretation, wording or the proposed next step, the reviewer
can use `objectionKind: "revision"`; a subsequent review is required but a new read is
not. Omitted objection kinds default to `evidence`. A later revision request cannot
erase an already outstanding evidence requirement.
Every configured reviewer must complete a review; changed specialist findings or
supervisor tool use invalidate reviews that depend on those sources. Reviews without
an explicit dependency declaration cover the whole team. Sessions and budgets remain shared
as before, and reviewers gain no tools or delegation rights.

Each reviewer can be invoked at most three times against an unchanged observation
set: an initial review, a wording correction and an opportunity for a reviewer-owned
read. Further dispatch returns the outstanding requirements to the supervisor before
consuming another model call or delegation. A distinct observation permits a new
round; editing investigation notes alone does not. Shell envelope observation times
are excluded from evidence deduplication, while the actual output, errors and outcome
remain significant. This gate does not approve a finding or clear an objection.
Corrective supervisor turns count observations or fewer outstanding obligations as
progress, rather than register edits.

Review requests keep the original team task separate from the supervisor's submitted
proposal. The latter cannot redefine the review objective or dictate approval. Supplied
`openChecks` are retained for `needs_input` as well as attempted completion and placed
before narrative evidence so a bounded pending-question summary preserves them.
All delegated members, including specialists, receive the latest actual reports from
other members, with member IDs and statuses, separately from the supervisor's assignment.
This lets them check omitted or altered handoff summaries and inspect their counterpart.
These reports
remain untrusted claims, not independent evidence or additional permissions.
For existence/completeness claims, reviewers compare the full claimed and observed
object identities, types and sizes against cited originals. Companion metadata or
similarly named entries do not establish that the referenced data exists. Diagnosis
also separates failed fallback branches from the operation that remains blocked,
and registered/source content from the actual copy consumed by the failing operation.
Diagnosis distinguishes a demonstrated current configuration-level blocker from the
unproven attribution of a historical failure. A post-repair experiment or identifying
the person who introduced it is not required to describe the current blocker; proposed
changes still retain their authorization and policy preconditions.
Diagnosis also distinguishes selecting a step or starting a wrapper from actually
executing its child command. Members trace the failed phase's input dependencies and
compare complete expected/observed sets. Comparison values retain their object,
version, property, algorithm/unit and scope: a constituent digest is not comparable
to a whole-object digest. Missing, extra and changed entries are kept explicit.
Handoffs distinguish raw observations and
evidence IDs from interpretations: an untested causal exclusion must not become a
fixed premise for the next reviewer. These are general investigation instructions,
without product-specific filenames, error-code mappings or predetermined diagnoses.

A malformed delegation response gets one format-only correction in the same member
session with tools disabled. The correction counts toward the shared model budget,
preserves open objections and does not replay actions. A second invalid response
fails that delegation. The host reports `member_response_invalid` for the correction.
Unknown review dependency keys follow this same format-correction path. A protocol
failure blocks completion but does not create an evidence objection; any genuine
evidence objection from an earlier valid review remains in force.
Before each model call, agents also receive their remaining shared budgets so they
can reserve calls for necessary follow-up and review.

On a premature final answer the host journals `team_completion_blocked` and asks
the supervisor to continue within the existing budgets and timeout. New distinct
observations or fewer outstanding member/review obligations permit further rounds;
two consecutive corrective turns without either kind of progress stop the loop.
If the obligations remain open, the final report includes unresolved member IDs and
questions and the host prevents a completed outcome. JSON output is subject
to the same gate. Review approval concerns the requested outcome, not merely finishing
the review. Remaining actionable checks must be reported as open. The protocol gate
does not prove the semantic correctness or relevance of a verdict or observation.

Material gaps must be reconciled before final review. Each follow-up identifies the
requested conclusion or remedy it could change and the observation that would decide
it. Once member obligations, shared checks and required reviews are complete, the
host proceeds directly to tool-free final synthesis without an extra exploration
round. Incidental chronology or historical authorship must not reopen a bounded
diagnosis unless it can change the requested answer.
A first plausible explanation is insufficient: credible alternatives, unexplained
contradictions and indications of multiple faults still require discriminating checks.
New counterevidence reopens the affected investigation and review. Tools, model
selection and budgets are unchanged by these stopping rules.

`Agents:ModelMaxOutputTokens` defaults to **250,000 output tokens per model call**.
The active profile can impose a lower output limit; `run_context` records both values.
Provider model limits still apply; NodePilot does not infer model capabilities from its name.
The independent `Agents:MaxContextCharacters` input guard defaults to 250,000 characters,
including instructions, conversation, tool descriptions/schemas and message overhead.
This input guard is not a token-window estimate and is not accumulated across calls.

#### Context and evidence management

Before **every inner model call**, including function-invocation rounds, NodePilot
compacts old complete exchanges when the input reaches 75% of the input guard, aiming
for 50%. The original first task, latest assignment and two recent complete exchanges
remain verbatim. Tool-call/result groups are indivisible. Instructions, schemas and
host review/budget/permission state are not summarized. A prefix fingerprint prevents
applying a summary to a replaced framework history. If the protected current input
alone cannot fit, the request fails explicitly before reaching the provider.

Summaries are assistant working notes, never higher-priority instructions or new
observations. Bounded, tool-free summary calls use the same member's model/profile,
at most 8,192 output tokens (or a lower configured/profile limit), and consume the shared
model-call/time budget. The final-answer call is reserved. Invalid summaries leave the
original history intact. `context_compacted` records before/after sizes, removed messages,
summary calls and redacted working notes. Summary quality still depends on the model.
An output-truncated context summary is discarded and requested once more as shorter
notes from the same original exchanges. This tool-free recovery consumes model/time
budget and never repeats an external operation. A second truncated answer stops
explicitly without discarding the current chunk. The journal records
`model_output_truncated` and `context_summary_retry`; ordinary truncated agent/tool
responses still fail without executing their incomplete tool calls.

Tool results are redacted and saved as immutable `evidence_snapshot` journal pages
before exposing their IDs. Every original response includes its actual evidence ID,
including short complete responses; members need not infer a shared sequence merely
to cite a value. Large results return separately labelled beginning/end
excerpts with offsets; the omitted middle remains recallable from `nextOffset`.
This keeps recent records and result footers visible without presenting the two
excerpts as contiguous or claiming full coverage. `evidence_list` and `evidence_read` are automatic run-memory capabilities for
every member, including reviewers: they retrieve prior observations without rerunning
shell/HTTP/MCP/workflow operations. They cannot access another run, change targets or
credentials, or satisfy the host's requirement for a **new** external observation.
Empty optional `memberId`/`evidenceId` filters in `evidence_list` mean no filter;
nonempty unknown evidence IDs remain errors.
`evidence_read(query, offset)` can locate a literal case-insensitive passage within
the stored text and return surrounding originals with absolute character offsets.
It helps reviewers check decisive claims without rereading unrelated pages. A
missing match applies only to that snapshot and searched range, not the live system.
Each snapshot records its member, target ID when present, receipt timestamp, tool,
query excerpt and character range; the original invocation is in `tool_started`.
Source-file lines/byte ranges remain those supplied by the original tool. A returned
excerpt does not prove a whole log was read. Evidence is limited to 1 million characters
per returned observation and 16 million characters (including invocation text) per run;
the separate 250 MB raw-log transfer store and its cleanup remain unchanged.

`evidence_analyze` processes up to four bounded, overlapping sections per call using
separate tool-free model requests, with previous section notes as untrusted context.
Its `nextOffset` supports continuation; `evidence_analyzed` records exact examined
ranges, question and findings/open questions. Analysis IDs can be paged through
`evidence_read`; `evidence_list(evidenceId, after)` lists all section notes. These tools
count against the existing tool budget, and each section counts as a model call.
Agents correlate source IDs, concrete identities, times and counterevidence across
sections and delegate bounded follow-ups. The host records coverage; it does not
pretend partial coverage or an LLM interpretation proves completeness or a cause.

Original selected observations and notes remain in the authorized execution journal
after raw logs are deleted. Live display, reconnect, export and retention use that
existing journal; no additional unauthenticated artifact endpoint is introduced.

Each model request additionally has an `Agents:ModelCallTimeoutSeconds` deadline
(default 180 seconds), covering the response headers and body. A shorter LLM-profile
timeout and the overall run deadline still apply. `Agents:ModelMaxOutputTokens`
(default 250,000) caps generation independently of the input guard;
a lower profile output limit is preserved. Both settings are available in Agent administration.
`MaxResultCharacters` still limits the final workflow result; it no longer caps every
intermediate model response. Final reports should cite stored evidence instead of embedding logs.
Timeouts, HTTP 408/429 and HTTP 500/502/503/504, positively identified socket resets/
aborts and prematurely ended HTTP responses may retry the same model request once
after one cancellable second. Each attempt consumes a model call; the final-report
reservation and overall deadline remain enforced. A tool-enabled or working-summary
retry requires room for a following answer, checked atomically against concurrent
members at admission. `model_retrying` identifies the member and failed call.
The retry stays inside the transport adapter: it reuses the conversation, including
existing tool results, without replaying tools, delegations or workflow activities.
Authentication/TLS failures, other HTTP errors, unidentified unreachable endpoints,
DNS failures, refused connections, malformed/truncated responses and tool failures
are not retried. Connection interruption classification uses exception types/codes,
never localized error messages; TLS failures remain terminal even with an inner reset.
Persistent or non-retryable failures produce `model_failed` and fail the whole run;
nested delegation cannot swallow the error. Cancellation releases run resources; completed actions
are not undone. Existing chats and `llmQuery` keep their own profile limits.


### Current read-only policy

One host-side permission check runs before native tool execution; skill scripts and their
arguments are checked before staging. It is independent of model instructions and applies
to every team member. PowerShell, CMD and Bash remain the existing tools. There is no
write-mode switch yet; future write authorization belongs at this same boundary.

PowerShell permits a small parsed language of literal read commands and selection/formatting
pipelines. Examples are `Get-Service`, `Get-Content -LiteralPath 'C:\Windows\win.ini'`,
`Get-CimInstance -ClassName Win32_Service | Select-Object Name,State`, and event queries
with an explicit log name. CIM classes and namespaces are checked: `Win32_Product`, arbitrary
WQL and remote-session overrides are rejected. Nonliteral assignments, dynamic invocation, script blocks,
subprocesses, redirection and unapproved parameters are rejected rather than guessed safe.
Native shell calls execute a canonical form with explicit module/executable names.
Packaged CMD/Bash scripts execute that same checked canonical form, rather than
resolving commands from the uploaded package directory. PowerShell skill scripts
retain their verified original bytes and target signature/execution-policy checks.
CMD accepts one supported literal read command per call, such as `type`.
Bash also accepts pipelines of checked commands, such as `cat '/c/log.txt' | grep ERROR | head -n 20`.
This intentionally rejects some harmless but unsupported scripts.

Inline PowerShell accepts literal variable bindings which are substituted before
execution, parameter-based filters (`Where-Object Status -EQ Running`), JSON/CSV
decoding and noninteractive ErrorAction values. Single-class WQL SELECT queries
are translated into class/property/filter reads and use the same checked CIM
catalog. Event reads also accept FilterXPath and Oldest. These additions do not
authorize arbitrary Get-* providers, predicate script blocks or object methods.

Share diagnosis permits `Get-SmbShare`, `Get-SmbShareAccess` and `Win32_Share`.
Inline PowerShell UNC paths referring to the bound machine's hostname, FQDN or
local IPv4 address are resolved through that machine's local disk-share mapping.
Reads use the corresponding local path without an SMB connection or credential
delegation. This establishes local existence/content, not end-to-end SMB access or
another account's effective rights; inspect share ACLs and filesystem ACLs separately.
Other hosts, device/WebDAV paths, alternate streams, traversal and reparse points
are rejected. Delegate a different server's investigation to its configured member.
Packaged PowerShell scripts retain original bytes and therefore use local paths; inline UNC
resolution does not silently rewrite skill files. Existing file-tool allowed paths
and HTTP/MCP/workflow authorization remain unchanged.

Configuration Manager server diagnosis requires an explicitly assigned server
member, separate from a client member. The deployment assumes that the selected
NodePilot service account (or its Windows network computer identity) already has
the necessary WinRM and SMS Provider/RBAC permissions. Select service identity
explicitly and enable it in agent settings; an explicit credential remains an
alternative. Agents neither grant permissions nor silently change accounts or
targets. A local development user's identity is not a domain service identity.

Read-only provider discovery (`root/SMS:SMS_ProviderLocation`) and site queries for
package distribution, status, boundaries, boundary membership and site-system
associations are supported through the existing PowerShell tool. Deployment and
task-sequence reads include `SMS_Advertisement`, `SMS_TaskSequencePackage` and
`SMS_DeploymentSummary`; client reads include `CacheConfig` and
`CCM_Scheduler_ScheduledMessage`. These are class reads, never method calls. Provider discovery
does not authorize connecting to a different host. The Windows example's
`resources/server-content.md` explains how to distinguish missing distribution
from boundary selection and transport failures, and propose a scoped correction.
Packaged scripts still require literal commands with bound parameters; mutable
assignments are not authorized in signed/original script bytes.

Local firewall diagnosis uses `Get-NetFirewallRule` and `Get-NetFirewallProfile`
against the effective `ActiveStore`, plus `Get-NetConnectionProfile`. A rule query
may feed directly into its port, address, application or service filter read, then
selection/formatting. Remote policy stores, CIM sessions, GPO sessions and rule
changes remain denied. This extends the existing PowerShell tool, not its target
identity or write permissions.
`Resolve-DnsName` supports address/alias queries through the target's configured
resolver with DNS-only mode enforced. Arbitrary record types, alternate servers
and remote sessions remain denied.

IIS diagnosis additionally permits local `Get-Website`, `Get-WebApplication`, `Get-WebBinding` and
`Get-WebAppPoolState` reads from WebAdministration. PowerShell and CMD permit only
the exact `netsh winhttp show proxy` operation through the system executable;
proxy changes, target overrides and other netsh operations remain denied. Inline
`Get-Service` results preserve `Status` and `StartType` as named strings through
selection and JSON conversion. Packaged example scripts use the textual CIM
`Win32_Service.State` and `StartMode` fields instead, preserving original script
bytes and execution-policy enforcement.

The same PowerShell tool supports local IIS configuration/property inspection,
including authentication/access settings and pool limits, certificate-store listing,
Authenticode signature inspection, and online Windows capability/optional-feature
state. Servicing mutation commands and target overrides remain prohibited. Approved
ConfigMgr application/deployment-type reads retrieve lazy instance properties;
collection settings and client service windows are also readable. `Get-CimClass`
and `Get-WinEvent -ListProvider/-ListLog` provide schema/provider discovery.

`Select-String` supports literal patterns and up to 20 context lines on either side.
Regex-looking patterns without `-SimpleMatch` are rejected with an explanation and
the literal-array alternative; their meaning is never silently changed. File search
preserves complete short lines and marks omitted characters in long-line excerpts.
Snapshot misses direct the agent back to the source invocation and original line,
rather than treating an incomplete excerpt as evidence that the source lacks a value.
Native tool descriptions name the bound machine, and member handoffs retain its ID.

The Windows example skill's `resources/windows-http.md` explains hosts versus
DNS-only resolution, WinHTTP versus browser proxy settings, and services versus
IIS sites, pools, bindings and listeners. It also separates the WinRM transport
address from internal service addresses and current observations from old successes.
These are optional, selected diagnostic resources, not product-specific agent roles.

The checked read catalog also covers network/IP/route/connection state, DNS cache,
scheduled tasks, Defender status, storage, hotfixes, ACLs and selected SCCM client
and site-provider state classes. CIM filters are literal WHERE conditions on
approved classes, with no methods or target overrides. CMD additionally supports
local query forms of ipconfig, netstat, sc, tasklist and systeminfo. PowerShell
agents receive the supported command/parameter and CIM-class catalog directly in
the selected tool's description. Users do not maintain per-command registrations.
This remains a checked language, not a claim that every arbitrary read-only script
can be automatically classified. Unknown commands, dynamic code and side-effecting
queries such as Win32_Product stay denied; common unsupported read forms can still
require an implementation extension. HTTP/MCP/workflow read contracts are unchanged.

Diagnostic agents are instructed to connect the failed operation to effective
configuration and counterevidence, and give a scoped proposed remedy with verification.
Team supervisors request review of both causality and the remedy and follow up on
material gaps where tools and budget permit. These instructions improve investigation;
they do not guarantee a proven cause when relevant evidence is inaccessible.

File reads/searches and log collection remain available; `files_write` remains blocked.
Target file search returns at most 20 matching lines and a bounded excerpt budget.
It reports omitted matches explicitly. `order=last` selects the last matching lines
for appended logs; the default `first` preserves the first matches. File position
does not establish event chronology: the agent must still compare source timestamps.
HTTP accepts GET/HEAD without a body or redirects and uses the existing network/SSRF policy.
No URL whitelist is required. Existing per-tool host restrictions are optional. This enforces
HTTP read semantics, not the internal behavior of a remote service that misuses GET.

MCP requires an administrator read approval under Settings → AI agents, in addition to
the agent's tool selection. Approve only reviewed tools on trusted servers using credentials
restricted to reads. The tool must advertise read-only behavior without a destructive hint.
Approvals bind the server revision and complete tool contract (including schema and annotations);
changed connections/credentials or contracts require a new approval. Revocation, server revision,
fresh metadata and arguments are checked before each call. Annotation alone is not authorization,
and a remote server's unchanged metadata cannot prove its implementation remained unchanged.

The [MCP specification](https://modelcontextprotocol.io/specification/2025-06-18/server/tools)
requires treating annotations from untrusted servers as untrusted; approvals establish the
administrator trust decision in addition to protocol hints, not a sandbox around remote code.

Workflow calls require selected, published, enabled, unlocked workflows. The resolved definition
is inspected before dispatch and every step is checked after variable resolution. A host-owned
execution scope propagates through synchronous nested calls; it cannot be granted or removed
through model parameters. Scripts use the shell read policy after final template substitution.
Targets/credentials and nested workflow IDs must be fixed by the published definition.
Supported steps: manual trigger, log, return data, text generation, JSON/XML query, decision,
junction, delay, file hash, file existence, folder existence/listing, registry reads, checked
PowerShell, checked WMI class/SELECT reads, bodyless REST GET/HEAD (without redirects),
agents/teams and synchronous child calls. Other activities, plugins and detached
children are denied. Retries are disabled inside this scope. Ordinary workflow runs retain their
existing behavior. Tool denial is recorded in the existing failure journal.

This is a restricted execution language, not an OS sandbox for arbitrary scripts. It assumes
trusted installed shells, modules and providers. OS permissions still bound access to data.
NodePilot itself writes its journals and temporary transfer/process files; read-only means
no agent-requested changes to managed data or configuration, not zero disk writes by the host.

### Available integrations

Native tools: `files_list`, `files_read`, `files_write`, `files_search`, `logs_collect`,
`logs_search`, `http_request`, `workflow_run`, `powershell`, `cmd`, `bash`.
`files_search` filters on the target. `logs_collect` transfers bounded blocks into run
storage; `logs_search` searches the shared store with source excerpts. Collection is
limited to **250 MB total**, including every team member. Captured files may continue
growing; only the initial captured length is transferred. Before publishing an artifact,
its SHA-256 is compared with a bounded streaming reread of that same-length source prefix.
Rotation or overwriting that produces mismatched bytes, and truncation during transfer,
fail the collection and remove the partial copy without consuming the shared quota.
This checks transfer integrity; it is not an atomic filesystem snapshot of a live log.
Searches handle UTF-8 and BOM-tagged UTF-16; other legacy encodings
should first be converted on the target. Long lines are searched in bounded fragments.

Under Settings → AI agents, administrators register MCP servers (fixed absolute stdio
command/arguments or Streamable HTTP endpoint). Secrets are encrypted at rest and supplied
only to the connection: environment variables for stdio, headers for HTTP. Server and
skill selection is fixed by published configuration. Disabling a registration blocks its
next call. A changed MCP destination requires credentials to be supplied again or cleared.

Skills use a ZIP with `SKILL.md` at the root, YAML `name`/`description`, optional
`resources/` and `scripts/`. Import specifies an immutable version. Maximum package size
is 10 MB compressed and unpacked, with 200 files. Absolute paths, traversal, duplicate
paths and symbolic links are rejected. Selected skills expose `load_skill`,
`read_skill_resource` and `run_skill_script`. The latter accepts packaged script paths
and an argument array and requires the corresponding PowerShell, CMD or Git Bash tool.
Scripts must fit the same checked read language; PowerShell supports plain string parameters
with positional literal values. Scripts must be ASCII or UTF-8 with BOM so local validation
and Windows PowerShell decoding agree. Target execution policy remains in force: NodePilot
does not bypass a restricted policy. Existing complex scripts may be rejected until adapted.
Before uploading a PowerShell package, the runner reads the effective policy in the same
Windows PowerShell executable and target identity used for execution. `Restricted` blocks
script files (even signed ones) with `execution_policy_blocked`, before staging. `AllSigned`
requires a valid signature and a publisher already trusted on the target; NodePilot does
not install certificates, change policy or answer interactive trust prompts. Configure
these prerequisites administratively. Signed script bytes are transferred unchanged.
The runner checks the script hash and policy again before each invocation, including
cached packages. Windows PowerShell still enforces policy at invocation. `RemoteSigned`
retains Windows semantics: unsigned files without an Internet zone mark can execute.
Neither a signature nor execution policy replaces the host's read-operation checks.
Do not work around a script-policy denial by copying its contents into an inline call.
Skills consisting only of instructions/resources do not require script execution.

Selected main instructions are supplied completely to their member before its first
model call and retained across context compaction. Combined skill guidance is limited
to 64,000 characters or one third of the configured input context, whichever is smaller;
overflow fails explicitly. Skill enablement and version hash are rechecked before each
model call. Guidance remains subordinate to the task and host permissions.

`load_skill` lists already loaded guidance and available resources. Reference pages
from `read_skill_resource` are UTF-8-safe and sized to the serialized tool-output budget,
without a second evidence-excerpt layer; follow `hasMore` and byte `nextOffset`.
The journal records `skill_loaded` with version/hash and complete main delivery,
and resource calls with delivered ranges. Delivery does not prove comprehension.
Guidance reads do not count as target observations or advance evidence-based review
progress. Packaged script results retain the ordinary untrusted evidence treatment.
Nonzero/missing exit codes and timeouts produce `tool_failed`, with a stable error code
and redacted, bounded process details. Partial stdout is evidence of partial work, not
successful completion. Cancellation propagates to the run instead of becoming a retry.
Files are staged on the selected target, SHA-256 checked, then removed after the run.
An example is in [samples/agent-skills/windows-diagnostics](../samples/agent-skills/windows-diagnostics).

Results, counters and used excerpts remain in the execution journal. Raw collected files
are removed after the run and orphan local staging is removed at server startup. Cleanup
on an unreachable remote machine is best effort. No permanent log index is created.
Local raw-log storage is namespaced by the application installation directory, so a
different installation or test host cannot sweep its running agents' files.

## Diagnostic evidence

For investigation tasks, shared instructions distinguish observed facts, hypotheses,
demonstrated mechanisms and proposed checks. They require source/query scope, attention
to failed/truncated tool output, a focused discriminating check where available, and
explicit treatment of contradictory evidence. Missing fields, access denial and empty
searches do not by themselves establish corruption or a failed installation. A current
blocking condition is separate from the actor or earlier event that caused it.

Members receive the original team task and, for structured results, the supervisor's
result schema and field definitions in addition to their delegated assignment. Their
reply still uses the delegation status/content envelope. This keeps investigation,
review and final classification aligned without granting additional capabilities.

Agents compare failures and subsequent successes for the same operation, endpoint
and target. A different successful operation does not prove recovery. Changing TCP
snapshots are not inherently contradictory; empty filtered queries need a successful
same-scope cross-check when their error semantics are unclear. Unknown historical
timezones remain unresolved. Source identifier fields must retain exact names/paths,
and quoted evidence must be contiguous source text.

These rules are general-purpose and apply to individual agents and team members. A
reviewer repeating the same evidence does not count as independent corroboration. The
Windows example adds concrete source guidance in `resources/diagnostic-evidence.md`;
the desired immutable package version must be imported/selected to use that resource.
Existing imported package versions are not silently overwritten.

This is model guidance, not a deterministic proof checker or a guarantee of correct
root-cause analysis. The acceptance cases and limitations are recorded in
[original diagnostic acceptance](testing/ai-agent-diagnosis-acceptance.md) and the
[diagnosis quality follow-up](testing/ai-agent-diagnosis-quality.md).

## Timestamp evidence

`files_list` returns `LastWriteTimeUtc` as an invariant ISO-8601 string ending in `Z`,
including the available fractional seconds. It is file modification metadata, not the
time or timezone of events written inside that file. PowerShell/CMD/Bash and skill process
results include `timeContext` with `observedAtUtc`, `targetTimeZoneId` and
`observedUtcOffset`, measured on the execution target when the process is started.
The current target offset is not evidence of a source log's timezone or a historical
event's daylight-saving offset. Source text remains unchanged.

All team members are instructed to preserve source timestamps and explicit offsets,
keep observation/modification/event time separate, and report a missing source timezone
as unknown. Ambiguous/nonexistent local times require evidence before correlation.
Legacy `/Date(...)/` values in other tool results are not rewritten heuristically:
agents should obtain explicit timestamps from a suitable tool or report them unresolved.
These model instructions improve interpretation but are not a deterministic validator
for every statement in a natural-language report. Existing journals are not rewritten.

Regression and actual-model evidence: [timestamp acceptance](testing/ai-agent-time-acceptance.md).

## Budgets, cancellation and history

| Limit | Agent | Team (shared) |
|---|---:|---:|
| Model calls | 20 | 100 |
| Tool calls | 40 | 500 |
| Delegations | — | 20 |
| Parallel members | — | 3, capped by member count |
| Total time | 20 minutes | 30 minutes |

Settings section `Agents` sets ceilings and `Enabled`/`AllowServiceIdentity`.
`MaxConcurrentRuns` defaults to 2 per server process and requires restart; other values
are observed on new runs, and disabling execution is checked before further calls.
Queue time, tools and child waits count toward the total timeout. Team members use one
agent slot. Calls to another workflow are serialized within a team and use separate
database scopes. Waiting releases both scheduler and agent slots; other members may
continue working while those slots are released.
Retries of the activity or its tools are not supported. Only the bounded transient
model-request retry described above is automatic. Cancellation and restart do not undo completed actions;
starting again creates a new run. A malformed final JSON result has one repair round,
without tools and within the model budget, before the step fails.

Step output parameters: `agentRunId`, `outcome`, `outcomeReason`, `modelCalls`, `toolCalls`, `delegations`,
`promptTokens`, `completionTokens`. Unknown provider token counts remain empty.

`outcome` is separate from technical execution success: `completed` means the model
considers the requested task fully answered, `partial` retains useful findings with
material gaps, and `blocked` means the requested outcome could not be established.
`unassessed` means no final assessment was produced (including failures and older
runs). A successful diagnosis of a broken system may be `completed`; it does not
mean that system is healthy. These are model assessments with host protocol checks,
not a guarantee of factual correctness. Branch on `{{agent.param.outcome}}` when a
workflow requires complete task fulfillment in addition to `agent.success`.

After investigation and required reviews, a separate tool-free model call produces
a complete self-contained report and an assessment reason. It receives bounded,
explicitly marked excerpts of the initial report, latest update, every member's
findings and every investigation check's status. It cannot execute actions or
delegate. One model call is reserved within the configured budget for this stage;
allow at least two calls for an agent run. An invalid final envelope or user JSON
schema gets at most one tool-free format correction within the remaining budget.
The user's text/JSON result stays in `output`; assessment metadata does not change
its schema. Blocked investigation checks prevent a `completed` outcome.

If required reviews or investigation questions remain open after two correction
rounds without progress, or only the reserved report call remains, the team still
produces this final report. It includes the outstanding questions, missing reviews,
limitations and next checks. The host prevents a `completed` outcome; the result is
`partial` or `blocked`, while successful report delivery keeps the step successful.
The supervisor routes missing information to the responsible capable member, which
returns evidence or a specific inability to obtain it. Closing an open investigation
check counts as correction progress; paraphrasing or reassigning it does not.

The first report draft is saved before finalization. On failure/cancellation it
remains in the journal/result as **preliminary findings**, possibly superseded by
later evidence. It is not a validated final result and may not satisfy the user's
JSON schema. Technical status stays failed/cancelled and outcome stays `unassessed`.
Before a first draft exists there is no report to retain; collected evidence remains
in the journal. After the bounded model retry is exhausted, no extra call manufactures
a successful final answer. Persistent failure retains the preliminary draft when present.

Identical specialist findings and owner-only register reassignment do not stale
reviews. A reviewer may declare a nonempty `reviewDependencies` array using host
`reviewSources` keys: `member:<id>` for every member whose findings or original
observations support or could contradict the verdict, and `check:<id>` for each
dependent shared check. Cross-target dependencies must be included. Omission means
a whole-team review, invalidated by any material change. Empty or unknown dependencies
cannot grant approval. Changed sources invalidate their dependent reviews; introducing
a new shared question invalidates all reviews. Approval records the assignment's
starting revision, so changes during review still require reassessment. Host blockers
list affected reviewers and changed sources for focused follow-up. Dependency relevance,
like the verdict itself, remains a semantic judgment; the host enforces the protocol.
Tool schema errors identify the argument path and constraint
without echoing rejected values.
The live inspector/history displays persisted events and member status. After reconnect,
missing sequences are fetched over REST and deduplicated.

## API, CLI and MCP

Read routes: `GET /api/agents/runs?executionId=…`,
`GET /api/agents/runs/{id}/events?after=0&pageSize=200` (max 500).
They use the owning workflow's read permission. Registry routes under `/api/agents`:
`mcp-servers`, `mcp-servers/{id}/tools`, `skills`, `skills/{id}/enabled`.
Admin/Operator can list registrations; only Admin can create/update/delete them.

CLI: `np agent runs`, `events`, `mcp list|tools|save|delete`,
`skill list|import|enabled|delete` (see `--help` for arguments).
MCP: `list_agent_runs`, `get_agent_events`, `list_agent_mcp_servers`,
`discover_agent_mcp_tools`, `save_agent_mcp_server`, `list_agent_skills`,
`import_agent_skill`, `set_agent_skill_enabled`, `delete_agent_mcp_server`,
`delete_agent_skill`. The two delete tools use the existing destructive-tool gate.

Registry data lives in the application database. Retain native database and secret-key
backups; the portable configuration export does not currently include agent registries.
The existing secret re-encryption action also rotates MCP credentials and reports skipped
registrations. Re-enter skipped secrets before removing the legacy provider/key.

The scheduled test suite includes an opt-in live-model agent/team check, enabled only
with `NP_TESTSUITE_AI_AGENTS` and an active LLM profile. It selects no host tools. Hermetic
tests separately cover orchestration, cancellation, shell, MCP, skills and 250 MB logs;
they do not substitute for a WinRM/account-specific deployment acceptance run.

See [advanced diagnostic improvements and blind Luna repeats](testing/ai-agent-advanced-diagnostic-improvements.md)
for the checked read extensions, evidence handling, review progress guards and measured lab results.
