# Windows-Server-Deployment

Diese Betriebsart installiert NodePilot als Windows-Dienst für den produktiven Netzwerkbetrieb. Die mitgelieferten Skripte befinden sich unter `deploy\`. Die vollständige Parameterreferenz steht zusätzlich in `deploy\README.md`.

## Zielzustand

```text
Browser / API-Client
        |
        | HTTPS
        v
Kestrel im Windows-Dienst "NodePilot"
        |
        +--> SQL Server 2022
        |    oder
        +--> PostgreSQL 16+
        |
        +--> Windows-Zielsysteme über WinRM
```

Single-Node-Installationen verwenden Kestrel direkt. Active/Passive-Installationen verwenden die mitgelieferte HAProxy-Vorlage.

## Unterstützte Varianten

### Dienstidentität

| Variante | Einsatz |
|---|---|
| **LocalSystem** | Einfacher Einzelserver; Netzwerkzugriffe erfolgen als Computerkonto `DOMAIN\HOST$` |
| **gMSA** | Least-Privilege, gemeinsame Identität und empfohlener HA-Pfad |

### Datenbank

| Provider | Authentifizierung | Produktions-TLS |
|---|---|---|
| SQL Server 2022 | Windows Integrated Security | `Encrypt=Strict;TrustServerCertificate=False` |
| PostgreSQL 16+ | Benutzername und Passwort | `SSL Mode=VerifyFull` mit Root-CA |

## Voraussetzungen

### Zielserver

- Windows Server 2022 oder 2025
- Domain-Mitgliedschaft
- PowerShell 5.1 oder PowerShell 7
- .NET 10 ASP.NET Core Hosting Bundle
- Netzwerkzugriff zur Datenbank
- TLS-Zertifikat mit privatem Schlüssel in `LocalMachine\My`
- Lokale Administratorrechte für die Installation

### Build-Host

- .NET 10 SDK
- Node.js LTS und npm
- Code-Signing-Zertifikat für Artefaktmanifest und Verteilung

### Datenbank

Die Datenbank muss vor der Installation existieren. Die NodePilot-Identität benötigt DDL-Rechte, damit EF-Migrationen beim Start angewendet werden können.

## 1. Dienstidentität vorbereiten

### Variante A: LocalSystem

Keine Dienstkontoanlage ist erforderlich. Für SQL Server muss das Computerkonto des NodePilot-Hosts als Login vorhanden sein:

```sql
USE master;
CREATE LOGIN [CONTOSO\NPSRV01$] FROM WINDOWS;

CREATE DATABASE NodePilot;
USE NodePilot;
CREATE USER [CONTOSO\NPSRV01$] FOR LOGIN [CONTOSO\NPSRV01$];
ALTER ROLE db_owner ADD MEMBER [CONTOSO\NPSRV01$];
```

### Variante B: gMSA

Das gMSA wird in Active Directory angelegt, für den Zielserver freigegeben und anschließend auf dem Zielserver installiert:

```powershell
Install-ADServiceAccount -Identity svc-nodepilot
Test-ADServiceAccount -Identity svc-nodepilot
```

Das erwartete Testergebnis ist `True`. Für SQL Server wird das gMSA analog zum Computerkonto als Login und Datenbankbenutzer mit `db_owner` angelegt.

## 2. Datenbank vorbereiten

### SQL Server

Zusätzlich zur NodePilot-Datenbank sollte Read-Committed-Snapshot-Isolation aktiviert sein:

```sql
ALTER DATABASE [NodePilot]
SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
```

Der Installer versucht diese Einstellung automatisch zu setzen. Ohne ausreichende Rechte gibt der Preflight die erforderliche SQL-Anweisung aus.

### PostgreSQL

```sql
CREATE ROLE nodepilot WITH LOGIN PASSWORD '<strong-secret>';
CREATE DATABASE nodepilot OWNER nodepilot;
```

Der PostgreSQL-Server muss ein Zertifikat präsentieren, dessen Hostname und Vertrauenskette geprüft werden können. Die Root-CA wird dem Installer als PEM-Datei übergeben.

## 3. HTTPS-Zertifikat importieren

```powershell
$certificatePassword = Read-Host -AsSecureString "PFX password"
Import-PfxCertificate `
  -FilePath C:\Certs\nodepilot.pfx `
  -CertStoreLocation Cert:\LocalMachine\My `
  -Password $certificatePassword
```

Thumbprint ermitteln:

