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
600 MiB → realistic ceilings: 6 MiB single-workflow, 300 MiB SCOrch XML import. Prevents
a /api/workflows/import call from pinning the heap with a malicious payload. The SCOrch
ceiling was later raised from 50 MiB: a whole-estate export is a single file, and at a
measured ~6.5 KiB per activity 50 MiB stopped at roughly 160 runbooks. That endpoint is
Admin/Operator-only and its 500-item cap bounds what a body of any size can write.

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

### H-18 — Install-Directory ACL Hardening
The install directory holds the service binaries and is registered as the image path of a
service running as LocalSystem or a gMSA, so write access to it *is* code execution as that
account. Only `DataPath` was hardened; `InstallPath` was created with a plain `New-Item -Force`
and inherited whatever the parent allowed. Under the `C:\Program Files\NodePilot` default that
is safe — which is why it went unnoticed — but a custom root such as a second data volume
inherits `BUILTIN\Users:(M)` straight off the volume root: any local user could replace the EXE
and own the next service start. The signed-manifest hash check proves the files are ours *at
install time*; it says nothing about the window afterwards, which is the whole attack.

Three separate guarantees, because each has its own failure mode:

- [Install-NodePilot.ps1](../deploy/Install-NodePilot.ps1) — `Assert-SafeInstallRoot` validates the *location* before anything is written: absolute, non-UNC, on an ACL-capable file system (NTFS/ReFS — a FAT/exFAT target silently discards every rule), and free of reparse points anywhere along the path (a junction lets the target be redirected after installation).
- [Install-NodePilot.ps1](../deploy/Install-NodePilot.ps1) — `Set-DirectoryAclForService -ReadOnlyForService` now runs on `InstallPath` too: inheritance dropped, SYSTEM + Administrators FullControl, the service account read-and-execute only. It executes the binaries, it never rewrites them.
- [ArtifactSecurity.ps1](../deploy/ArtifactSecurity.ps1) — `Assert-NodePilotInstallRootHardened` re-verifies after the copy that no untrusted principal holds a write-shaped right (`WriteData`/`AppendData`/`Delete`/`DeleteSubdirectoriesAndFiles`/`ChangePermissions`/`TakeOwnership`).

It lives in `ArtifactSecurity.ps1` rather than the installer because
[Update-NodePilot.ps1](../deploy/Update-NodePilot.ps1) has to answer the same question: an
installation made before this check keeps its inherited ACL forever, and replacing the binaries
on every upgrade would never notice. The updater calls it **without** `-RequireProtectedRules` —
an older installation under Program Files inherits a perfectly safe ACL, and failing that would
block upgrades on hosts that are not actually exposed.

The updater goes through `Assert-NodePilotInstallRootHardenedOrRepair`, which repairs once before
it gives up. Verifying alone turned a fixable condition into a dead end, and at the worst moment:
the check runs *after* the binaries have been replaced, so a refusal costs a rollback and leaves
the host on the old version with no route forward. An ACE granted after the installation was laid
down is exactly what an update should clear, and the same `Set-DirectoryAclForService` the
installer uses clears it — inheritance dropped, every explicit ACE wiped, owner forced back to
Administrators. The property is unchanged, because the repair is followed by a second
`Assert-NodePilotInstallRootHardened`: if the directory is still writable by an untrusted
principal, that throws and the caller rolls back. Repair-then-verify, never repair-and-hope.

Prerequisite for the read-only DACL: everything the *service* writes at runtime already lives
under `DataPath` (`Jwt:KeyPath`, `DataProtection:KeyRingPath`, `Setup:AdminSetupTokenPath`,
`Settings:RuntimeOverridesPath`, logs, archives). `Test-DeploymentTemplates.ps1` pins all four
so a future template edit cannot quietly move one back into the now read-only install directory.

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

