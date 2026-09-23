# Real update compliance validation — 2026-09-20

Scope: acceptance points 4 (Required without installation) and 6 (Unknown), using
real CLIENT1 and CM1 over WinRM. Five members: a supervisor, client/server specialists,
and client/server reviewers. Tasks contained the symptom, CI and machine identity,
not the controller's fault or expected answer. Tools were checked PowerShell reads
and bounded client log reads. General diagnosis resources were supplied as member
instructions; this run does not validate remote skill-script execution.

## Controller preparation

The user explicitly authorized uninstalling an update on CLIENT1. The controller
used Microsoft's supported Defender platform rollback, from 4.18.26080.4 to
4.18.26070.9. This was a controller action, never an agent command. No cumulative
Windows update was removed and no reboot was needed for rollback.

Two temporary assignments targeted collection CHQ00014, independently checked to
contain only CLIENT1 (ResourceID 16777220). Both deadlines were September 22,
02:29 local Pacific time, with UseGMTTimes=false. Tests ran September 20.

- Required: CI 16777533, KB4052623 platform 4.18.26080.4;
  assignment 16777218 / {EC8035B6-BA55-4873-9795-713AEA0831AE}.
- Unknown: CI 16778649, KB2267602 intelligence 1.459.293.0;
  assignment 16777219 / {5F0A1904-3CC2-4485-8062-9B4AE4045A55}.

For Unknown the controller blocked CLIENT1 outbound TCP/8530 and 8531 to CM1
10.0.0.7 with rule NP-UpdateValidation-ScanBlock (display Lab-Traffic-72C4).
Public HTTP(S) downloads were separately blocked during the agent windows to
limit independent background installations; private CM traffic was excluded.
The extra host-access NIC/rule and a two-hour firewall cleanup task were temporary.

## Unknown result: not accepted

| Execution | Model / tool / delegation calls | Finding |
|---|---:|---|
| 9f710af4-8cbb-463c-8403-a847559ecce4 | 20 / 78 / 4 | Failed scan recognized; wrong WMI keys/classes wasted calls; actual firewall cause missed. |
| 8ad39162-e646-4325-8b30-9a2e1cca34f3 | 19 / 46 / 6 | Assignment/unknown asset mapping improved; actual firewall cause still missed. |

Both executions returned engine Succeeded. Neither passes root-cause acceptance.
The repeat established the failed scan and missing compliance, but promoted the
invalid-source/TTL log message into a cause without checking the available effective
network configuration. The supervisor asked reviewers to close with limitations
instead of investigating their material objections, despite remaining tool budget.
The review host gate enforced follow-up messages; it did not enforce their substantive
quality. Fixing that behavior remains an acceptance blocker.

