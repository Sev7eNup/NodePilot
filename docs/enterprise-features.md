# NodePilot enterprise features

Overview of availability, configuration and rollout requirements. Detailed setup and
operating procedures are linked per feature. **AD SSO remains Preview** until the full
production-equivalent field-test matrix passes; OIDC/SCIM has a separate release gate.

| Feature | Status | Default / enable with |
|---|---|---|
| Active/passive HA | Implemented and field-tested | Off; `Cluster:Enabled=true`. |
| Secret providers | Implemented and field-tested | DPAPI; portable encryption with `Secrets:Provider=AesGcm`. |
| SIEM logging | Implemented | Shipped format `cmtrace`; enable `Logging:Format=ecs-json`. Missing format key falls back to `text`. |
| Folder RBAC | Implemented | Always active; permissions on shared folders. |
| LDAP / Windows SSO | AD SSO Preview | Off; `Authentication:Ldap:Enabled` / `Authentication:Windows:Enabled`. |
| OIDC / SCIM 2.0 | Implemented; separate release gate | Off; `Authentication:Oidc:Enabled` / `Authentication:Scim:Enabled`. |

## High availability

Instances share one database. Only the leader runs workflows and accepts mutations;
followers return `503` with `Retry-After`. Failover uses a database lease and fencing.
Foreign running/paused executions are cancelled during recovery; durable pending jobs
with a dispatch-outbox entry are retained for the new leader.

- Use shared AES-GCM encryption keys and the same JWT signing key on every node;
  machine-bound DPAPI is rejected in cluster mode.
- Route traffic using `/healthz/leader`; keep proxy connections compatible with Windows SSO.
- This is active/passive application HA. Database HA is a separate responsibility;
  active/active and multi-region operation are outside the supported scope.

[HA setup, configuration and operating runbook](ha-active-passive.md) ·
[Architecture decision](adr/0002-active-passive-ha.md).

## Secret providers and rotation

DPAPI is the default; the production template uses `LocalMachine` scope. AES-GCM uses
a shared 32-byte key for portable encryption. Protect keys outside tracked configuration
and logs. Unknown providers or DPAPI scopes fail startup.

For provider/key changes, configure the old provider as a temporary read fallback and run
`POST /api/secrets/reencrypt` as an administrator. Keep the legacy configuration until the
sweep returns `200 OK`, `partialSuccess:false` and **all seven skipped counters are zero**.
`207 Multi-Status` requires resolving the reported skips; a zero legacy-read metric alone
is insufficient. Rotation is explicitly admin-triggered.

[Provider configuration, key storage and migration procedure](secrets-providers.md)
cover credentials, globals, workflow history, agent MCP secrets, notification routes,
pending dispatch parameters and runtime settings. Network vaults and HSM-backed keys
are outside the current implementation.

## SIEM logging

`Logging:Format=ecs-json` writes one ECS-JSON event per line to the rolling application log,
including structured audit events. Configure a separate log forwarder for the target SIEM.

[Field reference and Filebeat/Splunk recipes](siem-logging.md).

## Folder permissions

Shared folders form a tree. User and directory-group grants inherit downwards, and the
highest grant wins; a weaker child grant cannot restrict an inherited stronger one.
Roles are `FolderViewer < FolderOperator < FolderEditor < FolderAdmin`.

- Global Admin bypasses folder restrictions. Global Viewer/Operator roles cap folder rights.
- Newly created local Operators receive `FolderEditor` on Root; Viewers receive
  `FolderViewer`. Remove broad Root grants when restricting a user to one subtree.
  Restarting the service does not restore revoked grants.
- Unreadable workflows return `404`. Sub-workflow execution and SignalR updates enforce
  workflow access too. Folder permissions are not a sandbox for code execution.
- User/group grants are supported; group keys are scoped to their AD authority or OIDC/SCIM
  issuer and evaluated against server-side memberships. Role principals are unsupported.
- FolderEditor may delete an empty folder. Recursive deletion, including workflows and
  execution history, requires global Admin and refuses conflicting edit locks.
- Permissions are folder-level. Per-workflow ACLs, permission templates and per-folder
  audit views are outside the current scope; the audit endpoint remains admin-only.

Manage grants through **Workflows → folder → Permissions…**, including on Root.
The [folder RBAC guide](../src/nodepilot-docs-ui/content/en/enterprise/folder-rbac.md)
contains the UI procedures, capability rules and API table.

## Enterprise authentication

All external providers are opt-in. Local sign-in defaults to `BreakGlassOnly`.
Keep an active local break-glass administrator before enabling external authentication;
startup rejects an existing database without one. Authentication changes require a service restart.

**LDAP and Windows:** use validated LDAPS, an allowed-group SID list and a directory service
bind. Windows login also requires a service-identity HTTP SPN, browser intranet policy and
verified NTLM denial. LDAP and Windows resolve the same AD SID to the same NodePilot user.
Existing local accounts are never automatically merged with external identities.

**Multiple DCs require consensus, not login failover.** An unreachable or disagreeing DC
makes login fail closed. Background membership refresh runs every one to five minutes;
external authorization may be at most 15 minutes stale. Deactivation or access-group removal
revokes sessions and stops affected automated and interactive executions.

**OIDC and SCIM:** use Authorization Code + PKCE for login and SCIM Users/Groups for provisioning.
Issuer, subject and group IDs are opaque and case-sensitive; SCIM authority must match the
OIDC issuer for shared identities. Group-overage fallback requires an explicit overage signal
and an issuer-matching snapshot no older than 15 minutes. A SCIM user update does not refresh
group authority. SAML is outside scope.

[Authentication configuration and acceptance tests](ldap-windows-sso.md) cover LDAPS,
Kerberos/HAProxy, session revocation, browser policies, IdP callbacks, SCIM token rotation and
recovery. The [identity architecture decision](adr/0009-enterprise-identity-sessions-and-provisioning.md)
documents the shared authorization model.

## Rollout checks

1. Verify local break-glass access and recovery before enabling an external provider.
2. Configure directory trust, service bind and allowed groups; test the connection and restart.
3. Prove membership refresh, group removal and session/execution revocation within the configured limit.
4. Test Kerberos through the intended proxy and browser path, including NTLM rejection and
   LDAP/Windows identity consistency. Keep AD SSO Preview until the full field-test matrix passes.
5. Validate OIDC/SCIM separately for the actual IdP, including group overage, deprovisioning
   and complete reprovisioning after restore.

Use the [authentication test matrix](ldap-windows-sso.md#required-field-test-before-production-status)
and [HA verification checklist](ha-active-passive.md#cluster-mode-verification-checklist)
for acceptance evidence.
