# Windows-SSO/Kerberos-Feldtest gegen echtes AD (Hyper-V-Lab)

Wire-Level-Verifikation des NodePilot-Windows-SSO-Pfads (`POST /api/auth/windows`,
Negotiate/Kerberos) gegen ein **echtes** Active Directory. Schließt die Lücke, dass alle
Backend-Tests an einem synthetischen `ClaimsPrincipal` bzw. an der
`ILdapConnectionAdapter`-Seam enden — es gibt dort keinen einzigen echten Handshake.

> **Status: Kerberos-Kern am 2026-08-02 gegen echtes AD verifiziert.** Lauf gegen
> Windows Server 2025 (`corp.contoso.com`, Domain-/Forest-Mode 2016, ein DC), NodePilot
> unter einem gMSA auf einem Member-Server, Test von einem domänengejointen Windows-11-
> Client: **22 PASS / 1 FAIL / 6 SKIP**. Der eine FAIL war ein falsch übergebener
> Break-Glass-Parameter, kein Produktfehler.
>
> Weiterhin offen: HAProxy-Pfad, Multi-DC-Konsens, OIDC/SCIM, HA-Restart, sowie
> W15/W16 (Revocation), W14 (DC-Ausfall), W20/W21 (Restrict-NTLM-GPO), W22 und der
> Browser-Beleg W6b/W6c. Das Preview-Label kann deshalb **präzisiert, aber nicht
> gestrichen** werden.
>
> **Wichtig zum Browser-Pfad:** der Lab-Lauf belegt den HTTP-Stack, **nicht** den Browser.
> Ein erster manueller Versuch am 2026-08-02 öffnete einen Credential-Dialog, weil die
> Browser-Policy auf dem Client nie gesetzt war; der stille zweite Versuch nach einem
> Logout war der Credential-Cache der Browser-Sitzung, kein SSO. W6b ist damit **offen**,
> nicht bestanden — und wurde um genau dieses Kriterium geschärft.

### Ergebnisse des Lab-Laufs 2026-08-02

| Punkt | Beleg |
|---|---|
| **W6a Kerberos** | Nach dem Login steht `Server: HTTP/cm1.corp.contoso.com @ CORP.CONTOSO.COM` im Ticket-Cache — ein echtes Service-Ticket, kein NTLM |
| **W5d transitive Gruppen** | `nptest.alice` ist **ausschließlich** in `NPTest-Admins`; das Admission-Gate steht auf `NPTest-Access`. Login liefert 200 + Role=Admin ⇒ `tokenGroups` wird transitiv aufgelöst, nicht `memberOf`. Audit: `oldGroupSidsCount: 4` |
| **W9 Identitätsgleichheit** | Derselbe Mensch über beide Pfade ⇒ **dieselbe UserId**. Audit zeigt `LOGIN_SUCCESS … source: "Ldap"` und `source: "Windows"` auf einer Identität |
| W5a Ambient-Pfad | Der Pfad, den der Browser-Button geht, liefert 200 |
| W5c / W8 | `np_auth` httpOnly + `np_csrf` gesetzt, kein Token im Body; JWT enthält **keine** Gruppen-/SID-Claims |
| W10 | Re-Login ⇒ `USER_WINDOWS_JIT_UPDATED`, gleiche UserId, kein Duplikat |
| W11 / W12 | `carol` → Viewer; `bob` → 401 mit Audit `USER_DIRECTORY_ACCESS_REFUSED / reason=no_allowed_directory_group` |
| W13 | Deaktiviertes AD-Konto ⇒ kein Login |
| W18 | 5 parallele Erst-Logins ⇒ alle 200, genau ein JIT-Create |
| W25 | SSO ohne `np_csrf` ⇒ 200, kein 403 |
| W2 | `/healthz/directory` `Healthy` über echtes LDAPS gegen die Enterprise-CA |
| **W19 unschlüssig** | 401, aber **ohne** Ablehnungsmeldung und ohne `windows_ntlm_disabled` im Audit — der Server hat gar keinen NTLM-Versuch gesehen. Siehe „Falscher PASS" unten |

Auf einem WORKGROUP-Rechner ohne KDC wurde zusätzlich verifiziert: **W0** (Boot bricht bei
`AllowNtlmFallback=true` ab), der **Zwei-Phasen-Bootstrap** (Bootstrap-Admin erhält
`IsBreakGlass=true`), `/healthz/ready` bleibt bei totem DC 200 — und dort **fiel der
App-Level-NTLM-Zweig wirklich**: 401 mit *„Kerberos required — NTLM fallback is disabled"*
plus Audit `reason=windows_ntlm_disabled, mechanism=NTLM`. Der Check ist also **nicht
inert**; er ließ sich im Lab nur nicht auslösen.

### Falscher PASS: warum W19 im Lab nichts beweist

