# CLIENT1 cause-and-remedy follow-up

Date: 2026-09-19. Branch: feature/ai-agent-activities.

## Lab licensing

The controller used the remaining supported Windows evaluation rearms and
restarted CLIENT1 and GW1. CLIENT1 additionally required successful slmgr /ato
once routing was available. Both reported LicenseStatus 1. Reported expiration:
CLIENT1 2026-12-18; GW1 2027-03-18. No licensing enforcement was disabled.
This removes the observed expired-evaluation shutdown cause; it is not a
promise that the machines can never shut down for another reason.

## Read capability and diagnosis

The existing PowerShell tool now exposes a generated catalog of its supported
read commands and parameters. Added effective local firewall rules and filters,
DNS resolution, addresses/routes (including Find-NetRoute), TCP connections,
adapters, storage, tasks, Defender status, and selected known read CIM classes
including ConfigMgr client/site data. CMD and Bash read variants were expanded.
No replacement diagnostic tools or user-maintained command whitelist were added.

Remote target/credential overrides, arbitrary CIM methods, Win32_Product,
dynamic code, writes and unsafe command variants remain rejected. This is a
checked command subset, NOT universal proof that any arbitrary script is read-only.
A legitimate unsupported command may still need an implementation extension.
HTTP/MCP/workflow permission semantics were not relaxed in this change.

Runtime instructions request concrete cause, discriminating evidence, minimal
proposed remedy, limitations and post-change verification. The versioned Windows
skill adds firewall filter matching, DNS/route checks, Block-rule precedence,
and warnings against inferring current failure from historical errors. Changes
are advisory improvements, not a guarantee of correct model reasoning.

## Automated validation

- New positive permission cases failed before their implementation.
- Final permission/shell test selection: 121 passed, 0 failed.
- Agent runtime tests: 9 passed.
- API build: zero errors; readiness Healthy. Installed service untouched.

## Blind live tests over HTTPS WinRM

Each run created fresh supervisor, log analyst, configuration analyst and reviewer
sessions, explicitly bound to CLIENT1. The task contained a symptom, not the
injected rule or controller oracle. Only the controller created/restored faults.
Agents could propose a repair but could not execute it. Workflows were disabled
after execution. Full journals and controller evidence: .runlogs/agent-cause/.

| Run | Execution | Result |
| --- | --- | --- |
| Scan initial | 8813c7a4-a6a5-4a9c-aa75-b6f0736d76ac | Found matching block; correctly reported missing DNS correlation. DNS reads subsequently added. |
| Scan repeat | 7ea8da3f-dcd6-491f-b026-1ff0ce900249 | Independently matched CM1 DNS 10.0.0.7, configured WSUS TCP/8530 and active Lab-Traffic-41A7 rule/filters; proposed scoped block correction. |
| Policy initial | 69980627-9f6b-4838-9d6f-a4ae319adb1e | Matched MP HTTP/80 and Lab-Traffic-93B2; route review incorrectly treated missing /32 as inconclusive. Added Find-NetRoute and covering-route guidance. |
| Policy repeat | f5e5b97f-4036-49e9-a37f-2e5144b1d762 | Matched current block, DNS, MP and WinHTTP 12029. Proposed correcting the specific rule, not disabling firewall or adding an ineffective Allow. Preserved uncertainty about historical attribution and an established TCP connection. Remote skill scripts succeeded. |

The policy repeat did not fully correlate the established connection's owner and
creation time with the failed request. The supervisor retained the limitation
instead of doing a further delegation. The current matching blocker was found;
exclusive historical causality was NOT established. A reviewer also attempted
the nonexistent Get-TCPConnection; correct Get-NetTCPConnection is supported.

Each of these four run windows had zero MsiInstaller events and zero checked
WindowsUpdateClient installation events (IDs 19/20/21). This is scoped event
and tool-journal evidence, not universal proof of absence of all possible writes.

Controller restoration returned HTTP 200 for both endpoints. WUAHandler recorded
Successfully completed scan at 14:35:49.938+420; PolicyAgent recorded a successful
assignment response (no new assignments) at 14:51:57.938+420. Raw CMTrace timestamps
are preserved; their +420 bias must not be read as an ISO timezone offset.

## Final cleanup

Cleanup completed: CurrentUser policy restored to Undefined (effective Restricted). Temporary HTTPS certificate, host trust, NIC and firewall access removed; original listener restored and checked. Both fault rules and watchdog absent. CLIENT1, GW1, DC1 and CM1 remain running for continued testing.

## Remaining acceptance limits

This follow-up covers client-side scan/policy network causes. It does not close
the genuine Required-but-not-installed scenario, every content/server-side
cause, or a universal read-only shell guarantee. Server-side investigations need
an explicitly bound server member; CLIENT1 tools may not silently switch targets.

Follow-up: [server content diagnosis](ai-agent-server-content.md) adds and live-tests
CM1 provider/boundary reads with fault and restoration controls. Its scope remains
server-side; it does not replace a fresh simultaneous CLIENT1 download test.

Later [real update compliance validation](ai-agent-update-validation.md) reproduces
Required through an authorized platform rollback and repeats Unknown with CLIENT1
and CM1. It records partial/failed diagnosis acceptance and a local-deadline time
representation issue; engine success must not be read as a fully passed validation.

### Healthy countercheck iteration

Execution c143b00a-591e-4f52-b862-38ca0f9ffa0e found the successful scan and did
not assert a current scan failure. However it described later historical MP
errors as a possibly remaining fault without correlating the newer successful
PolicyAgent assignment response. Treat this as a diagnosis-quality failure,
not a fully passed healthy control. Its MSI/WU installation event counts were 0.

Skill version 1.0.7 now explicitly requests cross-log recovery correlation and
recognizes a successful assignment response with no new assignments as success.
The controller independently triggered scan schedule 113 after restoring all
faults; WUAHandler reports success at 14:55:24.847+420. This controller action is
not an agent action. A fresh blind healthy repeat uses the same symptom-only task.


### Final healthy repeat and audit qualification

Execution 4dbf8c3c-54fc-455c-b557-ad1021cb97b4 succeeded in 187.3 seconds,
26 model calls, 74 tool calls and four delegations. The supervisor DID perform
a follow-up after review. The final report identifies the latest successful
scan, reads the later successful MP deliveries and does not assert a demonstrated
remaining fault or propose a blind repair. It remains excessively cautious about
current health and historical attribution; no universal diagnosis guarantee follows.

The event audit found zero MsiInstaller events but TWO WindowsUpdateClient event
19 records in this final window: 5803 at 21:55:44.856Z and 5807 at 21:56:05.481Z,
both Microsoft.WindowsAppRuntime.2. Therefore this run is NOT a zero-installation
event window and must not be described as such.

AppXDeploymentServer events correlate these with x-windowsupdate package staging
under SYSTEM, options BackgroundTaskOption/LowPriorityRequest (x86/x64 2.5.1.0).
Both completed before the first agent PowerShell command at 21:56:13Z; the only
skill executed before them was inspect-sccm.ps1 at 21:55:31Z, containing solely
Get-Service CcmExec plus Select-Object/ConvertTo-Json. Other calls then were log
reads. The journal contains no installer, update invocation or CIM method.
This supports an independent background update rather than an agent-initiated
installation, but the original background initiator was not conclusively traced.
No unrelated background update was uninstalled or disabled to clean the result.
Evidence: healthy-repeat-audit.json, recent-update-events.json, appx-events.json,
complete run journal, execution-policy-restored.json and cleanup.json.

