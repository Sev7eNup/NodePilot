# appsettings-Übersicht

NodePilot ist konfigurationsgetrieben. Die meisten Verhaltensaspekte sind über `appsettings.json` (bzw. `appsettings.Production.json`) steuerbar. Umgebungsspezifische Overrides via Umgebungsvariablen (Doppelter Unterstrich `__` = `:`).

## Provider

| Setting | Werte | Default |
|---|---|---|
| `Database:Provider` | `postgres` \| `sqlserver` | `postgres` |
| `Remote:Provider` | `winrm` \| `noop` | `winrm` |
| `Secrets:Provider` | `Dpapi` \| `AesGcm` | `Dpapi` |
| `Logging:Format` | `text` \| `cmtrace` \| `json` \| `ecs-json` | `text` |

`noop`-Remote muss quittiert werden (`Remote:AllowNoop=true` bzw. `NODEPILOT_ALLOW_NOOP_REMOTE=1`), sonst Boot-Abbruch.

## Connection Strings

| Provider | Key |
|---|---|
| PostgreSQL | `ConnectionStrings:Postgres` |
| SQL Server | `ConnectionStrings:DefaultConnection` |

## Auth

| Key | Default | Anforderung |
|---|---|---|
| `Jwt:Key` / `Jwt:Issuer` / `Jwt:Audience` | auto-generiertes `jwt-secret.key` falls `Jwt:Key` fehlt | auf allen HA-Nodes identisch |
| `Authentication:LocalLoginMode` | `BreakGlassOnly` | `Disabled`, `BreakGlassOnly` oder `Enabled` |
| `Authentication:SessionAbsoluteLifetimeHours` | `8` | 1–168; Refresh verlängert die absolute Grenze nicht |
| `Authentication:MaxAuthorizationStalenessMinutes` | `15` | 1–15; stale externe Snapshots werden abgewiesen |
| `Authentication:Ldap:Enabled` | `false` | bei `true`: LDAPS-Endpunkt, Base-DN, UPN-Suffix, Service-Bind und Allowlist erforderlich |
| `Authentication:Ldap:Endpoints` | `[]` | geordnete DC-Liste, z. B. `ldaps://dc01:636`; Bind versucht sie der Reihe nach, der Lookup danach verlangt aber **All-DC-Konsens** (kein Login-Failover — ein DC-Ausfall ⇒ 503) |
| `Authentication:Ldap:UseSsl` | `true` | bei aktiviertem LDAP verpflichtend; Zertifikatsprüfung bleibt an |
| `Authentication:Ldap:BindTimeoutSeconds` | `5` | 1–5 (Boot-Validierung erzwingt diese Grenze) |
| `Authentication:Ldap:AllowedGroupSids` | `[]` | mindestens eine AD-Gruppen-SID für LDAP oder Windows-SSO |
| `Authentication:Ldap:DirectorySyncIntervalMinutes` | `5` | 1–5 |
| `Authentication:Ldap:DirectorySyncMaxConcurrency` | `16` | 1–32 parallele Service-Bind-Lookups pro Sync-Pass |
| `Authentication:Windows:Enabled` | `false` | HTTP-SPN, Browser-Intranet-Policy und vollständige LDAPS-Service-Bind-Konfiguration erforderlich |
| `Authentication:Windows:AllowNtlmFallback` | `false` | muss `false` bleiben |
| `Authentication:Windows:NtlmDisabledByPolicy` | `false` | muss vor Aktivierung von Windows-SSO als `true` bestätigt sein |
| `Authentication:Oidc:Enabled` | `false` | HTTPS-Authority, Client-ID/-Secret und Gruppen-Allowlist; release-gated |
| `Authentication:Scim:Enabled` | `false` | Bearer-Token mit 32–4096 Zeichen; release-gated |
| `Authentication:Scim:PreviousBearerToken` | `null` | alter Token nur während einer kontrollierten Rotationsüberlappung; danach löschen |
| `DataProtection:KeyRingPath` | `data-protection-keys` | bei HA+OIDC persistenter gemeinsamer Pfad für alle Nodes |
| `DataProtection:CertificateThumbprint` | `null` | bei HA+OIDC gemeinsames Zertifikat mit Private Key in `LocalMachine\My` |
| `DataProtection:SharedKeyRing` | `false` | muss bei HA+OIDC nach verifiziertem Shared Storage `true` sein |
| `ExternalTrigger:ApiKey` | leer | leer bedeutet: External Trigger inaktiv |

Die komplette `Authentication`-Sektion ist boot-fest. Saves über die Admin-Einstellungen setzen den Restart-Marker; aktiv werden sie erst nach einem Service-Neustart. LDAP kann vor dem Speichern über `POST /api/admin/settings/test/ldap` gegen den aktuellen Entwurf geprüft werden. Secrets gehören in Umgebungsvariablen oder den Secret-Provider.

Im Cluster ist Authentication **Config-as-Code**: `PUT /api/admin/settings/Authentication` antwortet mit `409 CLUSTER_CONFIG_AS_CODE_REQUIRED`. Konfiguration und Secrets müssen identisch auf alle Nodes ausgerollt werden; danach ist ein Cluster-Neustart erforderlich. Der gemeinsame Data-Protection-Keyring schützt OIDC-Correlation, Nonce und serverseitige Tickets über Node-Wechsel hinweg. Das Zertifikat kann vom Kestrel-TLS-Zertifikat getrennt sein, muss aber mit Private Key auf jedem Node verfügbar sein.

Details: [AD SSO Preview](../enterprise/ldap-windows-sso), [Authentifizierung](../api/authentication).

## Cluster / HA

`Cluster:Enabled` (default `false`). Details: [High Availability](../enterprise/high-availability).

## KI

`Llm:Enabled` (default `false`). Details: [AI-Features](../ai-features).

## Observability

`OpenTelemetry:Exporters:PrometheusScrapeAllowAnonymous` (default `false`). Details: [Observability](../observability).

## Retention

Opt-out via `Retention:*:Enabled: false`. Idempotency-Keys (24 h, fixe TTL) laufen **immer**. Details: [Retention-Services](./retention).

## Hardening

Default `true` (hardened), `appsettings.Development.json` relaxt auf `false`. Details: [Hardening-Flags](../security/hardening).

## Pfade (Produktion)

| Key | Zweck | Fallback |
|---|---|---|
| `Jwt:KeyPath` | Pfad für `jwt-secret.key` | `{ContentRoot}/jwt-secret.key` |
| `Security:AdminSetupTokenPath` | Pfad für `admin-setup.token` | `{ContentRoot}/admin-setup.token` |
| `Logging:File:Path` | Serilog Rolling-File | `{ContentRoot}/logs/nodepilot-.log` |
| `Kestrel:Https:*` | Kestrel-Direct-HTTPS aus Windows Cert Store | Default-Binding |

`Credentials:DpapiScope` in Produktion auf `LocalMachine` setzen.