Der Negotiate-Handler challenged mit `WWW-Authenticate: Negotiate`. Ein `CredentialCache`,
der nur für das Paket `NTLM` registriert ist, findet dafür keinen Eintrag und schickt
**überhaupt keinen** `Authorization`-Header — der Server sieht nie einen NTLM-Versuch und
antwortet mit einem leeren 401. Die Probe meldete daraufhin PASS, obwohl im Audit keine
einzige `windows_ntlm_disabled`-Zeile stand.

`Invoke-NtlmProbe.ps1` verlangt jetzt zusätzlich die **Ablehnungsmeldung der Anwendung** im
Body; ein 401 ohne sie gilt als *unschlüssig*, nicht als Erfolg. Um W19 im Lab wirklich zu
belegen, braucht es `-Mode Alias`: einen A-Record auf dieselbe IP, für den **kein**
HTTP-SPN existiert, plus diesen Namen als zweite SAN im Kestrel-Zertifikat. Dann scheitert
die SPN-Suche, SPNEGO fällt auf NTLM zurück, und die Anwendung kommt überhaupt erst zum Zug.

**Ergänzt** `scripts/ldap-testdc/` (LDAP-Passwortpfad gegen Samba, 13/13 PASS am
2026-07-24). Der dortige Samba-DC kann bewusst kein Kerberos: der Negotiate-Handshake
braucht Domain-Join, und ASP.NET Negotiate nutzt auf Windows SSPI, nicht GSSAPI/Keytab.

**Ersetzt NICHT** den vollständigen Feldtest aus `docs/ldap-windows-sso.md` (10-Punkte-
Matrix). Nicht abgedeckt: HAProxy-Pfad (Punkt 3 bleibt halb offen), Multi-DC-Konsens
(Punkte 1/6), OIDC/SCIM (8/9), HA-Restart (10). Das Preview-Label kann nach einem grünen
Lauf **präzisiert, aber nicht gestrichen** werden.

## Lab-Topologie

| Rolle | FQDN | Aufgabe |
|---|---|---|
| DC | `dc01.np.lab` | AD DS, DNS, LDAPS:636 |
| API-Member | `npapi01.np.lab` | NodePilot-API (Kestrel HTTPS:443), Postgres |
| Client | `npcli01.np.lab` | Edge/Chrome, Testsuite |
| NTLM-Alias | `npapi01-ntlm.np.lab` | **A-Record auf dieselbe IP, ohne SPN** |

Realm `NP.LAB`, NetBIOS `NPLAB`, BaseDn `DC=np,DC=lab`, UPN-Suffix `np.lab`.

Der NTLM-Alias ist der Kern des Negativtests: gleiche IP, kein `HTTP/`-SPN → SPNEGO fällt
deterministisch auf NTLM zurück, TLS bleibt gültig (Alias als zweite SAN im
Kestrel-Zertifikat). Sauberer als der Zugriff per IP-Adresse, weil kein
Zertifikatswarnungs-Interstitial dazwischenkommt.

## Testverzeichnis

| Konto | Gruppen | Zweck |
|---|---|---|
| `svc-npdir` | – | LDAPS-Service-Bind |
| `np.alice` | **nur** `NodePilot-Admins` (in `NodePilot-Access`) | Happy path, transitives Mapping → Admin |
| `np.carol` | `NodePilot-Access` | Viewer-Default (kein RoleMapping-Treffer) |
| `np.bob` | keine | AllowedGroup-Gate: 401, **kein** JIT-Row |
| `np.dave` | `NodePilot-Access` | Race-Drill (bis W18 unberührt lassen) |
| `np.erin` | `NodePilot-Access` | Disable-/Entzugs-Drills |

Das Nesting ist essenziell: alice ist **nur** in `NodePilot-Admins`. Ein erfolgreicher
Login beweist damit, dass NodePilot transitive `tokenGroups` liest statt bloß `memberOf`.

## Skripte

| Datei | Host | Zweck |
|---|---|---|
| `Setup-LabDirectory.ps1` | dc01 | OU, Gruppen (inkl. Nesting), User, UPNs — idempotent |
| `Get-LabSids.ps1` | dc01 | SIDs auslesen, `-AsEnvBlock` rendert den PHASE-B-Env-Block |
| `Setup-ApiHost.ps1` | npapi01 | Read-only-Preflight: Domain-Join, Zeitversatz, DNS/CNAME, SPN-Lage, Etypes, Zert-SAN, LDAPS-Bind, Firewall |
| `Set-BrowserSsoPolicy.ps1` | npcli01 | `AuthServerAllowlist` + ZoneMap, `-Remove` zum Zurückbauen |
| `ad-sso-live-test.ps1` | npcli01 | Hauptsuite (W1–W25) |
| `Invoke-NtlmProbe.ps1` | npcli01 | Erzwungener NTLM-Request (W19) |
| `Get-KerberosEvidence.ps1` | beide | klist / Security-4624 / NTLM-Operational / SPN-Stand → `evidence/` |
| `Assert-DbState.ps1` | npapi01 | DB-Belege (Identität, Gate, Race, Revocation, NTLM, Audit) |

## Ablauf

### 1. Zeitsynchronisation (zuerst!)