An additional unsupported assertion called IsLatest=true / IsSuperseded=true a
metadata contradiction. Latest CI version and supersedence by another CI are distinct
concepts; that combination alone does not prove corruption. See Microsoft's
[CI property definitions](https://learn.microsoft.com/en-us/intune/configmgr/develop/reference/compliance/sms_configurationitemlatestbaseclass-server-wmi-class).

Controller evidence independently confirmed SQL compliance Status=0, last scan
state 5 and error 0x80240438. Removing only the SUP block and triggering a controller
scan produced WUAHandler success at 02:42:18.705+420; server scan state became 3,
error 0. This is fault/restoration evidence, not evidence that the agent discovered
the cause.

## Targeted corrections validated

- Get-CimClass now reads schemas only for the same approved namespace/class pairs;
  alternate targets, arbitrary classes and method execution remain rejected.
- Replaced nonexistent SMS_UpdateScanStatus with the actual read-only
  SMS_CIDeploymentUnknownAssetDetails query capability.
- Added software-update guidance separating client GUIDs, server CI_ID/MachineID,
  assignment mapping, property arrays and client/server status enums.
- Two new positive tests failed before the correction; all 130 permission tests
  subsequently passed. Dev API build succeeded with zero warnings/errors.
- Example skill version 1.0.10 is imported in dev (d003c89a-2e07-4d8b-93fc-4435d8d60336).
  Existing workflow skill references retain their immutable versions.

## Required result: partial acceptance

Execution a0fad761-74ef-4a71-aee4-1c69d208331c completed in 202 seconds with
20 model calls, 62 tool calls and 5 delegations. Controller scan/re-evaluation
and state-message submission had established a real missing update: UpdatesStore
Missing, ClientSDK ComplianceState=0, server Status=2 Required, current scan state=3
and error=0. These controller cycles occurred before the agent run.

The team correctly mapped CI, update GUID and assignment across CLIENT1 and CM1,
identified the future enforcement deadline, and proposed waiting rather than
reinstalling the client or forcing an update. Review captured pre-download activity
without claiming it proved a successful download or installation. However, the final
answer repeated the shifted September 21 19:29 deadline and inferred an approximate
September 22 02:29Z. The actual configured local deadline is September 22 02:29
Pacific (09:29Z). The core future-deadline diagnosis passes; the exact-time result
does not. This is not a fully green acceptance or a test of every maintenance-window,
content-download or past-deadline failure.

Some queries still used invalid comma-joined property strings, wrong exact GUID
filters or nonexistent properties despite corrected guidance. No instruction-only
claim of reliable self-correction is made. Query quality and substantive review
follow-up remain open.

## Read-only audit and cleanup

All three execution windows were separately queried on both CLIENT1 and CM1.
Each had zero MsiInstaller events and zero WindowsUpdateClient installation events
19/20/21. Tool journals contain delegated work, checked PowerShell reads and bounded
file reads; no installer, Win32_Product query, CIM method or repair command appears.
This is scoped journal/event evidence, not a guarantee about every possible OS write.

The controller removed both test assignments and update groups, then restored
Defender 4.18.26080.4 using the original Microsoft-signed amd64 platform package.
Signature was Valid and package SHA1 matched WSUS metadata
314C7BFDD3E57ABDF156B40C4F97AF62530F63A1. Installer exit was 0 at 09:47:16Z.
Final checks: platform 4.18.26080.4, WinDefend Running, zero temporary rules/tasks/client
assignments; temporary CLIENT1 NIC removed, TrustedHosts restored to localhost,
CurrentUser execution policy still Undefined. A final controller scan succeeded at
02:47:32.890+420. CLIENT1, CM1, GW1 and DC1 remain running. Test workflows are disabled.

## Remaining release acceptance

1. Close Unknown root-cause diagnosis and substantive reviewer follow-up; guard against
   unsupported secondary claims and repeated invalid queries. Repeat blind validation.
2. Preserve raw local/wildcard DMTF schedule semantics and repeat exact deadline checks.
3. Fresh combined CLIENT1 + CM1 content-download diagnosis and healthy countercheck;
   previous server-only distribution tests do not cover this end-to-end path.
4. Real 250 MB WinRM collection with rotation, long lines and encoding variants.
5. Remote CMD/Git Bash skills and AllSigned validation.
6. Restricted-user endpoint authorization/redaction; external MCP transports,
   authentication failures and disconnects; portable workflow dependency handling.
7. Complete CI/designer E2E and architecture/catalog/settings/documentation checks,
   consolidated acceptance documents and review of older lab artifacts. Arbitrary
   shell syntax is still not universally accepted merely because its intent is reading.

The older background WindowsAppRuntime installation attribution limitation documented
in ai-agent-cause-followup.md is unchanged by these clean execution windows.

Full evidence and controller scripts are local under .runlogs/agent-update-validation/.
They include configuration, exact tool journals, SQL/controller comparisons and
event audits. Installed NodePilot service was not restarted or modified.

## Additional time-representation issue

Controller comparison of the same client assignment found raw WMI DMTF
`20260922022900.000000+***` with UseGMTTimes=false, while Get-CimInstance's formatted
DateTime appeared as September 21, 19:29 on the Pacific machine. The SQL assignment
holds September 22, 02:29. The wildcard DMTF timezone and local scheduling semantics
must not be replaced by an assumed UTC instant. Both dates are in the future during
this test, but exact scheduled-time reporting needs a separate correction and
regression test. General log timezone handling does not cover this case.

Follow-up: [review and scheduling-time corrections](ai-agent-review-time-fix.md)
records the fixes, failed intermediate attempts, successful real five-member cause
and time tests, event audits and completed lab cleanup on September 20.
