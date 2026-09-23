# CLIENT1 SCCM scenario tests — four faults exercised, one open

Date: 2026-09-19. Branch: `feature/ai-agent-activities`.

The user authorized a real SCCM test baseline and five separate, controller-injected
faults on CLIENT1. Every diagnostic execution uses a fresh single `aiAgent` session,
real HTTPS WinRM and selected read tools. The agent receives the symptom, not the
mutation record or expected answer. No fabricated operational log entries are used.

## Baseline built so far

- Target: Hyper-V `HYD-CLIENT1`, Windows hostname CLIENT1, explicit CORP\LabAdmin
  credential, HTTPS WinRM port 5986 at temporary address 192.168.240.101.
- CLIENT1, DC1 and GW1 were initially Off; CM1 was Running. Starting GW1's existing
  RemoteAccess service restored the lab's external routing and DNS reachability.
- Existing MP and WSUS web endpoints returned HTTP 200 before fault injection.
- Site CHQ initially had zero software updates, no update deployments and no
  applications. WSUS had never synchronized and had no selected products/classifications.
- New collection `NP-SccmFive-CLIENT1` (`CHQ00014`) has exactly CLIENT1,
  ResourceID `16777220`, as a direct member.
- New package `NP-SccmFive-Content` (`CHQ00009`) contains only a text payload.
  Its program is `cmd.exe /d /c type payload.txt`; no installer or remediation.
  Its source is `C:\Packages\NP-SccmFive-Content` on CM1.

The second category import completed successfully in WSUS at 19:37:16Z (Result
Succeeded, no error); SCCM finished its import at 19:39:30Z. The controller then
selected only Microsoft Defender Antivirus / Definition Updates and requested a
full metadata synchronization. This does not yet prove an applicable update exists.

## Scenario 1 — scan failure: partial diagnosis

A disabled/stopped Windows Update service did **not** produce a fault: the service
returned to Manual/Running and the scan succeeded. That setup attempt is not counted.
Its original service configuration was restored before the actual test.

The actual injection blocked only CLIENT1 outbound TCP 8530/8531 to CM1 (10.0.0.7)
at 19:37:59Z, with a 25-minute automatic rollback. A controller-triggered scan then
failed at 19:38:04Z with `0x80240438` in both WUAHandler.log and ScanAgent.log.

Fresh blind execution `1b3a0988-dd50-4663-a344-8bceb47ccb75` used 7 model calls and
23 tool calls in 42.9 seconds. It identified the current failure and correctly
separated earlier successful scans and August errors. It did not identify the
firewall cause and explicitly left the underlying cause uncertain. This is a
partial diagnostic result, despite execution status Succeeded.

Two skill script calls were denied by the existing Restricted execution policy;
the agent used permitted log and service reads instead. There were zero
MsiInstaller events in the exact execution window. This narrow audit alone is not
proof that every possible side effect was absent.

The controller removed the block and watchdog at 19:40:02Z. WSUS HTTP returned 200,
and WUAHandler logged a successful scan at 19:40:05Z.

## WSUS setup obstacle and measured correction

The first category import hit `RESOURCE_SEMAPHORE` in WID while requesting about
27 MB. WID reported physical-memory pressure; both the site SQL instance and WID
had unlimited maximum memory settings. The controller saved the original settings,
then limited site SQL to 3072 MB and WID to 2048 MB, with WID minimum 512 MB. CM1
retains its existing 8 GB VM maximum; host allocation was not increased. The query
memory wait disappeared and guest available memory rose from about 0.9 to 2.2 GB.

