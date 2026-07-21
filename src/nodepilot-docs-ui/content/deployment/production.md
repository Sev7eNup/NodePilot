# Produktions-Rollout

Vollständige Doku im Repo unter `deploy/README.md`. Die `deploy/`-Skripte werden im Dev-Mode **nicht** ausgeführt — diese Seite gibt den Architektur-Überblick.

## Ziel-Topologie

- **Windows Service** unter einer **gMSA** (`DOMAIN\svc-nodepilot$`, `sc.exe create` mit leerem Passwort — `New-Service` kann keine gMSA), delayed auto-start, Recovery-Actions.
- **Kestrel bindet HTTPS direkt** auf den Ports aus `Kestrel:Https:HttpsPort|HttpPort`, Zertifikat per Thumbprint aus `LocalMachine\My`. Single-Node läuft ohne IIS; HA nutzt die gehärtete HAProxy-Vorlage. SPA und API bleiben auf **einem Origin**.
- **Externer SQL Server 2022** (trusted connection) oder **PostgreSQL 16+** (User/Password). Das gMSA-Login / die Postgres-Role braucht DDL-Rechte.
- **gMSA-Identity als WinRM-Auth:** `NegotiateWithImplicitCredential` erlaubt Kerberos zu Zielmaschinen ohne gespeicherte Credentials (resource-based constrained delegation vorausgesetzt).

## Install-Dir / Data-Dir-Split

