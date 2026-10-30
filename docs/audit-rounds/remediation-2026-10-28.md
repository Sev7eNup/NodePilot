# Audit remediation and coherence follow-up — 2026-10-28

## 1. Executive summary

Work is isolated on `fix/audit-security-and-coherence`, based on audited revision `a004114d`. Changes are local and uncommitted. The user's pre-existing website image-manifest change is excluded from this work.

Both High findings, **R01 and D01**, are implemented. **87 of the 88 Medium findings** are implemented: 72 in the first pass, UI-F02 and 14 delivery findings in a follow-up pass on 2026-10-30. D28 is open again because its fix was reverted on request. All 88 have an English GitHub issue: 81 new issues (#658–#738) and seven existing matching issues. “Implemented” describes the working tree and verified behavior; it does not mean deployed or that the GitHub issue has been closed.

The repository is mostly coherent with some remaining drift. This pass concentrates policy at shared seams instead of adding independent fixes to each caller.

## 2. Detected architecture

The existing dependency direction is retained: Core owns domain contracts, Data owns persistence, Ai owns model transport and generation, Engine owns execution, Scheduler consumes execution/notification contracts, and Api exposes them. CLI and MCP remain HTTP clients. React authoring, live execution and documentation remain separate projects.

The main mismatches addressed were duplicated readiness checks, settings read/write/provider semantics, editor graph reference rewrites, debug pause state, recipient-bound AI history, and synthetic harnesses that could report success without the expected evidence.

## 3. Coherence scorecard

These are qualitative engineering assessments, not measured coverage percentages or a claim that every repository line was re-audited after these changes. Higher AI Artifact Risk score means **lower drift risk**. Scores above 8 would require stronger repository-wide uniformity.

| Dimension | Before | After | Evidence and remaining limit |
|---|---:|---:|---|
| Overall Architecture Coherence | 7 | 8 | Shared settings, AI and execution contracts now align across callers; the installer and editor seams are fixed but the installer side is proven only offline. |
| Project Structure Consistency | 8 | 8 | Changes follow existing project ownership; no new layer or dependency cycle is introduced. |
| Naming Consistency | 7 | 7 | Recipient keys, pause snapshots and graph references have explicit shared meanings; broader naming cleanup was not required for these fixes. |
| Dependency Discipline | 8 | 8 | Existing project direction retained; no new package dependencies or lockfile churn. |
| Duplication Control | 5 | 7 | Shared settings snapshots, graph reference visitor, SSE completion writer, lab identities, CI classifier and MCP readiness remove competing implementations. Form save completions use the shared `useAsyncIdentity` session check. |
| Pattern Consistency | 5 | 7 | Revision fencing, configuration validation, debug overrides and history binding use the same policy at their real callers. Installer rollback now snapshots service registrations and install-root ACLs before changing them; verified with fixtures only. |
| Test Consistency | 6 | 8 | Behavioral regressions cover real export/restore, stale callbacks, concurrent saves, reconnects, exporter output and negative harness cases. Live AD/install/Prometheus environments remain outside local evidence. |
| Maintainability | 6 | 7 | Settings merging and custom-activity revision handling now have one owner; shared UI reference traversal and client readiness reduce change locations. Large restore/installer flows still need care. |
| AI Artifact Risk (high = low risk) | 5 | 7 | Actual exported metrics, emitted SSE, mirrored DTOs, real editor flows and runnable runbook contracts replace assumptions. Deployment fixes without a real install, AD or SQL run prevent a stronger rating. |

## 4. Major findings and outcomes

- **R01 — High:** persist the latest group observation on the exact external identity. SCIM changes fence older OIDC group snapshots, including folder/access changes. Equal-second tokens may replay the same group set but cannot replace it with a conflicting set. Migration backfill and restore initialization fence pre-existing tokens; a new login may be required.
- **D01 — High:** installer health/bootstrap uses a shared, per-request HTTPS helper pinned to the configured local server certificate. It rejects redirects and does not install a process-wide trust bypass.
- **R02–R13:** logout revokes the session family atomically; timestamps compare normalized instants; restores preserve same-name identities, valid global names and monotonic custom-activity revisions; settings payloads and ETags use one captured runtime document; strict host policy uses the same boot/save validator; machine statistics use the resolved identity; exports respect the restore envelope limit; subscriber removal is conditional; restore compensation cannot overwrite a newer settings document.
- **CD01–CD04 / RT01–RT13:** fresh transaction attempts, explicit refusal audit outcomes, shared reserved variables, bounded graph analysis, recipient-bound history, redacted errors, overlapping debug pauses, explicit credential validation, durable terminal notification collection, actual call depth, faithful condition evaluation, full-token/block redaction, ACL-preserving text replacement, explicit stream completion, breakpoint manual inputs and literal substitutions.
- **UI:** all 26 findings implemented. Shared graph traversal, draft identity, confirmed database writes, reconnect hydration, pause-scoped overrides, recipient consent, bounded persistence and corrected examples/runbooks have regression coverage. UI-F02 binds save completions in every CRUD editor to the editing session that started them and locks the form while the save is pending.
- **Delivery:** 33 of 34 findings implemented including D01; D28 was implemented and then reverted on request and is open again. Shared client readiness, redirect/token policy, paged suite verification, stronger lab evidence, metrics contracts and CI classification came in the first pass. The follow-up pass adds caller-version updates in CLI/MCP (D02), service and install-root ACL snapshots for rollback (D04, D30), fail-stop uninstall (D09), locale-independent ownership recovery (D26), SID-bound desktop token handoff (D27), redacted MCP webhook targets (D25), SCOrch transport validation in both directions (D23), and corrected test-suite and example scripts (D11, D15, D16, D18, D19, D29).

The settings writer additionally protects temporary content with the target access policy before writing, cleans temporary files after failure and requires an atomic replacement instead of falling back to a permission-weakening move.

## 5. Duplication and consolidation

| Shared module | Callers / behavior concentrated |
|---|---|
| `SettingsConfigurationSnapshot` | Settings GET, successful PUT, stale-ETag response and pre-save validation use a captured effective provider chain. Existing atomic-list and secret adapters remain authoritative. |
| `AllowedHostsBootValidator` | Boot validation and settings-save validation share strict host policy. |
| `CustomActivityDefinitionStore.StageRevision` | Normal edits, rollback and backup overwrite share history, version and concurrency-token updates. |
| `DebugOverrides` and paused snapshots | API and engine validate before consuming a pause; API/UI/CLI/MCP expose copied, redacted pause state. |
| `LlmHistoryContext` / `AiStreamSupport` | Workflow and knowledge chat bind history to recipient policy and emit the same completion metadata; script consent checks the acknowledged recipient before creating a client. |
| `workflowReferences` / graph commands | Rename, duplicate and clipboard transformations traverse the actual condition/configuration shapes; palette actions use normal history/dirty-state commands. |
| MCP `ReadyHttp` | Central readiness replaces 99 scattered guards and covers previously omitted endpoint partials. |
| `useAsyncIdentity` | Workflow execution and all CRUD editors (settings, custom activities, alerting rules and policies, credentials, globals, maintenance windows, users, machines) ignore save completions from an earlier editing session. |
| MCP `AlertRouteTargetRedactor` | Rule, policy, delivery and test-fire output show webhook targets only as scheme, host and port. |
| Switcher `RequireScorchTransport` | Switch preflight in both directions and the `ScorchApiClient` constructor share the remote-HTTPS rule. |
| `LocalServerHttp.ps1`, `LabIdentity.ps1`, `TestSuiteVerification.ps1`, `classify-changes.sh` | Setup TLS, lab identities, suite result verification and CI/CodeQL classification have shared owners. |

## 6. Structural problems

No project restructuring was needed to implement the fixes. The follow-up pass closed the structural items previously listed here: form completion guards (UI-F02), workflow replace concurrency (D02), installer rollback ownership (D04/D09/D30), and scope/identity handling in sample and maintenance scripts. What remains is proof in real environments (section 11), not open code.

## 7. Naming and terminology

The implementation uses existing domain vocabulary: Workflow, Execution, Step, ExternalIdentity, DirectoryMembership and CustomActivityDefinition. `GroupsObservedAt` is an authorization observation fence, not login activity. `HistoryContextKey` identifies the allowed AI-history recipient context; `ExpectedRecipientKey` records the recipient acknowledged for script sharing. `DebugPausedStepSnapshot` is detached display state, not the mutable resume handle.

## 8. Pattern consistency

Use compare-and-update semantics when a response or rollback depends on an earlier snapshot. Use one captured profile/configuration for a request. Keep validation before state consumption. Preserve explicit identities across asynchronous completions. Tests exercise those policies through controllers, stores, streams, UI commands and real serialized files.

The backup upload limit is 64 MiB for the archive; the multipart request allows 1 MiB additional framing. Settings files require a filesystem supporting atomic replacement; a failed replacement preserves the original file. CLI/MCP token filenames now use collision-resistant profile identity: existing locally cached sessions require one fresh login.

## 9. AI/contributor drift indicators

Corrected concrete drift includes invalid PowerShell interpolation, unsupported Filebeat HTTP output guidance, incorrect metric names/labels, contradictory gMSA/RBCD guidance, ignored visible editor defaults, mismatched pause DTOs and dropped knowledge-history SSE metadata. The score reflects these corrections and executable guards, not a guess about who authored the original code.

## 10. Target architecture

Keep the current project layers. Shared modules own state transitions and externally visible contracts; callers supply intent and identity. Add a new interface only for a real second implementation or a meaningful test seam. Continue extracting policy only where multiple real callers currently diverge.

## 11. Prioritized remaining work

1. Validate the remaining deployment/telemetry contracts in their real environments. D04, D09, D26, D27 (handoff to the installing SID), D30, D15 and D16 passed in the lab (section 12). Still unvalidated: the D27 handoff under UAC with other credentials, RDP, user switching and SYSTEM-run installs (no interactive desktop in the lab), and a live D23 Switcher preflight against SCOrch.
2. Found in the lab, not fixed: after a failed server reinstall the Apps & Features entry is missing. `Install-NodePilot.ps1` treats it as stale once the install directory is emptied for the swap, and the rollback restores `unins000.exe` but not the registry entry; the next successful update recreates it.
3. D02 is a deliberate contract change: `np workflow publish` requires `--expected-version`, and the MCP tools `publish_workflow` and `update_workflow_definition` require `expectedVersion`. Existing scripts and agent prompts must pass it.
4. D28 is open: a fix that rejected a certificate host name differing from the server address was reverted on request, so setup can still connect through the certificate host name to a different SQL Server instance.
5. D11: nightly run folders created before the ownership marker existed are never pruned; remove them once by hand. D16: each test-suite install rotates the webhook secret, so a driver run during or after a failed install can fail once.

### Complete issue register

| Finding | GitHub issue | Working-tree status | Issue title |
|---|---|---|---|
| R02 | [#658](https://github.com/Sev7eNup/NodePilot/issues/658) | Implemented | Revoke the session family reliably when logout races with refresh |
| R03 | [#659](https://github.com/Sev7eNup/NodePilot/issues/659) | Implemented | Normalize maintenance-window timestamps before validating their order |
| R04 | [#660](https://github.com/Sev7eNup/NodePilot/issues/660) | Implemented | Preserve distinct same-name resources during backup restore |
| R05 | [#661](https://github.com/Sev7eNup/NodePilot/issues/661) | Implemented | Build settings responses and ETags from the same configuration snapshot |
| R06 | [#662](https://github.com/Sev7eNup/NodePilot/issues/662) | Implemented | Preserve inherited authentication secrets when validating settings updates |
| R07 | [#663](https://github.com/Sev7eNup/NodePilot/issues/663) | Implemented | Apply production hardening validation before saving settings |
| R08 | [#664](https://github.com/Sev7eNup/NodePilot/issues/664) | Implemented | Attribute machine step statistics to the resolved managed-machine identity |
| R09 | [#665](https://github.com/Sev7eNup/NodePilot/issues/665) | Implemented | Keep backup export sizes compatible with the restore upload limit |
| R10 | [#666](https://github.com/Sev7eNup/NodePilot/issues/666) | Implemented | Update execution subscription counts atomically across joins and leaves |
| R11 | [#667](https://github.com/Sev7eNup/NodePilot/issues/667) | Implemented | Generate valid global-variable identifiers during duplicate restore |
| R12 | [#668](https://github.com/Sev7eNup/NodePilot/issues/668) | Implemented | Preserve custom-activity concurrency and history invariants during restore |
| R13 | [#669](https://github.com/Sev7eNup/NodePilot/issues/669) | Implemented | Avoid overwriting concurrent settings changes during restore rollback |
| CD01 | [#670](https://github.com/Sev7eNup/NodePilot/issues/670) | Implemented | Retry notification-rule mutations from fresh database state |
| CD02 | [#671](https://github.com/Sev7eNup/NodePilot/issues/671) | Implemented | Classify refused directory authentication as failed audit events |
| CD03 | [#672](https://github.com/Sev7eNup/NodePilot/issues/672) | Implemented | Share PowerShell reserved-variable validation for custom activities |
| CD04 | [#673](https://github.com/Sev7eNup/NodePilot/issues/673) | Implemented | Bound workflow graph analysis for large valid definitions |
| RT01 | [#674](https://github.com/Sev7eNup/NodePilot/issues/674) | Implemented ([#740](https://github.com/Sev7eNup/NodePilot/pull/740)) | Bind knowledge-assistant history to the active LLM recipient |
| RT02 | [#675](https://github.com/Sev7eNup/NodePilot/issues/675) | Implemented | Redact provider and retry errors before logging them |
| RT03 | [#676](https://github.com/Sev7eNup/NodePilot/issues/676) | Implemented | Keep execution timeout suspended until all paused branches resume |
| RT04 | [#677](https://github.com/Sev7eNup/NodePilot/issues/677) | Implemented | Reject invalid explicit dynamic credentials before execution |
| RT05 | [#678](https://github.com/Sev7eNup/NodePilot/issues/678) | Implemented | Collect execution notifications without losing late database commits |
| RT06 | [#679](https://github.com/Sev7eNup/NodePilot/issues/679) | Implemented | Preserve actual execution call depth in notification context |
| RT07 | [#680](https://github.com/Sev7eNup/NodePilot/issues/680) | Implemented | Fail conditions explicitly when regex input cannot be evaluated faithfully |
| RT08 | [#681](https://github.com/Sev7eNup/NodePilot/issues/681) | Implemented | Remove the complete GitHub token from redacted output |
| RT09 | [#682](https://github.com/Sev7eNup/NodePilot/issues/682) | Implemented | Redact secret blocks before truncating activity output |
| RT10 | [#683](https://github.com/Sev7eNup/NodePilot/issues/683) | Implemented | Preserve file security metadata when replacing edited text files |
| RT11 | [#684](https://github.com/Sev7eNup/NodePilot/issues/684) | Implemented ([#739](https://github.com/Sev7eNup/NodePilot/pull/739)) | Reject truncated LLM event streams instead of reporting successful generation |
| RT12 | [#685](https://github.com/Sev7eNup/NodePilot/issues/685) | Implemented | Resolve manual inputs in breakpoint conditions |
| RT13 | [#686](https://github.com/Sev7eNup/NodePilot/issues/686) | Implemented | Keep substituted template-looking input values literal |
| UI-S1 | [#574](https://github.com/Sev7eNup/NodePilot/issues/574) (existing) | Implemented | Mask secret scalar leaves in workflow clipboard fragments |
| UI-S2 | [#687](https://github.com/Sev7eNup/NodePilot/issues/687) | Implemented | Use authenticated HTTPS in the secret rotation runbook |
| UI-S3 | [#688](https://github.com/Sev7eNup/NodePilot/issues/688) | Implemented | Bind script-context consent to the effective AI recipient |
| UI-F01 | [#689](https://github.com/Sev7eNup/NodePilot/issues/689) | Implemented | Rewrite all workflow references when an output alias changes |
| UI-F02 | [#690](https://github.com/Sev7eNup/NodePilot/issues/690) | Implemented | Bind asynchronous form completions to their editing session |
| UI-F03 | [#606](https://github.com/Sev7eNup/NodePilot/issues/606) (existing) | Implemented | Reconcile known execution steps after SignalR reconnect |
| UI-F04 | [#691](https://github.com/Sev7eNup/NodePilot/issues/691) | Implemented | Require explicit confirmed-write execution in the database viewer |
| UI-F05 | [#692](https://github.com/Sev7eNup/NodePilot/issues/692) | Implemented | Guard asynchronous workflow operations by draft identity |
| UI-F06 | [#693](https://github.com/Sev7eNup/NodePilot/issues/693) | Implemented | Preserve branch guards when inserting an activity on an edge |
| UI-F07 | [#694](https://github.com/Sev7eNup/NodePilot/issues/694) | Implemented | Route command palette deletion through graph mutation commands |
| UI-F08 | [#695](https://github.com/Sev7eNup/NodePilot/issues/695) | Implemented | Keep property editors synchronized with undo and restored graph state |
| UI-F09 | [#601](https://github.com/Sev7eNup/NodePilot/issues/601) (existing) | Implemented | Make text-file delete modes persist their selected state |
| UI-F10 | [#696](https://github.com/Sev7eNup/NodePilot/issues/696) | Implemented | Scope debugger override drafts to a single pause |
| UI-F11 | [#697](https://github.com/Sev7eNup/NodePilot/issues/697) | Implemented | Reject read-only debug overrides before resuming a step |
| UI-F12 | [#603](https://github.com/Sev7eNup/NodePilot/issues/603) (existing) | Implemented | Distinguish current configuration from historical execution input |
| UI-F13 | [#698](https://github.com/Sev7eNup/NodePilot/issues/698) | Implemented | Repair PowerShell interpolation in the exportable SCCM/AD example |
| UI-F14 | [#699](https://github.com/Sev7eNup/NodePilot/issues/699) | Implemented | Document correct certificate private-key ACL paths |
| UI-F15 | [#700](https://github.com/Sev7eNup/NodePilot/issues/700) | Implemented | Replace the nonexistent Filebeat HTTP output recipe |
| UI-F16 | [#701](https://github.com/Sev7eNup/NodePilot/issues/701) | Implemented | Keep chat usable when browser persistence reaches quota |
| UI-F17 | [#702](https://github.com/Sev7eNup/NodePilot/issues/702) | Implemented | Persist the displayed port default for wait-for-condition |
| UI-F18 | [#703](https://github.com/Sev7eNup/NodePilot/issues/703) | Implemented | Keep duplicated output aliases independent from existing workflow bindings |
| UI-F19 | [#595](https://github.com/Sev7eNup/NodePilot/issues/595) (existing) | Implemented | Capture property-edit history before the first mutation |
| UI-F20 | [#704](https://github.com/Sev7eNup/NodePilot/issues/704) | Implemented | Qualify provider retention claims in AI documentation |
| UI-F21 | [#705](https://github.com/Sev7eNup/NodePilot/issues/705) | Implemented | Restrict scheduled-task authoring to supported Run-As contexts |
| UI-F22 | [#706](https://github.com/Sev7eNup/NodePilot/issues/706) | Implemented | Consume the actual SignalR live-event envelope |
| UI-F23 | [#707](https://github.com/Sev7eNup/NodePilot/issues/707) | Implemented | Remove blanket delegation grants from direct service-identity WinRM guidance |
| D02 | [#708](https://github.com/Sev7eNup/NodePilot/issues/708) | Implemented | Preserve the caller base version when replacing workflows |
| D03 | [#709](https://github.com/Sev7eNup/NodePilot/issues/709) | Implemented | Preserve custom instance parameters during desktop updates |
| D04 | [#710](https://github.com/Sev7eNup/NodePilot/issues/710) | Implemented | Restore deleted desktop services when provisioning rolls back |
| D05 | [#711](https://github.com/Sev7eNup/NodePilot/issues/711) | Implemented | Keep authentication headers from following cross-origin redirects |
| D06 | [#620](https://github.com/Sev7eNup/NodePilot/issues/620) (existing) | Implemented | Give every client profile a unique token-store identity |
| D07 | [#712](https://github.com/Sev7eNup/NodePilot/issues/712) | Implemented | Clear cached MCP authentication after local session removal |
| D08 | [#713](https://github.com/Sev7eNup/NodePilot/issues/713) | Implemented | Preserve existing principals in gMSA and RBCD repair guidance |
| D09 | [#714](https://github.com/Sev7eNup/NodePilot/issues/714) | Implemented | Stop installer uninstallation when service cleanup fails |
| D10 | [#715](https://github.com/Sev7eNup/NodePilot/issues/715) | Implemented | Recover the original leader after a failed failover test |
| D11 | [#716](https://github.com/Sev7eNup/NodePilot/issues/716) | Implemented | Restrict nightly retention to owned test-run directories |
| D12 | [#717](https://github.com/Sev7eNup/NodePilot/issues/717) | Implemented | Make the SSO revocation drill verify the requested execution |
| D13 | [#718](https://github.com/Sev7eNup/NodePilot/issues/718) | Implemented | Verify every execution in test-suite acceptance windows |
| D14 | [#719](https://github.com/Sev7eNup/NodePilot/issues/719) | Implemented | Reject test-suite acceptance without required test coverage |
| D15 | [#720](https://github.com/Sev7eNup/NodePilot/issues/720) | Implemented | Remove host shutdown cancellation from default continuous tests |
| D16 | [#721](https://github.com/Sev7eNup/NodePilot/issues/721) | Implemented | Constrain webhook test acknowledgements to owned files |
| D17 | [#722](https://github.com/Sev7eNup/NodePilot/issues/722) | Implemented | Use one subworkflow identity across tech-demo seed and callers |
| D18 | [#723](https://github.com/Sev7eNup/NodePilot/issues/723) | Implemented | Keep prior cleanup archives when deleting original files |
| D19 | [#724](https://github.com/Sev7eNup/NodePilot/issues/724) | Implemented | Evaluate fleet health probe results before reporting success |
| D20 | [#725](https://github.com/Sev7eNup/NodePilot/issues/725) | Implemented | Reconcile CLI execution watchers after lost terminal events |
| D21 | [#726](https://github.com/Sev7eNup/NodePilot/issues/726) | Implemented | Reject malformed graph members before MCP definition merging |
| D22 | [#727](https://github.com/Sev7eNup/NodePilot/issues/727) | Implemented | Enforce MCP transport readiness on dependency requests |
| D23 | [#728](https://github.com/Sev7eNup/NodePilot/issues/728) | Implemented | Validate the SCOrch source connection before switching workloads |
| D24 | [#729](https://github.com/Sev7eNup/NodePilot/issues/729) | Implemented | Make CI change classification safe for large file lists |
| D25 | [#730](https://github.com/Sev7eNup/NodePilot/issues/730) | Implemented | Redact credential-bearing webhook targets from MCP alert output |
| D26 | [#731](https://github.com/Sev7eNup/NodePilot/issues/731) | Implemented | Make Windows data purge ownership recovery locale independent |
| D27 | [#732](https://github.com/Sev7eNup/NodePilot/issues/732) | Implemented | Bind desktop bootstrap handoff to the original setup user |
| D28 | [#733](https://github.com/Sev7eNup/NodePilot/issues/733) | Open (fix reverted) | Preserve the requested SQL destination during certificate validation |
| D29 | [#734](https://github.com/Sev7eNup/NodePilot/issues/734) | Implemented | Apply the configured update freshness window in the health example |
| D30 | [#735](https://github.com/Sev7eNup/NodePilot/issues/735) | Implemented | Restore install-directory ACLs when server identity changes roll back |
| D31 | [#645](https://github.com/Sev7eNup/NodePilot/issues/645) (existing) | Implemented | Use the provisioned identity prefix throughout the SSO evidence harness |
| D32 | [#736](https://github.com/Sev7eNup/NodePilot/issues/736) | Implemented | Use exported execution-counter names in observability summaries |
| D33 | [#737](https://github.com/Sev7eNup/NodePilot/issues/737) | Implemented | Include never-successful workflows in reliability rankings |
| D34 | [#738](https://github.com/Sev7eNup/NodePilot/issues/738) | Implemented | Align the HA production example with mandatory SQL TLS validation |

## 12. Verification and guardrails

Validation results are recorded below after the final integration checks. Independent overlapping test runs must not be added together as unique tests.

- UI: all 260 files, **3,799 tests**; **32 hermetic browser checks**; production and demo builds. ESLint: zero errors, ten existing warnings within the configured limit.
- Documentation site: **227 tests**, build successful; six embedded example PowerShell scripts parse.
- Core/Engine/Scheduler: **588 selected tests**.
- Ai: **65 selected tests**.
- Data: **21 migration/notification tests**, including both new migrations, provider-specific SQL generation and round trips.
- MCP: **238 tests**, complete project including documentation counts.
- CLI: **160 scoped tests**, then **37 affected watcher/parity checks** after final changes; these overlap.
- API monitoring/catalog/TLS: **42 tests**, then the strengthened real exporter contract checked again; these overlap.
- PowerShell: **16** local HTTPS assertions, **287** setup assertions, **36** lab assertions, **10** suite-verification assertions and **five** synthetic HA failure cases. Deployment template/desktop/failover contracts pass; all 24 changed scripts parse on Windows PowerShell 5.1.
- Python: **19 tests**; Grafana provisioning: **three tests**.
- Root API integration: **735 tests**, including settings, authentication/OIDC/SCIM, restore/export, AI/SSE, debug, hub concurrency, architecture and frontend contract guards.
- Final Data store/migration checks: **28 tests**, including custom-activity revision history and both migrations (overlaps the earlier Data run).
- Final Engine contract guards: **52 tests** for analyzer/frontend parity, template grammar, reserved variables, trigger contracts, step-state persistence and edge conditions (overlaps the earlier Engine run).

Follow-up pass (2026-10-30), scoped runs; these overlap and must not be added together:

- UI-F02: 11 Vitest files, **135 tests** (the seven editor suites plus four mobile variants). With the session check forced to "current", all seven new regressions fail. `tsc` (app) and ESLint clean on all touched files. Demo: 8 files, **147 tests**, after the guided cleanup tour learned the new `archivePath` step.
- .NET: MCP complete project **244 tests** (includes `DocumentationCountsTests`), Switcher complete project **175**, CLI `CommandIntegrationTests` **67** plus publish/`ApiDtoParityTests` **13**, Engine `TempCleanupWorkflowTests` + `TestSuiteCoverageTests` **10**, `DocumentationCountsTests` again after the script documentation changes **29**.
- PowerShell 5.1: `Test-DeploymentTemplates`, `Test-SetupAdapter` (**294** assertions), `Test-DesktopUpdate`, `Test-DesktopDatabaseProvisioning`, `Test-DesktopUpdatePayload`, `Test-ArtifactSecurity`, `Test-SetupExperience` (**45**), `Test-ServerWizardCompile`; new `Test-TestSuiteSafety` (**80**), `Test-ExampleWorkflowScripts` (**36**) and `Test-NightlyRetention`, plus `Test-WorkflowCatalogue`, `Test-InvasiveFixtureOwnership`, `Test-TestSuiteVerification` and `Test-ContinuousTest1MinBundle`. The three new harnesses are registered in both CI PowerShell steps; their PowerShell 7 run is covered only by CI.
- Python: `test_nodepilot_admin_scripts` **15**. Documentation site: **227 tests**.

Lab pass (2026-10-30, Hyper-V lab, signed `1.5.4-audit1` server and desktop setups built from the working tree), limited to the cases the follow-up fixes changed:

- D26: on German Windows the old `takeown /R /D Y` exits 1 and the folder stays locked; `Grant-NodePilotPurgeAccess` reports no errors and a SYSTEM-owned folder can be deleted.
- D30 (server, gMSA): a reinstall from gMSA A to B that fails readiness (`DENY CONNECT SQL` for B, exit 7) leaves the install directory SDDL identical to before, with no SID of B on the directory or the API binary; the service runs as A again and `/healthz/ready` returns 200. An update with unchanged identity exits 0.
- D09: with service deletion denied (`sc.exe sdset`), server and desktop uninstall exit 1 and keep the service, files, `deploy\`, marker and uninstall entry; a retry after restoring the rights removes everything.
- D27 (desktop): the provisioner gets the installing account's SID; the handoff file exists only in that profile with an ACL of SYSTEM and that SID. An unknown SID fails with "No profile directory is registered".
- D04 (desktop): the first run failed because the restored `pgsql` kept the protected ACL inherited inside `rollback\` and PostgreSQL could not start. `Update-Desktop.ps1` now restores each component's ACL after moving it back (regression in `Test-DesktopUpdate.ps1`). With the fix, both services return with identical registrations, the database is restored and `/healthz/ready` returns 200. The signed setup did not yet contain this fix; it was applied to the installed copy.
- D16/D15 (CM1): `NP_TESTSUITE_WEBHOOK_SECRET` is stored as a secret global after install, the driver run including the webhook succeeds, the old `[TestSuite] powerManagement` is disabled and the invasive copy is created disabled.
- D23: the CM1 SCOrch URL is loopback and stays allowed under `RequireScorchTransport`; no live preflight was run.

The excluded runtime check was not retried. No production installation, AD provisioning, live HA failover or destructive sample workflow was executed. PowerShell 7 and promtool are unavailable locally: CI covers the former; PromQL was reviewed but not evaluated by Prometheus. Actual exported metric names and labels were tested.

The previously stopped development API was restarted from this workspace after validation. Both loopback listeners on port 5000 and HTTP probes for `/healthz/live` and `/healthz/ready` were verified healthy. The configured local telemetry collector remains unavailable; this does not invalidate the in-memory exporter contract test. No installed Windows service was restarted.

## 13. Final verdict

**Mostly coherent with some drift.** All dimensions previously below 7 have concrete improvements and behavioral evidence; the remaining deviations are listed rather than averaged away. Only D28 remains open in the working tree; the remaining real-environment checks and the lab finding are listed in section 11.