### M-30 — Whole-Row Projection Bypass of the Secret-Column Mask
The two original `DbAdminSecretColumns` layers both key on **names**: layer 1 rejects a statement
that *names* a protected column, layer 2 masks a *result column* whose name matches one. A row
serializer defeats both simultaneously — `SELECT to_json(u) FROM "Users" u` never mentions
`PasswordHash` and returns it inside a column called `to_json`. Same for `u::text` (PostgreSQL
whole-row cast) and `SELECT * FROM Users FOR JSON AUTO` (SQL Server). The leaked values are
BCrypt hashes, credential ciphertext and `GlobalVariable.Value`.

Reachable through `/api/dbadmin/query` (Admin) and, before the 2026-08 follow-up audit, through the
AI-Chat tool `execute_readonly_sql` for Operators. Raw knowledge-database tools now require the
explicit global-Admin fact at capability discovery, reader injection, tool registration and tool
execution; folder grants never elevate an Operator. The knowledge-assistant path still audits only
a SQL fingerprint, never the statement, so the SQL guard and result redaction remain defense in
depth for the remaining Admin-only surface.

Layer 3 refuses any statement that combines a table carrying a masked column with a whole-row
serializer. Deliberately blunt (it also fires on `SELECT "Id"::text FROM "Users"`) and, being a
blocklist, not exhaustive against every provider extension — the authoritative fix is a
least-privilege DB login without `SELECT` on the secret columns, which is still open.

- [DbAdminSecretColumns.cs](../src/NodePilot.Api/Services/DbAdmin/DbAdminSecretColumns.cs) — `ReferencesProtectedRowProjection` + the protected-table set
- [DbAdminReadOnlySqlGuard.cs](../src/NodePilot.Api/Services/DbAdmin/DbAdminReadOnlySqlGuard.cs) — `::` emitted as a token, `ReferencesIdentifierPair` for `FOR JSON` / `FOR XML`
- [DbAdminController.cs](../src/NodePilot.Api/Controllers/DbAdminController.cs) — `protected_row_projection` on the read path
- [SqlKnowledgeReader.cs](../src/NodePilot.Api/Ai/SqlKnowledgeReader.cs) — same refusal for the text2sql tool

### M-31 — Scheduler Logged Unredacted Step Errors
`StepRunner` returns the **raw** `ActivityResult` on purpose — the data bus has to resolve
`{{step.error}}` to the real value — and redacts only on the way out to the DB, the UI, telemetry
and the support log. `WorkflowScheduler` then interpolated `result.ErrorOutput` straight into a
`LogWarning`, which made the main log (and any SIEM shipping it) the single sink that saw
unredacted stderr while the UI showed `***`. The reason is already logged, redacted, by
`StepRunner`'s `STEP_FAILED` support event on every failure, so the payload was pure duplication:
the scheduler now logs the step id and points at that event.

- [WorkflowScheduler.cs](../src/NodePilot.Engine/Execution/WorkflowScheduler.cs) — failure logged without the payload
- [StepRunner.cs](../src/NodePilot.Engine/Execution/StepRunner.cs) — `LogStepFailedAsSupport` is the redacted reason's owner

### M-32 — Pre-Auth Request Bounds on the Anonymous Endpoints
`POST /api/auth/login` and `POST /api/trigger/{name}` are `AllowAnonymous`, so their bodies are
model-bound in full before any credential is compared. The rate limiter caps requests per minute,
never bytes per request, and without an endpoint limit both inherited Kestrel's 30 MiB default.
The username was already capped ([ExternalLoginThrottle.MaximumUsernameLength]); the password and
the trigger parameter map were not.

- [AuthController.cs](../src/NodePilot.Api/Controllers/AuthController.cs) — `MaxLoginBodyBytes` (8 KiB) + a `MaxPasswordBytes` check before BCrypt. Every password-setting path already runs `ValidatePasswordPolicy`, which caps at the same 72 bytes, so a longer login password cannot correspond to any stored hash — it is unauthenticatable by construction.
- [ExternalTriggerController.cs](../src/NodePilot.Api/Controllers/ExternalTriggerController.cs) — `MaxTriggerBodyBytes` (256 KiB) plus count/key/value caps on `Parameters`. Every entry is copied into the execution's variable dictionary and resolved into each step's config, so an unbounded map is engine work, not just bytes.