| Pfad | Inhalt | Service-ACL |
|---|---|---|
| `C:\Program Files\NodePilot\` | `NodePilot.Api.exe`, DLLs, `wwwroot/` | Read |
| `C:\Program Files\NodePilot\appsettings.Production.json` | Config + Secrets | Read (inheritance off) |
| `C:\ProgramData\NodePilot\` | JWT-Key, Setup-Token, Logs, Install-Report | Modify (inheritance off) |

Produktionsartefakte bestehen aus ZIP, signiertem SHA-256-Manifest und detached CMS-Signatur. Installer und Updater verlangen einen explizit gepinnten Code-Signing-Thumbprint und prüfen Signatur, Zertifikatskette, Dateiname, Länge und Hash, bevor sie Dienst oder Installationsverzeichnis verändern. Update-Backups enthalten keine `appsettings.Production.json`.

## Config-Keys

| Key | Zweck | Fallback |
|---|---|---|
| `Jwt:KeyPath` | Absoluter Pfad für `jwt-secret.key` | `{ContentRoot}/jwt-secret.key` |
| `Security:AdminSetupTokenPath` | Absoluter Pfad für `admin-setup.token` | `{ContentRoot}/admin-setup.token` |
| `Logging:File:Path` | Absoluter Pfad für Serilog-Rolling-File | `{ContentRoot}/logs/nodepilot-.log` |
| `Kestrel:Https:*` | Kestrel-Direct-HTTPS aus Windows Cert Store | Default-Binding |
| `Authentication:*` | Loginwege, Session-, Directory- und Provisioning-Policy | boot-fest; Änderungen benötigen Neustart |
| `DataProtection:KeyRingPath` | persistente Correlation-/Nonce-/Ticket-Keys | bei HA+OIDC gemeinsamer Pfad auf allen Nodes |
| `DataProtection:CertificateThumbprint` | schützt den Data-Protection-Keyring | bei HA+OIDC gemeinsames Zertifikat mit Private Key in `LocalMachine\My` |
| `DataProtection:SharedKeyRing` | Operator-Attestation für Shared Storage | bei HA+OIDC `true` |
| `Database:AllowInsecureTls` | Expliziter Development-Override für DB-Zertifikatsprüfung | `false`; in Produktion verboten |

`Credentials:DpapiScope` in Produktion auf `LocalMachine` (sonst Break bei Service-Account-Wechsel). Im Cluster AES-GCM verwenden — siehe [Secret-Provider](../enterprise/secrets-providers).

## Gotchas (aus erstem Lab-Rollout)

- **PS 5.1-Kompat:** `RandomNumberGenerator.Fill()` ist .NET-Core-only — `RNGCryptoServiceProvider.GetBytes()` verwenden. Deploy-Skripte müssen auf PS 5.1 **und** PS 7 laufen.
- **Em-Dashes (`—`) in PS-Skripten** brechen PS 5.1-Parsing, wenn die Datei ohne BOM gespeichert wird. ASCII-Punctuation in Deploy-Skripten.
- **`Set-StrictMode -Version Latest`** + `& npm ...`-Shim triggert `PropertyNotFoundStrict` auf `.Statement`. `Version 3.0` in Deploy-Skripten.
- **`New-Service` unterstützt keine gMSA** (verlangt Passwort). Workaround: `sc.exe create ... obj= DOMAIN\acct$ password= ""`.

## HA in Produktion

Für Active/Passive-Betrieb siehe [High Availability](../enterprise/high-availability). Wichtig: `Jwt:Key`+`Issuer`+`Audience` müssen auf allen Nodes identisch sein; `jwt-secret.key` auf Disk wird im Cluster **nicht** verwendet.

## Rollout-Empfehlung (Enterprise-Features)

1. **SIEM-Logging** zuerst aktivieren, damit alle folgenden Auth- und Offboarding-Ereignisse zentral sichtbar sind.
2. **Secret-Provider und HA** einrichten; alle Nodes brauchen identische JWT-Parameter und Authentication-Config. Mit OIDC benötigen sie zusätzlich dasselbe persistente, zertifikatgeschützte Data-Protection-Keyring.
3. Mindestens ein lokales Konto explizit als **Break-Glass** markieren und den Modus `BreakGlassOnly` verifizieren.
4. Für **AD SSO Preview** vertrauenswürdige LDAPS-Zertifikate, mindestens zwei DC-Endpunkte, Service-Bind, Gruppen-Allowlist, 5-Minuten-Sync und `DirectorySyncMaxConcurrency` (Default 16, Bereich 1–32) konfigurieren. Den LDAP-Entwurf vor dem Save testen.
5. Service neu starten und `/healthz/ready` sowie `/healthz/directory` getrennt prüfen. Readiness bleibt absichtlich DB-only.
6. Für Windows-SSO HTTP-SPN, Browser-Intranet-Policy, Kerberos-taugliches HAProxy und eine Host-/Domain-Policy gegen NTLM einrichten. `AllowNtlmFallback` bleibt `false`; jeder Login muss zusätzlich den autoritativen LDAPS-Snapshot erreichen können.
7. OIDC und SCIM erst nach ihrem separaten Provider-/Offboarding-Release-Gate aktivieren. SAML ist nicht vorgesehen.

Im Cluster ist die Authentication-Sektion Config-as-Code. Der Admin-Settings-PUT antwortet mit `409 CLUSTER_CONFIG_AS_CODE_REQUIRED`; Konfiguration und Secrets werden außerhalb der UI identisch auf alle Nodes verteilt und durch einen Cluster-Neustart aktiviert.

## Offenes Feldtest-Gate

Bis diese Tests in der realen Zielumgebung bestanden sind, lautet der Status **AD SSO Preview**, nicht Enterprise-ready:

- LDAP und Windows mappen dieselbe AD-SID auf denselben NodePilot-Benutzer;
- Windows ignoriert PAC-Gruppen und übernimmt Gruppen ausschließlich aus dem aktuellen LDAPS-Snapshot;
- LDAPS akzeptiert nur die vollständige vertrauenswürdige Zertifikatskette und fällt auf einen zweiten DC um;
- Kerberos funktioniert über HAProxy mit persistentem HTTP/1.1 und `http-reuse never`;
- ein NTLM-Handshake wird abgelehnt;
- Gruppenentzug oder AD-Deaktivierung stoppt Sessions, geplante Jobs und Trigger innerhalb von 15 Minuten;
- OIDC-Tokengruppen werden abgelehnt, wenn `iat` fehlt oder älter als 15 Minuten ist;
- SCIM `externalId` entspricht exakt dem OIDC-`sub`, und vollständige Membership-Snapshots beziehungsweise Heartbeats erneuern jede relevante Membership mindestens alle 15 Minuten;
- OIDC-Failover funktioniert mit gemeinsamem Data-Protection-Keyring und Zertifikat über verschiedene Nodes.
