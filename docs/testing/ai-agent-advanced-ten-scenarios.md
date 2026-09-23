# Advanced SCCM/Windows Update blind tests — 21 September 2026

Test round completed on `feature/ai-agent-activities`: seven distinct scenarios
reproduced, eight blind runs including the case-6 repeat; three scenarios remain
unscored because their live prerequisites/effects were not established. Diagnostic agents use
`gpt-5.6-luna` exclusively. Fault preparation and restoration are separate controller
actions; agents receive the symptom, target and time window, not the injected cause.
No scenario-specific product rules or skills are added.

`Succeeded` below is the workflow execution state, not a diagnosis score. A run can
finish normally while missing the injected cause; assessment uses the independently
recorded fault and native before/after evidence.

## Scope and baseline

Targets: CLIENT1 (Windows 11 Enterprise Evaluation, build 26200), CM1 (site CHQ,
ConfigMgr 5.00.9141.1000). Both are running Hyper-V guests. Initial inspection found
no application deployments or update assignments, no WSUS HTTPS certificate bound
to port 8531, and no upgrade media mounted on CLIENT1. Existing installed Defender
platform is 4.18.26080.4; a previously verified restoration installer is available.

The controller temporarily restored the existing direct host-access NIC/firewall
rule for CLIENT1 and added the two lab IPs to host TrustedHosts, preserving the
original value (`localhost`) for final cleanup. NodePilot's connection tests passed
for both configured machines before diagnosis.

## Results

| Case | Scenario | State |
|---|---|---|
| 1 | Per-update-class scan source conflicts with intended WSUS source | Two preparation attempts; no fresh native scan proved the effect; restored, not scored |
| 2 | Delta download path fails while ordinary downloads work | Requires applicable delta/UUP update baseline |
| 3 | WSUS HTTPS incorrectly requires client certificates | **Partial diagnosis**; cause identified, effective configuration and precise remediation incomplete |
| 4 | WsusPool recycles under an insufficient memory limit | **Partial diagnosis**: stopped pool found, memory cause missed; actual HTTP 503 reproduced |
| 5 | Update maximum runtime exceeds open maintenance window | **Partial diagnosis**: window blocker found; actual 30-minute window versus 60-minute runtime not proved |
| 6 | Third-party update signing trust missing | **Partial on repeat**: signature/trust stage found; precise missing TrustedPublisher entry not proved |
| 7 | Client installed state differs from delayed server compliance | **Failed run, partial findings**: exact queued message found; stopped component missed; review budget exhausted |
| 8 | Installed application fails registry detection | **Failed diagnosis**, details below |
| 9 | Feature upgrade compatibility blocker | Requires suitable upgrade media and a reproducible blocker |
| 10 | Inappropriate servicing repair source | **Cause diagnosed (FoD variant)**; missing source/payload proved; independent capability-status read blocked |

Prepared controllers are not executed tests or passes. A configured fault without
an observed matching symptom is not counted as a reproduced scenario.

## Cases not scored

- **1 ? scan-source policy:** two reversible preparations were restored. The first
  used the wrong parent location for UseUpdateClassPolicySource; the second used
  the AU location. No fresh native scan established actual misrouting under the
  second configuration. A registry mismatch by itself is not counted as a live
  reproduction, and no blind agent result is claimed.
- **2 ? delta/UUP path:** the current nonexpired, nonsuperseded ConfigMgr catalog
  does not contain an applicable Windows 11 update baseline. Blocking a port
  without first observing a real delta/UUP transfer would not test this scenario.
  A matching update/content baseline must be provisioned before fault injection.
- **9 ? upgrade compatibility:** no suitable Windows upgrade ISO/extracted setup
  source was found in the inspected lab locations. A request for a media path
  remains unanswered. No fabricated CompatData logs or unobserved blocker is
  counted as a compatibility test.

## Case 8 — application detection