### M-33 — SignalR Subscriptions Survived a Folder Move
`JoinExecution`/`JoinWorkflow` authorize once, at join, and the notifier fans out to the group
without re-checking. Moving a workflow to another folder — or moving a folder subtree, which
re-parents inherited permissions for everything below it — changed the RBAC basis without
touching the live subscriptions: REST answered 404 for a viewer who had just lost Read while the
already-joined socket kept streaming step output. Permission *revocation* was already covered
(`UserSessionInvalidation.BumpSecurityStamp` → `HubRevocationSweeper`); moves bumped nothing.

Both move paths now evict the affected memberships. The SPA's re-join re-runs the RBAC gate, so
callers who kept access simply resubscribe and callers who lost it are refused.

- [ExecutionHub.cs](../src/NodePilot.Api/Hubs/ExecutionHub.cs) — `RevokeSubscriptionsAsync` + `SubscribedExecutionIds` (narrows the "which executions are affected" query to the handful actually being watched, instead of the whole retention window)
- [SharedWorkflowFoldersController.cs](../src/NodePilot.Api/Controllers/SharedWorkflowFoldersController.cs) — `RevokeLiveSubscriptionsAsync` on both `MoveWorkflow` and folder `Move`
- [SignalRExecutionNotifier.cs](../src/NodePilot.Api/Hubs/SignalRExecutionNotifier.cs) — `IWorkflowFolderProjection.InvalidateWorkflowFolder`. The per-connection ops-feed scope is a documented snapshot, but the notifier's `workflowId → folderId` cache is a *server-side* mapping: left stale it routes a moved workflow's status events to the old folder's watchers indefinitely, and hides them from the new folder's.

### M-34 — SSRF Guard Missed the Unspecified Address
`0.0.0.0` and its IPv6 counterpart `::` reach the **local host** when used as a connect target,
but neither is a loopback address as far as `IPAddress.IsLoopback` is concerned, and neither falls
into any RFC1918 / ULA range. The private-network set therefore did not contain them, and
`RestApi:BlockPrivateNetworks` — on by default — waved them through: `http://0.0.0.0:5000/`
reached every service bound on the machine, NodePilot's own API included, from a `restApi` step
whose URL can be assembled out of trigger payloads.

Both spellings now count as private. They are deliberately part of the *private* set rather than
the unconditionally-blocked link-local set, so they behave exactly like `127.0.0.1`: refused by
default, reachable in a development setup that switches the guard off on purpose.

- [NetworkGuard.cs](../src/NodePilot.Engine/Security/NetworkGuard.cs) — `IsPrivateNetwork`, one line per address family
- [NetworkGuardTests.cs](../tests/NodePilot.Engine.Tests/Security/NetworkGuardTests.cs) — `Default_BlocksUnspecifiedAddress` / `WhenExplicitlyDisabled_AllowsUnspecifiedAddress`, verified in both directions (the guard tests fail without the fix)

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

## Dependency Advisories (npm)

Resolution of `npm audit --audit-level=moderate` HIGH advisories in `src/nodepilot-ui` and
`src/nodepilot-docs-ui`. Both are gated in CI; re-run the command in each package after any
bump to keep this section honest. All three below turned the pipeline red on 2026-07-22
without any code change — the advisories were published against already-pinned versions.

### DEP-4 — postcss HIGH (GHSA-r28c-9q8g-f849) — FIXED
Path traversal in the previous-source-map auto-loader (`sourceMappingURL`) allows disclosure
of arbitrary `.map` files. Vulnerable `<= 8.5.17`, patched 8.5.18. Purely transitive through
`vite` in both packages, and vite's own `^8.5.15` range already covered the fix — a lockfile
refresh (`npm update postcss` → 8.5.23) was sufficient. No manifest change.