Kerberos toleriert ±5 min. Ein größerer Versatz liefert `KRB_AP_ERR_SKEW` und sieht im
Fehlerbild aus wie „SPN falsch". **Messen, nicht pauschal umkonfigurieren:**

```powershell
$cred = Import-CliXml C:\lab-cred\hyd.xml
$a = Invoke-Command -VMName <DC>  -Credential $cred -ScriptBlock { (Get-Date).ToUniversalTime() }
$b = Invoke-Command -VMName <API> -Credential $cred -ScriptBlock { (Get-Date).ToUniversalTime() }
"Versatz: {0:N1}s (Grenze 300s)" -f ($b - $a).TotalSeconds
```

> Die verbreitete Empfehlung, in den Gästen die Hyper-V-Zeitsynchronisation zu
> deaktivieren, gilt **nicht pauschal**. Hinter einem privaten Switch ohne NTP-Ausgang ist
> der IC-Provider oft die *einzige* Zeitquelle. Im vermessenen Lab lief der DC auf
> Stratum 6 über „VM IC Time Synchronization Provider", der Versatz zwischen DC und
> API-Host betrug **0,3 Sekunden** — Deaktivieren hätte die Synchronisation zerstört
> statt sie zu verbessern.
>
> Die Zeit**zone** ist irrelevant: Kerberos rechnet in UTC. Im Lab meldeten die Gäste
> `Pacific Standard Time` bei einer lokalen Anzeige in MESZ, der UTC-Versatz lag trotzdem
> unter einer Sekunde. Nie aus der lokalen Uhrzeit auf Skew schließen.

### 2. Verzeichnis auf dem DC

```powershell
.\Setup-LabDirectory.ps1
.\Get-LabSids.ps1                 # Übersicht
.\Get-LabSids.ps1 -AsEnvBlock     # Env-Block für PHASE B
```

Die Domain-SID ist pro Provisionierung neu — SIDs **nie** hartkodieren.

### 3. LDAPS auf dem DC

Entweder AD CS als Enterprise-Root (der DC zieht automatisch ein
`Kerberos Authentication`-Zertifikat, LDAPS ist danach ohne weiteres Zutun aktiv), oder
self-signed mit `CN=dc01.np.lab`, passender SAN und EKU *Server Authentication* in
`LocalMachine\My` des DC.

Die Root-CA muss in `LocalMachine\Root` des **API-Hosts** liegen. Es gibt keinen
In-App-Bypass — die Zertifikatsvalidierung ist unbedingt.

### 4. Preflight auf npapi01 (elevated)

```powershell
.\Setup-ApiHost.ps1 -ApiFqdn npapi01.np.lab -NtlmAliasFqdn npapi01-ntlm.np.lab -DcFqdn dc01.np.lab
```

Jeder `FAIL` erzeugt später ein Kerberos-Fehlerbild, das wie ein Produktdefekt aussieht.
Ohne grünen Preflight nicht weitermachen.

**Dienst-Identität — zwei Varianten:**

*Variante A (Empfehlung für den Erstdurchlauf): LocalSystem / Maschinenkonto.* Beim
Domain-Join registriert Windows `HOST/npapi01.np.lab`; `HOST` ist über `sPNMappings` auf
`http` gemappt, der KDC stellt das Ticket für `HTTP/npapi01.np.lab` also **implizit** aus.
Kein `setspn` nötig — das eliminiert die häufigste Fehlerursache.

*Variante B (Pflicht-Nachlauf, weil so dokumentiert): gMSA.* Ein expliziter SPN gewinnt
gegen den impliziten HOST-Alias:

```powershell
Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10))   # Lab-Abkürzung
New-ADServiceAccount svc-nodepilot -DNSHostName npapi01.np.lab `
    -PrincipalsAllowedToRetrieveManagedPassword 'NPAPI01$'
setspn -S HTTP/npapi01.np.lab NPLAB\svc-nodepilot$
setspn -Q HTTP/npapi01.np.lab   # muss GENAU einen Treffer zeigen
```

`-S` statt `-A` ist nicht kosmetisch: `-A` legt Duplikate an, und ein doppelter SPN führt
zu `KRB_AP_ERR_MODIFIED` oder zum **stillen** NTLM-Fallback.

Matrix komplett unter Variante A fahren, danach W5/W6/W19/W20 unter Variante B
wiederholen — sonst bleibt der dokumentierte Produktionspfad unbelegt.

### 5. NTLM-Policy (GPO auf der npapi01-OU)

*Computer Configuration → Windows Settings → Security Settings → Local Policies →
Security Options*

1. **Zuerst Auditmodus:** *Restrict NTLM: Audit Incoming NTLM Traffic* =
   `Enable auditing for all accounts` → Event **8004**.
2. **Erst nach W19 durchsetzen:** *Restrict NTLM: Incoming NTLM traffic* =
   `Deny all accounts` → Event **4004**, Registry
   `HKLM\SYSTEM\CurrentControlSet\Control\Lsa\MSV1_0\RestrictReceivingNTLMTraffic = 2`.

**Reihenfolge ist zwingend.** Mit aktiver Deny-GPO weist bereits SSPI
(`AcceptSecurityContext`) NTLM ab — der Applikationszweig in `AuthController.WindowsLogin`,
der `windows_ntlm_disabled` auditiert, ist dann **unerreichbar**. W19 (App lehnt ab) und
W20 (OS lehnt ab) belegen zusammen Punkt 4 der Feldtest-Matrix; einer allein nicht.

`Authentication:Windows:NtlmDisabledByPolicy=true` ist eine Operator-**Attestierung**,
keine Durchsetzung. Erst Schritt 2 belegt sie.

### 6. Browser-Policy auf npcli01 (elevated)

```powershell
.\Set-BrowserSsoPolicy.ps1 -ApiFqdn npapi01.np.lab
```

Danach Edge/Chrome **komplett** neu starten und `edge://policy` prüfen —
`AuthServerAllowlist` muss als *Applied* erscheinen (Screenshot = Evidenz).

