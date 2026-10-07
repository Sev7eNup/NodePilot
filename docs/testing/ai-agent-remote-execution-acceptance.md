# Remote agent execution acceptance

Date: 2026-09-22. Branch: `feature/ai-agent-activities`.

Nine live workflow runs exercised the development API against CLIENT1 and CM1.
Every model was `gpt-5.6-luna`. Agent actions were read-only. The independent test
controller created synthetic data and temporary connection infrastructure; it did
not inject SCCM faults, repair software or change the installed NodePilot service.

## Results

| Case | Execution | Result | Seconds |
|---|---|---|---:|
| Four-member team on CLIENT1 and CM1 | See local `remote-team-result.json` | Both targets observed, CMD and PowerShell reads, real CcmExec log collected/searched | 170.7 |
| Packaged PowerShell under Restricted | `remote-skill-blocked-result.json` | `execution_policy_blocked`, no inline fallback | 12.7 |
| Same PowerShell package under temporary RemoteSigned | `remote-skill-allowed-result.json` | Successful actual CLIENT1 script execution | 11.2 |
| Packaged CMD | `remote-skill-cmd-result.json` | Successful actual CLIENT1 script execution | 9.2 |
| Large remote log and search | `remote-logs-full-result.json` | 249,000,028 bytes, exact EOF marker at line 2,490,001 | 56.2 |
| Cancel active collection | `remote-logs-cancel-result.json` | Cancelled during tool execution | 3.6 |
| Collection total time budget | `remote-logs-timeout-result.json` | Failed with time-budget error at configured 20 seconds | 20.1 |
| Cancel active PowerShell file read | `remote-logs-process-cancel-result.json` | Cancelled; no matching fixture process remained | 3.7 |
| PowerShell total time budget | `remote-logs-process-timeout-result.json` | Failed with time-budget error at configured 15 seconds | 15.1 |

Evidence files are under `.runlogs/agent-remote-acceptance/`; `summary.json` records
execution/run IDs, outcomes and actual tool errors. Technical success for the
Restricted-policy probe means the agent reported the rejection, not that its script
ran. All nine workflows are disabled and terminal; journals have contiguous event
sequences. The test skill was disabled after the runs. All 347 Engine agent tests pass.

The team used five delegations, 33 model calls and 43 tool calls. Its initial
PowerShell `hostname` commands were rejected by the supported read language; CMD
hostname and PowerShell Get-ComputerInfo/Get-Service provided valid observations.
Two investigation updates exceeded the conclusion length limit, received precise
schema feedback and were corrected. These failures remain in the journal; this
test is not evidence of universally minimal delegation or unrestricted PowerShell.

## Environment and cleanup verification

Initial NodePilot connection tests failed: the old CLIENT1 host-access adapter had
been removed and TrustedHosts contained only localhost. A temporary adapter on the
existing isolated NP-HostAccess switch restored 192.168.240.101; its WinRM firewall
rule allowed only host 192.168.240.1. The two lab IPs were temporarily appended to
TrustedHosts. Both NodePilot connection tests then passed. This round used HTTP
WinRM with explicit credentials, not a new HTTPS/certificate acceptance. Earlier
HTTPS evidence remains in the WinRM and skill acceptance reports.

The controller used PowerShell Direct for CLIENT1 setup and independent inspection.
Agent tools used NodePilot's WinRM transport. CurrentUser execution policy was
restored to Undefined (effective Restricted). Independent post-run checks found no
target staging directories, command temporary files or fixture PowerShell processes.
No local collected artifact files remained. Latest MsiInstaller 1035 record stayed
2541 across the observed test window; no new MSI reconfiguration was observed.

## Remaining acceptance boundaries

- Git Bash is absent on both targets; successful remote Bash execution is not yet
  demonstrated by this round.
- Real signed execution under AllSigned needs a signing/trust setup. Previous
  metadata tests and RemoteSigned runs do not substitute for this check.
- Abrupt network loss and cleanup after reconnection are distinct from cooperative
  cancellation and have not been exercised here.
- The large fixture is static ASCII. It does not prove every legacy encoding,
  rotation or truncation behavior of live operational files.

The temporary CLIENT1 firewall rule and adapter were removed and host TrustedHosts was restored to
`localhost`; development API readiness returned 200. Remote fixture deletion was
rejected by automatic execution review, first in the combined cleanup and then in
a narrower known-file/empty-directory cleanup, both with `blocked by policy` and no
further explanation. The synthetic file
`C:\ProgramData\NodePilot-RemoteAcceptance-20260922\large.log` therefore remains
pending manual cleanup; it is not an agent artifact or running process. Additional
approval was requested for temporary portable Git Bash and a test signing/trust
setup. Full remote acceptance is not claimed while those checks remain open.