Created an isolated available application `NP Advanced Inventory 20260921`, CI
16779071, package CHQ0000C, targeting only CLIENT1 in collection CHQ00016. Its installer
is a harmless CMD file writing a unique REG_SZ marker in the 64-bit registry. No
MSI, reboot or third-party software installation is involved.

Baseline succeeded: installer exit 0, application Installed, marker Version=1.0.
An initial attempt to induce failure using only the 32-bit detection flag did not
fail: that detection can also check the 64-bit location. This preparation attempt
was not scored. The actual injected fault used an explicitly wrong
`SOFTWARE\WOW6432Node\NPAdvanced20260921` path in the detection rule while the
installer writes `SOFTWARE\NPAdvanced20260921` with `/reg:64`.

Reproduction confirmed: application revision 4 / deployment-type revision 3;
installer exit 0 followed by negative discovery and error 2278556452 (`0x87D00324`).
The blind task disclosed only the application name, installation symptom and UTC
window. Five members: supervisor, client/server specialists and client/server reviewers.

Execution `51a91847-315a-443b-9e01-245242166ae8`, run
`45282ff1-b8d5-429e-bc89-446373eb7c4f`: Failed, 537.1 seconds, 99 model calls,
164 tool calls, 16 delegations. The remaining call was reserved for final reporting;
unfinished review prevented that stage.

The team correctly distinguished installer exit success from failed detection and
read the actual client registry marker. It did not retrieve the deployed detection
definition. Two attempted CIM reads were rejected by the read-only namespace/class
policy. Repeated reviews and register edits then consumed the investigation budget.
Reviewers also failed to settle the CMTrace `+420` time interpretation despite
available timezone evidence. This is not solely a model-quality conclusion: a host
read-capability restriction is directly observed.

The controller restored the correct server detection rule in `finally` at
2026-09-21T16:55:38Z. Client policy convergence was verified at 16:58:46Z:
revision 5, Installed, EvaluationState 1, ErrorCode 0, marker Version=1.0.
Final owned-asset cleanup remains pending. The NodePilot test workflow is terminal
and disabled.

## Case 3 — WSUS HTTPS client-certificate requirement

Established a temporary trusted HTTPS binding on port 8531 and verified HTTP 200
from CLIENT1. Enabled `Ssl,SslNegotiateCert,SslRequireCert` only on the WSUS
ClientWebService directory. The same request then returned HTTP 403 with IIS
substatus 7. The blind brief disclosed the URL, failed request and time only;
it explicitly did not claim an update installation or scan had failed.

Execution `5d0450c9-5af1-4b87-b47f-8cfe8e876b52`, run
`d1ee354d-a37d-4811-99b7-e02a9cecd26e`: Succeeded, 401.7 seconds,
72 model calls, 152 tool calls, 10 delegations. Test assessment: **partial**.
The report identifies the client-certificate requirement from the exact IIS
403.7 entry and distinguishes the active HTTP update policy from the HTTPS probe.
It does not retrieve the effective SSL configuration, and its remediation
incorrectly refers to a binding where the relevant setting is scoped to the
IIS directory. It also leaves standard W3C UTC interpretation unresolved.
Some reads were rejected, including `Select-String -Context`; the report correctly
treats missing reads as evidence gaps rather than absence of a fault.

Initial controller restoration failed because the saved numeric SSL flag string
`0` was not accepted by WebAdministration's setter. The controller now translates
that saved value to `None`. Restoration then completed and was independently
verified: SSL flags 0, original empty HTTPS certificate binding, both temporary
certificates removed, HTTP ClientWebService probe 200. No production code changed.

## Case 4 — WsusPool memory recycling

Original private-memory limit: 1,265,011 KB. Controller changed it to 50,000 KB
and enabled recycle-reason logging, preserving both original settings. Fifteen
initial HTTP probes returned 200. Native WAS event 5117 then repeated at roughly
one-minute intervals. Event 5002 subsequently disabled WsusPool; a CLIENT1 HTTP
probe at 17:22:02Z returned 503. The scenario therefore developed from a proved
recycle into a proved service outage during the investigation.

