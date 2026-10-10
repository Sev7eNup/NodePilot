# Ten additional blind diagnostics on CLIENT1 and CM1

Date: 2026-09-20. Branch: `feature/ai-agent-activities`.

Result: **5 causes identified with appropriate targeted recovery, 1 partial diagnosis,
4 not identified**. All ten workflow executions technically succeeded; this is not
equivalent to ten correct diagnoses. No hangs or budget exhaustion occurred.

## Method

Ten real, isolated configuration/service faults are introduced sequentially on the
lab VMs. Each fresh workflow has five members: a supervisor, client/server specialists
and independent client/server reviewers. Each member retains only its own run session.
The task describes an observable symptom and investigation start time, not the injected
setting, service name, recovery command or oracle. Workflow names contain only case numbers.

Agents use the existing read-only PowerShell and file tools over WinRM. They cannot
repair or initiate scans, installations, downloads or policy cycles. A separate controller
injects and restores the fault, with a 25-minute recovery task as fallback. The configured
run timeout is 1,100 seconds; the common budgets are 40 model calls, 500 tools and
20 delegations. No agent prompt, provider or production code is changed between cases.

The test deliberately separates an observed broken prerequisite from an actual failed
deployment or transfer. No synthetic error lines are inserted into product logs. A fresh
job/scan is not claimed if none was triggered. Existing logs are available as supporting
evidence and counterevidence. A successful workflow status alone does not prove diagnostic
correctness; the final diagnosis is compared with the controller's original state.

## Controller oracle

| Case | Target | Injected fault | Restoration |
|---|---|---|---|
| 01 | CLIENT1 | BITS disabled/stopped | Original start mode and service state |
| 02 | CLIENT1 | Windows Update service disabled/stopped | Original start mode and service state |
| 03 | CLIENT1 | SMS Agent Host disabled/stopped | Original start mode and service state |
| 04 | CM1 | WsusPool stopped | Start previously running pool |
| 05 | CM1 | Default Web Site stopped | Start previously running site |
| 06 | CM1 | SMS_EXECUTIVE disabled/stopped | Original start mode and service state |
| 07 | CLIENT1 | CM1 names mapped to 127.0.0.2 in hosts | Exact original file bytes, clear resolver cache |
| 08 | CLIENT1 | WinHTTP proxy set to unused localhost port 18765 | Restore original direct configuration |
| 09 | CM1 | WSUS HTTP binding moved from 8530 to 18530 | Restore exact original binding |
| 10 | CM1 | WsusService disabled/stopped | Original start mode and service state |

