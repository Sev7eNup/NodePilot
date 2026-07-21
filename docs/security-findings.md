# Security Findings — Audit Reference

This file aggregates all security-relevant code-level decisions tagged in the codebase
with `H-N` / `M-N` / `L-N` / `C-N` / `F-N` markers. Each entry is implemented and
in production — this is a *reference for "why does this code exist"*, not a backlog.

When you see a comment like `// H-3: Capacity-Cap rejections...` and want context on
**which finding triggered it** and **what other places address the same theme**, look
it up here.

When adding a new finding tag, also extend this file. When removing a finding's last
implementation, remove the row.

| Prefix | Severity | Description |
|---|---|---|
| `C-N` | Critical-class | Cross-cutting concerns that touch multiple flows (e.g. debug-session ownership) |
| `H-N` | High | Authentication, secret leakage, capacity exhaustion, lifecycle integrity |
| `M-N` | Medium | Hardening with non-trivial blast radius (XML/JSON parsing, redirect handling, redaction) |
| `L-N` | Low | Defensive measures and ergonomic safety nets |
| `F-N` | Functional | Bug-class fixes that double as security improvements (timeouts, fail-closed paths) |

## Critical / Cross-cutting

### C-2 — Debug-Session Ownership

Only the user who started a debug execution (or an Admin) may step / continue / stop it
or inject variable Overrides. Locked into the debug-resume flow plus the engine's
override-validation pipeline.