Bewusst **nicht** gesetzt: `AuthNegotiateDelegateAllowlist` (Delegation ist hier weder
nötig noch erwünscht) und `AuthSchemes` (der Browser ist der falsche Ort, um NTLM zu
blocken — SPNEGO transportiert NTLM auch unter `negotiate`; dafür ist Schritt 5 zuständig).

### 7. PHASE A — API ohne SSO, Break-Glass bootstrappen

Die Break-Glass-Invariante bricht den Boot ab, wenn bei aktivem externem Provider kein
aktiver lokaler Admin mit Passwort und `IsBreakGlass=true` existiert. Mit aktivem SSO ist
der Bootstrap unerreichbar. **Die Reihenfolge ist Pflicht.**

**Zuerst das Token-Verzeichnis korrekt anlegen.** `RestrictedFileWriter` prüft **jeden**
übergeordneten Ordner und verweigert, sobald ein nicht vertrauenswürdiger Principal dort
`Delete`/`CreateFiles` besitzt. Ein NTFS-Pfad allein genügt nicht: `%TEMP%` trägt
typischerweise fremde ACEs, und ein frisch unter `C:\ProgramData` angelegter Ordner erbt
`Write` für `BUILTIN\Benutzer`. Ohne diesen Schritt startet die API zwar, protokolliert
aber „could not be written with restrictive ACLs" und **der Bootstrap ist deaktiviert**:

```powershell
$dir = 'C:\ProgramData\NodePilot-Labtest'
New-Item -ItemType Directory -Path $dir -Force | Out-Null
$me  = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
$acl = New-Object System.Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)   # Vererbung aus, nichts uebernehmen
$acl.SetOwner($me)
foreach ($sid in @($me,
                   (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')),
                   (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')))) {
  $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
    $sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
}
Set-Acl -Path $dir -AclObject $acl
```

```powershell
$env:ASPNETCORE_ENVIRONMENT        = 'Production'
$env:ConnectionStrings__Postgres   = '...Database=nodepilot_adssotest...'
$env:Security__AdminSetupTokenPath = 'C:\ProgramData\NodePilot-Labtest\admin-setup.token'
$env:Kestrel__Https__Enabled               = 'true'
$env:Kestrel__Https__HttpsPort             = '443'
$env:Kestrel__Https__CertificateThumbprint = '<Thumbprint>'
$env:Authentication__Ldap__Enabled    = 'false'
$env:Authentication__Windows__Enabled = 'false'
```

```powershell
$token = (Get-Content 'C:\ProgramData\NodePilot-Labtest\admin-setup.token' -Raw).Trim()
Invoke-RestMethod -Uri https://npapi01.np.lab/api/auth/login -Method Post `
  -ContentType 'application/json' -Headers @{ 'X-Setup-Token' = $token } `
  -Body (@{ username='breakglass.admin'; password='Boot#20260802!Adm1n' } | ConvertTo-Json)
```

Läuft der Dienst als LocalSystem/gMSA, ist die Token-Datei per Owner-only-ACL auch für
Admins nicht direkt lesbar → per Backup-Semantik lesen:
`robocopy C:\ProgramData\NodePilot-Labtest $env:TEMP admin-setup.token /B`

### 8. PHASE B — API mit LDAPS + Windows-SSO neu starten

Env-Block aus `Get-LabSids.ps1 -AsEnvBlock` übernehmen, zusätzlich die Kestrel-Variablen
aus Phase A beibehalten. Env-Vars gewinnen gegen `appsettings.runtime.json`
(Deployment-Policy > UI) — die Dev-Konfiguration bleibt unberührt.

**Warum LDAPS trotz „nur Windows-SSO" Pflicht ist:** Der Boot-Validator läuft bereits bei
`Ldap:Enabled || Windows:Enabled` und fordert Endpoints, `BaseDn`, Service-Bind und
mindestens eine `AllowedGroupSids`. Zur Laufzeit macht der Controller bei **jedem**
Windows-Login einen autoritativen LDAPS-`LookupBySubjectAsync` — Kerberos-PAC-Gruppen
werden bewusst nicht getraut, weil sie stundenalt sein können.

