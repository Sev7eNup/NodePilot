# NodePilot threat model

Owner: NodePilot maintainer. Reviewed against source on 2026-10-08.
This document records the existing security contract; it does not grant new capabilities.
Update it when roles, execution identities, secret storage, external tools or network policy change.

## Deployment and protected assets

NodePilot is a privileged automation service within one administrative trust domain.
It is not a hostile multi-tenant execution platform. Protect service and target identities,
stored credentials and encryption keys, workflow definitions, execution data, agent evidence,
directory membership and availability of the automation estate.

Production assumes a trusted Windows host, database administrators, installed PowerShell
modules/providers, administrator-managed MCP services and skill packages. An attacker may
control an unauthenticated request, a Viewer account, a workflow ID outside their folder,
external webhook content, remote log/file content or a model/tool response. Compromising the
host or a trusted code-author identity can defeat application-level secret and folder controls.

## Trust boundaries and enforcement

| Boundary | Contract and enforcement | Limits / operator responsibility |
|---|---|---|
| Browser, CLI or MCP client → API | Server authentication, token validity and role gates; browser mutations require CSRF validation. Workflow and execution access additionally uses folder authorization. See `AuthenticationSetup`, `TokenValidityMiddleware`, `CsrfMiddleware`, `ResourceAuthorizationService` and `ResourceAuthorizationGateExtensions`. | Hidden UI controls and client-supplied roles are not authorization. Production TLS, proxy trust and Windows access must be configured by the operator. |
| Global role → folder permissions | Admin bypasses folder grants. Viewer is capped at read; Operator cannot acquire administrative rights through a folder grant. Read/run/edit still require the corresponding folder grant. | Folder RBAC controls supported application access; it does not confine code written by trusted authors. |
| Operator → credentials and machines | `CredentialsController` allows Admin/Operator to list credential metadata, create and update global credentials; deletion is Admin-only. Credential responses omit passwords. Execution resolves selected credentials on the server. | Credentials and machines are global, not per-folder vaults. Operators must be trusted with the accounts usable by automation, including the ability to replace credentials. Use separate installations/identities for mutually untrusted authors. |
| Workflow → local or remote execution | Ordinary script authors are trusted to execute under the configured account. Local execution uses the service identity; WinRM uses an explicit credential or integrated service identity. `WinRmSessionFactory` enforces `Remote:RequireWinRmSsl` when enabled and does not disable certificate checks. | Process isolation is not an OS privilege sandbox. Limit service/target account privileges and network access. HTTP WinRM uses Negotiate; transport policy is a deployment decision. |
| Parent → child workflow | `SubWorkflowAuthorizationResolver` resolves the run initiator or stable publisher and checks account state and cross-folder permissions. | Published/scheduled automation carries an identity even without a browser session. Review deactivation, group changes and publication together. |
| Database/configuration/backup → plaintext secrets | Credential providers protect stored secrets; decryption happens only where needed. API DTOs omit credential passwords; redactors protect known secret-bearing outputs. Backup payloads are passphrase protected. | Encryption does not protect against a compromised running service or arbitrary trusted-author code. DPAPI recovery needs the relevant machine/user context; AES-GCM recovery needs its key. Configuration backups are not complete disaster recovery. |
| Workflow configuration → agent tools | Publishing authorizes selected autonomous tools. `AgentTargetFactory` binds target and credential; an explicitly selected missing target fails. Service identity requires workflow selection plus administrator opt-in. `AgentPermissionPolicy` checks shell commands before execution; `AgentReadOnlyWorkflowScope` propagates checks into selected synchronous children. | An unspecified target may intentionally mean the NodePilot server. An explicit unknown target never silently becomes local. Current agents have a read policy, not arbitrary-code sandboxing. Trusted providers and OS permissions still matter. |
| Agent → HTTP/MCP/skills | HTTP tools accept GET/HEAD without bodies or redirects and use destination policy. MCP requires an administrator-approved server revision and tool-contract fingerprint, checked again at invocation. Skills are administrator-managed, with revocation checks. | GET/HEAD is not proof that a remote service has no side effects. MCP annotations alone do not establish safety. Only approve trusted services with suitable credentials. Shell/network egress remains an OS/network responsibility. |
| External content → model → action | Files, logs, tool results and delegated answers are untrusted input. Host tool selection, argument checks and execution policy remain authoritative. | Prompt injection may influence permitted reads or disclosure without gaining a new tool. Read-only does not mean confidential: selected content can reach the configured model/provider. Review data classification, destinations and provider retention. |

Supporting decisions: [security findings](security-findings.md),
[agent authorization](adr/0016-general-ai-agent-activities.md#authorization-and-trust),
[agent read policy](ai-agents.md#current-read-only-policy),
[backup contract](adr/0001-system-configuration-backup-restore.md),
[database availability](adr/0011-database-availability-breaker.md).

## Abuse cases and regression evidence

| Attempt | Expected result | Existing regression coverage |
|---|---|---|
| Viewer calls a mutation/run endpoint or uses an oversized folder grant | Denied by role cap/server middleware | `RoleMatrixSmokeTests`, `ResourceAuthorizationServiceTests` |
| Caller substitutes a sibling-folder workflow or child-workflow ID | Denied under the effective principal | `ResourceAuthorizationServiceTests`, sub-workflow authorization tests |
| Credential list/get exposes a password or protected blob | Only explicit metadata DTOs leave the API | `CredentialsControllerTests`; source review of DTO construction |
| Agent selects an unknown target, omits credentials, or keeps using revoked service identity | Rejected without a target fallback or new execution | `AgentTargetTests` |
| Prompt/tool output requests shell mutation, dynamic execution, write HTTP or a writing child workflow | Denied by host policy before external execution | `AgentPermissionPolicyTests`, `AgentReadCallsTests`, `AgentReadWorkflowTests` |
| MCP server revision/contract changes after approval | Invocation rejected until administrator re-approval | `AgentReadCallsTests` |
| WinRM setup disables required TLS or puts connection-error secrets in spans | Rejected / sanitized | `WinRmSessionFactoryGuardTests`, `WinRmSessionSpanRedactionTests` |

Tests are evidence for these cases, not proof that every bypass is impossible. A focused
review and executed-check record is kept in [the review report](security-review-2026-10-08.md).

## Independent deployment validation

A reviewer outside the implementation should run the above attacks against a disposable
Production-mode installation using separate Admin, Operator and Viewer accounts and two
disjoint folder trees. Include an external directory user whose grants are revoked, WinRM
accounts with deliberately limited OS rights, and a controlled HTTP/MCP service whose contract
changes during a run. Verify denial in server responses and absence of the forbidden action
at the destination, not just a UI message. Use synthetic secrets and check API responses,
logs, telemetry, agent events and backups for exposure.

Record the exact commit, production configuration, identity/provider, request or workflow,
expected result, observed result and evidence location. A local source review is not an
independent penetration test. Real domain/WinRM behaviour, model-provider disclosure,
certificate configuration and operator privilege choices require this deployment validation.
