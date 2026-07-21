# AD SSO Preview

> **Status: Preview.** LDAP/LDAPS und Windows Negotiate sind implementiert, gelten aber erst nach realen AD-, Kerberos-, LDAPS- und NTLM-Ablehnungstests über die Produktions-Topologie als Enterprise-ready. OIDC und SCIM haben ein separates Release-Gate.

## Anmeldewege

| Pfad | Endpoint | Default | Zweck |
|---|---|---|---|
| Lokales Passwort (BCrypt) | `POST /api/auth/login` | `BreakGlassOnly` | explizit markierte Notfallkonten |
| LDAP Simple Bind über LDAPS | `POST /api/auth/login` | aus | Domänen-Login mit Benutzername und Passwort |
| Windows Negotiate/Kerberos | `POST /api/auth/windows` | aus | Browser-SSO für domänengebundene Windows-Clients |
| OpenID Connect | `GET /api/auth/oidc` | aus, release-gated | allgemeiner Enterprise-IdP-Pfad mit Authorization Code + PKCE |

Alle Wege erzeugen dieselbe serverseitig widerrufbare Session. Das JWT-Cookie enthält keine Gruppenliste; die absolute Session-Lebensdauer beträgt standardmäßig acht Stunden.

## Sichere Basiskonfiguration

```jsonc
{
  "Authentication": {
    "LocalLoginMode": "BreakGlassOnly",
    "SessionAbsoluteLifetimeHours": 8,
    "MaxAuthorizationStalenessMinutes": 15,
    "Ldap": {
      "Enabled": false,
      "Endpoints": [
        "ldaps://dc01.firma.de:636",
        "ldaps://dc02.firma.de:636"
      ],
      "Port": 636,
      "UseSsl": true,
      "BaseDn": "DC=firma,DC=de",
      "UpnSuffix": "firma.de",
      "BindTimeoutSeconds": 5,
      "ServiceBindDn": "CN=svc-nodepilot-ldap,OU=Services,DC=firma,DC=de",
      "ServicePassword": "<secret>",
      "AllowedGroupSids": [
        "S-1-5-21-...-512",
        "S-1-5-21-...-1108"
      ],
      "DirectorySyncIntervalMinutes": 5,
      "DirectorySyncMaxConcurrency": 16,
      "GlobalRoleMappings": [
        { "GroupSid": "S-1-5-21-...-512", "Role": "Admin" },
        { "GroupSid": "S-1-5-21-...-1108", "Role": "Operator" }
      ],
      "JitUserDefaultRootRole": null
    },
    "Windows": {
      "Enabled": false,
      "AllowNtlmFallback": false,
      "NtlmDisabledByPolicy": false
    },
    "Oidc": {
      "Enabled": false,
      "Authority": "https://idp.example.com/tenant/v2.0",
      "ClientId": "<client-id>",
      "ClientSecret": "<secret>",
      "DisplayName": "Firmenkonto",
      "NameClaimType": "preferred_username",
      "GroupsClaimType": "groups",
      "Scopes": ["openid", "profile", "email"],
      "AllowedGroupIds": ["<admin-group-object-id>", "<operator-group-object-id>"],
      "GlobalRoleMappings": [
        { "GroupId": "<admin-group-object-id>", "Role": "Admin" },
        { "GroupId": "<operator-group-object-id>", "Role": "Operator" }
      ]
    },
    "Scim": {
      "Enabled": false,
      "Authority": "https://idp.example.com/tenant/v2.0",
      "BearerToken": "<random-secret-with-at-least-32-characters>",
      "PreviousBearerToken": null
    }
  }
}
```

- Bei aktiviertem LDAP **oder Windows-SSO** sind mindestens ein LDAPS-Endpunkt, `BaseDn`, Service-Bind-Credentials und eine `AllowedGroupSids`-SID erforderlich; LDAP-Passwortlogin benötigt zusätzlich `UpnSuffix`.
- LDAPS mit vollständiger Zertifikatsprüfung ist verpflichtend: Das DC-Zertifikat muss gegen den Windows-Zertifikatsspeicher des API-Hosts validieren, einen In-App-Bypass gibt es nicht. Plaintext-LDAP und StartTLS auf Port 389 sind kein unterstützter Enterprise-Pfad. LDAP-Referrals werden nie verfolgt — jede Abfrage wird vom bewusst konfigurierten Endpunkt beantwortet.
- `AllowedGroupSids` ist die Zugangs-Policy; `GlobalRoleMappings` bestimmt davon unabhängig die globale Rolle. Ohne Rollen-Match gilt `Viewer`.
- `DirectorySyncIntervalMinutes` liegt zwischen 1 und 5 Minuten. `DirectorySyncMaxConcurrency` begrenzt einen Sync-Pass auf 1–32 parallele Service-Bind-Lookups, Default 16.
- Windows-SSO ist Kerberos-only. `AllowNtlmFallback=true` wird abgelehnt; `NtlmDisabledByPolicy=true` bestätigt die zusätzlich umgesetzte Host-/Domain-Policy.
- Secrets gehören in Umgebungsvariablen oder den Secret-Provider, nicht in eine eingecheckte Datei.

