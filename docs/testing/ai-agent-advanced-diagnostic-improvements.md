# General diagnostic improvements after the advanced ten-scenario round

Branch: `feature/ai-agent-activities`. Lab: CLIENT1 and CM1, site CHQ.
Diagnostic model: `gpt-5.6-luna` for every member. Fault controllers and restoration
are separate from the read-only agent workflows. Test briefs disclose symptoms,
affected objects and the current observation window, never the injected cause.

## Changes

- Search preserves complete ordinary log lines. Long-line snippets carry fragment
  offsets and explicit omission markers; bounded memory/output remain enforced.
  A failed search of stored, potentially clipped evidence does not establish
  absence in the original. Agents are directed to retrieve original context.
  A missing recall term still returns a bounded original page, so an empty successful
  command result is not mistaken for inaccessible evidence. Input/output views remain
  separate; queried paths need not occur in the command's output.
- PowerShell search no longer silently turns regex alternatives into literal text.
  Unsupported regex requests fail with an explanation and a supported literal-array
  alternative. Literal matching and bounded surrounding context are supported.
- Checked reads cover effective local IIS configuration, pool state, certificate
  inventory, Authenticode signatures, online servicing capability/feature state,
  event-provider discovery, application/deployment-type definitions, collection
  settings and client service windows. ConfigMgr lazy application properties use
  an instance GET, rather than treating an enumeration's empty property as absent.
  Explicit `SDMPackageXML` projections are removed from the enumeration and fetched
  by that GET: the SMS provider rejects the lazy field in SELECT projections.
  Other projected fields remain validated by the provider. Literal schema discovery
  is allowed within existing namespaces without granting instance access to those classes.
- Target-bound tools advertise their actual target; team findings retain that
  machine ID. Instructions distinguish a failed query on the wrong machine from
  a failed check on the intended machine, and require schema/provider discovery.
- Correlation instructions distinguish current operation/object/phase evidence,
  stale errors, effective configuration and worker state. Known W3C-format dates
  are UTC, while ambiguous timestamps remain explicitly unresolved.
  The newly permitted CCM_ServiceWindow read also preserves raw DMTF fields. A live
  read exposed CIM displaying `06:39` for the provider's raw `13:39...+000` while
  the native window was active at local 13:54; implicit conversion is not retained.
- A reviewer receives at most three dispatches without a distinct new system
  observation. Changing shared notes does not reset that gate or count as progress.
  Shell clock/duration envelope changes alone do not create new observations;
  actual outputs and errors still do. Review approval is never manufactured.

These changes contain no scenario-specific fault signatures, expected answers or
diagnostic decision tables. The existing generic Windows/ConfigMgr skill is unchanged.
The shell policy remains a checked subset, not a guarantee that every conceivable
read-only PowerShell expression is accepted. Win32_Product and mutation paths remain
prohibited.

## Validation

The final Engine agent suite passed 344 tests and the AI suite passed 609 tests.
Direct WinRM probes exercised the new IIS, certificate, signature,
servicing and event-provider commands. An actual application instance returned
7,376 characters of SDMPackageXML through the new lazy-property path. The corrected
explicit projection returned 7,400 characters for deployment type CI 16779096.

Live repeats and final cleanup are recorded below. A terminal workflow is
not automatically a correct diagnosis; each conclusion is compared with controller
evidence and the proposed remedy.

| Case | Repeat result | Evidence and limits |
|---|---|---|
| 3: HTTPS client certificate | Cause and targeted remedy identified | Effective `SslRequireCert` read; 63 model / 123 tool calls, 12 delegations, 367.8 s. Execution `c19447f1-ca26-4df5-b048-78c6cfa05ff8`. This repeat preceded the final widening of adjacent IIS authentication reads and W3C-time clarification. |
| 4: WsusPool memory limit | Mechanism established; remedy still too conditional | Actual WAS 5117 and effective `privateMemory=50000` were correlated. Luna did not establish the setting's unit/sizing and withheld a concrete correction pending that check. No current failed client scan was invented. 66 model / 106 tool calls, 12 delegations. |
| 5: maintenance window, first repeat | Incomplete: bounded model timeout | The client identified the assignment-level `0x87D00667` and 3,600-second requirement, but server reviewer call 32 exceeded 180 seconds. Run failed without retry, 329.4 s, 76 tools / 4 delegations. This run exposed the ServiceWindow time-conversion issue corrected above. The controller originally waited on the per-update error code; native assignment failure existed while that code remained 0, so the controller proof was corrected to use the assignment log. |
| 5: maintenance window, corrected timestamps | Incomplete: investigation exhausted budget | 99 model / 141 tool calls, 14 delegations, 478.1 s; failed before a reviewed final report. Raw timestamps were preserved. The team identified the scheduling failure but misclassified Type 6 as a software-update window (it represents non-working hours), instead of correctly interpreting the Type-4 1,800-second window against the 3,600-second requirement. Additional permission changes do not resolve this incorrect interpretation. |
| 6: publisher trust | Wrong causal lead; not solved | 90 model / 205 tool calls, 12 delegations, 586.1 s. The team pursued unrelated historical DP content and did not inspect the available converted Windows Update log/current publisher trust. The final report qualified historical attribution but still promoted the wrong DP object as the plausible cause. Controller proof: native download `0x8024B303`; restoring only the owned publisher certificate made it succeed with `0x00000000`, downloaded=true. No installation occurred. |
| 7: stopped StateSys component | Processing block established; root-cause/remedy qualification still excessive | 99 model / 168 tool calls, 17 delegations, 589.9 s. The report ties the exact installed update to its queued `X10K6KPX.SMX` and unchanged server Required record, and finds the StateSys stop despite SMS_EXECUTIVE Running. It still treats that stop as only a plausible mechanism and leaves targeted resumption conditional. Reviewers additionally ask for post-repair success during a read-only fault diagnosis. Execution `eba06a43-22cb-4b76-bf19-0610137eac5d`. Partial, not a complete causal/remediation pass. |
| 8: application detection, first repeat | Partial cause; completed instead of failing | Negative detection despite successful installer and existing marker identified, but the concrete detection expression was still missing. 100 model / 157 tool calls, 14 delegations, 535.1 s. Execution `fedd5ad5-202a-425d-acc9-e24bde89d9d2`. This exposed the explicit lazy-field projection failure fixed above; it is not counted as a full diagnosis. |
| 8: application detection, corrected projection | Concrete mismatch and correction found; revision claim wrong | 88 model / 147 tool calls, 14 delegations, 483.6 s. The report identifies `SOFTWARE\\WOW6432Node\\NPAdvanced20260921` versus the installed `SOFTWARE\\NPAdvanced20260921` marker. It incorrectly claims DeploymentType /5 and treats the client's /4 as a separate historical version. Controller inspection of the same XML proves Application Version=5 and DeploymentType Version=4. The erroneous revision claim also affects its success criteria, so this is not an unqualified pass. |
| 10: missing FoD source | Cause and targeted remedy identified | Native missing-CAB/local-source-only failure and current `NotPresent` state; 73 model / 175 tool calls, 10 delegations, 365.0 s. Execution `3982b242-d4d3-4d4b-a62c-e2c1cc7d9c20`. Exposed unnecessary recall retries on empty command output; the bounded no-match preview was added afterwards. Historical CBS timezone remained explicitly unresolved. |

