# appsettings overview

NodePilot reads configuration from `appsettings.json`, environment-specific files and environment variables. Later sources override earlier ones. In environment variables, a double underscore `__` replaces the colon of a configuration hierarchy.

Example: `ConnectionStrings__Postgres` corresponds to `ConnectionStrings:Postgres`.

## Providers

| Setting | Values | Default |
|---|---|---|
| `Database:Provider` | `postgres` \| `sqlserver` | `postgres` |
| `Remote:Provider` | `winrm` \| `noop` | `winrm` |
| `Secrets:Provider` | `Dpapi` \| `AesGcm` | `Dpapi` |
| `Logging:Format` | `text` \| `cmtrace` \| `json` \| `ecs-json` | `text` |

The `noop` remote provider has to be acknowledged (`Remote:AllowNoop=true` or `NODEPILOT_ALLOW_NOOP_REMOTE=1`), otherwise the boot is aborted.

Provider connections, timeout budgets and the behaviour during database outages are described in
[Database providers](./database).

## Connection strings

| Provider | Key |
|---|---|
| PostgreSQL | `ConnectionStrings:Postgres` |
| SQL Server | `ConnectionStrings:DefaultConnection` |

## Authentication

| Key | Default | Requirement |
|---|---|---|
| `Jwt:Key` / `Jwt:Issuer` / `Jwt:Audience` | An auto-generated `jwt-secret.key` if `Jwt:Key` is absent | Identical on all HA nodes |
| `Authentication:LocalLoginMode` | `BreakGlassOnly` | `Disabled`, `BreakGlassOnly` or `Enabled` |
| `Authentication:SessionAbsoluteLifetimeHours` | `8` | 1–168; a refresh does not extend the absolute limit |
| `Authentication:MaxAuthorizationStalenessMinutes` | `15` | 1–15; stale external snapshots are rejected |
| `Authentication:Ldap:Enabled` | `false` | With `true`: an LDAPS endpoint, base DN, UPN suffix, service bind and allow-list are required |
| `Authentication:Ldap:Endpoints` | `[]` | An ordered DC list, e.g. `ldaps://dc01:636`; the bind tries them in order, but the lookup afterwards requires **consensus across all DCs** (no login failover — one DC down ⇒ 503) |
| `Authentication:Ldap:UseSsl` | `true` | Mandatory when LDAP is enabled; certificate validation stays on |
| `Authentication:Ldap:BindTimeoutSeconds` | `5` | 1–5 (boot validation enforces this limit) |
| `Authentication:Ldap:AllowedGroupSids` | `[]` | At least one AD group SID for LDAP or Windows SSO |
| `Authentication:Ldap:DirectorySyncIntervalMinutes` | `5` | 1–5 |
| `Authentication:Ldap:DirectorySyncMaxConcurrency` | `16` | 1–32 concurrent service-bind lookups per sync pass |
| `Authentication:Windows:Enabled` | `false` | An HTTP SPN, a browser intranet policy and a complete LDAPS service-bind configuration are required |
| `Authentication:Windows:AllowNtlmFallback` | `false` | Has to stay `false` |
| `Authentication:Windows:NtlmDisabledByPolicy` | `false` | Has to be confirmed as `true` before Windows SSO is enabled |
| `Authentication:Oidc:Enabled` | `false` | An HTTPS authority, client ID/secret and a group allow-list; release-gated |
| `Authentication:Scim:Enabled` | `false` | A bearer token of 32–4096 characters; release-gated |
| `Authentication:Scim:PreviousBearerToken` | `null` | The old token, only during a controlled rotation overlap; delete it afterwards |
| `DataProtection:KeyRingPath` | `data-protection-keys` | With HA+OIDC, a persistent shared path for all nodes |
| `DataProtection:CertificateThumbprint` | `null` | With HA+OIDC, a shared certificate with its private key in `LocalMachine\My` |
| `DataProtection:SharedKeyRing` | `false` | Has to be `true` with HA+OIDC once shared storage is verified |
| `ExternalTrigger:Keys:<id>:KeyHash` | Empty | The SHA-256 of the integration key as Base64; the plaintext is not persisted |
| `ExternalTrigger:Keys:<id>:AllowedWorkflowIds` | `[]` | Immutable workflow GUIDs; empty means deny-all |
| `ExternalTrigger:ApiKey` | Empty | The legacy plaintext key; only effective together with the allow-list below |
| `ExternalTrigger:AllowedWorkflowIds` | `[]` | The GUID scope of the legacy key; empty means deny-all |

