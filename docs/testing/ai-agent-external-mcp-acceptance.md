# External MCP acceptance on CLIENT1

Date: 2026-09-22. Branch: `feature/ai-agent-activities`.

NodePilot's development API connected over the network to a temporary MCP server
on CLIENT1, not loopback. Eleven actual agent workflows used `gpt-5.6-luna`.
The server offered one read-only status tool returning its actual hostname and a
random marker unavailable in the agent prompt. Host-owned authentication used a
random secret header. The isolated endpoint was
`http://192.168.240.101:18765/mcp`, reachable only from the test host through a
temporary firewall rule and Hyper-V adapter. CM1 was not changed.

## Live results

| Case | Verified behavior |
|---|---|
| Valid authentication and read | Actual CLIENT1 hostname and exact random marker; one remote call |
| Wrong authentication at discovery | HTTP 401 observed by server; NodePilot discovery rejected |
| Authentication revoked during run | 401 during contract recheck; tool failed; zero remote tool calls |
| Tool description changed | Rejected before remote tool invocation |
| Input schema changed | Rejected before remote tool invocation |
| Read-only annotation revoked / destructive annotation set | Rejected before remote tool invocation |
| Selected tool removed | Rejected before remote tool invocation |
| Connection interrupted after dispatch | Truncated HTTP response produced tool failure; server recorded exactly one call, no automatic replay |
| Server returns `isError=true` | After correction: `tool_failed`, code `mcp_tool_failed`, blocked task outcome, exactly one remote call |
| Healthy new run after failures | Correct CLIENT1 response and marker, completed outcome |

Contract mutations happened between the initial tool listing and the host's
pre-invocation recheck. Request counts come from the independent server audit,
not the model's report. Successful transport recovery means a fresh workflow;
it does not claim replay or continuation of the interrupted request.

The healthy initial execution was `b5a09e0b-ba9e-4bc4-a066-ae15ad33bd7d` (10.1 s).
The disconnect execution was `70a570d3-09b5-491e-a77b-da29125f5e85` (8.1 s).
The corrected explicit-error execution was `9ee8fdc1-8a04-45a3-a5f3-0446a9e6e9d3`
(11.4 s); final healthy recovery was `05af9bff-6c67-4d73-b1c9-1dd893398c00`.
Other IDs, server request traces and original journals are retained under
`.runlogs/agent-mcp-client1/`, with final repeats in its `final/` directory.

## Finding and correction

The original host returned MCP `isError=true` as ordinary error-shaped text.
Luna correctly described the failure, but the journal recorded `tool_completed`
and the activity ledger counted a success. The initial report-content oracle
therefore passed while the journal review found this defect. That initial result
is retained and is not evidence of correct error-event classification.

`AgentToolHost` now throws `AgentToolExecutionException` with `mcp_tool_failed`.
Existing runtime failure handling provides redaction, failure events and model
feedback without retrying the call. A regression test failed before this change
and passes afterward, asserting the failure code, source message and one call.
All **348 Engine agent tests pass**. API build: zero errors, 54 existing warnings.
Final live error and recovery tests both pass with the rebuilt API.

An earlier fixture attempt failed during SDK `server/discover`: a PowerShell
switch continued to write after replying to an unknown method. The fixture was
corrected to send one response. Its initial logs remain; no agent run occurred
in that attempt. This was a test-server defect, not a NodePilot defect.

## Cleanup and limits

All eleven workflows are terminal and disabled; their journals are contiguous.
All members used Luna. The authentication secret is absent from saved agent-run
evidence. All three temporary MCP registrations are disabled, their stored test
secrets cleared and test read approvals removed. Other registrations and grants
were preserved. The server acknowledged shutdown; independent inspection found
no listening socket, no test firewall rule and no temporary adapter. CLIENT1's
execution policy stayed Restricted; host TrustedHosts stayed localhost. The
server ran from an in-memory controller script and left no deployed server files.
Development API and designer both returned HTTP 200. The unrelated pre-existing
large remote-test file was not touched by this round.

This accepts the exercised **external Streamable HTTP integration with static
header authentication**. Automated tests also exercised the real local SDK stdio
server. It is not a hosted vendor/OAuth acceptance, TLS certificate lifecycle test,
proxy test, resumable SSE test or exhaustive MCP protocol conformance suite. A
specific production provider still needs validation with its actual authentication
and deployment settings. The MCP server's read-only contract remains a trust
boundary; NodePilot does not sandbox the server implementation.