Local controller evidence and complete events: `.runlogs/agent-advanced-fixes/`.

An initial case-7 launch overlapped the controlled API restart and was cancelled
after two model calls. It is an invalid trial, excluded from diagnostic scoring;
its workflow was explicitly disabled. The stopped component was restored and its
queue drained to zero with server compliance Installed before further tests.
The next case-7 preparation found the server still Installed, so no diagnostic run
was launched against that false premise. A separate native rollback/scan/report cycle
first established server Required, then state processing was stopped and the client
platform restored. Only after confirming client Installed, server Required and a
blocked incoming queue was the final blind run started.

## Remaining quality gaps

The six technical improvement areas are implemented, but their intended diagnostic
outcomes are not universally achieved. In particular, stale-object correlation and
discovery of relevant log subdirectories still fail in case 6. Enum/unit meanings
and nested object revisions are misinterpreted in cases 5 and 8. Review dispatches
are bounded, yet new reads and changing evidence still permit expensive review loops;
closed-entry updates are rejected rather than making progress. Case 7 demonstrates
that reviewers can incorrectly demand observed post-repair recovery before accepting
a read-only diagnosis and proposed success check.

These are measured remaining failures, not proof that the model alone is responsible.
Do not describe all scenarios as solved or add their known answers to production prompts.
Cases 1, 2 and 9 were not repeated here; their earlier lab prerequisites remain outside
this validation. This round contains nine completed diagnostic trials over seven cases
and one explicitly invalid cancelled launch.

## Restoration and action audit

Verified at 2026-09-21 21:32 UTC:

- StateSys Running, incoming queue empty, exact update compliance Status 3/Installed.
  Defender platform restored to 4.18.26080.4; saved scan-source policies match.
- WsusPool Started with original 1,265,011 KB limit; WSUS HTTP 200, original sslFlags
  and empty HTTPS certificate assignment restored. Owned publisher fault assets,
  certificates, FoD source, update deployment/window and recovery tasks removed.
- Owned application/deployment/distribution, collection, source, marker and exact
  owned client cache entry removed. Application deletion required one retry while
  ConfigMgr propagated removal of the deployment.
- Temporary CLIENT1 NIC/firewall removed; host TrustedHosts restored to `localhost`.
  VMs and installed NodePilot service remain running. Dev readiness returns HTTP 200.
- All ten workflows are disabled with terminal executions; every configured member
  uses `gpt-5.6-luna`. Run history/evidence is retained.
- Audited 335 shell/external-tool invocation records from the nine completed trials.
  The three mutation-pattern candidates were only `Get-Content` reads of `DISM.log`.
  No MSI/Win32_Product or target mutation invocation was found. Both targets have
  zero MsiInstaller events in the checked period starting 16:39 UTC. Controller
  fault injection and restoration are separate authorized actions, not agent tools.
  This bounded audit is not a proof of safety for every possible future command.

Final artifacts: `server-final.json`, `client-final.json`, `host-final.json`,
`assets-*-cleanup.json`, `workflow-final.json`, `run-summary.json` and
`tool-audit.jsonl` under `.runlogs/agent-advanced-fixes/`.

## References

- [ConfigMgr lazy properties](https://learn.microsoft.com/en-us/intune/configmgr/develop/core/understand/configuration-manager-lazy-properties)
- [Get-CimInstance instance retrieval](https://learn.microsoft.com/en-us/powershell/module/cimcmdlets/get-ciminstance)
- [W3C log time fields](https://learn.microsoft.com/en-us/windows/win32/http/w3c-logging)
- [CCM_ServiceWindow fields and units](https://learn.microsoft.com/en-us/intune/configmgr/develop/reference/core/clients/sdk/ccm_servicewindow-client-wmi-class)
