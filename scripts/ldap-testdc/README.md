# LDAP-Live-Test gegen Samba-AD-DC (Docker)

Wire-Level-Verifikation des NodePilot-LDAP-Pfads (`SystemLdapConnectionAdapter`,
LDAPS inkl. scharfer Zertifikatsvalidierung, Simple-Bind, `tokenGroups`, JIT,
Gruppen-Gate, Fail-Closed) gegen ein **echtes** AD-Verzeichnis — lokal, ohne
Firmen-AD. Schließt die Lücke, dass alle Unit-/Controller-Tests an der
`ILdapConnectionAdapter`-Seam enden.

> **Validiert am 2026-07-24:** 13/13 Tests PASS + Outage-Drill PASS
> (Samba 4.19/Ubuntu 24.04, Windows 11, Docker Desktop 29.x).
>
> **Ersetzt NICHT** den in `docs/ldap-windows-sso.md` geforderten Real-AD-Feldtest:
> kein Kerberos/Negotiate-SSO, kein Multi-DC-Konsens, kein HAProxy-Pfad, Samba ≠ MS-AD.

## Was der Test abdeckt

| # | Szenario | Erwartung |
|---|---|---|
| 1 | `/api/auth/methods` Discovery | `ldap=true` |
| 2 | `/healthz/directory` (Service-Bind + BaseDn-Read über LDAPS) | `Healthy` |
| 3 | Lokaler Break-Glass-Login trotz aktivem LDAP (Local-Shadow) | 200 Admin |
| 4 | LDAP-Login `alice.demo` (UPN) — **nur transitiv** in der Allowed-Group | 200, Role **Admin** (beweist `tokenGroups`-Nesting) |
| 5 | `/api/auth/me` mit Cookie-Session | Identität korrekt |
| 6 | Bare Username + `UpnSuffix`-Normalisierung | 200, gleiche UserId |
| 7 | Falsches Passwort (echter AD-Fehler 49) | 401 |
| 8 | Leeres Passwort (H-17 unauthenticated-bind) | 401 |
| 9 | `carol.demo` ohne RoleMapping-Treffer | 200, Role Viewer |
| 10 | `bob.demo` in keiner `AllowedGroupSids` | 401, **kein** JIT-Row |
| 11 | Unbekannter Directory-User | 401 |
| 12 | Re-Login = JIT-Update, kein Duplikat | gleiche UserId |
| 13 | Admin-Settings „LDAP testen"-Probe | ok=true |
| 14/15 | optional `-IncludeOutageDrill`: DC stop/start | 503 fail-closed → 200 Recovery |

## Testverzeichnis (wird beim ersten Containerstart provisioniert)

Realm `NODEPILOT.TEST`, BaseDn `DC=nodepilot,DC=test`, UPN-Suffix `nodepilot.test`.

| Konto | Passwort | Gruppen | Zweck |
|---|---|---|---|
| `svc-nodepilot` | `Bind#20260724!Kq7z` | — | Service-Bind |
| `alice.demo` | `Login#20260724!Mv3p` | NUR `NodePilot-Admins` (⊂ `NodePilot-Access`) | Happy path + Nesting + Admin-Mapping |
| `carol.demo` | `Login#20260724!Tw8r` | `NodePilot-Access` | Viewer-Default |
| `bob.demo` | `Login#20260724!Zh5c` | keine | AllowedGroup-Gate |
| `Administrator` | `DcRoot#20260724!Aa` (Env `ADMIN_PASS`) | — | nur Samba-intern |

**Achtung:** Die Domain-SID ist **pro Provisionierung neu** — Gruppen-SIDs nach jedem
frischen Container neu auslesen (Schritt 3). `ldap-live-test.ps1` liest die
Access-SID selbst per `docker exec`.

## Ablauf

Voraussetzungen: Docker Desktop (Linux-Container), openssl (Git für Windows),
lokales Dev-Postgres (`C:\NodePilot-Postgres`), einmalige UAC-Bestätigung für den
CA-Import.

### 1. Zertifikate + Image + Container

