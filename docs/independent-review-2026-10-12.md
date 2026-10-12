# Independent architecture and security review — 2026-10-12

## Review basis and independence

This is a fresh review of the working tree on `audit/practical-impact-2026-10-12`, including its existing uncommitted changes. No earlier audit reports, fix lists, handoffs, audit logs, Git history or orientation diffs were read. `src/nodepilot-docs-ui/src/site/image-manifest.json` was excluded. The root reviewer independently challenged candidate findings against current implementation and product contracts.

The review used `improve-codebase-architecture` including `LANGUAGE.md`, and `audit-ai-slop-code`. Repository instructions, `CONTEXT.md`, `docs/agents/`, relevant ADRs and ordinary product documentation were permitted inputs. Two fresh exploration agents reviewed execution and client/operations families without inherited conversation context. This is a risk-based source review, not a line-by-line certification or a production penetration test.

Only Critical/High issues and Medium issues with concrete ordinary operational impact qualify. Documented trusted Operator code execution, intentionally excluded backup data, cosmetic duplication, hypothetical abstractions and unusual unsupported layouts do not become findings merely because they look risky in isolation.

**Status:** Five qualified Medium findings were confirmed and corrected locally: one security finding (#147) and four architecture/operational findings (#143-146). No Critical or High finding was confirmed in the reviewed scope. Targeted checks are recorded below. No commits, deployment, service actions or external workloads formed part of this review; no migration was applied to an existing database.

## Product and entry-point inventory

| Family | External inputs and consumers |
|---|---|
| `NodePilot.Api` | ASP.NET controllers; JWT cookies/Bearer, CSRF, local/LDAP/Windows/OIDC login, SCIM; webhook/HMAC and scoped trigger keys; SignalR `/hubs/execution`; health/metrics; configuration and backup uploads; workflow/SCOrch import; diagnostic downloads |
| `NodePilot.Core` | Workflow Definitions, Activity Catalog, contracts, validators, shared HTTP-client/session and SQL/security primitives consumed by other product families |
| `NodePilot.Data` | PostgreSQL/SQL Server persistence, migration startup, encrypted credentials/settings, execution/alert outboxes, folder/access state, availability breaker |
| `NodePilot.Engine` | Workflow admission and execution, graph/step lifecycle, local processes/runspaces, child Workflows, AI Agent tools and evidence |
| `NodePilot.Scheduler` | Quartz schedules, filesystem/database/eventlog listeners, alert samplers and durable delivery attempts, retention, cluster leases |
| `NodePilot.Remote` | WinRM destinations, supplied credentials, pooled sessions and remote script results |
| `NodePilot.Ai` | Configured model endpoints, model output/tool requests, agent delegation and knowledge/chat tools |
| `NodePilot.Telemetry` | OpenTelemetry exporters and configured Prometheus query endpoint; HTTP consumers in Api |
| React SPA | Browser identity/session, designer edits and publication, query caches, SignalR feeds, imports and local persistence |
| Electron desktop | `desktop.json` handoff, renderer IPC, navigation/download requests, pinned API certificate, setup/readiness |
| WPF Switcher | Local service/control state, loopback machine-key calls, CLI and SCOrch workload reconciliation |
| CLI and MCP | Command arguments, environment/profile/session data, HTTP/TLS responses; MCP stdio and tool calls |
| Website/docs/demo | Static content/build inputs, prerendering, publication and isolated demo fetches |
| Deployment and CI | Signed artifact archives, manifests, installer answer files, certificates, database/service setup, update/rollback, workflow triggers and build artifacts |

All controller route/method and authorization declarations were inventoried from current source. This inventory does not imply that every controller action body received the same depth of review. The scope matrix below records that distinction.

## Security findings

### Critical

No qualified finding confirmed in the reviewed paths.

### High

No qualified finding confirmed in the reviewed paths.

### Medium

#### IR-147 - Seed-backup passphrase exposed by service rollback registry permissions

**Description:** A failed reinstall/repair can restore plaintext service Environment values into a newly recreated registry key without restoring its restrictive ACL. This applies to the supported SQL Server installation with a configured encrypted seed backup.

**Attack Scenario:** A local ordinary user can read the restored `SeedBackupPassphrase` from the service Environment registry value after the failed repair. If that user also possesses a copy of the original encrypted seed-backup artifact, the passphrase enables its decryption. Possession of that backup is an explicit prerequisite; this is not a demonstrated direct database compromise. The seeder deletes its consumed seed file, but leaves the service Environment passphrase, so that deletion does not remove this repair-time exposure.

**Affected Files:** `deploy/Install-NodePilot.ps1:818` (`Restore-ServiceRollbackSnapshot`); `src/NodePilot.Api/Security/ProvisioningSeeder.cs:119` provides the seed-consumption lifecycle. Seeded installation is described in the normal production deployment documentation.

**Exact Root Cause:** Rollback recreates the service, then restores the complete Environment block. ACL hardening previously ran only when the restored block contained `ConnectionStrings__Postgres`; a SQL Server seed passphrase did not satisfy that condition. The newly created service key otherwise retains ordinary readable permissions.

**Fix Recommendation and disposition:** Implemented unconditional secret ACL protection before restoring any saved Environment block. ACL failure aborts before Environment write and service restart. `deploy/Test-DeploymentTemplates.ps1:2359` extracts and executes the real rollback function with OS operations stubbed: the seed-only SQL Server case failed before the fix (`create -> write -> start`) and passes after it (`create -> acl -> write -> start`). A failing ACL seam also proves that write/restart do not proceed. This is a behavioral control-flow proof, not a live Windows registry/attacker login exercise.

### Low

Outside the requested threshold; no Low list was produced.

### Summary

- Total Findings: 1 Medium security finding, corrected locally; no confirmed Critical/High. This is not a statement that the whole product is free of vulnerabilities.
- Estimated Breach Risk: Not quantitatively assessed. Real host/domain/proxy/identity-provider trust and least-privilege configuration were not exercised.
- Most Likely Attack Vectors: Review concentrated on anonymous webhook/trigger admission, session invalidation, Viewer/folder substitution, external model/tool output, file/artifact inputs and local desktop trust seams.
- Top 5 Immediate Actions: Apply the #147 ACL repair through the normal release process. The evidence supports this one security repair rather than an artificial list of five. Operational repairs are listed separately.

## Architecture and operational findings

### IR-143 — Medium: Pending event alerts disappear after the sampling lookback

`NotificationDispatcher.RecoverPendingAsync` reconstructs a persisted Pending delivery through `SystemAlertEvaluator.TryReconstructContextAsync`. Reconstruction asks event sources to sample again. `ExecutionResultSource` and `AuditEventSource` apply the current lookback window, normally five minutes. An existing execution/audit row therefore becomes invisible to recovery after a normal outage or delayed retry, and the dispatcher permanently fails the delivery despite remaining retry attempts.

This conflicts with the durable Pending-recovery contract in ADR 0008 and `docs/alerting.md`. Deletion/retention is a legitimate terminal failure, but the failing case retains the source row. The narrow repair is a source-instance lookup for recovery which preserves the normal sampling window.

Architecture: observation and recovery have different Interface semantics. Giving the source Module ownership of exact event reconstruction improves Locality and lets a Dispatcher test exercise the same Seam used in production. It does not require reducing alert functionality or broadening the sampling window.

Implemented: `ISystemAlertSource.FindDeliveryObservationAsync` (`src/NodePilot.Scheduler/SystemAlerts/ISystemAlertSource.cs:53`) gives event adapters an exact lookup; `ExecutionResultSource.cs:61` and `AuditEventSource.cs:113` implement it; `SystemAlertEvaluator.cs:204` consumes it. Normal sampling remains bounded and action/severity selection is retained. Four dispatcher regression cases cover retained/deleted source rows for both source types. GREEN verified; no pre-fix RED test execution was performed for this finding.

### IR-144 — Medium: Agent member lifetime owns scarce WinRM leases

`AgentTarget` caches its first pooled session until disposal. `AgentActivityRunner` disposes member sessions only after the entire Agent Run finishes. A pool key permits five checked-out sessions by default, while an Agent Team can contain twelve members. Three completed delegates continue owning their leases; a subsequent batch of three on the same target and credential can obtain only two further leases. The final member waits for a release which cannot happen until the waiting batch and runtime finish.

The ordinary consequence is a stuck team until its deadline; parallel Agent Runs can exhaust the same capacity. The per-script timeout begins after checkout and cannot resolve that wait. PowerMode removes the team deadline. Local service-identity process execution and different pool keys are outside this specific failure.

The narrow repair is to own a WinRM lease for the actual operation, while retaining member metadata/staging paths and allowing the pool to own idle connections. This places resource lifetime inside the operation's Interface instead of requiring callers to understand a hidden run-wide reservation. A bounded pool reproduction can use local stub runspaces without contacting a Managed Machine.

Implemented: `src/NodePilot.Engine/Agents/AgentTarget.cs:72` owns the session with `await using` per actual operation. `AgentProcessScript` launches a new process for each invocation already, so persistent shell state is not part of the script contract; member filesystem working roots and cleanup remain intact. A five-slot real pool using local runspaces completes six member targets across sequential batches, and existing skill transfer/hash/execute/cleanup integrations pass. Failure/cancellation pool cases are included in the targeted pool test class. GREEN verified; no pre-fix RED execution was performed. No real WinRM endpoint was exercised.

### IR-145 - Medium: Repeated alert samples mutate cooldown and occurrence counts

System-event sources resample retained rows and custom elapsed-execution collectors intentionally rescan running/queued jobs using stable EventKeys. Both dispatcher paths previously called suppression before checking the delivery ledger. A delivered old occurrence could renew a finished cooldown, blocking a new event in the same workflow dedup group. An unsent single event could also satisfy `MinOccurrences=3` through three polls rather than three distinct occurrences, including across dispatcher restarts.

Four actual pre-fix RED cases demonstrate both behaviors in the System and Custom families. The shared `StageOccurrenceAsync` Interface (`src/NodePilot.Scheduler/NotificationDispatcher.cs:243`) now removes already delivered routes before touching suppression. This removes duplicated decision logic from the two callers. `ShouldFireAsync` (`:456`) persists counted identities per rule/EventKey only when MinOccurrences is enabled and after the cooldown gate. The identity, suppression, source cursor/episode and Pending deliveries commit together before send. MinOccurrences=1 retains delayed delivery after cooldown.

`NotificationOccurrenceState` (`src/NodePilot.Core/Models/NotificationState.cs:10`), its EF mapping (`src/NodePilot.Data/NodePilotDbContext.cs:615`) and migration `20261012070731_TrackNotificationOccurrences` provide that durable identity. Repeated observations refresh LastObservedAt; `NotificationRetentionService.cs:123` prunes identities no longer observed using the existing configured retention horizon. `NotificationRuleStore.DeleteAsync` also removes occurrence identities in the rule-deletion transaction, alongside suppression and delivery rows. No general state table is repurposed. The module owns occurrence identity and its lifecycle, improving Locality while preserving both collector Interfaces.

Disposition: implemented; four RED failures became GREEN, the surrounding alert/retention scope passes 96 tests, and all 12 MigrationDriftTests pass, including local migration/provider script checks. No migration was applied to a live database.

### IR-146 - Medium: Desktop staged rollback leaves a mismatched API handoff

Full staged Desktop update promises rollback of binaries, configuration and database. Provisioning can select a newly available lower HTTPS port and rewrite the external `desktop.json`. The failure handler previously restored binary directories and the database but left that handoff. The old API could then listen on port 47001 while Electron attempted the new port 47000, leaving the recovered installation unusable through its normal UI.

`deploy/desktop/Update-Desktop.ps1:126` now captures original handoff bytes and ACL before mutations; rollback at `:223` restores them before restarting the database/API. If the file was originally absent, rollback removes the newly created file. This makes the rollback module own all configuration in its documented lifecycle rather than only files inside the binary directories.

Disposition: implemented. `deploy/Test-DesktopUpdate.ps1:86` executes the actual extracted rollback body with isolated file fixtures and stubbed OS/service seams. The port mismatch assertion failed before the repair and passes after it; ACL restoration and failclosed behavior are covered. The fixture supplies the captured snapshot, so it does not claim to execute snapshot capture or the complete staged installer. Real service updates were not performed.

## Discarded candidates and counterevidence

- Agent MCP/skill registries are absent from portable configuration backup. `docs/ai-agents.md` expressly excludes those registries and requires native database plus secret-key backup. Their absence alone is therefore not a qualified defect under the current product contract.
- Trusted Operator execution under configured local/remote identities is an explicit product capability and was not treated as a privilege-escalation finding.
- Missing stored credentials throw rather than silently falling back to the service identity in the traced credential path.
- Local runspace script aliases are isolated by wrapper scopes in the traced path; the initial cross-run contamination suspicion was not established.
- Custom installer path/service-name forwarding was observed, but no ordinary documented layout impact was established; no separate finding/fix was made.
- ProcessExecutionEngine buffers script output in host memory before persistence truncation. The mechanism was observed, but ordinary workload impact was not reproduced. Full output is part of the Databus contract; no truncation or function reduction was proposed. This remains an unconfirmed observation, not a security finding.

## Scope and actual depth

| Area | Source depth reached | Remaining limits |
|---|---|---|
| API/authentication | JWT setup, session issuance/refresh, token validity, CSRF and bootstrap read in detail; OIDC mapper, SCIM auth and LDAP adapter selected paths | No real AD/Kerberos/OIDC/SCIM deployment; not all provisioning mutations inspected end-to-end |
| Authorization and HTTP consumers | Folder/global-role resolution, workflow create/update/publish CAS and enabled state, execution list/debug, Agent run/event reads, credential/global DTOs, AI knowledge/chat reader gates | Every route declaration inventoried; remaining individual action bodies are risk-based samples, not exhaustive full reads |
| External admission | Webhook HMAC/header validation, body bounds, replay admission; external trigger scopes/idempotency paths | No live HTTP attack exercise or proxy matrix |
| Persistence/configuration | Backup envelope and restore transaction/remapping/configuration compensation, secret protectors and rotation examples, migration startup, runtime writer | Full migration history/provider behavior and every backup part not individually exercised |
| Diagnostics/telemetry | Diagnostics path resolution/downloads, execution-log ownership/redaction, Prometheus consumer and HTTP gates, host metrics mapping | Exporter internals and live monitoring deployment not comprehensively tested |
| Availability/HA | Database HTTP gate, auth budget handling, host fencing/recovery consumer paths | ClusterLeaderService subsequently read fully; no active/passive failover test |
| Remote/PowerShell | Session factory/pool and Agent consumer chain; Runspace engine, RunScript wrapper, remote base and WaitForCondition | ProcessExecutionEngine subsequently read fully; real WinRM transport and job-object enforcement not exercised |
| Engine/AI | Agent target/runner/tool host/read policy/journal; delegation batch; execution admission/capacity/subworkflow and scheduler/step-runner core | AgentContextManager, LlmHttpTransport, AgenticToolLoop, ChatToolDispatch and ToolCallAccumulator subsequently read fully; full graph/retry/terminal persistence, all activities/providers and MCP/archive internals not exhaustively inspected |
| Scheduler/alerts | Dispatcher/Evaluator and execution/audit alert sources; database trigger and filesystem source | ScheduleTriggerSource, ClusterLeaderService, retention base/Execution/Notifications subsequently read fully; other alert/trigger sources remain partial |
| SPA/designer | Identity generations, cross-tab identity, Save/Publish/editor consumers, lock/run flow, principal SignalR hydration/reconnect, backup UI | Not every page/mutation or reducer branch; no browser reproduction |
| Electron | Main/setup, IPC sender checks, configuration, certificate pin, navigation/popups/downloads, HTTP deadline | No running Electron/Windows test; packaging only inventoried |
| Switcher | Coordinator/UI call chain, workload reconciliation, CLI and SCOrch HTTP/redirect/paging/count consumers | SCM/process-kill internals and full reconciliation endings partial |
| CLI/MCP | Shared session/TLS/refresh, execute/wait/follow and backup; MCP registration/destructive gate, workflow mutation/import/redaction seams | Not all commands/tools; Core patcher internals and some supporting groups partial |
| Docs/website/demo | Build separation/assembly, publishing order, redirect/host configuration, demo fetch isolation, HTML sinks | Not every route/prerenderer; excluded image manifest untouched |
| Install/update/CI | Desktop staged updater/provisioning, server updater/artifact trust/ACL samples, CI triggers and gates | Server rollback/seed/Environment ACL consumer chain subsequently traced; no lab matrix or CI execution |

## Executed checks

Tests were local and targeted; no complete suite or coverage run was performed. Existing analyzer/build warnings were present. No dependencies were installed. The PowerShell tests stub OS/service/registry mutations. The .NET integration fixtures use local SQLite/local runspaces rather than production databases or remote machines.

| Check | Exact scope/result | Evidence |
|---|---|---|
| #143/#144 GREEN | `dotnet test tests/NodePilot.Engine.Tests/NodePilot.Engine.Tests.csproj --no-restore --filter "FullyQualifiedName~NotificationDispatcherTests\|FullyQualifiedName~SystemAlertEvaluatorTests\|FullyQualifiedName~SystemAlertDispatcherTests\|FullyQualifiedName~SystemAlertSourceTests\|FullyQualifiedName~AuditEventSourceTests\|FullyQualifiedName~AgentTargetTests\|FullyQualifiedName~WinRmSessionPoolTests" --verbosity minimal` - 102/102 pass | Tool session 12119; no log file; no RED executed |
| #144 skill integration | Same project with `--no-build --no-restore --filter "FullyQualifiedName~SkillRunner_TransfersAndVerifiesResources_ExecutesOnBoundSession_ThenCleansUpEvenAfterRevocation" --verbosity minimal` - 2/2 pass | Tool session 33793; no log file |
| #145 RED | `dotnet test tests/NodePilot.Engine.Tests --no-restore --filter 'FullyQualifiedName~ElapsedAlertResampling_DoesNotCountOrCooldownTwice\|FullyQualifiedName~SystemAlertDeliveredEpisode_DoesNotRenewCooldownForLaterEvent\|FullyQualifiedName~SystemAlertMinOccurrences_CountsDistinctEpisodesAcrossDispatcherRestarts' --verbosity quiet` - 4 failed, 0 passed before product fix | `E:/NodePilot-independent-145-red.log` |
| #145 GREEN + surrounding integration | `dotnet test tests/NodePilot.Engine.Tests --no-restore --filter 'FullyQualifiedName~NotificationDispatcherTests\|FullyQualifiedName~SystemAlertEvaluatorTests\|FullyQualifiedName~SystemAlertDispatcherTests\|FullyQualifiedName~SystemAlertSourceTests\|FullyQualifiedName~AuditEventSourceTests\|FullyQualifiedName~NotificationRetentionServiceTests' --verbosity minimal` - 96/96 pass | `E:/NodePilot-independent-145-green.log` |
| Migration portability/drift | `dotnet test tests/NodePilot.Data.Tests --no-restore --filter 'FullyQualifiedName~MigrationDriftTests' --verbosity minimal` - 12/12 pass | `E:/NodePilot-independent-145-drift.log` |
| Rule lifecycle | `dotnet test tests/NodePilot.Data.Tests --no-restore --filter 'FullyQualifiedName~NotificationRuleStoreTests' --verbosity minimal` - 6/6 pass, including occurrence cleanup on delete | `E:/NodePilot-independent-145-rule-lifecycle.log` |
| #146/#147 PowerShell | `deploy/Test-DesktopUpdate.ps1`, `deploy/Test-DesktopUpdatePayload.ps1`, `deploy/Test-DeploymentTemplates.ps1` - all pass under Windows PowerShell 5.1 | Actual rollback assertions RED before corresponding product fixes, GREEN afterwards; child tool output, no saved log files. PowerShell 7 unavailable. |

Migration generation used `dotnet ef migrations add TrackNotificationOccurrences --project src/NodePilot.Data --startup-project src/NodePilot.Api --context NodePilotDbContext --configuration Independent145`, followed by required provider portability postprocessing. The initial Debug build failed because an existing NodePilot.Api process locked its output DLLs (`E:/NodePilot-independent-145-build.log`); that process was neither stopped nor changed. The isolated build/generation succeeded (`E:/NodePilot-independent-145-migration.log`). Generator stderr caused the shell wrapper to report a nonzero exit despite its successful `Done` output and generated files; the subsequent drift tests validate those files. No database-update command was used.

## Separate post-fix review

The root reviewer independently checked each candidate against source and product contracts, then reviewed each repair. A second source pass through #143/#144 confirmed exact event lookup retains action/severity behavior and normal bounded sampling, and per-operation leases preserve skill working-root lifetime and cleanup. Additional full reads covered ProcessExecutionEngine, ClusterLeaderService, ScheduleTriggerSource, Retention base/Execution/Notifications, AgentContextManager, LlmHttpTransport, AgenticToolLoop, ChatToolDispatch and ToolCallAccumulator; TriggerOrchestrator admission/checkpoints and WorkflowEngine/StepRunner were revisited selectively. No further qualified finding was established in that pass. #145 shared decision/identity/retention integration received both root source counterreview and an execution reviewer independent of its implementation. That recheck identified the missing occurrence cleanup on rule deletion; it was added to the existing transaction and deletion regression test. No further functional blocker was found. The reviewer did not rerun tests or claim independent execution of the reported GREEN results.

The remaining limits in the scope matrix are intentional disclosures: no claim is made to exhaustive review of every activity, route body, provider, graph transition or frontend branch, or to real WinRM, HA failover, identity-provider or installer validation.