- [ExecutionDebugController.cs:47](../src/NodePilot.Api/Controllers/ExecutionDebugController.cs#L47) — owner-check on `POST /api/executions/{id}/resume`
- [ExecutionsController.cs](../src/NodePilot.Api/Controllers/ExecutionsController.cs) — `Execute`/`Retry` capture `StartedByUserId`
- [ExternalTriggerController.cs](../src/NodePilot.Api/Controllers/ExternalTriggerController.cs) — same capture for external triggers
- [DebugCoordinator.cs:123](../src/NodePilot.Engine/Debug/DebugCoordinator.cs#L123) — `C-2-b`: rejects override keys that target reserved engine variables (`__callDepth`, `globals.*`)

## High

### H-1 — Rate-Limit on `/api/trigger/{name}`
Without a rate-limit partition, a holder of a leaked external API key can fire workflows
at unlimited RPS. Each trigger spawns engine + DB work.

- [ExternalTriggerController.cs:90](../src/NodePilot.Api/Controllers/ExternalTriggerController.cs#L90) — `[EnableRateLimiting("trigger")]`
- [RateLimitingSetup.cs:65](../src/NodePilot.Api/Hosting/RateLimitingSetup.cs#L65) — partition definition (30/min per IP)

### H-3 — Concurrent-Execution Capacity Caps
Per-process global cap and per-user cap on running executions, enforced atomically with
the `_runningExecutions` dict. Prevents a single user (or a misconfigured trigger-storm)
from exhausting thread-pool / DB-pool resources for everyone else.

- [WorkflowEngine.cs:37](../src/NodePilot.Engine/WorkflowEngine.cs#L37) — `_userExecutionCounts` per-user counter
- [WorkflowEngine.cs:157](../src/NodePilot.Engine/WorkflowEngine.cs#L157) — capacity check before enqueue
- [WorkflowEngine.cs:516](../src/NodePilot.Engine/WorkflowEngine.cs#L516) — counter decrement in `finally`
- [ExecutionCapacityException.cs](../src/NodePilot.Core/Exceptions/ExecutionCapacityException.cs) — typed signal so dispatch can return 503/429
- [EngineMetrics.cs:37](../src/NodePilot.Engine/EngineMetrics.cs#L37) — `RedactionHits`-counterpart `CapacityRejections` for dashboards

### H-4 — Account Lockout + Static-Cache Lifecycle
Two separate concerns under the same finding:
1. Login: 10 failures in 15 minutes locks the account for 15 minutes.
2. Engine: the three static dicts (`_runningExecutions`, `_debugHandles`, `_userExecutionCounts`) are atomically cleaned in `finally`.

- [AuthController.cs:64](../src/NodePilot.Api/Controllers/AuthController.cs#L64) — `MaxFailedLogins` / `LockoutWindow`
- [AuthController.cs:176](../src/NodePilot.Api/Controllers/AuthController.cs#L176) — branch logic: locked / lockout-just-expired / fresh-failure
- [AuthController.cs:301](../src/NodePilot.Api/Controllers/AuthController.cs#L301) — failure-counter increment on bad password
- [AuthController.cs:334](../src/NodePilot.Api/Controllers/AuthController.cs#L334) — counter clear on successful login
- [WorkflowEngine.cs:347](../src/NodePilot.Engine/WorkflowEngine.cs#L347) — finally-block ensures all three caches release together

### H-5 — JWT in HttpOnly Cookie + CSRF Double-Submit
JWT lives in an `HttpOnly; Secure; SameSite=Strict` cookie that JS cannot read.
A separate JS-readable CSRF token is sent in the request header and matched server-side.

- [AuthController.cs:71](../src/NodePilot.Api/Controllers/AuthController.cs#L71) — cookie-name constants
- [AuthController.cs:344](../src/NodePilot.Api/Controllers/AuthController.cs#L344) — login-time cookie set
- [AuthController.cs:365](../src/NodePilot.Api/Controllers/AuthController.cs#L365) — clear cookies even on malformed tokens
- [AuthController.cs:477](../src/NodePilot.Api/Controllers/AuthController.cs#L477) — refresh rotates both cookies together

### H-8 — Input-Parameter Redaction
Workflow input parameters can carry secrets resolved from globals — they get the same
OutputRedactor pass before persistence as step output does.

- [WorkflowEngine.cs:244](../src/NodePilot.Engine/WorkflowEngine.cs#L244) — input redaction at execute-start
- [WorkflowEngine.cs:497](../src/NodePilot.Engine/WorkflowEngine.cs#L497) — error-message redaction (paired with H-9)

### H-9 — Return-Data Redaction
A child workflow's `returnData` step output flows up to the parent as `{{stepId.param.*}}`.
Without redaction, a child that echoes a secret leaks it into the parent's audit trail.

- [ReturnDataActivity.cs:78](../src/NodePilot.Engine/Activities/ReturnDataActivity.cs#L78) — redactor pass + 32 KiB cap
- [WorkflowEngine.cs:497](../src/NodePilot.Engine/WorkflowEngine.cs#L497) — error-path redaction

### H-10 — VariablesSnapshot Redaction for Non-Privileged Roles
The `VariablesSnapshot` column on `StepExecution` carries raw template-resolved values
(webhook bodies, trigger params, upstream step outputs). Viewer-role accounts get the
same redaction as Output / ErrorOutput so they cannot bulk-scrape secrets.

- [ExecutionsController.cs:135](../src/NodePilot.Api/Controllers/ExecutionsController.cs#L135) — `Scrub()` on `GetSteps` response

### H-13 — Database-Trigger ConnectionRef Enforcement
When `Trigger:Database:RequireConnectionRef=true`, inline connection strings on
`databaseTrigger` are rejected; only named refs from `Trigger:Database:ConnectionStrings:*`
are honored. Stops a workflow author from baking DB credentials into the workflow JSON.

- [DatabaseTriggerSource.cs:22](../src/NodePilot.Scheduler/Sources/DatabaseTriggerSource.cs#L22) — config check + rejection

### H-14 — Webhook Secret Required (Hardening Flag)
When `Webhook:RequireSecret=true`, any `webhookTrigger` saved without a `secret` is
rejected at fire-time. Default-tolerant during dev, strict in Production templates.

- [WebhooksController.cs:118](../src/NodePilot.Api/Controllers/WebhooksController.cs#L118) — fire-path check

### H-15 — Sub-Workflow Cancellation Inheritance
Fire-and-forget child workflows inherit the *parent's* cancellation token. Previously
they ran with `CancellationToken.None` so a parent cancel would orphan them.

- [StartWorkflowActivity.cs:212](../src/NodePilot.Engine/Activities/StartWorkflowActivity.cs#L212) — token propagation

### H-16 — Import-Body Size Cap
600 MiB → realistic ceilings: 6 MiB single-workflow, 50 MiB SCOrch XML import. Prevents
a /api/workflows/import call from pinning the heap with a malicious payload.

- [WorkflowImportExportController.cs:73](../src/NodePilot.Api/Controllers/WorkflowImportExportController.cs#L73) — workflow-import cap
- [WorkflowImportExportController.cs:183](../src/NodePilot.Api/Controllers/WorkflowImportExportController.cs#L183) — SCOrch-XML-import cap

### H-17 — LDAP Empty-Password Unauthenticated-Bind Rejection
A simple-bind carrying a populated UPN but a zero-length password is, per RFC 4513 §5.1.2,
an *unauthenticated bind*: Active Directory answers it with `LDAP_SUCCESS` instead of error
49. Forwarding a client-supplied empty password into `LdapConnection.Bind` turned "attacker
knows a valid username" into a full authentication bypass (a configured service account made
it worse — the post-bind directory search then succeeded too, JIT-provisioning the victim's
identity + role). Both LDAP layers now reject an empty/whitespace-only password up front as a
clean invalid-credentials verdict, before any bind — never falling through to the local path.

- [LdapAuthenticator.cs:69](../src/NodePilot.Api/Security/Ldap/LdapAuthenticator.cs#L69) — primary guard at the shared choke point (`InvalidCredentials`, no breaker/network)
- [SystemLdapConnectionAdapter.cs:47](../src/NodePilot.Api/Security/Ldap/SystemLdapConnectionAdapter.cs#L47) — defense-in-depth guard immediately before `Bind`

## Medium

### M-2 — JWT Key Resolved Once at Startup
`Jwt:Key` is loaded once via `IJwtKeyProvider` and cached as a singleton, instead of
reading the file on every login. A key-file deletion can no longer cause an authenticated
session to fail mid-flight.

- [AuthController.cs:485](../src/NodePilot.Api/Controllers/AuthController.cs#L485) — uses cached provider
- [JwtKeyResolver.cs:143](../src/NodePilot.Api/Security/JwtKeyResolver.cs#L143) — startup-time resolution + validation

### M-4 — OutputRedactor Fail-Open on Regex Timeout
Catastrophic-backtracking on a custom user pattern shouldn't nuke the entire output.
Timeout returns the original string and emits a warning + metric so ops can spot the
broken pattern before secrets leak repeatedly.

- [OutputRedactor.cs:124](../src/NodePilot.Engine/Security/OutputRedactor.cs#L124) — `RegexMatchTimeoutException` catch

### M-5 — Widened Secret Pattern Coverage
Default redactor patterns now cover commas/semicolons inside values, double- and
single-quoted forms, JSON shape, plus standalone token shapes (AWS/GitHub/Slack/GitLab).

- [OutputRedactor.cs:34](../src/NodePilot.Engine/Security/OutputRedactor.cs#L34) — widened value class
- [OutputRedactor.cs:55](../src/NodePilot.Engine/Security/OutputRedactor.cs#L55) — catch-all set

### M-7 — JSON Payload Size Cap
8 MiB cap on both file-mode and inline-mode `jsonQuery` payloads. A malicious endpoint
returning a 10 GiB JSON document cannot OOM the engine.

- [JsonQueryActivity.cs:32](../src/NodePilot.Engine/Activities/JsonQueryActivity.cs#L32) — class-level doc
- [JsonQueryActivity.cs:83](../src/NodePilot.Engine/Activities/JsonQueryActivity.cs#L83) — file-size pre-check
- [JsonQueryActivity.cs:97](../src/NodePilot.Engine/Activities/JsonQueryActivity.cs#L97) — inline-payload cap

### M-8 — File-Mode Paths Through PathGuard
`jsonQuery` and `xmlQuery` in file mode go through the same `PathGuard` (allow-list +
traversal rejection) as `fileOperation` / `folderOperation`, so admins can opt into
traversal-rejection once and have it apply everywhere.

- [JsonQueryActivity.cs:69](../src/NodePilot.Engine/Activities/JsonQueryActivity.cs#L69)
- [XmlQueryActivity.cs:60](../src/NodePilot.Engine/Activities/XmlQueryActivity.cs#L60)

### M-9 — `forEach` Item Cap
Hard cap (4096 items) on the iterable input. A misconfigured upstream step
(`Get-ADUser -Filter *`) cannot fan out into hundreds of thousands of step rows.

- [ForEachActivity.cs:121](../src/NodePilot.Engine/Activities/ForEachActivity.cs#L121)

### M-10 — Scrub-Time Window Clamp
External callers passing absurd `windowDays` values for replay scrub get clamped to a
max of 7 days so a webhook can't request a year-long replay snapshot.

- [WorkflowEngine.cs:316](../src/NodePilot.Engine/WorkflowEngine.cs#L316)

### M-11 — RestApi 307/308 Redirect Hardening
RFC says 307/308 preserve method + body. But when the redirect target is on a different
host, NodePilot strips the `Authorization` header to avoid leaking it to a third party.

- [RestApiActivity.cs:110](../src/NodePilot.Engine/Activities/RestApiActivity.cs#L110)

### M-12 — RestApi Bounded Response Read
Hard cap on response-body read so a malicious endpoint that returns a 10 GiB stream
cannot pin the engine's heap.

- [RestApiActivity.cs:132](../src/NodePilot.Engine/Activities/RestApiActivity.cs#L132)

### M-13 — Webhook Body Validation
Body read only on methods that carry one (POST/PUT/PATCH); strict UTF-8 parsing rejects
invalid byte sequences that could otherwise be smuggled into workflow variables.

- [WebhooksController.cs:146](../src/NodePilot.Api/Controllers/WebhooksController.cs#L146) — method check
- [WebhooksController.cs:155](../src/NodePilot.Api/Controllers/WebhooksController.cs#L155) — strict UTF-8

### M-14 — Hardened SCOrch XmlReader
`XmlReaderSettings` shared across both `Parse` overloads with DTD/Resolver/External
disabled. Stream-based overload preferred for large imports to avoid double-allocation.

- [ScorchImporter.cs:52](../src/NodePilot.Engine/Scorch/ScorchImporter.cs#L52) — settings
- [ScorchImporter.cs:92](../src/NodePilot.Engine/Scorch/ScorchImporter.cs#L92) — stream-based parse

### M-15 — Quartz Misfire Policy
Explicit `MisfireInstruction = DoNothing`. Quartz's default would fire missed schedules
all at once on service restart — a 4-hour outage means N×scheduled-fires hammering at
boot.

- [ScheduleTriggerSource.cs:89](../src/NodePilot.Scheduler/Sources/ScheduleTriggerSource.cs#L89)

### M-20 — Observability PromQL Authorization
Raw PromQL queries (and the pre-composed summary) can leak infrastructure metrics to
non-Admin roles. The endpoint enforces Admin-only.

- [ObservabilityController.cs:157](../src/NodePilot.Api/Controllers/ObservabilityController.cs#L157)

### M-23 — Variable-Shortname Denylist
`{{paramKey}}` (without step prefix) is a footgun for params with reserved names like
`Authorization`. Denied at resolution time — consumers must use fully-qualified
`{{stepId.param.Authorization}}`.

- [VariableResolver.cs:21](../src/NodePilot.Engine/Execution/VariableResolver.cs#L21) — class doc
- [VariableResolver.cs:81](../src/NodePilot.Engine/Execution/VariableResolver.cs#L81) — denylist enforcement

### M-24 — Secret-Demotion Guard
Toggling `IsSecret=false` on a global variable without supplying a new plaintext value
would decrypt an existing secret into plain storage. Blocked unless the caller passes
a fresh value.

- [GlobalVariableStore.cs:87](../src/NodePilot.Data/GlobalVariableStore.cs#L87) — guard
- [GlobalVariableStore.cs:106](../src/NodePilot.Data/GlobalVariableStore.cs#L106) — paired update path

### M-28 — FileWatcher Per-Path Debounce + Buffer Sizing
Single shared debounce-timestamp lost simultaneous events on different files. Now
per-path. Also: `InternalBufferSize` raised from default 8 KiB to a tunable value so
high-volume directories don't lose events under burst.

- [FileWatcherTriggerSource.cs:32](../src/NodePilot.Scheduler/Sources/FileWatcherTriggerSource.cs#L32) — debounce dict
- [FileWatcherTriggerSource.cs:77](../src/NodePilot.Scheduler/Sources/FileWatcherTriggerSource.cs#L77) — buffer config

### M-29 — Uniform 404 on External-Trigger
"Not found" and "exists but disabled" both return 404, so a holder of a valid API key
cannot enumerate workflow names by probing.

- [ExternalTriggerController.cs:133](../src/NodePilot.Api/Controllers/ExternalTriggerController.cs#L133)

## Low

### L-2 — Resume-Override Size Caps
`Resume` body's `overrides` dict is bounded (max 256 entries, max 64 KiB per value) so a
debug-session caller cannot OOM the engine's variable-resolution pass.

- [ExecutionDebugController.cs:65](../src/NodePilot.Api/Controllers/ExecutionDebugController.cs#L65) — controller-level limits
- [DebugCoordinator.cs:124](../src/NodePilot.Engine/Debug/DebugCoordinator.cs#L124) — engine-level limits

### L-5 — DPAPI Scope Fail-Fast on Typo
Previous `== "LocalMachine" ? LocalMachine : CurrentUser` ternary silently fell back to
`CurrentUser` on a typo. Now: explicit set, throws on unknown values.

- [DpapiScopeResolver.cs:18](../src/NodePilot.Data/DpapiScopeResolver.cs#L18)

### L-9 — Concurrent Debug-Session Cap
Hard cap on simultaneous debug handles in memory so a malicious / buggy caller can't
exhaust the per-execution dict.

- [WorkflowEngine.cs:330](../src/NodePilot.Engine/WorkflowEngine.cs#L330)

### L-11 — EventLog Manual-Run Validation
Even on manual-run, the log name is attacker-controllable via workflow JSON. Enforce
the `Application/System/Security/Setup` allow-list before opening the channel.

- [EventLogTrigger.cs:55](../src/NodePilot.Engine/Triggers/EventLogTrigger.cs#L55)

### L-14 — Trigger Host-Shutdown Propagation
Captured token in `TriggerOrchestrator` so `FireAsync` propagates host shutdown into
`engine.ExecuteAsync` — otherwise scheduled fires after a shutdown signal would still
race the engine's own cancellation.

- [TriggerOrchestrator.cs:53](../src/NodePilot.Scheduler/TriggerOrchestrator.cs#L53)

### L-15 — Audit-Safe Username Rendering
Login-failure audit logs render the presented username through a length-cap and
control-char strip, so an attacker can't poison the audit table with payload-sized
or escape-laden username fields.

- [AuthController.cs:161](../src/NodePilot.Api/Controllers/AuthController.cs#L161)

### L-16 — Retention Archive-Path Probe
One-shot startup probe validates the configured archive path (normalize + write-test +
ACL-check). Bad config fails loudly at startup, not silently at first retention sweep.

- [ExecutionRetentionService.cs:149](../src/NodePilot.Scheduler/ExecutionRetentionService.cs#L149) — execution archive
- [AuditLogRetentionService.cs:157](../src/NodePilot.Scheduler/AuditLogRetentionService.cs#L157) — audit archive

## Functional (security-adjacent bug fixes)

### F-1 — WinRM Real Timeout Enforcement
`PowerShell.Invoke()` doesn't observe a `CancellationToken` natively. F-1 wires a real
timeout onto WinRM session execution so a hung remote script can't hold an engine slot
forever.

- [WinRmSession.cs:13](../src/NodePilot.Remote/WinRmSession.cs#L13) — poison flag
- [WinRmSession.cs:84](../src/NodePilot.Remote/WinRmSession.cs#L84) — timeout enforcement

### F-2 — Fail-Closed JSON Redaction for Workflow Definitions
Non-privileged callers reading a workflow definition get a fail-closed shell (only
metadata) when the redaction pass fails. Better to leak nothing than to leak everything.

- [WorkflowsControllerBase.cs:55](../src/NodePilot.Api/Controllers/WorkflowsControllerBase.cs#L55)

### F-4 — Audit-Archive Atomicity
Archive succeeded but DB delete failed → previously left orphan audit rows in DB plus
a bogus archive file. F-4 closes the gap with a delete-or-roll-back-archive flow.

- [AuditLogRetentionService.cs:132](../src/NodePilot.Scheduler/AuditLogRetentionService.cs#L132) — orphan-prevention catch
- [AuditLogRetentionService.cs:212](../src/NodePilot.Scheduler/AuditLogRetentionService.cs#L212) — rollback flow doc

## Dependency Advisories (transitive NuGet)

Resolution of `dotnet list package --vulnerable` HIGH advisories. Re-run that command after
any package bump to keep this section honest.

### DEP-1 — Microsoft.OpenApi HIGH (GHSA-v5pm-xwqc-g5wc) — FIXED
`Swashbuckle.AspNetCore` 10.1.7 (and `WireMock.Net.OpenApiParser` in the test graph) pulled
`Microsoft.OpenApi` 2.4.1, vulnerable to an uncontrolled-recursion DoS on circular schema
refs. Overridden with a direct pin to 2.9.0 (still 2.x → Swashbuckle Models-namespace stays
compatible).

- [NodePilot.Api.csproj](../src/NodePilot.Api/NodePilot.Api.csproj) — `Microsoft.OpenApi` 2.9.0
- [NodePilot.Api.Tests.csproj](../tests/NodePilot.Api.Tests/NodePilot.Api.Tests.csproj) — same pin for the WireMock path

### DEP-2 — Scriban.Signed HIGH (GHSA-24c8-4792-22hx) — FIXED
`WireMock.Net` (test-only) pulled `Scriban.Signed` 5.5.0, vulnerable to an `array.insert_at`
unbounded-allocation OOM DoS. Overridden with a direct pin to 7.2.5 in every WireMock-using
test project. All four suites (Ai/Api/Cli/Mcp) stay green — NodePilot does not use WireMock's
Scriban response-templating, so the major-version bump has no runtime effect.

- Test projects: `NodePilot.{Ai,Api,Cli,Mcp}.Tests` — `Scriban.Signed` 7.2.5

### DEP-3 — SQLitePCLRaw.lib.e_sqlite3 HIGH (GHSA-2m69-gcr7-jv3q / CVE-2025-6965) — FIXED
Bundled SQLite < 3.50.2 can corrupt memory when a query's aggregate-term count exceeds the
column count. Was RISK-ACCEPTED (build-time `NuGetAuditSuppress` in `Directory.Build.props`)
while no upstream fix existed — the vulnerable 2.1.11 line was the newest on nuget.org, the
flaw was reachable only by authenticated Admin/Operator workflow authors who already hold
arbitrary code execution via `runScript`, and `SqlActivity:RequireConnectionRef` constrained
which databases a workflow may open.

Fixed 2026-07-10: upstream shipped the SQLitePCLRaw **3.x line** (managed packages 3.0.x,
native `lib.*` packages versioned after the bundled SQLite), whose `bundle_e_sqlite3` pulls
SQLite 3.50.4 via `SourceGear.sqlite3` — outside the advisory's `<= 2.1.11` range.
`Microsoft.Data.Sqlite` 10.x still floors the bundle at 2.1.11, so the fix is a direct pin
that lifts the transitive graph. The audit suppression was removed.

- [Directory.Packages.props](../Directory.Packages.props) — `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3 + rationale
- Direct `PackageReference` in every project referencing a `*Sqlite` package:
  `NodePilot.{Data,Engine,Scheduler}`, `NodePilot.{Api,Data,Engine}.Tests`, `NodePilot.TestCommons`
