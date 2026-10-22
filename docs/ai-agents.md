# AI agent activities

`aiAgent` runs an agent within a workflow step, where it selects tools and uses their
results to continue the task. `aiAgentTeam` adds a supervisor who coordinates specialists
and reviews their findings. `llmQuery` remains a single model request.

## Configure an agent

1. Enable AI in Settings and select an active LLM profile with function calling support.
2. Add **AI Agent** or **AI Agent Team** in the designer. Enter the task and instructions.
   For teams, choose the supervisor and define each member's role. Describe assignments
   in ordinary language. NodePilot supplies the tool and delegation protocols.
3. Select tools and optional skill versions for each member. File tools need allowed
   absolute paths, workflow calls need selected published workflows, and MCP tools
   need administrator registrations and read approval.
4. Assign a machine and credential for file or shell access. Without a machine, execution
   targets the NodePilot server. Service identity requires explicit selection and
   `Agents:AllowServiceIdentity`. Team members have separate bindings.
5. Choose text or JSON output, supplying a local JSON Schema for JSON. Adjust budgets,
   save and publish. Publishing authorizes the selected actions within the read policy,
   without a separate approval dialog for each call.

**Draft team from description** creates a proposal for Admins and Operators. The configured
LLM receives inventory names, including machine hostnames and credential names, but no
credential secrets. The preview shows resolved tools, targets and inherited settings.
Unresolved or ambiguous machine and credential references block applying the draft.
**Apply to node** changes the open node only. Saving, publishing and running remain separate.

## Team execution

The supervisor assigns independent questions in parallel and keeps dependent work in
separate batches. `maxParallelMembers` is capped by the administrator's limit and the
number of specialists. A value of 1 makes execution sequential. Members share the run's
budgets and evidence, while their tools and target bindings remain individually configured.

A required reviewer must have the reviewer function enabled in the member editor.
The role name alone does not enable it. Open member questions, investigation checks and
missing reviews prevent a `completed` outcome. Changed findings require reassessment by
affected reviewers. If the team cannot resolve the remaining work, its report retains
the findings and identifies the gaps. Review approval remains a model assessment.

## Current read-only policy

NodePilot checks permissions before tool execution for every member, including skill
scripts. There is no write mode. The available operations are:

| Tool | Scope |
|---|---|
| Files and logs | List, read, search and collect within allowed paths. `files_write` is blocked. |
| PowerShell, CMD and Bash | Supported literal read commands on the configured target. Dynamic code, changes and target overrides are rejected. |
| HTTP | GET/HEAD without a body or redirects, subject to the network policy. Host restrictions are optional. |
| MCP | Selected tools on trusted registered servers with administrator read approval. Changed connections, credentials or tool contracts require renewed approval. |
| Workflows | Selected, published, enabled and unlocked workflows. Steps and synchronous child calls must satisfy the same read policy. |

Shell tools expose their supported commands to the model. Unsupported scripts may be
rejected even when harmless. These checks restrict execution but provide no OS sandbox
for arbitrary code. Access still depends on the selected account's permissions, and MCP
approval depends on trust in the remote server. NodePilot writes its own journals and
temporary files during a run.

Skills are imported under Settings / AI agents as versioned ZIP packages with `SKILL.md`
at the root and optional `resources/` and `scripts/`. Packages allow up to 200 files and
10 MB, both compressed and unpacked. Scripts require the corresponding shell tool and
must satisfy the read policy as well as the target's execution policy. NodePilot does
not bypass signing requirements or change that policy.

Log collection is limited to 250 MB per run, shared across the team. Raw collected files
are removed after the run, while observations used by the agents remain in the journal.
Cleanup on an unreachable remote target is best effort.

## Limits and cancellation

The `Agents` settings define the ceilings. Activity settings can lower them.

| Default limit | Agent | Team, shared |
|---|---:|---:|
| Model calls | 20 | 100 |
| Tool calls | 40 | 500 |
| Delegations | Not applicable | 20 |
| Parallel specialists | Not applicable | 3, capped by member count |
| Total time | 20 minutes | 30 minutes |

`Agents:MaxConcurrentRuns` defaults to 2 per server process and requires a restart.
Queue time, tool execution and child waits count toward the total timeout.

`Agents:PowerMode` defaults to `false`. When enabled by an administrator, it removes
model-call, tool-call, delegation and overall agent-time budgets for new runs, including
activity overrides. Usage is still counted. Permissions, cancellation, progress guards,
concurrency limits, workflow timeouts and request/context/output limits remain active.
Power mode can increase runtime and API costs.

Cancellation does not undo completed actions. Activity and tool retries are unsupported,
although transient model requests may be retried once without repeating tools.

## Results and history

The result panel separates technical execution success from the task assessment:

| `outcome` | Meaning |
|---|---|
| `completed` | The model considers the requested task fully answered. |
| `partial` | Useful findings remain, with material gaps. |
| `blocked` | The requested outcome could not be established. |
| `unassessed` | No final assessment was produced, including interrupted or older runs. |

A completed diagnosis does not mean the investigated system is healthy. Workflows that
require a complete answer should check `{{agent.param.outcome}}` in addition to
`agent.success`. The requested text or JSON stays in `output`. Separate parameters include
`agentRunId`, `outcomeReason`, call counters and token usage when supplied by the provider.

Live step details and execution history show the report, unresolved work and journal.
**Open large view** expands the same information. **Export full journal** downloads JSON
after all event pages have loaded, regardless of member filters. Exports from running
agents are marked incomplete. Cancellation or failure retains available preliminary
findings, which may differ from a final report or the requested JSON schema.

## Administration and references

Run history and registries are available through `/api/agents`, `np agent` and the agent
MCP tools. History uses the owning workflow's read permission. Admins and Operators can
list registrations, while only Admins can change them. Agent registries are excluded
from portable configuration exports, so recovery requires database and secret-key backups.

- [Architecture and trust boundaries](adr/0016-general-ai-agent-activities.md)
- [Parallel team delegation](adr/0017-parallel-team-delegation.md)
- [Windows diagnostic skill](../samples/agent-skills/windows-diagnostics) and [diagnostic result schema](../samples/agent-diagnosis-result.schema.json)
- [Diagnostic acceptance](testing/ai-agent-diagnosis-acceptance.md), [quality checks](testing/ai-agent-diagnosis-quality.md) and [timestamp checks](testing/ai-agent-time-acceptance.md)
- [Advanced diagnostic checks and measured results](testing/ai-agent-advanced-diagnostic-improvements.md)