Execution `c351702c-b283-4f13-b27b-3dac7ea7c475`, run
`dcf411f5-a1de-49d9-919b-e6a16a338a5f`: Succeeded, 524.4 seconds,
92 model calls, 155 tool calls, 20 delegations. Assessment: **partial**, since the
report finds the stopped pool but explicitly cannot identify its initiating cause.

Two distinct obstacles were observed. The model repeatedly filtered on provider
`WAS` instead of the actual `Microsoft-Windows-WAS`, missing the available events.
Its configuration search used a regex alternation. `AgentPermissionPolicy` forces
`Select-String` to `SimpleMatch=true`, so that alternation becomes a literal search
and returns no matching configuration. This is an implementation limitation as
well as a failed query strategy, not proof that the source lacked data.

Restoration returned the limit to 1,265,011 KB and the original event-log flags,
removed the recovery task, and restarted the pool because the fault had stopped
it. Independent check: Started and HTTP 200. Workflow terminal and disabled.

## Case 10 — missing servicing source, FoD variant

This is a native Add-WindowsCapability failure, not a damaged-component-store
RestoreHealth experiment. `Tools.Graphics.DirectX~~~~0.0.1.0` was NotPresent before
and after. The controller used an owned local source directory containing only a
readme and `LimitAccess`; DISM/CBS natively reported missing payload,
`0x80070002`, and `0x800f0912 / CBS_E_ONDEMAND_LOCALSOURCE_NOT_FOUND`.

Execution `55bbc4b0-e3d2-43da-a1be-c1e6be74ca62`, run
`17e8b301-b00a-4c54-8c7d-73a95e70b981`: Succeeded, 266.1 seconds,
50 model calls, 118 tool calls, 7 delegations. The final report correctly traces
the exact source path to the missing CAB, identifies the needed compatible FoD
payload, proposes replacing the incomplete source, and describes verification.
It does not claim to have repaired anything.

The server specialist initially attributed an unrelated SCCM application source
to this failure. Review caught that unsupported causal link; the final report
explicitly says that source was not proved to be used by DISM. The report is still
unnecessarily long and cautious about time conversion. A `Win32_OptionalFeature`
read was rejected, so the agent correctly leaves independent current capability
status unverified. The controller separately verified NotPresent and removed only
the owned incomplete source at 17:27:14Z. No installed capability was removed.

## Case 6 — published-update signing trust

Created a small EXE that can only write its own inventory marker after checking a
unique eligibility file. No MSI is used. Published it with the native WSUS API,
signed the CAB with a temporary owned certificate, and approved it only to a new
WSUS group containing CLIENT1. No installer invocation was made. WSUS publication
needed a temporary trusted HTTPS API binding; ordinary update traffic remained
on HTTP 8530.

Native search found update `bc1a01f5-8e50-4a49-a8cd-321c729527d6`,
`NP Advanced Inventory Update 20260921`, not installed and not downloaded. The
controller removed only its signer from CLIENT1 TrustedPublisher, retaining the
same signer in Root and `AcceptTrustedPublisherCerts=1`. The CAB's actual signer
thumbprint matches the staged certificate. A first download attempt through
remote COM failed with E_ACCESSDENIED, so it was not used as the scenario evidence.
A local SYSTEM scheduled action performed the real WUA download instead.

At 17:28:07Z this native download returned result 4, `0x8024B303`, IsDownloaded=false.
Converted native ETL shows a successful DO transfer followed by signature chain
checks `800B0109` and rejection `8024B303`. The source CAB was
`http://cm1.corp.contoso.com:8530/Content/9B/D03339B39DA0F25704D817754E57C49441A30B9B.cab`.
The agent brief discloses the update identity, download symptom and time, not trust
configuration. The converted Windows Update log is available on the target.

