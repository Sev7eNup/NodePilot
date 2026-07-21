# NodePilot — Windows Server Deployment

Turnkey installer für NodePilot als Windows-Service auf einem domain-joined Windows Server 2022/2025. TLS wird direkt von Kestrel terminiert (Cert aus `LocalMachine\My`), die Datenbank läuft auf **SQL Server 2022 (Trusted Connection)** oder **PostgreSQL 16+** (user/password) — umschaltbar per `-DbProvider`.

Der Dienst läuft wahlweise unter:

- **LocalSystem** (`-UseLocalSystem`) — kein gMSA nötig. Im Netzwerk authentifiziert sich der Dienst dann als **Computer-Konto** `DOMAIN\<host>$`: SQL-Server-Trusted-Connection und integriertes WinRM nutzen dieses Konto. Einfachste Variante für Einzelserver.
- **gMSA** (`-ServiceAccount 'CONTOSO\svc-nodepilot$'`) — dedizierte, AD-verwaltete Identität. Least-Privilege und in HA-Clustern die saubere Wahl, weil alle Knoten dieselbe Identität teilen (bei LocalSystem hat jeder Knoten ein eigenes Computer-Konto, das jeweils einzeln am SQL Server / an den WinRM-Zielen berechtigt werden muss).

## Dateien

| Datei | Zweck |
|---|---|
| [Build-Artifact.ps1](Build-Artifact.ps1) | Baut `out\NodePilot-<version>.zip` aus dem Repo (dotnet publish + npm build + Template) |
| [Install-NodePilot.ps1](Install-NodePilot.ps1) | Hauptinstaller — Service, ACLs, Firewall, Cert-Key-Access |
| [Update-NodePilot.ps1](Update-NodePilot.ps1) | In-Place-Upgrade, erhält appsettings + SQL-DB, rollt bei Fehler zurück |
| [Uninstall-NodePilot.ps1](Uninstall-NodePilot.ps1) | Stoppt Dienst, entfernt Binaries + Firewall-Regel. DB bleibt unberührt |
| [Test-Failover.ps1](Test-Failover.ps1) | HA-Failover-Smoke-Test gegen zwei Knoten (killt Leader, misst RTO bis `/healthz/leader` am Standby grün wird) |
| [Test-DeploymentTemplates.ps1](Test-DeploymentTemplates.ps1) | Statischer Sicherheits-/Vertragscheck für HAProxy- und Appsettings-Templates |
| [templates/appsettings.Production.json.template](templates/appsettings.Production.json.template) | Produktions-Config-Template (Single-Node) |
| [templates/appsettings.Cluster.json.template](templates/appsettings.Cluster.json.template) | Config-Template für Active/Passive-HA (`Cluster:Enabled=true`, `Secrets:Provider=AesGcm`) — siehe `docs/ha-active-passive.md` |
| [templates/haproxy.cfg.template](templates/haproxy.cfg.template) | HAProxy-Beispielconfig mit `GET /healthz/leader`-Probe für das HA-Setup |

## Voraussetzungen (einmalig, per Hand vor dem ersten Install)

### 1. Server-Host

- Windows Server 2022 oder 2025, Domain-joined
- PowerShell ≥ 5.1 (Windows PowerShell) oder 7+ (empfohlen)
- **.NET 10 ASP.NET Core Hosting Bundle** — Download unter <https://dotnet.microsoft.com/download>
- Zielserver kann den SQL Server auf Port 1433 erreichen

### 2. Service-Identität

> **Nur bei LocalSystem (`-UseLocalSystem`):** Dieser ganze Abschnitt entfällt. Es muss kein gMSA erstellt oder installiert werden — Windows liefert die Identität (Computer-Konto) inhärent. Weiter mit Abschnitt 3 (Datenbank); beachte dort die Computer-Konto-Variante des SQL-Logins.

#### Group Managed Service Account (gMSA) — nur für den gMSA-Pfad

Am Domain Controller (oder von einem Host mit RSAT-AD-PowerShell):