`Ldap:Enabled=true` ist für den Lauf nötig, weil W9 (Identitätsgleichheit) den
Passwortpfad braucht. Die Windows-only-Topologie prüft W23 separat.

`Cluster:Enabled` auf `false` lassen — sonst laufen `DirectorySynchronizationService` und
`ExternalAuthorizationStalenessService` nicht und die Revocation-Drills messen nichts.
Vorab `/healthz/leader` → 200 prüfen.

### 9. Suite laufen lassen (npcli01, lokal interaktiv als np.alice)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\ad-sso-live-test.ps1 `
    -IncludeNtlmProbe -IncludeRaceDrill -IncludeDisableDrill `
    -DbPassword '<postgres-pw>' -DbName nodepilot_adssotest
```

Drills separat, weil langlaufend bzw. störend:

```powershell
.\ad-sso-live-test.ps1 -IncludeOutageDrill -DcVmName dc01 -HyperVHost <hv-host>
.\ad-sso-live-test.ps1 -IncludeRevocationDrill -RevocationWorkflowId <guid>   # 3-16 min
.\ad-sso-live-test.ps1 -IncludeRateLimitDrill                                 # drosselt die IP ~60 s
```

### 10. Evidenz sichern

```powershell
.\Get-KerberosEvidence.ps1 -Role Client              # auf npcli01
.\Get-KerberosEvidence.ps1 -Role Server              # auf npapi01, elevated
.\Assert-DbState.ps1 -DbPassword '<pw>' -DbName nodepilot_adssotest
```

### 11. Aufräumen

```powershell
.\Set-BrowserSsoPolicy.ps1 -Remove                   # npcli01
# Variante-C-SPNs (falls die API zwischendurch als Domänenbenutzer lief) zurückbauen:
setspn -D HTTP/npapi01.np.lab NPLAB\<konto>
# Test-DB: DROP DATABASE nodepilot_adssotest WITH (FORCE);
Remove-Item -Recurse -Force 'C:\ProgramData\NodePilot-Labtest'
```

## Testmatrix

Automatisiert in `ad-sso-live-test.ps1`:

| # | Szenario | Erwartung |
|---|---|---|
| W1 | `/api/auth/methods` | `windows=true`, `windowsEndpoint=/api/auth/windows` |
| W2 | `/healthz/{live,ready,directory,leader}` | alle 200, directory `Healthy` |
| W3 | Break-Glass-Login trotz aktivem SSO | 200, Admin |
| W5 | **Happy Path Kerberos** | 200, Role **Admin** (nur transitiv!), `np_auth` httpOnly + `np_csrf`, **kein** Token im Body |
| W6a | `klist tickets` | Service-Ticket `HTTP/npapi01.np.lab` vorhanden |
| W7 | `/api/auth/me` mit SSO-Cookie | 200, korrekte Identität |
| W8 | JWT-Payload dekodiert | **keine** Gruppen-/SID-Claims (Positivform siehe unten) |
| W9 | LDAP-Login derselben Person | gleiche `userId`, genau eine `ExternalIdentity` |
| W10 | Zweiter SSO-Login | gleiche `userId` (JIT-Update, kein Duplikat) |
| W11 | carol | 200, Role Viewer |
| W12 | bob | 401, **kein** User-Row |
| W25 | SSO ohne `np_csrf` | 200 (Cookie-Bootstrap-Ausnahme), kein 403 |
| W13 | `-IncludeDisableDrill` | deaktiviertes AD-Konto → kein Login |
| W14 | `-IncludeOutageDrill` | DC weg → **503** fail-closed, Break-Glass bleibt 200, `/healthz/ready` bleibt 200 |
| W15/16 | `-IncludeRevocationDrill` | Gruppenentzug → Session revoked ≤ 15 min, Execution gestoppt |
| W18 | `-IncludeRaceDrill` | 5 parallele Erst-Logins → genau 1 User, 1 Identity, 1 `USER_WINDOWS_JIT_CREATED` |
| W19 | `-IncludeNtlmProbe` | erzwungenes NTLM → 401, kein `np_auth`, Audit `windows_ntlm_disabled` |
| W22 | `-IncludeRateLimitDrill` | 429; misst, wie viele Tokens ein SSO-Login kostet |

### Manuelle Testpunkte

Nicht skriptbar — Belege gehören nach `evidence/`:

| # | Szenario | Vorgehen / Beleg |
|---|---|---|
| W0 | Boot-Validator | PHASE B mit `Authentication__Windows__AllowNtlmFallback=true` starten → Startabbruch („Kerberos-only"). Danach zurücksetzen |
| W4 | Break-Glass-Invariante | Zweit-DB mit einem User ohne `IsBreakGlass`, SSO an → Startabbruch vor SSO-Traffic |
| W6b | **Browser-SSO ohne Prompt** | **Vorbedingung:** Browser-Policy gesetzt (`Set-BrowserSsoPolicy.ps1`), **alle** Browser-Prozesse beendet (`Get-Process msedge \| Stop-Process -Force`), `cmdkey /list` ohne Eintrag für die Origin, kein ESSO-/Credential-Filler installiert. Dann Edge starten, `edge://policy` zeigt `AuthServerAllowlist` als *Applied*, `/login` öffnen, SSO-Button klicken. **Bestanden ist nur ein Login ganz OHNE Credential-Dialog** — ein 200 allein genügt nicht. Zusätzlich Fiddler: zwei Legs, `401 WWW-Authenticate: Negotiate` → `200` mit `Authorization: Negotiate YII...` (SPNEGO, **nicht** `TlRMTVNTUA==` = NTLMSSP). Evidenz: `.saz`, `edge://policy`-Screenshot, Dashboard-Screenshot |
| W6c | **Gegenprobe zu W6b** | `Set-BrowserSsoPolicy.ps1 -Remove`, erneut **kalt** starten, klicken → der Dialog **muss** zurückkommen. Ohne diesen Punkt bleibt „kein Dialog" ein Zufallsbefund: ein warmer Cache, ein gespeichertes Kennwort oder ein Credential-Filler erzeugen dasselbe Bild. Danach Policy wiederherstellen |
| W17 | Kein Falsch-Tombstoning | `BaseDn` auf eine leere OU biegen, Neustart, einen Sync-Zyklus abwarten → Log „all-not-found pass rejected", `IsTombstoned`-Count unverändert |
| W20 | NTLM-Negativ (OS-Ebene) | Deny-GPO aktiv, W19 wiederholen → 401 aus dem Handler, **kein** App-Audit; Event 4004 |
| W21 | Kerberos überlebt die GPO | W5 mit aktiver Deny-GPO → 200 |
| W23 | Windows-only-Topologie | Neustart mit `Ldap__Enabled=false` → Boot ok, SSO ok, `methods.ldap=false`, `/healthz/directory` weiter `Healthy` |
| W24 | Restart-Semantik | `Windows__Enabled=false` **ohne** Restart → `methods` meldet weiter `windows=true` (Prozessstart-Snapshot); nach Restart `windows=false` und Endpoint 404 |
| W26 | Username-Kollision | Lokalen User `np.alice@np.lab` ohne External Identity anlegen, SSO mit anderer SID → 401, Audit `USER_WINDOWS_REFUSED_COLLISION`, **kein** Auto-Merge |
| W27 | SPN-Duplikat (destruktiv, zuletzt) | `setspn -A HTTP/npapi01.np.lab NPLAB\np.bob`, W5 wiederholen → Kerberos bricht; danach `setspn -D`. Zweck: das Fehlerbild dokumentieren |

### Reihenfolge-Abhängigkeiten

W0/W4 vor PHASE B · W1–W12 in Reihe · W13/W15/W16 nur mit `np.erin` (nie mit alice, sonst
ist der Admin weg) · W18 vor jedem anderen `np.dave`-Kontakt · **W19 zwingend vor W20**
(danach ist der App-Zweig tot) · W17/W23/W24 brauchen Neustarts, ans Ende · W27 ganz zuletzt.

## Stolperfallen

| Symptom / Risiko | Ursache & Fix |
|---|---|
| Diffuse 401 trotz korrektem SPN | **HTTP/2.** Das HTTP/1.1-Pinning greift nur bei `Kestrel:Https:Enabled=true` **und** `Authentication:Windows:Enabled=true` (`KestrelHttpsConfigurator.cs:92/138`). `dotnet run --urls https://...` umgeht den Pfad, ALPN handelt h2 aus. Entweder `Kestrel__Https__Enabled=true` oder bewusst Klartext-HTTP |
| `KRB_AP_ERR_SKEW`, sieht aus wie „SPN falsch" | Zeitversatz > 5 min. **UTC-Versatz messen** (Schritt 1) statt blind die IC-Zeitsync abzuschalten — hinter einem privaten Switch ist sie oft die einzige Quelle |
| **gMSA/Dienstkonto ohne SPN** ⇒ Kerberos scheitert still | Läuft der Dienst unter einem gMSA oder Domänenbenutzer, deckt das `HOST/`-SPN des **Computerkontos** ihn nicht ab: das Ticket ist mit dem Schlüssel des Computerkontos verschlüsselt und für den Dienstprozess unlesbar. SPNEGO fällt auf NTLM zurück, der Test meldet fälschlich „NTLM-Ablehnung funktioniert". Prüfen mit `setspn -L '<DOMAIN>\<gmsa>$'`, setzen mit `setspn -S HTTP/<fqdn> '<DOMAIN>\<gmsa>$'` |
| TLS bricht mit „underlying connection was closed: An unexpected error occurred on a send" | **Kein TLS-Problem.** In einem Remote-Runspace (`Invoke-Command`) kann `ServerCertificateValidationCallback = { $true }` nicht laufen — „There is no Runspace available to run scripts in this thread" —, der Callback wirft und die Verbindung bricht. Richtige Lösung: das Zertifikat auf dem Client wirklich vertrauen (`LocalMachine\Root`), nicht die Validierung umgehen. Gegenprobe: `curl.exe` ohne `-k` |
| Suite bricht mit „running scripts is disabled on this system" ab | Execution Policy des Clients. Über einen Child-Prozess starten: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File …`; die Kerberos-Tickets der Anmeldesitzung bleiben dabei erhalten |
| **Credential-Dialog beim Klick auf den SSO-Button** | Die Origin steht nicht in `AuthServerAllowlist` bzw. nicht in der Intranet-Zone. Der Browser weist das vorhandene Ticket dann **nicht** automatisch vor und fragt stattdessen. `Set-BrowserSsoPolicy.ps1` ausführen, Browser **komplett** neu starten, `edge://policy` auf *Applied* prüfen |
| **Erster Klick fragt, zweiter nicht** | Der HTTP-Auth-Credential-Cache der Browser-**Sitzung**. Ein NodePilot-Logout beendet die Anwendungssession, nicht diesen Cache. Das ist **kein** SSO-Beleg — nur ein Kaltstart aller Browser-Prozesse macht den Test aussagekräftig |
| **Still trotz Kaltstart, obwohl keine Policy gesetzt ist** | Persistenter Eintrag in der Windows-Anmeldeinformationsverwaltung („Anmeldedaten speichern" im Dialog) oder ein ESSO-/Credential-Filler auf dem Endgerät. Beides füllt das Kennwort ein und **maskiert damit die fehlende Policy**. `cmdkey /list` an der Konsole prüfen; der Nachweis ist nur auf einem Client ohne Ausfüllwerkzeug zu führen |
| W19 meldet 401, aber das Audit bleibt leer | Unbeantwortete `Negotiate`-Challenge, kein NTLM-Versuch. `-Mode Alias` mit SPN-freiem A-Record nutzen — siehe „Falscher PASS" oben |
| Zugriff **vom API-Host selbst** fällt auf NTLM/401 | LSA-Loopback-Prüfung. Immer von npcli01 testen. `DisableLoopbackCheck` **nicht** setzen — das verfälscht das Ergebnis und ist eine Sicherheitsregression |
| `localhost` statt FQDN | Kein `HTTP/localhost`-SPN. Für LDAP zusätzlich `::1`-Auflösung → `LdapException 81`. Überall FQDNs |
| Kerberos bricht **still** auf NTLM | SPN-Duplikat oder SPN auf dem falschen Konto. `setspn -S` (nie `-A`), `setspn -X` vor jedem Lauf. Variante-C-SPNs nach dem Umbau löschen |
| SPN passt nicht zum aufgerufenen Namen | **CNAME-Kanonisierung** — Chromium bildet den SPN aus dem A-Record-Ziel. Im Lab direkten A-Record verwenden |
| TLS-Warnung oder falscher SPN | Zertifikatsname ≠ SPN-Host. Eine Kette: URL-Host = SPN-Host = Zert-SAN; NTLM-Alias als zweite SAN |
| „Bootstrap token … could not be written with restrictive ACLs" | **NTFS reicht nicht.** `RestrictedFileWriter` prüft jeden Vorfahren auf `Delete`/`CreateFiles` für untrusted Principals. `%TEMP%` fällt an fremden ACEs durch, ein neuer `C:\ProgramData`-Ordner an geerbtem `Write` für `BUILTIN\Benutzer`, ein exFAT-Laufwerk an fehlenden ACLs (Owner „Jeder"). Verzeichnis mit dem Snippet aus Schritt 7 geschützt anlegen. **Die API startet trotzdem** — nur der Bootstrap ist still deaktiviert |
| Bootstrap liefert 401/503 obwohl Token korrekt | SSO war schon aktiv — PHASE A vor PHASE B |
| `windows_ntlm_disabled` fehlt im Audit | Deny-GPO ist bereits aktiv, SSPI lehnt vor dem Controller ab. Für W19 in den Auditmodus zurück |
| `-UseDefaultCredentials` liefert 401 | **Double-Hop.** Über `Enter-PSSession`/WinRM hat der Prozess kein delegierbares TGT. Lokale interaktive Sitzung verwenden |
| `LdapException 81` „server unavailable" trotz laufendem DC | Maskiert auch Zertifikatsprobleme: DC-SAN, CA-Trust im `LocalMachine\Root` des API-Hosts, FQDN statt IP prüfen |
| `/api/auth/methods` zeigt alte Werte | `ActiveAuthenticationConfiguration` ist ein Prozessstart-Snapshot. Nach jeder `Authentication:*`-Änderung neu starten |
| `KDC_ERR_ETYPE_NOSUPP` | RC4-only-Konto plus AES-erzwingende Policy. `Get-ADComputer NPAPI01 -Properties msDS-SupportedEncryptionTypes` |
| gMSA lässt sich nicht anlegen | KDS-Root-Key braucht 10 h. Im Lab `Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10))` |
| 429 früher als erwartet | `UseRateLimiter()` steht **vor** `UseAuthentication()` — der anonyme Challenge-Leg verbraucht ebenfalls ein Token. Effektives Budget ≈25 Logins/min/IP statt 50 |

### Erwartete JWT-Form (W8)

Der Token trägt die **langen** Claim-URIs, nicht die Kurzformen — gegen die Dev-API am
2026-08-02 verifiziert:

```
http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier
http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name
http://schemas.microsoft.com/ws/2008/06/identity/claims/role
jti, iat, np_iat_ms, np_session, np_secstamp, exp, iss, aud
```

Die Prüfung ist deshalb bewusst eine **Negativ**-Prüfung (kein `group`/`primarysid`/
`objectsid`/`sid`-Claim) und keine Positiv-Whitelist — letztere wäre gegen jede Änderung
des `OutboundClaimTypeMap` spröde. In URI-Form heißt ein Gruppen-Claim
`.../claims/groupsid`, das Muster fängt also beide Schreibweisen.

### Integrated Windows Authentication vs. Credential-Filler (ESSO)

Beide Verfahren führen den Anwender ohne Tippen ins Ziel — sie sind trotzdem nicht dasselbe,
und die Verwechslung kostet den ganzen Testwert.

- **IWA/Kerberos** (das, was NodePilot macht): der Browser weist ein kurzlebiges,
  dienstgebundenes Ticket vor. **Im gesamten Vorgang existiert kein Passwort.**
- **Credential-Filler** (Windows-Anmeldeinformationsverwaltung, ESSO-Produkte wie Imprivata,
  Evidian, NetIQ SecureLogin): ein Kennwort wird in den Dialog eingetragen — nur eben nicht
  vom Menschen. Legitim und für Anwendungen gedacht, die kein Kerberos können; NodePilot
  braucht es nicht.

Für den Test entscheidend: **serverseitig sind beide nicht unterscheidbar.** SSPI erzeugt in
beiden Fällen ein gültiges Kerberos-AP-REQ, im Audit steht identisch `source: "Windows"`.
Ein Filler maskiert damit eine fehlende Browser-Policy vollständig. Der Nachweis lässt sich
deshalb nur clientseitig führen — auf einem Client ohne Ausfüllwerkzeug, mit leerem Tresor
und nach Kaltstart. Genau dafür existiert die Gegenprobe W6c.

## Diagnose-Fallen, die kein Produktdefekt sind

- **Die UI verschluckt die Fehlerursache.** `LoginPage` mappt 401 (kein Ticket), 401
  (NTLM), 401 (keine Gruppe), 503 (DC weg), 404 (SSO aus) und 429 auf **dieselbe**
  Meldung. Urteile nie aus der UI ableiten, immer aus HTTP-Status + Audit-Zeile.
- **`windows_directory_unavailable` ist doppeldeutig.** Eine LDAPS-Fehlkonfiguration
  liefert denselben Reason-Code wie ein echter DC-Ausfall. Für W14 muss
  `/healthz/directory` vorher `Healthy` gewesen sein, sonst ist das 503 nicht aussagekräftig.
- **Kollisionen erzeugen kein `LOGIN_FAILED`.** Bei Username-Kollision, Identitätskonflikt
  und Tombstone schreibt der Controller nur eine Metrik; das Audit kommt aus
  `ExternalUserMapper` als `USER_WINDOWS_REFUSED_*`. SIEM-Regeln, die nur `LOGIN_*`
  beobachten, verlieren diese Fälle.
- **Zwei unabhängige Uhren steuern die Revocation.** `DirectorySynchronizationService`
  (1–5 min, leader-only) reagiert auf Gruppenentzug/Disable;
  `ExternalAuthorizationStalenessService` hält die 15-Minuten-Decke und fängt den
  DC-Ausfall ab. Für die Drills `DirectorySyncIntervalMinutes=1` setzen, aber die
  15-Minuten-Obergrenze als Bestehensschwelle nehmen.

## Grenzen

- **Kein HAProxy** — Punkt 3 der Feldtest-Matrix (Handshake über den Produktions-Proxy,
  `http-reuse never`, connection-scoped Negotiate) bleibt offen.
- **Nur ein DC** — Multi-DC-Konsens (`ReconcileEndpointResults`: jeder Endpoint-Fehler
  ⇒ 503, **kein** Failover) ist nicht ausübbar. Punkte 1 und 6 bleiben offen, ebenso die
  in `docs/ldap-windows-sso.md` notierte Design-vs-Doc-Spannung.
- **Kein OIDC/SCIM** (Punkte 8/9) und **kein HA-Restart** (Punkt 10).
- Der Browser-Handshake und die GPO-Wirksamkeit bleiben manuell.
  `Invoke-WebRequest -UseDefaultCredentials` beweist Kerberos auf dem HTTP-Stack, **nicht**
  die Browser-Policy — beide Nachweise sind nötig, einer ersetzt den anderen nicht.