### DEP-5 — brace-expansion HIGH (GHSA-mh99-v99m-4gvg) — FIXED
Unbounded expansion length causes an OOM crash. The advisory range is `<= 5.0.7` with 5.0.8
as the only patched release — unbounded below, so every 1.x/2.x/3.x release counts as
vulnerable too. Dev-only in both packages (eslint's glob chain).

- `docs-ui` was already on eslint 10 → `minimatch@10` → `brace-expansion@5.0.7`, in-range
  fixable by a lockfile bump to 5.0.8.
- `nodepilot-ui` sat on eslint 9 → `minimatch@3` → `brace-expansion@1.1.16`, where the 1.x
  line has no fix at all. Only route out was **eslint 9 → 10**, which in turn requires
  `eslint-plugin-react-hooks` ≥ 7.1 (7.0.1 refuses to resolve against eslint 10).

That plugin bump promoted three React Compiler diagnostics to errors and surfaced two more
`incompatible-library` warnings. The rules were briefly suppressed to unblock CI, then the four
sites behind the six errors were fixed and all three rules re-enabled:

- `react-hooks/refs` — `EdgeReshapeHandles` had a curried `(handle) => (e) => …` pointer-down
  handler that had to be *called* during render to produce the prop, and `WorkflowEditorPage`
  built its node-context-menu callbacks inside a `(() => { … })()` IIFE in JSX. Both put ref
  access on a render-reachable path. Fixed by un-currying the handler and replacing the IIFE
  with plain conditional rendering.
- `react-hooks/preserve-manual-memoization` — `WorkflowEditorPage.triggerCancel` and
  `SubWorkflowPreviewModal.definition` read a guarded sub-property inside the hook body, so the
  compiler inferred the whole object as the dependency while the source listed the properties.
  Both components lost auto-memoization as a result. Fixed by reading the values into locals
  first.
- `react-hooks/use-memo` — `useNodeAnnotations` keyed a memo on an inline
  `nodes.map(…).join(',')`. The derived key is deliberate (recompute only when the assigned
  machines change, not on every drag), so it was hoisted into a local; the dependency list is
  now a simple expression and the intent is unchanged.

Warning cap moved 11 → 13 — the newer compiler pass recognises two more TanStack
`useVirtualizer` call sites as unmemoizable. Rationale recorded in
[ci.yml](../.github/workflows/ci.yml).

### DEP-6 — react-router HIGH (GHSA-qwww-vcr4-c8h2) — FIXED
RSC-mode CSRF bypass: an action can execute before the 400 response. Vulnerable
`>= 7.12.0, < 8.3.0`, patched 8.3.0.

Not exploitable here — both apps are SPAs with no RSC/server request handler
(`createBrowserRouter` in declarative mode, and a `HashRouter` in docs-ui; no `@react-router/*`
packages in either lockfile). Fixed forward anyway rather than risk-accepted, because the v7
line has no patched release: `react-router-dom` stops at 7.18.1 and pins `react-router`
exactly, so neither an `overrides` entry nor a caret bump can reach 8.3.0. npm's own
suggestion was a **downgrade** to 7.11.0 — rejected as a dead end that the next advisory
would reopen.

Migrated both packages to `react-router` 8.x, which dissolved the `react-router-dom` package:
general APIs now import from `react-router`, DOM-specific ones from `react-router/dom` (in
this repo only `RouterProvider`, in [App.tsx](../src/nodepilot-ui/src/App.tsx)). 55 files
touched, plus four `vi.mock('react-router-dom')` call sites. v8 also raises the floors to
node ≥ 22.22 and react ≥ 19.2.7 — hence the CI runner bump from Node 20 to 22 across all
three frontend jobs and the react/react-dom bump in both packages.