```powershell
# Einmalig pro Domain:
Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10))

# Sicherheitsgruppe mit den NodePilot-Hosts:
New-ADGroup -Name 'NodePilot-Servers' -GroupScope Global -GroupCategory Security
Add-ADGroupMember -Identity 'NodePilot-Servers' -Members (Get-ADComputer -Identity $targetServer)

# gMSA erzeugen:
New-ADServiceAccount -Name svc-nodepilot `
    -DNSHostName "svc-nodepilot.$((Get-ADDomain).DNSRoot)" `
    -PrincipalsAllowedToRetrieveManagedPassword 'NodePilot-Servers'
```

Am Zielserver:

```powershell
Install-ADServiceAccount -Identity svc-nodepilot
Test-ADServiceAccount -Identity svc-nodepilot    # → True
```

### 3. Datenbank

#### Variante A: SQL Server 2022 (Default)

Am SQL Server als `sysadmin`. Der Windows-Login ist die **Netzwerk-Identität** des Dienstes:

- **gMSA-Pfad** → der gMSA: `CONTOSO\svc-nodepilot$`
- **LocalSystem-Pfad** → das **Computer-Konto** des NodePilot-Servers: `CONTOSO\NPSRV01$` (NetBIOS-Domäne + Hostname + `$`). Bei mehreren Knoten jeden Server einzeln anlegen.

```sql
USE master;
-- gMSA:        CREATE LOGIN [CONTOSO\svc-nodepilot$] FROM WINDOWS;
-- LocalSystem: CREATE LOGIN [CONTOSO\NPSRV01$]       FROM WINDOWS;
CREATE LOGIN [CONTOSO\NPSRV01$] FROM WINDOWS;

CREATE DATABASE NodePilot;
USE NodePilot;
CREATE USER [CONTOSO\NPSRV01$] FOR LOGIN [CONTOSO\NPSRV01$];
ALTER ROLE db_owner ADD MEMBER [CONTOSO\NPSRV01$];
```

`db_owner` ist nötig, damit der MigrationBootstrapper beim ersten Start das EF-Migrations-Set anwenden kann.

> Der Installer-Pre-Flight prüft die SQL-Erreichbarkeit mit der Identität des **installierenden Admins**, nicht mit der Dienst-Identität. Bei LocalSystem gibt er nach erfolgreichem Check zusätzlich genau das `CREATE LOGIN [<host>$]`-Snippet aus, das der laufende Dienst braucht — fehlt der Login, startet der Dienst, scheitert aber an `/healthz/ready` (503).

**RCSI (Read-Committed-Snapshot-Isolation)** wird vom Installer automatisch aktiviert (`Enable-SqlReadCommittedSnapshot` im Pre-Flight). Das ist das SQL-Server-Pendant zu Postgres-MVCC: ohne RCSI blockieren langlaufende Reader (Stats-Refresh, Retention-Sweeps) jeden parallelen `INSERT` in `WorkflowExecutions`/`StepExecutions` unter dem Default-2PL-Locking. Falls der Installer-Schritt am Permission-Check scheitert (Login hat kein `ALTER DATABASE`), zeigt er die T-SQL-Anweisung für den DBA an. Manuelle Aktivierung:

```sql
ALTER DATABASE [NodePilot] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
SELECT name, is_read_committed_snapshot_on FROM sys.databases WHERE name = 'NodePilot';  -- → 1
```

#### Variante B: PostgreSQL 16+

Am Postgres-Server als Superuser:

```sql
CREATE ROLE nodepilot WITH LOGIN PASSWORD '<choose-strong-secret>';
CREATE DATABASE nodepilot OWNER nodepilot;
```

Der Installer pollt nur die TCP-Reachability (Port 5432). Auth-Probleme (falsches Passwort, pg_hba-Block) zeigen sich erst beim `/healthz/ready`-Poll nach Service-Start — dann mit der exakten Npgsql-Fehlermeldung im Serilog-Rolling-File unter `C:\ProgramData\NodePilot\logs`.