The first run's file tools were limited to selected log roots. They rejected a
read of `C:\Windows\WindowsUpdate.log`. This is a test-workflow configuration
restriction, distinct from product-level command/CIM restrictions. Following runs
allow file reads across `C:\` on the assigned target, consistent with the user's
requested read scope. The first run is preserved unchanged; a blind repeat uses
the same fault, task and Luna model with broader file access. No new skill or
scenario-specific diagnostic instruction is introduced.

First execution `444caa82-3298-4ec5-9f22-9bec08eafd20`, run
`7d3eb610-f124-4a95-99ee-488de8459790`: Succeeded, 597.9 seconds,
100 model calls, 162 tool calls, 18 delegations. **Cause not found.** The team
followed older application-content HTTP 401 entries rather than this WSUS update.
Review eventually prevented that old failure from becoming a definite current
cause; the final report leaves the actual cause unknown. The native converted log
was inside an allowed log subtree even in this first run, but the team never
discovered/read it. Consequently the one denied WindowsUpdate.log path is not by
itself proof that wider permissions would have solved the investigation.

Repeat execution `1c59c696-e8c2-4572-a8a5-957b4cbad79d`, run
`3ffd28ce-0e6c-4f5b-983d-f102fd0022a1`: Succeeded, 367.1 seconds,
78 model calls, 142 tool calls, 11 delegations. **Partial diagnosis.** The team
discovers the actual Windows Update log and identifies the post-transfer trust
failure, exact CAB, matching server hash and native final failure. It correctly
does not equate hash equality with a valid signature. It cannot complete a native
signature/signer comparison with the exposed read commands and leaves bad server
signature versus missing client trust unresolved. No direct certificate-store
comparison established the specifically missing TrustedPublisher entry.

Controller counterfactual: restored only that owned TrustedPublisher certificate,
then repeated the native download. At 17:45:29Z it returned result 2, HRESULT 0,
IsDownloaded=true. Native ETL confirms `Trusted Publisher: Yes` and
`DownloadResult: DownloadSucceeded`. No installed marker was observed; no installer
was invoked. The report's proposed success criterion "no 0x800B0109" is too strict:
the healthy native operation still logs two such intermediate chain-policy failures
before accepting the trusted publisher and completing successfully. Assess the
concrete operation's final outcome, not absence of every error-looking line.

Removed CLIENT1 test trust/policy/eligibility, the owned WSUS approval and update,
the temporary computer group, signer certificates and publisher staging, and the
temporary HTTPS binding/certificates. Initial WSUS deletion required correcting
the controller to delete its approval before deleting the deployed revision;
cleanup completed at 17:46:15Z. Ordinary downloaded-update cache and native logs
are not manually purged wholesale.

## Case 5 — maximum runtime exceeds the open update window

Created an enabled, one-shot SoftwareUpdatesOnly window for CLIENT1's isolated
collection CHQ00016, 17:44–18:14 UTC, duration 1800 seconds. The client natively
confirmed `IsWindowAvailableNow` true for 1 second and false for 3600 seconds.
Rollback of the Defender platform made the already catalogued nonexpired update
CI 16777533 / KB4052623 / 4.18.26080.4 applicable. A verified signed restoration
installer is present; a separate SYSTEM recovery task restores it after 45 minutes
if ordinary cleanup is interrupted.

Required assignment `{119E0440-AD51-4CD3-89EB-4A9FA2C73A82}` targets only CLIENT1,
has a deadline in the past and does not override service windows. Native scan
succeeded, the client reports Missing, content download completed, and the native
deployment log at 17:48:40Z rejects installation because no current or future
window can accommodate 3600 seconds, ending with `0x87D00667`. The client SDK reports
MaxExecutionTime 3600; the actual window duration is 1800.

Execution `d747bbab-4576-4ba2-8f2c-e03b7c9734b0`, run
`90effef0-5aa0-4aa1-8b3d-87773b4f6a89`: Succeeded, 400.2 seconds, 67 model calls,
97 tool calls, 15 delegations. **Partial diagnosis.** The blind brief names
the update, assignment, error and timeframe, not the window configuration.
Server compliance has independently reached Required (Status 2), providing the
native starting point for the later state-processing backlog scenario.

The team proves the direct window blocker but leaves absent/short/out-of-time
windows unresolved. A client namespace read ran on CM1 and its failure was later
partially attributed to CLIENT1. One membership query used nonexistent ResourceName
instead of Name; its failure is not proof of unavailable membership data. Actual
window configuration was not retrieved. The final report also proposes changing
an elapsed deployment deadline: an enforcement deadline is not an expiry date,
so that recommendation is not justified. Original CMTrace +420 times remained
unconverted despite target timezone context. These are material diagnosis and
recommendation gaps, not just stylistic issues.

The controller removed its required deployment, update group and maintenance
window, restored MaxExecutionTime, reinstalled the signed Defender platform
4.18.26080.4 and removed its fallback task. Native scan at 17:59:14Z identifies
update dc8cd110-fb75-45bb-8379-859df2409586 as Installed.

## Case 7 ? installed client, stale server compliance

At 17:56:33Z the controller stopped only the SMS_STATE_SYSTEM component thread,
leaving SMS_EXECUTIVE running. Before stopping, native CM compliance for CLIENT1
(ResourceID 16777220) / CI 16777533 was Required (Status 2). After restoring the
Defender platform, the client scan completes successfully and records UPDATE_INSTALLED
for the exact update at 17:59:14Z. At 17:59:35Z the client successfully forwards state
messages and receives a positive acknowledgement. CM1 has an incoming SMX file,
the state processor remains Stopped, and server compliance remains Status 2.
This is a native state transition and actual processing backlog, not edited logs
or injected database records. A 45-minute independent recovery task covers the
component stop; the ordinary run wrapper restores it in finally.

The blind brief names only the update, Installed/Required discrepancy and timeframe.
Execution `2c9200ec-b940-4e7c-9b49-73fd5f6c3af8`, run
`395ab5da-3910-417b-8e9d-71d5a50342f7`: Failed, 526.1 seconds,
99 model calls, 190 tool calls, 20 successful delegations. **Failed run with partial
findings.** The team finds and reads the precise SMX file, including CLIENT1 and
the full update GUID with state 3. It repeatedly confirms the file remains in
incoming, but never inspects the stopped component itself. Earlier successful
summary jobs and the Running parent service are treated as general counterevidence.
No direct current SMS_UpdateComplianceStatus query is attempted despite the allowed
class and supplied CI. Reviewers ultimately correctly reject the unproved claim
that a normal processing delay has been excluded. Repeated reviews consume the
budget without a discriminating component-state read. Final error is Team review
incomplete; no completed user diagnosis is produced.

Controller counterfactual: restart only SMS_STATE_SYSTEM. At 18:09:17Z the component
is Running, the incoming queue is empty and exact CLIENT1/CI compliance is Status 3
(Installed), with the client's 17:59:14Z status timestamp. This directly validates
the injected cause. The agent did not establish that cause independently.

Artifacts and scripts: `.runlogs/agent-advanced-ten/`. Original scripts and baseline
helpers remain unchanged except their normal output artifacts. No application
source files or production agent behavior were modified by this test series.

## Restoration and bounded action audit

Verified at 18:09?18:11Z:

- Defender platform 4.18.26080.4 restored; scan-source policy values match saved
  before-images. Service startup types are unchanged; wuauserv can remain Running
  after a native scan, rather than its initially idle Stopped state.
- WsusPool Started, original private-memory limit 1265011 KB, HTTP probe 200.
  ClientWebService sslFlags 0 and original port-8531 binding with empty certificate
  assignment restored. Owned test certificates absent from both machines.
- SMS_STATE_SYSTEM Running, incoming queue empty, update compliance Installed.
  No active recovery snapshots or NPAdvanced recovery tasks remain.
- Owned application deployment, distributed content, application, collection,
  source directory, registry marker and one exact owned CCM cache entry removed.
  CM application cleanup needed a retry after deployment-count propagation; an
  already-removed distribution is now guarded in the controller.
- Owned WSUS update/approval/group and publisher staging removed. Historical
  native logs, converted evidence logs and restoration snapshots remain for audit;
  unrelated caches/logs are not purged.
- Temporary CLIENT1 access NIC and firewall rule removed. Host TrustedHosts restored
  to exactly localhost. VMs, installed NodePilot service and Dev instance are left
  running; no unrelated application process is stopped.
- All eight test workflows are disabled, their executions terminal. Stored member
  configurations use gpt-5.6-luna exclusively. Evidence/history remains available.
- Reviewed 200 shell/external-tool invocation records across the eight runs; no
  MSI, Win32_Product, CIM-method or other target mutation request was found. Tool
  inventories contain reads and internal investigation/delegation operations.
  Neither target logged MsiInstaller events from 16:39Z through the final checks.
  This is an audit of this round, not a proof about every possible future command.

No production agent code, permissions or diagnostic skills were changed in this
round. The case-6 repeat broadens only the test members' file path configuration to
C:\; it retains the same model, blind brief and read-only host policy.

## Follow-up candidates observed in these runs

- Make excerpt boundaries explicit and recover full source context when a reviewer
  needs it. In case 7, searching for the timestamp clips `UPDATE_INSTALLED` to
  `PDATE_INSTALLED`; a later literal search of that stored excerpt necessarily
  fails. The agents treat this as unverifiable evidence instead of reading the
  identified original source line.
- Preserve query semantics: do not silently interpret a requested regex as a
  literal string. Either support bounded matching or reject unsupported syntax
  with a precise explanation and an equivalent supported query strategy.
- Extend genuinely read-only inspection paths for application/deployment-type
  definitions, effective IIS settings, certificates and servicing capability
  status. Keep side-effecting providers such as Win32_Product prohibited.
- Discover actual event-provider names and relevant log subdirectories instead
  of repeatedly querying an assumed provider or only the parent directory.
- Correlate resource identity, operation and time before promoting an old error
  into the current cause. Matching host or similar display name alone is not
  sufficient.
- Avoid repeated unchanged reviews and edits to closed investigation entries;
  use the remaining budget for a discriminating new read or an honest bounded
  conclusion. These are general improvements, not rules for individual scenarios.

## Primary references for the test mechanisms

- [WSUS publishing API](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bb902478(v=vs.85))
- [Locally published update trust](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/bb902479(v=vs.85))
- [Client update evaluation states](https://learn.microsoft.com/en-us/intune/configmgr/develop/reference/core/clients/sdk/ccm_softwareupdate-client-wmi-class)
- [Maximum runtime and maintenance windows](https://learn.microsoft.com/en-us/intune/configmgr/develop/reference/core/clients/sdk/iswindowavailablenow-method-in-class-ccm_servicewindowmanager)
- [Windows Setup compatibility evidence](https://learn.microsoft.com/en-us/troubleshoot/windows-client/setup-upgrade-and-drivers/use-windows-setup-compatibility-scan-logs-to-identify-blocking-issues)

- [State-message processing and backlog](https://learn.microsoft.com/en-us/troubleshoot/mem/configmgr/setup-migrate-backup-recovery/state-message-processing-performance)
- [WSUS connection failures](https://learn.microsoft.com/en-us/troubleshoot/mem/configmgr/update-management/troubleshoot-wsus-connection-failures)
- [WSUS recycling and memory limits](https://learn.microsoft.com/en-us/troubleshoot/mem/configmgr/update-management/windows-server-update-services-best-practices)
- [Maintenance windows](https://learn.microsoft.com/en-us/intune/configmgr/core/clients/manage/collections/use-maintenance-windows)
- [WSUS and scan-source policies](https://learn.microsoft.com/en-us/windows/deployment/update/wufb-wsus)