`ExternalTrigger:Keys` is read as a complete map from the highest-priority provider that declares it. A higher-priority `Keys: {}` therefore reliably revokes all lower-priority keys; individual hashes and scopes are never assembled from different provider snapshots. `AllowedWorkflowIds` is likewise not merged index by index: `[A]` replaces a lower-priority list `[A,B]`, and `[]` is deny-all. A provider override of the `Keys` map therefore has to contain every integration that should still exist.

The entire `Authentication` section is fixed at boot. Saves through the admin settings set the restart marker; they only become active after a service restart. LDAP can be checked against the current draft before saving via `POST /api/admin/settings/test/ldap`. Secrets belong in environment variables or the secret provider.

In a cluster, authentication is **config-as-code**: `PUT /api/admin/settings/Authentication` answers with `409 CLUSTER_CONFIG_AS_CODE_REQUIRED`. The configuration and secrets have to be rolled out identically to all nodes, after which a cluster restart is required. The shared data-protection key ring protects OIDC correlation, nonce and server-side tickets across node changes. The certificate can be separate from the Kestrel TLS certificate, but has to be available with its private key on every node.

Details: [AD SSO Preview](../enterprise/ldap-windows-sso), [Authentication](../api/authentication).

## Cluster / HA

`Cluster:Enabled` (default `false`). Details: [High availability](../enterprise/high-availability).

## AI

`Llm:Enabled` (default `false`) plus at least one profile under `Llm:Profiles` and an `Llm:ActiveProfileId` pointing at it. If outbound traffic is only permitted through a proxy, `Llm:Proxy:Mode` is added (default `Off`, otherwise `System` or `Custom`). Details: [AI features](../ai-features).

## Observability

`OpenTelemetry:Exporters:PrometheusScrapeAllowAnonymous` (default `false`). Details: [Observability](../observability).

## Retention

Opt out via `Retention:*:Enabled: false`. Idempotency keys (24 h, fixed TTL) run **always**. Details: [Retention services](./retention).

## Performance

`Performance:ManualTuning` (default `false`, requires a restart). With `false`, NodePilot derives `Engine:Runspace:*`, `Engine:MaxConcurrentSteps`, `Threading:*` and `ExecutionDispatch:*` from the detected CPU + RAM — the numbers of those sections in the configuration are then an **inert preset**. What is actually in force: `GET /api/admin/settings/effective-sizing` or `np settings effective-sizing`. Details: [Performance sizing](./performance).

## Hardening

Default `true` (hardened); `appsettings.Development.json` relaxes them to `false`. Details: [Hardening flags](../security/hardening).

## Paths (production)

| Key | Purpose | Fallback |
|---|---|---|
| `Jwt:KeyPath` | Path for `jwt-secret.key` | `{ContentRoot}/jwt-secret.key` |
| `Security:AdminSetupTokenPath` | Path for `admin-setup.token` | `{ContentRoot}/admin-setup.token` |
| `Logging:File:Path` | The Serilog rolling file | `{ContentRoot}/logs/nodepilot-.log` |
| `Kestrel:Https:*` | Kestrel direct HTTPS from the Windows certificate store | Default binding |

Set `Credentials:DpapiScope` to `LocalMachine` in production.