Microsoft references used by the controller when assessing findings:
[WSUS troubleshooting](https://learn.microsoft.com/en-us/windows-server/administration/windows-server-update-services/manage/wsus-messages-and-troubleshooting-tips),
[WinHTTP commands](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/netsh-winhttp).
These references and the oracle are not supplied to the diagnostic workflows.

## Evidence and results

Execution records, full paginated journals, fault snapshots, restore scripts and
post-restoration checks are stored locally under ignored `.runlogs/agent-ten/`.
The workflows remain available, disabled, with their execution histories.

All members use the recorded active model `gpt-5.6-luna`. No external HTTP tool, MCP
server or skill was selected in this particular team; the findings apply to the
PowerShell/file diagnostic path, not every possible NodePilot configuration.

| Case | Diagnostic assessment | Seconds | Execution |
|---|---|---:|---|
| 01 | Identified BITS Stopped/Disabled and targeted restoration; correctly did not claim a newly failed SCCM transfer | 268.9 | `36072ecd-b62e-44df-ab79-bf0f7ef4788a` |
| 02 | Identified wuauserv Stopped/Disabled and targeted restoration; did not invent a fresh scan failure | 211.5 | `e0bbfb23-c7c1-4267-b1f9-d84dd42e9c20` |
| 03 | Identified CcmExec Stopped/Disabled, shutdown log and appropriate recovery | 127.3 | `64cb716d-30a8-4b05-9303-d45489fc08a0` |
| 04 | Missed the stopped WsusPool despite controller-confirmed HTTP 503 throughout the run | 166.1 | `bbfb5df5-37ea-402a-9650-24bb3b533570` |
| 05 | Partial: found current MP error 12029, but not the stopped website or a specific repair | 213.8 | `49864ba3-4d07-4ec9-98b4-a434175f66f4` |
| 06 | Identified SMS_EXECUTIVE Stopped/Disabled and targeted recovery; separated unproven client effects | 192.8 | `8e89434d-54f2-494d-b725-10fc98434f66` |
| 07 | Missed hosts override; incorrect service status labels survived review into the final answer | 288.7 | `6439ad40-87d5-491d-a22e-058abe41f7ef` |
| 08 | Did not identify the configured dead WinHTTP proxy; explicitly reported inability to interpret effective settings | 230.2 | `08c09909-16d1-4a04-bd3f-f2c1cd162520` |
| 09 | Missed moved binding; a correct missing-listener observation was discounted using earlier HTTP success | 258.1 | `60b94202-9871-4c89-86a7-6d0968f1ec56` |
| 10 | Identified WsusService Stopped/Disabled, targeted recovery, and separated unproven historical client effects | 193.4 | `67203ba1-9456-4a35-bd14-b338bee7a840` |

Total agent execution time: **2,150.8 seconds (35m 51s)**; 255 model calls,
869 tools including 72 delegations, with 205 PowerShell calls. Each run took between
127.3 and 288.7 seconds. Maximum observed usage was 34 model calls and 126 tools,
below the configured 40/500 budgets. Technical execution status is Succeeded for
every row; diagnostic success is assessed separately above.

## Findings

- The shell read contract does not expose direct IIS runtime queries such as
  `Get-WebAppPoolState`, `Get-Website` or `Get-WebBinding`, or a semantic WinHTTP proxy
  query. Case 08 explicitly recognized the latter limitation. This does not mean the
  underlying account lacks permissions. The separate controller queried these states.
- Case 07 never inspected the allowed hosts file. The DNS-only resolver returned
  `10.0.0.7`, while the controller's application resolver returned `127.0.0.2`.
  DNS-only evidence was overgeneralized to effective application name resolution.
- In case 07 the final report called numeric `Get-Service` status 4 "Stopped" for
  CcmExec/W3SVC/WAS. The raw observation was Running. This was a decoding error, not
  a witnessed service transition. Reviewer approval did not catch it.
- Older successes repeatedly influenced current-health claims. In case 09 an HTTP
  probe immediately after the binding edit still succeeded during propagation;
  a fresh check at 17:47:30Z found the 18530 binding and the original endpoint timed out.
  The endpoint was still unavailable at 17:51:19Z, and returned 200 after restoration
  at 17:51:21Z. The agent found no 8530 listener after propagation, then discounted
  that observation using the earlier 17:46:52 IIS success. Historical success cannot
  establish present listener health.
- CM1's WinRM transport address `192.168.240.10` and its internal service address
  `10.0.0.7` caused repeated identity questions. An address difference alone is not a
  DNS fault. Some file searches targeted directories and failed with Access denied;
  this does not establish a filesystem permission problem for the actual log files.
- Several reviewers did correct chronology and scope errors, especially cases 02,
  05 and 06. The controls prevent some overclaims but are not an independent oracle.

Configuration faults were separate, but earlier log entries and in-flight client
operations were retained. The controller verified restored state before the next
injection; it did not erase logs or fabricate a fresh deployment/scan. Reports must
therefore distinguish current observations from residual earlier incidents.

## Restoration and side effects

All ten injected faults were restored. Final controller checks at 17:55:24–25Z
confirmed original CLIENT1 service start modes/states, direct WinHTTP configuration,
restored hosts content/resolution, running CM1 services, started IIS site/pool and
the original WSUS HTTP binding. Both MP and WSUS HTTP probes returned 200. The temporary
CLIENT1 test NIC/firewall rule and recovery tasks were removed; TrustedHosts was restored
to its original `localhost`. The ten test workflows were disabled and remain available
with their histories. VM power states were preserved.

All **20 machine/execution windows** have zero MsiInstaller events and zero
WindowsUpdateClient installation events 19/20/21. The 205 recorded PowerShell calls
contain no service changes, installation/repair commands, scan triggers, CIM method
calls or Win32_Product queries. This supports the observed read behavior of these
runs, not a universal proof of side-effect freedom. Controller mutations are explicitly
outside the diagnostic agent runs.

Seven tool errors were recorded: two nonexistent guessed log filenames, two directory
paths passed to file search, and three denied nonliteral/interpolated shell expressions
in case 10. No denied request was a service change or installation command. The case 10
agents continued with literal read queries. No skills were selected or executed.

The controller initially needed the temporary WinRM trust restored after the previous
test cleanup. Case 01 restoration encountered a PowerShell JSON enum serialization
issue (`value`/`Value` duplicate keys); the controller recovered the original restore
command with Python, restored the service, and resumed at case 02. Neither setup issue
is counted as an agent diagnosis failure. The recovery task remained armed until the
successful explicit restoration. Product code and model configuration were unchanged.