> **DB-TLS ist Pflicht in Produktion.** Der `DatabaseTlsBootValidator` bricht den Boot ab, wenn die DB-Verbindung den Server nicht verifiziert — SQL Server wird als `Encrypt=Strict;TrustServerCertificate=False` erzwungen (Installer setzt bei Bedarf `-SqlCertificateHostName`), Postgres als `SSL Mode=VerifyFull` gegen die per `-PostgresRootCertificate` übergebene Root-CA (PEM). Der DB-Server muss also ein von dieser CA ausgestelltes Server-Zertifikat auf den Connect-Hostnamen präsentieren. `Database:AllowInsecureTls=true` ist ein reiner Dev-Loopback-Escape und in Produktion untersagt.

### 4. TLS-Zertifikat

Cert (PFX) am Zielserver in den **LocalMachine\My**-Store importieren — mit privatem Key:

```powershell
Import-PfxCertificate -FilePath C:\Certs\nodepilot.pfx `
    -CertStoreLocation Cert:\LocalMachine\My `
    -Password (Read-Host -AsSecureString 'PFX password') `
    -Exportable
```

Thumbprint notieren:

```powershell
Get-ChildItem Cert:\LocalMachine\My |
    Where-Object { $_.Subject -like '*nodepilot*' } |
    Select-Object Subject, Thumbprint, NotAfter