```powershell
Get-ChildItem Cert:\LocalMachine\My |
  Where-Object Subject -Like "*nodepilot*" |
  Select-Object Subject, Thumbprint, NotAfter
```

Der Zertifikatsname muss zum öffentlichen Hostnamen passen.

## 4. Produktionsartefakt bauen

Im Repository auf dem Build-Host:

```powershell
$releaseSigner = "0123456789ABCDEF0123456789ABCDEF01234567"
.\deploy\Build-Artifact.ps1 `
  -Version 2026.07.27 `
  -SigningCertificateThumbprint $releaseSigner
```

Ergebnis:

```text
out\NodePilot-2026.07.27.zip
out\NodePilot-2026.07.27.zip.manifest.json
out\NodePilot-2026.07.27.zip.manifest.json.p7s
```

Installer und Updater prüfen Signatur, Zertifikatskette, Dateiname, Länge und SHA-256-Hash vor jeder Änderung.

## 5. NodePilot installieren

Die Installationsbefehle laufen als lokaler Administrator auf dem Zielserver.

### SQL Server mit LocalSystem

```powershell
$releaseSigner = "0123456789ABCDEF0123456789ABCDEF01234567"
.\deploy\Install-NodePilot.ps1 `
  -ArtifactPath C:\Packages\NodePilot-2026.07.27.zip `
  -TrustedArtifactSignerThumbprint $releaseSigner `
  -UseLocalSystem `
  -SqlServer "sql01.contoso.local" `
  -SqlDatabase "NodePilot" `
  -CertThumbprint "A1B2C3D4E5F6..." `
  -PublicHostname "nodepilot.contoso.local"
```

### SQL Server mit gMSA

```powershell
$releaseSigner = "0123456789ABCDEF0123456789ABCDEF01234567"
.\deploy\Install-NodePilot.ps1 `
  -ArtifactPath C:\Packages\NodePilot-2026.07.27.zip `
  -TrustedArtifactSignerThumbprint $releaseSigner `
  -ServiceAccount "CONTOSO\svc-nodepilot$" `
  -SqlServer "sql01.contoso.local" `
  -SqlDatabase "NodePilot" `
  -CertThumbprint "A1B2C3D4E5F6..." `
  -PublicHostname "nodepilot.contoso.local"
```

### PostgreSQL mit gMSA

```powershell
$releaseSigner = "0123456789ABCDEF0123456789ABCDEF01234567"
$postgresPassword = Read-Host -AsSecureString "PostgreSQL password"

.\deploy\Install-NodePilot.ps1 `
  -ArtifactPath C:\Packages\NodePilot-2026.07.27.zip `
  -TrustedArtifactSignerThumbprint $releaseSigner `
  -ServiceAccount "CONTOSO\svc-nodepilot$" `
  -DbProvider postgres `
  -PostgresHost "pg01.contoso.local" `
  -PostgresDatabase "nodepilot" `
  -PostgresUser "nodepilot" `
  -PostgresPassword $postgresPassword `
  -PostgresRootCertificate C:\PKI\postgres-root-ca.pem `
  -CertThumbprint "A1B2C3D4E5F6..." `
  -PublicHostname "nodepilot.contoso.local"
