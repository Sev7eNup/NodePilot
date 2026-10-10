# Local AI-agent acceptance — 2026-09-19

Status: **local live acceptance passed for the exercised cases after correction**.
Remote deployment and release-wide checks remain (listed below).

Tested the development instance on this Windows PC, API `http://localhost:5000`,
designer `http://localhost:5173`, branch `feature/ai-agent-activities`. Runs used the
configured `gpt-5.6-luna` profile and real tool calls. No model responses were mocked.
The separate automated regression suite uses controlled providers where appropriate.

All model-directed host actions in these tests were reads. Inputs were synthetic
files under `.runlogs/agent-live/`, plus the API readiness endpoint and a local MCP
fixture. Shell calls were checked against the exact prescribed read commands.
Test setup created fixtures, workflows and registrations; the host also staged skills
and collected log copies. Source fixture hashes matched before and after the runs.

## Verified live results

| Case | Evidence |
|---|---|
| Single agent | Lists and reads files, computes quantity 32 and duration 120 minutes, returns the actual random file marker in locally schema-validated JSON. |
| Eight-member team, original instructions | Three corrected-backend runs complete with all eight members: 49.7, 51.0 and 48.0 seconds; 23–24 model calls, 17 tool calls including nine delegations each. No extra supervisor workaround instructions. |
| Team questions | Date checker returns `needs_input`; supervisor supplies the date and receives the completed answer. |
| Team session memory | Inventory checker answers a follow-up from its session; exactly one file read across both assignments. |
| Team review and writing | Reviewer receives collected evidence; writer produces a report containing the verified numbers, three error IDs and file markers. |
| Boundaries and foreign instructions | Injected file instructions do not obtain another tool; an actual read outside the selected path is rejected. No injected output file is created. |
| PowerShell, CMD, Git Bash | Each executes exactly its prescribed read command and returns the actual random marker from the file. |
| Skill package | Loads the selected package and resource, transfers and verifies files, executes the original packaged `Get-FileHash` script, returns the correct SHA-256, and removes target staging. 8.7 seconds. |
| MCP stdio | Official MCP client connects to the NodePilot MCP executable and calls two explicitly selected read-only tools. |
| MCP Streamable HTTP | Client calls a real loopback HTTP fixture using a required secret header; result contains the actual file marker. |
| Native HTTP | Reads `/healthz/ready` and receives HTTP 200 / Healthy. |
| Cancellation | Workflow and agent journal become Cancelled; no subsequent tool call. |
| Total timeout | One-second agent budget ends the run with a time-budget error. |
| Parent/child concurrency | Two parents start together with two agent slots. Both invoke a published child containing another agent and receive its verified result, without deadlock. |
| 250 MB logs | Exactly 250,000,000 bytes collected; three known markers found on lines 1, 1,250,000 and 2,500,000. Three model calls, two tool calls, 335.4 seconds on the corrected backend. Model receives excerpts, and the raw temporary copy in the installation-specific namespace is removed. |
| Real designer/history | Headless Chromium against the actual instance displays all eight members completed and all 102 journal events from the first corrected run, in order without duplicates. No page errors. |
| Interrupted live connection | During another team run, the browser is offline for 20 seconds and its real SignalR connection is closed. Its cursor stays at 47; reconnection fetches all events through 104 without gaps. Two real connections observed. |
| API process restart | API is stopped while a controlled read-only HTTP tool is waiting, then rebuilt and restarted. Execution and agent journal become Cancelled, exactly one HTTP request was received, no tool result is fabricated, and the orphan workspace is removed. |

The 250 MB assertion initially rejected the model's German thousands separators;
the oracle was corrected to verify exact line numbers in tool output and normalize
number formatting in the final answer. The recorded agent result itself was correct.
The first HTTP fixture attempt lacked chunked-request decoding; fixing the fixture
produced the successful MCP HTTP run listed above.

Useful local workflows:

- [Eight-member passing team](http://localhost:5173/workflows/2ea2ff33-a6c7-41d8-b056-566078788359),
  execution `a43b9e74-afb9-471b-8512-0baa361e485a`.
- [250 MB collection](http://localhost:5173/workflows/c306c967-be95-4fb7-9a62-4d62970c99bd),
  execution `38ca29d1-ce70-4924-967a-c1d8a1e24e16`.
- [Three-shell read test](http://localhost:5173/workflows/bd77bdba-dc71-430b-9ee7-5f8207bba667).
- [Successful original skill package](http://localhost:5173/workflows/c34274c5-d73a-44f4-88e5-30f89407f46e).
- [Team with interrupted browser connection](http://localhost:5173/workflows/b66b346a-b068-485c-b20f-4f7b1711d2f6).
- [Interrupted API run](http://localhost:5173/workflows/276b04cd-933d-4ebb-b485-5ef7376fd800).

These links refer to the local test database, not portable sample IDs. Test workflows
are manual. The temporary administrator opt-in for service identity was restored to
false after testing; running a host-tool test again requires an explicitly permitted
execution identity. The user's existing example workflow was not edited (version 5).

## Findings corrected and live-retested

1. **Supervisor had insufficient member information.** The first eight-member run
   assigned a long-line search to the log collector, which could access only the main
   event log. Repeated searches exhausted the shared tool budget; reviewer and writer
   were never reached. The workflow's successful completion status did not pass the
   acceptance oracle. The runtime now supplies member instructions and tool capabilities
   in the delegation roster and discourages repeated equivalent searches. All three
   original-configuration team runs now pass on the corrected backend. The earlier
   explicit-instructions workaround is not counted as proof of this correction.
2. **Skill upload depended on `Get-FileHash`.** The embedded local PowerShell host lacked
   this cmdlet, so the packaged script was not reached. Transfer integrity now uses a
   streaming .NET SHA-256 calculation. A regression test executes the transfer with the
   cmdlet deliberately unavailable and verifies script execution and cleanup. The live
   run verifies transfer integrity and reaches the original packaged script.
3. **MCP registry timestamps caused false conflicts.** A create response returned seven
   fractional-second digits, while PostgreSQL stored six. Reusing that response in an
   immediate update returned 409. Saved timestamps now use microsecond precision; the
   controller regression test covers the returned concurrency value. The real PostgreSQL
   probe returns and stores the identical timestamp `2026-09-19T08:20:22.254112Z`, and an
   immediate update using the create response succeeds.
4. **Temporary storage was shared between installations/test hosts.** An API recovery
   test using its own database attempted to sweep the live run's directory. Windows
   rejected deletion of the open log file, and the live collection finished correctly.
   Raw-log storage is now namespaced by the application installation path. Isolation
   and recovery tests pass while the corrected live 250 MB collection is running in a
   separate installation namespace. Live completion and restart cleanup both pass.
5. **The PowerShell child inherited only embedded-host module paths.** After upload was
   fixed, the original skill script still could not resolve `Get-FileHash`. The shell
   wrapper now prepends the selected Windows PowerShell installation's Modules directory
   to the child's module path, preserving inherited custom paths and leaving the host
   environment unchanged. A process-level regression reproduces the missing module
   before correction and passes afterward. The original package, without a script
   workaround, now returns the correct hash in the real agent run.

The corrected sources pass **308 targeted automated tests**: 256 agent/LLM tests,
47 engine agent tests and five API agent-controller tests. The API tests were built
in an isolated artifacts directory because the running development API locks its DLLs.

After the user's restart approval, the development API was rebuilt and restarted.
It now runs the corrected sources, including the additional child-process module fix,
on port 5000; readiness returns HTTP 200. The installed Windows service was not touched.
After the final build, skill, all three shells, single-agent JSON, path/injection
boundaries, parent/child concurrency, cancellation, timeout, MCP stdio and native HTTP
were repeated successfully. The team, MCP HTTP and 250 MB paths passed after their
respective corrections; the final change is confined to the shell child environment.

## Evidence and remaining checks

Ignored local evidence is in `.runlogs/agent-live/`: `*-result.json` contains workflow
definitions, execution details and actual agent events; `oracle.json` contains expected
synthetic values and fixture hashes; `acceptance-summary.json` and `ui-result.json`
record checks. Screenshots: `team8-live-designer.png`, `team8-live-history.png`.
`live_probe.py` runs the cases and takes the local login password on stdin, without
storing it. `probe_registry.py` tests the PostgreSQL timestamp case. `reconnect-result.json`
records the actual outage and cursor catch-up; `restart-result.json` records recovery
and the external HTTP fixture's request count. Earlier failing evidence is retained in
`before-retest-20260919-101936/` and `skill-before-child-module-fix.json`.

For complete deployment/release acceptance, the following remain:

1. **Remote execution:** the subsequent [CLIENT1 acceptance test](ai-agent-winrm-acceptance.md)
   verifies real authenticated HTTPS WinRM, eight-member delegation, real log transfer
   and staging cleanup. Remote packaged script execution is blocked by the target's
   execution policy. The control run also violated the read-only task through a
   Win32_Product query that triggered six MSI reconfigurations. Remote skill execution,
   a supported read-only diagnostic configuration and remote CMD/Git Bash remain open.
2. **Deployment-specific security and integrations:** live folder/RBAC checks with
   separate restricted users, credential redaction across their accessible surfaces,
   and authentication failure/disconnection against the intended external MCP systems.
   Automated authorization, redaction and transport tests do not replace these checks.
3. **Operational log data:** the subsequent CLIENT1 test collected actual SCCM and
   CBS logs and correctly identified an independently injected service fault. It also
   exposed unsupported registry inferences and incorrect timestamp handling. Larger
   remote transfers, legacy encodings and rotation/truncation still require coverage;
   the original local cases used static synthetic UTF-8/UTF-16 data and long lines.
4. **Release-wide regression:** full repository CI, complete designer E2E and remaining
   configuration/import/administration surfaces on the final release candidate. This
   session ran targeted regressions and real designer probes, not the entire CI matrix.

No failing assertion remains in the exercised corrected live cases. These results
verify specific agent behavior on this PC, not universal model reliability or a
complete production acceptance.