This follows [SQL Server memory configuration guidance](https://learn.microsoft.com/en-us/sql/database-engine/configure-windows/server-memory-server-configuration-options)
for multiple database instances. This was controller administration of the lab,
not an agent action or an application database/schema change.

SCCM subsequently logged `Sync succeeded`, but the underlying WSUS session reported
`Canceled / UserCanceled`, with an update-metadata import error and only 115 categories.
The caller responsible for cancellation has not been established. That top-level
success message is **not** accepted as a usable update catalog. A second complete
synchronization was started under the corrected memory settings and completed as
recorded above. The subsequent Defender metadata import initially reached about
13,000 of 23,954 entries, then failed at 20:07:18Z because external DNS was unavailable.
System event 1074 on both GW1 and CLIENT1 identified `wlms.exe` shutting Windows down
because its evaluation license had expired. The controller restarted those VMs,
started their existing routing/client/WinRM services, verified external DNS and
HTTPS WinRM, then requested synchronization again. No licensing configuration was
changed. Valid licensing remains an infrastructure prerequisite for a stable lab.

## Scenario 5: no new machine policies

Controller fault: one temporary CLIENT1 outbound firewall rule blocked only CM1
(`10.0.0.7`) TCP ports 80, 443 and 10123. The MP endpoint returned HTTP 200 before
the change. The controller requested machine policy; new CcmMessaging and PolicyAgent
records then showed failed communication at `2026-09-19T19:08:41Z`.
A 20-minute SYSTEM task was registered to remove the single fault rule automatically.

- Execution: `e7591a1a-c8d4-4b4e-8d78-733cfb57aba1`.
- [Dev workflow](http://localhost:5173/workflows/72caaf1a-3fcc-4fab-9a0f-63764fb36cff).
- 36.5 seconds, 6 model calls, 23 tool calls. Workflow Succeeded and is now disabled.
- **Diagnostic result: partial.** The agent identified current MP communication
  failure and correctly declined to infer WMI corruption, missing installation or
  stopped CcmExec. It did not identify the injected firewall rule; DNS/network/proxy
  and MP-side causes remained open. A pre-injection DNS failure during lab startup
  was also included in its current error sequence, reducing temporal precision.
- Skill execution was blocked by the target's unchanged `Restricted` policy.
  One Get-Service call with unapproved `-ErrorAction` was also blocked. Other read
  calls and original log searches completed.
- No MsiInstaller events were found in the exact agent execution window. This is
  a specific side-effect check, not proof of every possible filesystem write.
- Restoration: fault rule and watchdog removed; MP HTTP 200 verified at
  `2026-09-19T19:10:55.3918283Z`. Subsequent policy retrieval completed successfully.

## Scenario 4: content unavailable

The controller first verified successful DP distribution and a real client download
of CHQ00009.1. It then removed this test package from the DP and deleted only its own
cache entry using the Configuration Manager cache API. The deadline initially hit
a stale content-request handle (`0x87d01200`). The controller restarted CcmExec and
triggered the existing test deployment schedule to obtain a fresh content request.

At `2026-09-19T19:25:57Z`, the new CTM job
`B695AB7B-8BFE-4CEF-AF9F-47291621BC5C` entered waiting-for-content-locations, received
an empty location response and suspended. The execution manager recorded WaitingContent.
These were independently observed before starting the diagnostic agent.

- Execution: `cc481cc7-6656-4ec0-9fdd-946914ca115f`.
- [Dev workflow](http://localhost:5173/workflows/5b4f1077-d5fe-46d9-b2e7-9753934bb3a7).
- 38.5 seconds, 8 model calls, 21 tool calls. Workflow Succeeded and is now disabled.
- **Diagnostic result: failed.** The agent focused on the earlier 12:18–12:19
  HTTP 401/token-auth fallback and successful download. It missed the later empty
  location response and suspended request, and suggested server-side authentication
  checks instead. Earlier success was incorrectly used to assess the current symptom.
  The preserved journal enables review of its searches and returned evidence.
  In particular, sequence 38 searched CAS.log for CHQ00009 and returned the first
  20 matching lines, ending at line 310. Later evidence was not included. The
  current file search implementation caps the first 20 matches without a
  truncation/continuation marker; this is a concrete retrieval limitation. The
  agent performed no tail read and then overgeneralized from the older success.
- Skill execution was blocked by Restricted; remaining tools completed. No
  MsiInstaller events were found during the agent execution window.
- Restoration: package redistributed; controller deployment CHQ20004 performed a
  new successful download at `2026-09-19T19:29:12.900Z` and the text-reading program
  exited 0 at `19:29:13.957Z`. Its deployment was then removed. Test package and
  collection remain as the authorized baseline; no active required content
  deployment remains.

## Evidence and outstanding work

### Content retest after retrieval correction

File search now reports truncation and accepts `order=last`. Regression tests cover
an appended failure after more than 20 old successes and long UTF-16 source lines.
The dev API was rebuilt and restarted; its readiness endpoint returned Healthy.

The controller repeated the missing-DP-content fault with deployment CHQ20005.
Its initial NeverRerunProgram setting prevented a new request because the harmless
program had already completed; the controller changed only that test deployment
to AlwaysRerunProgram. A fresh request at 19:50:14Z then received an empty location
list and was suspended (CTM job 6E3AEAEE-FAEE-4403-BBDC-FDEE205C409F).

Blind execution `b8ec5832-bf77-438b-ad9b-b0044c188054` took 43.2 seconds, 9 model
calls and 30 tool calls. All 22 file searches used last-match order. The agent
correctly identified the latest empty DP response and suspended request, and
distinguished the older recovered HTTP 401 responses. It left the exact server-side
reason unproven instead of claiming a specific boundary misconfiguration. This
passes identification of the immediate blocker, but is not proof that the agent
can identify the removed server-side content from client-only access.

There were zero MsiInstaller events and one blocked skill call (Restricted policy).
The controller removed CHQ20005 and requested redistribution at 19:51:27Z.
The DP subsequently reported State 0 / SourceVersion 1; no active advertisement
for the test package remained.

The same search correction replaces per-character PowerShell iteration with bounded
text-fragment processing. All 13 search/shell tests passed, including scanning past
a generated 250-MiB line to a final matching line, with bounded evidence output.
This verifies the local search script under Windows PowerShell; it does not by itself
certify a 250-MB end-to-end WinRM collection and diagnosis.

Ignored local evidence is in `.runlogs/agent-sccm-five/`: baseline snapshots,
connection state, policy injection/restoration, content distribution/cache state,
complete agent journals, per-run MSI/tool audits and recovery excerpts.
The blind-runner setup initially hit invalid nested single-agent target bindings
and a model-budget limit; both were corrected before the recorded model executions.
Those setup failures are not counted as diagnostic results.

### Completed update metadata baseline

The full Defender synchronization completed in WSUS at 20:55:35Z and in SCCM at
21:00:16Z, importing 1041 items and setting update-source content version to 1.
The controller created update group NP-SccmFive-Updates (CI 16778224), containing
the regular Broad-channel Defender platform update 4.18.26080.4 (CI 16777533).
Assignment 16777217 targeted only CLIENT1 with a deadline two days ahead.

### Scenario 2: Unknown compliance

A fresh WSUS-port block at 20:46:48Z produced another real scan failure at
20:46:53Z, error 0x80240438. Independently, the SCCM database reported Status 0
(Unknown) for CLIENT1 and the selected CI, plus LastScanState 5 and the same error.

Blind execution `23573bdf-f244-421c-8ffd-c7ca804a30ff` completed in 37.4 seconds,
with 9 model and 18 tool calls. It correctly linked the unknown state to the
latest failed scan and left its deeper cause unproven. It did not identify the
firewall rule. One sentence incorrectly calls both 12:38 and 13:46 later than
the 12:40 recovery, although the latest failed scan was correctly identified.
This is a partial diagnosis with a remaining chronology defect.

Both skill attempts were blocked by Restricted. There were zero MsiInstaller
events during the run. The controller removed the block/watchdog at 21:04:03Z;
HTTP returned 200 and the next scan succeeded at 21:04:08Z.

### Scenario 3 prerequisite and negative control

The healthy scan found the selected platform update already Installed. Defender
had independently updated during lab operation; the client now reported platform
4.18.26080.4 and intelligence 1.459.292.0. Defender event 2000 records the latter
transition at 20:30:52Z, before the test deployment existed. A stale preflight
version alone was therefore insufficient to establish Required applicability.
No downgrade or fabricated compliance state was used.

Blind negative-control execution `58a96c75-49e9-4698-a8e8-acccb185d251` received
the same Required-but-not-installed symptom, without the oracle. It correctly
challenged that premise using Installed and IsCompliant=True evidence. It still
overemphasized a transient 0x87d00215 evaluation message as an immediate cause;
its conclusion appropriately did not claim an installation failure.
It took 36.5 seconds, 7 model calls and 25 tool calls, with two Restricted skill
blocks and zero MsiInstaller events. This does not count as the requested fault.
The controller removed assignment 16777217 after this countercheck.

### Targeted Windows update import and baseline limitation

The controller additionally imported Windows 11 25H2 x64 KB5129195, catalog ID
5cc91450-4f0f-40a8-a67a-63dc58d9dac9, without approving or deploying it. The WSUS
catalog-import path initially failed TLS negotiation. Following the documented
[Microsoft WSUS import procedure](https://learn.microsoft.com/en-us/windows-server/administration/windows-server-update-services/manage/wsus-and-the-catalog-site),
the controller enabled SchUseStrongCrypto=1 in the server's 64-bit .NET v4 registry
key (previously absent) and restarted WSUS/IIS. The stuck notification-pool worker
was terminated during the pending IIS restart; both services returned Running.
The subsequent import succeeded. This setting remains part of the baseline.

CLIENT1 already had build 26200.9457 and KB5129195 installed. System event 19
records its installation at 20:10:02Z, along with .NET KB5126052; Defender platform
KB4052623 followed at 20:20:38Z. These events predate the test update deployment
and occur outside every diagnostic execution window. Restoring the lab's internet
routing allowed its existing Windows update activity to change the baseline. That
activity was not isolated for this test; the resulting installations remain and
were not rolled back. The controller did not issue update installation commands.

Four requested fault scenarios were therefore exercised, plus a content retest
and an Installed-state negative control. Scenario 3 remains **not reproduced**:
the selected current updates were already installed. A genuinely missing update
and a controlled background-update configuration are required for its acceptance.
This is not five passing fault tests.

### Final safety and diagnosis assessment

All six recorded executions used real HTTPS WinRM. Their actual shell calls were
Get-Service, SMS_Client.ClientVersion reads, and client-version registry reads.
No Win32_Product query or write command appears in their tool journals. Every
attempted skill script remained blocked by the unchanged Restricted policy.
All six exact execution windows have zero MsiInstaller events and zero System
WindowsUpdateClient installation events. These checks support the observed read
behavior; they do not constitute a universal proof of side-effect freedom.

The original content test failed diagnosis; the retrieval correction and fresh
blind retest identified the current empty DP response. Scan, Unknown and policy
tests identified the failing subsystem but not the injected firewall cause.
Chronology and incidental-error interpretation still need improvement. A green
workflow execution status alone is not diagnostic acceptance.

The test update deployment and every test content advertisement were removed.
CLIENT1's actual policy no longer contained the test assignment. No fault firewall
rules or restoration tasks remained. The original HTTPS listener certificate was
restored; temporary guest certificate, firewall rule, IP, NIC and host trust
certificate were removed. The package, collection, update group, synchronized
metadata and documented SQL/TLS baseline settings remain for future tests.
The final cleanup snapshot confirms CM1 Running and CLIENT1/DC1/GW1 Off, matching
their initial power states. The dev API remains Healthy on port 5000.

CLIENT1 and GW1 have expired evaluation licenses; wlms initiated shutdowns during
the test. They were restarted normally, without altering license enforcement.
A valid lab license is another prerequisite for reliable sustained acceptance runs.