```

Für PostgreSQL mit LocalSystem ersetzt `-UseLocalSystem` den Parameter `-ServiceAccount`.

## 6. Installationswirkung

Der Installer führt folgende Schritte aus:

1. Voraussetzungen, Signatur, Zertifikat, Dienstkonto und Datenbankzugriff prüfen.
2. Vorhandenen NodePilot-Dienst kontrolliert stoppen.
3. Binaries nach `C:\Program Files\NodePilot` installieren.
4. Betriebsdaten unter `C:\ProgramData\NodePilot` anlegen.
5. Produktionskonfiguration rendern.
6. Dateisystem- und Zertifikats-ACLs setzen.
7. HTTPS-Firewallregel anlegen.
8. Windows-Dienst mit Delayed Auto Start und Recovery Actions registrieren.
9. Dienst starten und Readiness prüfen.
10. Admin-Setup-Token und External-Trigger-API-Key ausgeben.

Der PostgreSQL-Connection-String wird nicht in die JSON-Datei geschrieben. Er liegt im ACL-geschützten Service-Environment.

## 7. Installation prüfen

```powershell
Get-Service NodePilot
Invoke-WebRequest https://nodepilot.contoso.local/healthz/live
Invoke-WebRequest https://nodepilot.contoso.local/healthz/ready
```

Erwartete Ergebnisse:

| Prüfung | Erwartung |
|---|---|
| Dienststatus | `Running` |
| `/healthz/live` | HTTP 200 |
| `/healthz/ready` | HTTP 200 und erreichbare Datenbank |
| Browserzugriff | Login- oder Setup-Seite ohne Zertifikatswarnung |

Bei aktivierter Verzeichnisanbindung ist `/healthz/directory` separat zu prüfen. Die allgemeine Readiness bleibt absichtlich auf die Datenbank beschränkt.

## 8. Ersten Admin-Account anlegen

Der Installer zeigt den einmaligen Setup-Token aus `C:\ProgramData\NodePilot\admin-setup.token` an. Der Setup-Dialog verwendet diesen Token zum Anlegen des ersten lokalen Admin-Kontos. Nach erfolgreichem Setup wird der Token gelöscht.

Der External-Trigger-API-Key wird nur einmal angezeigt und muss in einem Secret-Management-System gespeichert werden.

## Verzeichnis- und Dateiaufteilung

| Pfad | Inhalt | Dienstzugriff |
|---|---|---|
| `C:\Program Files\NodePilot\` | API, DLLs und `wwwroot` | Lesen |
| `C:\Program Files\NodePilot\appsettings.Production.json` | Produktionskonfiguration | Lesen |
| `C:\ProgramData\NodePilot\` | Schlüssel, Setup-Token, Logs und Betriebsdaten | Ändern |

## Update und automatischer Rollback

```powershell
$releaseSigner = "0123456789ABCDEF0123456789ABCDEF01234567"
.\deploy\Update-NodePilot.ps1 `
  -ArtifactPath C:\Packages\NodePilot-2026.08.10.zip `
  -TrustedArtifactSignerThumbprint $releaseSigner
```

Der Updater:

- prüft das neue Artefakt,
- sichert die vorhandenen Binaries,
- erhält Datenbank, Dienstkonto und Produktionskonfiguration,
- startet den Dienst neu,
- prüft den Health-Endpunkt,
- stellt bei einem fehlgeschlagenen Health-Check die vorherigen Binaries wieder her.

Das Binärbackup enthält keine secret-haltige `appsettings.Production.json`.

## Deinstallation

```powershell
.\deploy\Uninstall-NodePilot.ps1
```

Logs und Konfiguration bleiben erhalten. Vollständige Entfernung der lokalen Betriebsdaten:

```powershell
.\deploy\Uninstall-NodePilot.ps1 -PurgeData
```

Die externe Datenbank wird nie automatisch gelöscht.

## Backup und Wiederherstellung

Für eine vollständige Sicherung sind zwei Backups erforderlich:

1. **System-Configuration-Backup:** Workflows, Maschinen, Credentials, Benutzer und Runtime-Einstellungen.
2. **Datenbank-Backup:** Ausführungshistorie, Audit-Log, Statistiken und vollständiger Datenbestand.

PostgreSQL verwendet beispielsweise `pg_dump`; SQL Server verwendet die native SQL-Server-Sicherung. Details enthält [Import, Export und Backup](../import-export).

## Hochverfügbarkeit

Active/Passive-Betrieb erfordert:

- mindestens zwei NodePilot-Nodes,
- gemeinsame externe Datenbank,
- identische JWT-Parameter,
- `Cluster:Enabled=true`,
- AES-GCM als Secret-Provider,
- HAProxy mit Leader-Probe auf `/healthz/leader`,
- bei OIDC einen gemeinsamen, zertifikatgeschützten Data-Protection-Keyring.

Die vollständige Einrichtung steht unter [High Availability](../enterprise/high-availability).

## Enterprise-Funktionen aktivieren

Empfohlene Reihenfolge:

1. SIEM-Logging aktivieren.
2. Secret-Provider und gegebenenfalls HA konfigurieren.
3. Lokales Break-Glass-Admin-Konto prüfen.
4. LDAP- oder Windows-Konfiguration gegen echte Domain Controller testen.
5. Dienst neu starten.
6. `/healthz/ready` und `/healthz/directory` prüfen.
7. OIDC und SCIM erst nach abgeschlossenem Provider- und Offboarding-Test aktivieren.

LDAP, Windows SSO, OIDC und SCIM bleiben bis zum bestandenen Feldtest als Preview einzuordnen. Details enthält [AD SSO Preview](../enterprise/ldap-windows-sso).