Die komplette `Authentication`-Sektion ist boot-fest. Gespeicherte Änderungen werden erst nach einem Service-Neustart aktiv. Der LDAP-Verbindungstest in den Admin-Einstellungen prüft den noch nicht gespeicherten Entwurf gegen TLS-Vertrauen, Service-Bind und Search Base.

Im Cluster ist Authentication **Config-as-Code**. `PUT /api/admin/settings/Authentication` liefert `409 CLUSTER_CONFIG_AS_CODE_REQUIRED`; identische Konfiguration und Secrets müssen auf jeden Node ausgerollt und durch einen Cluster-Neustart aktiviert werden.

### HA mit OIDC

OIDC-Correlation, Nonce und serverseitige Tickets werden mit ASP.NET Core Data Protection geschützt. Jeder HA-Node benötigt deshalb denselben persistenten Keyring und dasselbe Zertifikat mit Private Key:

```jsonc
{
  "DataProtection": {
    "KeyRingPath": "\\\\fileserver\\nodepilot\\data-protection-keys",
    "CertificateThumbprint": "<shared-certificate-thumbprint>",
    "SharedKeyRing": true
  }
}
```

`KeyRingPath` muss für alle Nodes auf denselben persistenten Storage zeigen. Das Zertifikat muss auf jedem Node in `LocalMachine\My` liegen; es kann vom Kestrel-TLS-Zertifikat getrennt sein. HA+OIDC startet ohne diese drei expliziten Angaben nicht.

## Identität, Gruppen und Offboarding

- Externe Identitäten werden über das unveränderliche Paar `(Authority, Subject)` gefunden.
- LDAP und Windows verwenden dieselbe AD-Authority und den Benutzer-`objectSid` als Subject. Beide Wege landen daher bei derselben Person auf demselben NodePilot-Benutzer.
- Windows verwendet aus dem Kerberos-Principal nur die primäre SID. Bei **jedem** Windows-Login lädt ein Service-Bind über LDAPS den aktuellen, autoritativen Benutzer- und Gruppen-Snapshot; möglicherweise alte PAC-Gruppen werden nicht vertraut. Ist der Directory-Lookup nicht möglich, schlägt der Login geschlossen fehl.
- OIDC verwendet den validierten Issuer als Authority und `sub` als Subject.
- Gleichnamige bestehende Benutzer werden nicht automatisch zusammengeführt. Kollisionen werden abgewiesen und auditiert.
- Gruppen liegen als serverseitige Membership-Snapshots vor und werden nicht in JWT oder Cookie geschrieben. Folder-Gruppenrechte nutzen diese Snapshots.
- Folder-Gruppenrechte sind mit `PrincipalAuthority` + `PrincipalKey` namespaced. AD-SIDs und gleichlautende OIDC-/SCIM-Gruppen-IDs können dadurch keine Rechte zwischen Providern erben.
- Der AD-Sync läuft standardmäßig alle fünf Minuten. Ein Gruppenentzug, eine Deaktivierung oder ein Tombstone widerruft Sessions und blockiert auch geplante Jobs und Trigger spätestens nach der maximalen Autorisierungs-Staleness von 15 Minuten.
- Eine lokal durch einen Admin gesetzte Deaktivierung ist sticky: weder ein gesunder AD-Snapshot noch SCIM `active=true` reaktiviert das Konto automatisch. Dafür ist die explizite Admin-Reaktivierung erforderlich.
- Gelöschte externe Identitäten bleiben als Tombstone erhalten und können nur explizit durch einen Admin reaktiviert werden.
- Ein All-Not-Found-Sync für sämtliche bekannten AD-Identitäten wird als fehlerhafte `BaseDn`/Search-Berechtigung verworfen und erzeugt keine Massen-Tombstones. Access bleibt nach dem Freshness-Limit trotzdem fail-closed.
- Bei aktivem LDAP, Windows, OIDC oder SCIM startet eine bestehende Datenbank nur mit einem aktiven lokalen Break-Glass-Admin. Eine leere Installation bleibt für den einmaligen lokalen Bootstrap startfähig.

## Kerberos und HAProxy

Für Windows-SSO muss der HTTP-SPN mit `setspn -S` auf dem Dienstkonto registriert sein. Browser müssen die URL als Intranet-Ziel behandeln.

Vor HAProxy gelten zusätzlich:

- HTTP/1.1 und persistente Verbindungen auf beiden Hops;
- `http-reuse never`, damit Backend-Verbindungen nie zwischen Clients geteilt werden;
- Source-Stickiness während des Negotiate-Handshakes;
- validiertes Backend-TLS mit CA, SNI und Hostname-Prüfung;
- vom Proxy gelöschte und neu gesetzte Forwarded Headers sowie eine enge `ForwardedHeaders:KnownProxies`-Liste.

Die vollständige Vorlage liegt unter `deploy/templates/haproxy.cfg.template`.

## OIDC und SCIM 2.0 — separates Release-Gate

OIDC verwendet Authorization Code + PKCE, HTTPS-Metadata sowie Issuer-, Audience-, State- und Nonce-Prüfung. Der Zugriff benötigt mindestens eine konfigurierte `Authentication:Oidc:AllowedGroupIds`-Gruppe. Das Discovery-Endpoint meldet `oidcEndpoint` und den konfigurierten Anzeigenamen.

Gruppen aus einem OIDC-Token gelten nur, wenn sein `iat` vorhanden, höchstens eine Minute in der Zukunft und höchstens `MaxAuthorizationStalenessMinutes` alt ist — maximal 15 Minuten. Bei einem expliziten Group-Overage-Signal darf NodePilot stattdessen ausschließlich authority-scoped SCIM-Memberships verwenden, deren `LastSeenAt` ebenfalls innerhalb dieses Fensters liegt. Ein Login selbst verlängert die SCIM-Freshness nicht.

SCIM stellt `ServiceProviderConfig`, `ResourceTypes`, `Schemas`, `/api/scim/v2/Users` und `/api/scim/v2/Groups` bereit. Bearer-Tokens müssen 32–4096 Zeichen lang sein. Für eine unterbrechungsfreie Rotation bleibt der alte Wert kurzzeitig unter `PreviousBearerToken`, bis der IdP den neuen `BearerToken` verwendet; danach wird der alte Slot gelöscht. `Authentication:Scim:Authority` muss zur OIDC-Authority passen oder fällt auf diese zurück. Beim Anlegen eines Users ist `externalId` verpflichtend und muss **exakt und case-sensitive** dem OIDC-`sub` entsprechen; es ist danach unveränderlich.

Ein SCIM-User-Update bestätigt keine Gruppen und erneuert deshalb keine Autorisierungs-Freshness. Für Group-Overage muss der IdP mindestens alle 15 Minuten einen vollständigen Group-Snapshot beziehungsweise einen semantisch gleichwertigen Membership-Heartbeat liefern, der auch unveränderte Memberships aktualisiert. Dieses Verhalten ist Teil des SCIM-Release-Gates.

OIDC und SCIM bleiben bis zu realen Provider-, Parallel-JIT-, Group-Overage- und Offboarding-Tests release-gated. SAML ist nicht Teil dieses Zielbilds.

## API und Betrieb

| Endpoint | Zweck |
|---|---|
| `GET /api/auth/methods` | aktive Loginwege inklusive OIDC-URL und Anzeigename |
| `POST /api/auth/login` | lokales Passwort oder LDAP |
| `POST /api/auth/windows` | Negotiate/Kerberos |
| `GET /api/auth/oidc` / `GET /api/auth/oidc/callback` | OIDC-Browserflow |
| `POST /api/admin/settings/test/ldap` | LDAPS-/Bind-/Search-Test des Entwurfs |
| `GET /healthz/ready` | DB-Readiness; enthält bewusst keinen Directory-Check |
| `GET /healthz/directory` | separater LDAPS-/Service-Bind-Healthcheck über alle DCs; ein ausgefallener Secondary ergibt `Degraded` |
| `/api/scim/v2/*` | SCIM-Discovery, Users und Groups |

Beim IdP wird `https://<nodepilot>/signin-oidc` als Redirect URI registriert.
`/api/auth/oidc/callback` ist nur die interne Landing-URL, nachdem der OIDC-Handler
Code, State, Nonce, Issuer und Signatur validiert hat.

Vor einer Hochstufung von **AD SSO Preview** müssen reale Tests über die Ziel-Topologie belegen: LDAP und Windows mappen dieselbe SID auf denselben Benutzer, der Windows-Pfad ignoriert PAC-Gruppen zugunsten des LDAPS-Snapshots, LDAPS lehnt ungültige Zertifikate ab, Kerberos funktioniert durch HAProxy, NTLM wird abgelehnt und Offboarding greift innerhalb von 15 Minuten. Für OIDC/SCIM gehören HA-Failover mit gemeinsamem Data-Protection-Keyring sowie vollständige Membership-Heartbeats innerhalb von 15 Minuten zusätzlich zum Release-Gate.