```powershell
cd scripts\ldap-testdc
.\make-certs.ps1                                  # Wegwerf-CA + Server-Zert (SAN localhost/127.0.0.1, 30 Tage)
docker build -t nodepilot-testdc .
docker run -d --name nodepilot-testdc --hostname dc1 --cap-add SYS_ADMIN -p 636:636 nodepilot-testdc
# ~60 s warten, dann pruefen:
docker logs nodepilot-testdc | Select-String "directory setup done"
```

`--cap-add SYS_ADMIN` ist Pflicht (sysvol-NT-ACLs = `security.*`-xattrs), sonst
bricht die Provisionierung mit `NT_STATUS_ACCESS_DENIED` ab.

### 2. Test-CA vertrauen (elevated, UAC-Prompt)

```powershell
Start-Process powershell -Verb RunAs -Wait -ArgumentList `
  "-NoProfile -Command Import-Certificate -FilePath '$PWD\certs\ca.crt' -CertStoreLocation Cert:\LocalMachine\Root"
```

Der nicht-elevierte Import nach `CurrentUser\Root` scheitert in nicht-interaktiven
Shells („Benutzeroberfläche … nicht zulässig") — deshalb Maschinen-Store via UAC.

### 3. Gruppen-SIDs auslesen

```powershell
docker exec nodepilot-testdc bash -c "for g in NodePilot-Access NodePilot-Admins; do ldbsearch -H /var/lib/samba/private/sam.ldb \"(sAMAccountName=`$g)\" objectSid | grep '^objectSid' | sed \"s/^/`$g /\"; done"
```

### 4. Wegwerf-DB anlegen

```powershell
# Passwort kommt aus den User-Secrets der API (Id aus NodePilot.Api.csproj):
$sec = Get-Content "$env:APPDATA\Microsoft\UserSecrets\37f6eb5a-82da-486c-ad19-f48b59916ab9\secrets.json" -Raw | ConvertFrom-Json
$cs  = if ($sec.PSObject.Properties['ConnectionStrings:Postgres']) { $sec.'ConnectionStrings:Postgres' } else { $sec.ConnectionStrings.Postgres }
if ($cs -match 'Password=([^;]+)') { $env:PGPASSWORD = $Matches[1] }
& 'C:\NodePilot-Postgres\pgsql\bin\psql.exe' -w -h 127.0.0.1 -U nodepilot -d nodepilot -c "CREATE DATABASE nodepilot_ldaptest;"
$env:PGPASSWORD = $null
```

(`psql -w` = niemals nach Passwort fragen — sonst hängt es in nicht-interaktiven Shells.)

### 5. PHASE A — API **ohne** LDAP starten und Break-Glass-Admin bootstrappen

Mit aktivem LDAP ist der Bootstrap **unerreichbar** (LDAP-first fängt unbekannte
Namen ab → 401/503, AD-User scheitern am Bootstrap-Gate). Reihenfolge ist Pflicht.

```powershell
$env:ConnectionStrings__Postgres     = ($cs -replace 'Database=[^;]+','Database=nodepilot_ldaptest')
$env:ASPNETCORE_ENVIRONMENT          = 'Development'
$env:Security__AdminSetupTokenPath   = "$env:USERPROFILE\np-ldaptest-token\admin-setup.token"   # NTFS! (E: ist exFAT -> Writer verweigert)
$env:Authentication__Ldap__Enabled   = 'false'
cd src\NodePilot.Api; dotnet run --no-launch-profile --urls http://localhost:5000
```

Dann (zweites Terminal):

```powershell
$token = (Get-Content "$env:USERPROFILE\np-ldaptest-token\admin-setup.token" -Raw).Trim()
Invoke-RestMethod -Uri http://localhost:5000/api/auth/login -Method Post -ContentType 'application/json' `
  -Headers @{ 'X-Setup-Token' = $token } `
  -Body (@{ username='breakglass.admin'; password='Boot#20260724!Adm1n' } | ConvertTo-Json)
```

### 6. PHASE B — API **mit** LDAP neu starten

API aus Phase A stoppen, dann zusätzlich zu den Phase-A-Variablen:

```powershell
$env:Authentication__Ldap__Enabled                          = 'true'
$env:Authentication__Ldap__Endpoints__0                     = '127.0.0.1:636'   # NIE localhost (-> ::1, Docker-IPv6-Relay -> wldap32-Fehler 81)
$env:Authentication__Ldap__BaseDn                           = 'DC=nodepilot,DC=test'
$env:Authentication__Ldap__UpnSuffix                        = 'nodepilot.test'
$env:Authentication__Ldap__ServiceBindDn                    = 'CN=svc-nodepilot,CN=Users,DC=nodepilot,DC=test'
$env:Authentication__Ldap__ServicePassword                  = 'Bind#20260724!Kq7z'
$env:Authentication__Ldap__AllowedGroupSids__0              = '<SID von NodePilot-Access aus Schritt 3>'
$env:Authentication__Ldap__GlobalRoleMappings__0__GroupSid  = '<SID von NodePilot-Admins aus Schritt 3>'
$env:Authentication__Ldap__GlobalRoleMappings__0__Role      = 'Admin'
cd src\NodePilot.Api; dotnet run --no-launch-profile --urls http://localhost:5000
```

Env-Vars gewinnen gegen `appsettings.runtime.json` (Deployment-Policy > UI) —
die Dev-Konfiguration bleibt unberührt.

### 7. Suite laufen lassen

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\ldap-testdc\ldap-live-test.ps1 -IncludeOutageDrill
```

### 8. Aufräumen

```powershell
docker rm -f nodepilot-testdc
# CA entfernen (UAC):
Start-Process powershell -Verb RunAs -Wait -ArgumentList "-NoProfile -Command `"Get-ChildItem Cert:\LocalMachine\Root | Where-Object Subject -eq 'CN=NodePilot LDAP Test CA' | Remove-Item`""
# Test-DB + Token-Verzeichnis:
#   DROP DATABASE nodepilot_ldaptest WITH (FORCE);   (psql wie in Schritt 4)
Remove-Item -Recurse -Force "$env:USERPROFILE\np-ldaptest-token"
```

## Stolperfallen (alle am 2026-07-24 live getroffen)

| Symptom | Ursache / Fix |
|---|---|
| Provision: `AD_DS_Attributes…ldf not found` | Paket `samba-ad-provision` fehlt (im Dockerfile enthalten) |
| Provision: `set_nt_acl … ACCESS_DENIED` | Container ohne `--cap-add SYS_ADMIN` gestartet |
| Samba stirbt still nach Start | Paket `winbind` fehlt (im Dockerfile enthalten) |
| Login/Health: `LdapException ErrorCode=81 "server unavailable"` trotz laufendem DC | `localhost` → `::1`; Dockers IPv6-Relay forwarded nicht → **`127.0.0.1:636`** verwenden. Fehler 81 maskiert auch Zertifikatsprobleme — im Firmennetz DC-**FQDN** verwenden |
| API-Log: „Bootstrap token … could not be written with restrictive ACLs" | Content-Root auf exFAT (Laufwerk `E:`) — `Security:AdminSetupTokenPath` auf NTFS-Pfad setzen |
| Bootstrap liefert 401/503 obwohl Token korrekt | LDAP war schon aktiv — Phase-Reihenfolge einhalten (erst Bootstrap, dann LDAP + Restart) |
| Settings-Probe liefert 403 | `np_csrf`-Cookie-Wert ist URL-encodiert — vor dem `X-CSRF-Token`-Header decodieren |
| Settings-Probe liefert 400 | Body braucht Wrapper `{ "settings": { … } }` |
| `psql` hängt endlos | Konsolen-Passwortprompt — immer `-w` + `PGPASSWORD` nutzen |

## Grenzen

- **Kein Kerberos/Windows-SSO** — Negotiate-Handshake braucht Domain-Join; bleibt dem
  Real-AD-Feldtest vorbehalten.
- **Nur 1 Endpoint** — das Multi-DC-Konsensverhalten (`ReconcileEndpointResults`:
  jeder Endpoint-Fehler ⇒ 503) wird hier bewusst nicht ausgeübt.
- Samba verhält sich protokollseitig wie AD (`objectSid`/`objectGUID`/`tokenGroups`/
  Fehler 49), ist aber kein Microsoft-DC.