```

Der Installer grantet dem gMSA automatisch Read-Access auf die Private-Key-Datei.

### AD SSO Preview

LDAP, Windows/Kerberos, OIDC und SCIM sind opt-in und bleiben bis zum bestandenen realen AD-/Kerberos-/LDAPS-Feldtest als **AD SSO Preview** gekennzeichnet. Für AD-Pfade sind LDAPS mit vollständiger Zertifikatsprüfung, mindestens eine zugelassene AD-Gruppen-SID, ein Service-Bind für den Verzeichnisabgleich und ein Sync-Intervall von höchstens fünf Minuten verpflichtend. Windows SSO setzt außerdem eine wirksame Host-/Domain-Policy voraus, die eingehendes NTLM ablehnt.

Änderungen unter `Authentication` werden beim Speichern validiert, greifen aber erst nach einem Dienstneustart vollständig. Das ausgelieferte Default-Profil lässt alle externen Provider deaktiviert und setzt lokale Anmeldung auf `BreakGlassOnly`. SCIM-Token werden ohne Unterbrechung rotiert, indem der alte Wert kurzzeitig unter `Authentication:Scim:PreviousBearerToken` bleibt und nach der IdP-Umstellung gelöscht wird.

### 5. Kerberos Constrained Delegation (für WinRM-Ziele)

> Dieser Abschnitt betrifft **nur den integrierten (credential-losen) WinRM-Pfad**. Wenn NodePilot je Maschine hinterlegte Credentials verwendet, ist keine Delegation nötig — die Identität des Dienstes spielt dann keine Rolle. Bei **LocalSystem** ist die zu delegierende Identität das **Computer-Konto** des NodePilot-Servers (`Get-ADComputer <NodePilot-Host>`) statt des gMSA — ansonsten identisches Vorgehen.

Damit der gMSA per implicit Kerberos auf Ziel-Maschinen zugreifen kann, muss **Resource-based Constrained Delegation** auf jedem Zielserver eingerichtet werden:

```powershell
# Auf einem DC:
$gmsa = Get-ADServiceAccount -Identity svc-nodepilot
foreach ($target in $targetHosts) {
    $computer = Get-ADComputer -Identity $target
    Set-ADComputer -Identity $target `
        -PrincipalsAllowedToDelegateToAccount (
            (Get-ADComputer $target).PrincipalsAllowedToDelegateToAccount + $gmsa
        )
}
```

WinRM-Endpunkt auf den Zielen (falls noch nicht aktiv):

```powershell
Enable-PSRemoting -Force
winrm quickconfig -transport:https   # für Remote:RequireWinRmSsl=true
```

## Artefakt bauen

Auf einem Build-Host mit .NET 10 SDK + Node.js LTS:

```powershell
git clone <repo> NodePilot
cd NodePilot
$releaseSigner = '0123456789ABCDEF0123456789ABCDEF01234567'
.\deploy\Build-Artifact.ps1 -Version 2026.04.23 -SigningCertificateThumbprint $releaseSigner
# → .\out\NodePilot-2026.04.23.zip
```

Den Zip auf den Zielserver kopieren.

## Installation

Als lokaler Administrator am Zielserver:

**SQL Server + LocalSystem (kein gMSA):**

```powershell
$releaseSigner = '0123456789ABCDEF0123456789ABCDEF01234567'
.\deploy\Install-NodePilot.ps1 `
    -ArtifactPath   C:\Packages\NodePilot-2026.04.23.zip `
    -TrustedArtifactSignerThumbprint $releaseSigner `
    -UseLocalSystem `
    -SqlServer      'sql01.contoso.local' `
    -SqlDatabase    'NodePilot' `
    -CertThumbprint 'A1B2C3D4E5F6...' `
    -PublicHostname 'nodepilot.contoso.local'
```

→ Der Dienst läuft als `LocalSystem`; am SQL Server muss das **Computer-Konto** `CONTOSO\<host>$` als `db_owner`-Login existieren (siehe Abschnitt 3).

**SQL Server + gMSA:**

```powershell
$releaseSigner = '0123456789ABCDEF0123456789ABCDEF01234567'
.\deploy\Install-NodePilot.ps1 `
    -ArtifactPath   C:\Packages\NodePilot-2026.04.23.zip `
    -TrustedArtifactSignerThumbprint $releaseSigner `
    -ServiceAccount 'CONTOSO\svc-nodepilot$' `
    -SqlServer      'sql01.contoso.local' `
    -SqlDatabase    'NodePilot' `
    -CertThumbprint 'A1B2C3D4E5F6...' `
    -PublicHostname 'nodepilot.contoso.local'
```

**PostgreSQL (mit gMSA — für LocalSystem einfach `-ServiceAccount` durch `-UseLocalSystem` ersetzen):**

```powershell
$releaseSigner = '0123456789ABCDEF0123456789ABCDEF01234567'
$pgPw = Read-Host -Prompt 'Postgres password' -AsSecureString

.\deploy\Install-NodePilot.ps1 `
    -ArtifactPath  C:\Packages\NodePilot-2026.04.23.zip `
    -TrustedArtifactSignerThumbprint $releaseSigner `
    -ServiceAccount 'CONTOSO\svc-nodepilot$' `
    -DbProvider       postgres `
    -PostgresHost     'pg01.contoso.local' `
    -PostgresDatabase 'nodepilot' `
    -PostgresUser     'nodepilot' `
    -PostgresPassword $pgPw `
    -PostgresRootCertificate C:\PKI\postgres-root-ca.pem `
    -CertThumbprint   'A1B2C3D4E5F6...' `
    -PublicHostname   'nodepilot.contoso.local'
```

Der Installer macht alles Weitere:

1. Pre-Flight (Admin, .NET 10, Cert vorhanden mit Private Key, gMSA verfügbar, SQL erreichbar)
2. Alten Dienst stoppen und entfernen (falls vorhanden)
3. `C:\Program Files\NodePilot` leeren und neu befüllen
4. `C:\ProgramData\NodePilot\logs` anlegen
5. `appsettings.Production.json` aus Template erzeugen
6. ACLs setzen (Service-Identität = gMSA bzw. `NT AUTHORITY\SYSTEM` bei LocalSystem):
   - DataPath: Service = Modify, Admins/SYSTEM = Full, sonst nichts. Bei LocalSystem deckt die SYSTEM-Full-ACE den Dienst bereits ab — keine zusätzliche ACE.
   - `appsettings.Production.json`: Service = Read, Admins/SYSTEM = Full (bei LocalSystem analog von SYSTEM-Full abgedeckt)
   - Cert Private Key: gMSA = Read; bei LocalSystem übersprungen (SYSTEM hat Read auf MachineKeys per Default)
    - PostgreSQL: `ConnectionStrings:Postgres` bleibt in JSON leer; der vollständig gequotete
      Connection-String liegt nur im service-scoped `ConnectionStrings__Postgres`-Environment.
      Vor dem Schreiben wird der Service-Registry-Key auf SYSTEM/Administrators beschränkt.
7. Firewall-Regel `NodePilot <Name> HTTPS` (Domain profile)
8. Dienst per `Win32_Service.Create` anlegen — gMSA (leeres Passwort + `sc.exe managedaccount` + „Log on as a service"-Grant) oder `LocalSystem` (keine dieser drei Schritte nötig), Recovery-Actions, `ASPNETCORE_ENVIRONMENT=Production`
9. Dienst starten, `https://localhost/healthz/ready` pollen
10. Admin-Bootstrap-Token + External-Trigger-API-Key auf der Konsole ausgeben

Nach erfolgreichem Install steht in der Konsole:

- URL: `https://<public-hostname>/`
- **Admin-Setup-Token** (aus `C:\ProgramData\NodePilot\admin-setup.token`) → im Browser beim ersten Login als `X-Setup-Token`-Header oder im Setup-Screen der SPA eingeben, dann Admin-User erstellen → Token wird gelöscht, Bootstrap-Fenster schließt.
- **External-Trigger API Key** — einmalig sichern, wird nicht erneut angezeigt.

### Parameter-Übersicht

| Parameter | Pflicht | Default |
|---|---|---|
| `-ArtifactPath` | ✓ | |
| `-TrustedArtifactSignerThumbprint` | ✓ | gepinnter Enterprise Code-Signing-Publisher |
| `-ServiceAccount` | ✓ im gMSA-Pfad (entfällt bei `-UseLocalSystem`) | |
| `-UseLocalSystem` | Alternative zu `-ServiceAccount` | off |
| `-CertThumbprint` | ✓ | |
| `-DbProvider` | | `sqlserver` (Alternative: `postgres`) |
| `-SqlServer` | ✓ (nur bei `sqlserver`) | |
| `-SqlDatabase` | | `NodePilot` |
| `-SqlCertificateHostName` | | Hostteil von `-SqlServer` |
| `-PostgresHost` | ✓ (nur bei `postgres`) | |
| `-PostgresPort` | | `5432` |
| `-PostgresDatabase` | | `nodepilot` |
| `-PostgresUser` | ✓ (nur bei `postgres`) | |
| `-PostgresPassword` | ✓ (nur bei `postgres`, SecureString) | |
| `-PostgresRootCertificate` | ✓ (nur bei `postgres`, PEM) | |
| `-PublicHostname` | | Machine-FQDN |
| `-HttpsPort` | | `443` |
| `-HttpPort` | | `80` (0 = kein HTTP-Binding) |
| `-InstallPath` | | `C:\Program Files\NodePilot` |
| `-DataPath` | | `C:\ProgramData\NodePilot` |
| `-ServiceName` | | `NodePilot` |
| `-ServiceDisplayName` | | `NodePilot Orchestrator` |
| `-ExternalTriggerApiKey` | | auto-generiert (48 bytes base64) |
| `-JwtIssuer` | | `nodepilot:prod:<machine>` |
| `-JwtAudience` | | `nodepilot:prod:<machine>` |
| `-AllowedHosts` | | PublicHostname |
| `-KnownProxyIps` | | leer (nur Loopback wird vertraut); bei HAProxy jede direkte Transport-IP angeben |
| `-SkipSqlConnectivityCheck` | | off |
| `-SkipGmsaCheck` | | off |

## HAProxy vor NodePilot

Für Active/Passive HA und Windows-Negotiate das mitgelieferte
[`haproxy.cfg.template`](templates/haproxy.cfg.template) verwenden. Vor dem Deployment
müssen alle `{{...}}`-Platzhalter ersetzt werden, insbesondere:

| Platzhalter | Bedeutung |
|---|---|
| `TLS_CERT_PATH` | PEM mit öffentlichem HAProxy-Zertifikat und Private Key |
| `BACKEND_CA_FILE` | PEM-CA-Kette, mit der HAProxy die Kestrel-Zertifikate validiert |
| `BACKEND_TLS_SERVER_NAME` | Gemeinsamer SAN-/SNI-Name auf beiden Backend-Zertifikaten und in NodePilots `AllowedHosts` (üblicherweise der öffentliche Service-Hostname) |
| `NODE_A_IP`, `NODE_B_IP` | Direkte Backend-Adressen; keine unvalidierten Hostnamen |

Die Backend-Verbindung bleibt für Negotiate persistent und exklusiv pro Client-Session
(`option http-keep-alive`, `http-reuse never`). Backend-TLS ist fail-closed; `verify none`
ist nicht unterstützt. Clientseitig eingespeiste Forwarded Headers werden gelöscht und von
HAProxy neu gesetzt.

NodePilot muss im Gegenzug ausschließlich den direkten HAProxy-Absendern vertrauen:

```powershell
.\deploy\Install-NodePilot.ps1 `
    <weitere Parameter> `
    -KnownProxyIps '10.0.1.5','10.0.1.6'
```

Nicht die öffentliche VIP und nicht pauschal ein privates Netz eintragen. Ohne explizite
Proxy-IP ignoriert NodePilot Forwarded Headers sicher; dann sehen Rate Limiter jedoch nur
die HAProxy-IP. Nach dem Rendern beide Prüfungen ausführen:

```powershell
.\deploy\Test-DeploymentTemplates.ps1
```

```bash
haproxy -c -f /etc/haproxy/haproxy.cfg
```

## Update

Für ein neues Artefakt:

```powershell
$releaseSigner = '0123456789ABCDEF0123456789ABCDEF01234567'
.\deploy\Update-NodePilot.ps1 `
    -ArtifactPath C:\Packages\NodePilot-2026.05.10.zip `
    -TrustedArtifactSignerThumbprint $releaseSigner
```

Erhält `appsettings.Production.json`, die DB (SQL Server oder Postgres) und den Service-Account. Das Backup unter `C:\Program Files\NodePilot.backup.<timestamp>` enthält nur Binaries und niemals die secret-haltige Produktionskonfiguration; automatischer Rollback bei Health-Check-Fehler.

## Uninstall

```powershell
.\deploy\Uninstall-NodePilot.ps1              # Logs + Config bleiben
.\deploy\Uninstall-NodePilot.ps1 -PurgeData   # alles außer DB weg
```

Die **SQL-Datenbank wird nie automatisch gelöscht** — nach Bedarf per DBA-Tooling droppen.

## Backup & Disaster Recovery

NodePilot bringt ein **System-Configuration-Backup** mit (Admin-only, UI unter `/backup` oder CLI `np backup`). Es sichert die *Konfiguration* portabel und passphrase-verschlüsselt: Workflows, Ordner/Freigaben, Maschinen, Anmeldedaten, globale Variablen, Benutzer und Runtime-Settings.

> **Kein Ersatz für ein DB-Backup.** Ausführungshistorie, Audit-Log und Statistiken sind **nicht** enthalten. Für vollständige Sicherung weiterhin das DB-eigene Backup fahren (Postgres `pg_dump` / SQL Server Backup). Das Config-Backup ist für „Instanz neu aufsetzen / auf anderen Host umziehen".

**Eigenschaften.** Die `.npbackup`-Datei ist mit einer **Passphrase** verschlüsselt (PBKDF2→AES-GCM) und mit einem Whole-file-MAC gegen Manipulation gesichert. **Ohne Passphrase ist das Backup unwiederbringlich** — sicher und getrennt von der Datei aufbewahren. Der Restore ist portabel (anderer Host, anderer Secret-Provider DPAPI↔AES-GCM), weil Secrets beim Restore mit dem Ziel-Provider neu verschlüsselt werden.

**Geplantes DR-Backup (headless, z. B. Scheduled Task).** Passphrase nie als Argument übergeben — per Env-Var oder Datei:

```powershell
$env:NP_BACKUP_PASS = '<aus-secret-store>'
np backup export --out "D:\backups\nodepilot-$(Get-Date -Format yyyyMMdd).npbackup" --passphrase-env NP_BACKUP_PASS
# Sektionen einschränken: --sections workflows,credentials,machines
```

**Wiederherstellung auf einer frischen Instanz.**

```powershell
np backup preview  .\nodepilot-20260529.npbackup --passphrase-env NP_BACKUP_PASS    # Dry-Run: was käme wo an
np backup restore  .\nodepilot-20260529.npbackup --passphrase-env NP_BACKUP_PASS --yes
# Konflikt-Policy bei Restore in bestehende DB: --policy skip|rename|overwrite bzw. section=policy
```

Restore läuft transaktional in Abhängigkeitsreihenfolge, validiert Referenzen vorab (Abbruch bei nicht auflösbaren), schützt den letzten aktiven Admin und invalidiert bei User-Overwrite bestehende Sessions. Settings werden zuletzt geschrieben (ggf. Service-Neustart nötig, damit sie greifen).

## Troubleshooting

| Symptom | Check |
|---|---|
| Service startet und stoppt sofort | Event Viewer → Windows Logs → Application, Quelle `<ServiceName>`. Meist Config- oder ACL-Problem. |
| Cert nicht gefunden / No private key | `Get-ChildItem Cert:\LocalMachine\My\<thumb>` — muss HasPrivateKey=True zeigen. Bei Re-Import `-KeyStorageFlags MachineKeySet,PersistKeySet,Exportable`. |
| 503 auf `/healthz/ready` | SQL nicht erreichbar. `sqlcmd -S sql01 -E -d NodePilot -Q "SELECT 1"` als gMSA (via `PsExec -u gMSA$ -s ...`) verifizieren. |
| WinRM-Remote-Calls schlagen mit "Access denied" fehl | gMSA hat keine Kerberos-Delegation zum Ziel. Siehe Abschnitt 5 (Resource-based Constrained Delegation). |
| SPA lädt, API-Calls 401 | Login über Setup-Token noch nicht erfolgt — `admin-setup.token` unter DataPath lesen. |
| DPAPI decrypt failed für Credentials nach Service-Account-Wechsel | Template setzt `Credentials:DpapiScope=LocalMachine`. Bei existierenden `CurrentUser`-verschlüsselten Credentials: Credentials neu eingeben (keine Migrations-Helper). |
| Nach Update sieht der Service die Config nicht | ACL auf `appsettings.Production.json` — Update-Skript setzt Read für den aktuellen Service-Account nach. Bei manuellem Eingriff: `icacls "<Install>\appsettings.Production.json" /grant "<gMSA$>:(R)"`. |
| Port 443 bereits belegt | `Get-NetTCPConnection -LocalPort 443` zeigt PID. Häufig IIS-Default-Site oder WinRM-HTTPS-Listener. Installer bindet via Kestrel-Socket, nicht http.sys — trotzdem bleibt ein Konflikt ein Konflikt. |
| Ticket gemeldet, wo schauen? | **Support-Log** — zwei Sub-Sinks aus demselben Filter: (1) Plain-Text-Datei `C:\ProgramData\NodePilot\logs\nodepilot-support-*.log` (90 Tage Retention) für RDP/tail-Diagnose, (2) strukturierte DB-Tabelle `SupportEvents` (90 Tage Retention via `Retention:SupportEvents`) für den Web-Viewer mit Filter+Cursor+Export. Im Browser: Admin-Settings → Tab „Support-Log" → Toggle „Tabelle (DB) | Plain-Text (Datei)". Volldiagnose bleibt im `nodepilot-*.log` daneben. |

## Was NICHT vom Installer gemacht wird

- Keine Installation des .NET Hosting Bundles — muss vorab vorhanden sein.
- Kein Erstellen des gMSA, der SQL-Login oder der Kerberos-Delegation — AD/DBA-Aufgabe.
- Keine Registrierung des NodePilot-HTTP-SPN und keine NTLM-Block-Policy — beides muss das AD-/Security-Team vor Aktivierung von Windows SSO ausrollen und prüfen.
- Keine Konfiguration eines OIDC-IdP oder SCIM-Clients — Redirect-URI, Claims, Gruppen-Allowlist und Provisioning-Bearer-Token bleiben IdP-/IAM-Aufgabe.
- Keine Backups der SQL-Datenbank — separat per SQL-Agent/Ola Hallengren/etc. einrichten.
- Keine Log-Forwarding/Monitoring-Integration — Logs landen unter `C:\ProgramData\NodePilot\logs`, Abholung per Winlogbeat/OTel-Collector/etc. nach Wahl.
- Keine Cross-Provider-Daten-Migration zwischen SQL Server und Postgres. Falls benötigt: Export/Import via `GET /api/workflows/export` → `POST /api/workflows/import`.
