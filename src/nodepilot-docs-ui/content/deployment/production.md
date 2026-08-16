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
| SQL Server 2022 **ab CU1** (Build ≥ 16.0.4003.1) | Windows Integrated Security | `Encrypt=Strict;TrustServerCertificate=False` |
| PostgreSQL 16+ | Benutzername und Passwort | `SSL Mode=VerifyFull` mit Root-CA |

`Encrypt=Strict` ist TDS 8.0: SQL Server 2019 und älter beherrschen es nicht, und SQL Server
2022 **RTM** enthält einen TDS-8.0-Fehler, der parametrisierte Abfragen mit Error 8005
abbrechen lässt — behoben ab CU1. Der Installer prüft den Patchstand im Preflight; manuell:
`SELECT SERVERPROPERTY('ProductVersion')`.

## Voraussetzungen

### Zielserver

- Windows Server 2022 oder 2025
- Domain-Mitgliedschaft
- PowerShell 5.1 oder PowerShell 7
- ASP.NET Core Runtime 10.0.11 oder neuer in der 10.x-Linie (x64) — die reine Runtime genügt, Kestrel hostet selbst; das Hosting Bundle nur bei bewusstem IIS-Einsatz (es konfiguriert IIS um und startet W3SVC neu). Das `(x64)` ist verbindlich: NodePilot wird als `win-x64` ausgeliefert; der Preflight weist 32-Bit- und ältere verwundbare 10.x-Runtimes mit Pfad und Version zurück
- Netzwerkzugriff zur Datenbank
- TLS-Zertifikat mit privatem Schlüssel in `LocalMachine\My`
- Lokale Administratorrechte für die Installation

### Netzwerkpfad zu den Clients

Die Live-Ansicht der Oberfläche (laufende Schritte, Ausführungsstatus, Dashboard-Zähler) hält eine
SignalR-Verbindung auf `/hubs/execution`. Der Client handelt den Transport aus und arbeitet dabei
eine Leiter ab: **WebSockets → Server-Sent Events → Long-Polling**.

Ein Proxy oder eine TLS-Inspektion, die den `Upgrade: websocket`-Handshake verwirft, legt die
Live-Ansicht deshalb **nicht** lahm — sie fällt selbsttätig auf Server-Sent Events zurück. Sichtbar
wird es nur als wiederkehrender Verbindungsfehler in der Browser-Konsole, dazu etwas mehr Last pro
Verbindung. Wer die Konsole sauber halten will, lässt den Host in Proxy bzw. PAC-Datei umgehen oder
WebSocket-Durchlass für ihn freischalten.

Der Vollständigkeit halber: Kestrel annonciert HTTP/2 per ALPN. Eine TLS-terminierende Appliance
muss WebSockets damit als *Extended CONNECT* (RFC 8441) weiterreichen — das beherrschen längst
nicht alle, während gewöhnliche Requests unauffällig durchgehen.

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

### Beides kann das Setup übernehmen

Wer mit `NodePilot-Server-Setup-<version>.exe` installiert, kann das SQL oben überspringen, sofern das ausführende Konto `sysadmin` ist (oder `CREATE ANY DATABASE` besitzt). Die Readiness-Seite prüft den Datenbank-Zugriff der **Dienst-Identität** getrennt von der reinen Erreichbarkeit — erreichbar wird als *installierender Admin* getestet, angemeldet wird zur Laufzeit der Dienst — und legt Login, Benutzer und `db_owner` bei Bedarf an. Die Zeile kommt vorangehakt; unbeaufsichtigt fordert `"provisioning": { "createDatabaseAndLogin": true }` in der Answer-File dasselbe an. Existenzgeprüft: ist alles vorhanden, passiert nichts. Fehlen die Rechte, wird nichts verändert und die Anweisungen für den DBA werden angezeigt.

**PostgreSQL ebenso, mit einem Unterschied.** Das Setup bringt `psql` mit, meldet sich im Pre-Flight als NodePilot-Rolle an (`sslmode=verify-full` gegen das angegebene Root-Zertifikat) und legt Rolle und Datenbank auf Wunsch an. Weil PostgreSQL kein Gegenstück zu `Trusted_Connection` hat, verlangt das **Superuser-Zugangsdaten**: zwei zusätzliche Felder auf der Credentials-Seite bzw. `provisioning.postgresSuperUser` / `.postgresSuperPassword` in der Answer-File. Ohne sie bleibt die Zeile eine Diagnose ohne Knopf. Das Passwort einer bereits vorhandenen Rolle wird dabei **nie** überschrieben.

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

## 4. Produktionsartefakt beziehen

Entweder das veröffentlichte Release herunterladen oder selbst bauen — der Installer verlangt in
beiden Fällen ein signiertes Artefakt und den Thumbprint des Publishers.

**Variante A — Release herunterladen.** Am [aktuellen Release](https://github.com/Sev7eNup/NodePilot/releases/latest)
hängen das Zip, `manifest.json`, `.p7s`, `NodePilot-<version>.SHA256SUMS.txt` und das öffentliche
Signaturzertifikat.
Prüfsummen vergleichen und den Thumbprint gegen die Release-Notes abgleichen — dieser Abgleich
**ist** die Vertrauensentscheidung. Der Installer verlangt genau diesen Signierer und prüft
Codesignatur-Zweck, KeyUsage und Gültigkeit; ob die Maschine dem Herausgeber vertraut, spielt keine
Rolle. Ein Import nach `Cert:\LocalMachine\Root` ist optional und bewirkt nur, dass Windows die
Authenticode-Signatur der Installer selbst validiert.

> Wer mit dem GUI-Setup installiert (Kapitel 5, Variante A), braucht aus diesem Kapitel nichts:
> `NodePilot-Server-Setup-<version>.exe` hängt am selben Release, trägt das signierte Artefakt in
> sich und prüft dessen Signatur selbst — eine Datei statt fünf, kein Thumbprint-Abgleich von Hand.

### Beim ersten Start: das blaue SmartScreen-Fenster

Ein **heruntergeladener** Installer löst beim Start „Der Computer wurde durch Windows geschützt"
aus, mit *Nicht ausführen* als einziger sichtbarer Schaltfläche. Das ist erwartet und kein Hinweis
auf eine beschädigte Datei.

Dafür müssen zwei Dinge zutreffen, und beim Download treffen beide zu:

- **Die Datei trägt einen Mark of the Web.** Alles, was ein Browser speichert, bekommt einen
  Alternate Data Stream mit der Zone „Internet", und SmartScreen bewertet ausschließlich Dateien
  mit dieser Markierung. Ein selbst gebauter Installer aus `out\` hat sie nicht und startet
  wortlos — deshalb erscheint der Dialog beim ersten *veröffentlichten* Release, obwohl frühere
  eigene Builds desselben Produkts ihn nie ausgelöst haben.
- **Der Herausgeber hat keine Reputation.** NodePilot wird mit einem selbstsignierten Zertifikat
  signiert; SmartScreen hat also nichts zu bewerten und meldet eine unbekannte App.

Die Markierung lässt sich sichtbar machen und entfernen:

```powershell
Get-Content -LiteralPath .\NodePilot-Server-Setup-<version>.exe -Stream Zone.Identifier
# ZoneId=3   (3 = Internet)

Unblock-File -Path .\NodePilot-Server-Setup-<version>.exe
```

**Erst prüfen, dann wegklicken.** Der Dialog ist nicht die Vertrauensentscheidung — Prüfsumme und
Thumbprint sind es. Beides vergleichen, danach *Weitere Informationen → Trotzdem ausführen*.
`Get-AuthenticodeSignature` meldet dabei `UnknownError`; das ist bei einem selbstsignierten
Herausgeber der erwartete Befund, nicht ein Fehler.

Dasselbe gilt für den Zip-Weg: Aus einem markierten Archiv entpackte Dateien erben die Markierung,
also das Zip vor dem Entpacken mit `Unblock-File` freigeben — sonst behandelt Windows die darin
enthaltenen Skripte als heruntergeladenen Inhalt.

Antiviren-Ausschlüsse ändern daran nichts; SmartScreen ist ein getrennter Reputationsdienst und
ignoriert sie (siehe [Antiviren-Ausschlüsse](./av-exclusions)). Dauerhaft verschwindet der Dialog
nur mit einem reputationstragenden Zertifikat.

**Variante B — selbst bauen.** Im Repository auf dem Build-Host:

```powershell
$releaseSigner = "0123456789ABCDEF0123456789ABCDEF01234567"
.\deploy\Build-Artifact.ps1 -SigningCertificateThumbprint $releaseSigner
```

`-Version` ist optional und fällt auf die Produktversion aus `Directory.Build.props` zurück.

Ergebnis:

```text
out\NodePilot-1.2.0.zip
out\NodePilot-1.2.0.zip.manifest.json
out\NodePilot-1.2.0.zip.manifest.json.p7s
out\NodePilot-1.2.0.SHA256SUMS.txt
```

Mit `-IncludeServerInstaller` entsteht im selben Lauf zusätzlich `NodePilot-Server-Setup-1.2.0.exe`,
mit `-IncludeDesktopInstaller -PgBinariesPath <pgsql>` außerdem `NodePilot-Desktop-Setup-1.2.0.exe`
— alles unter derselben Version. `-InstallerSigningCertificateThumbprint <tp>` signiert beide
Installer als Teil des Laufs, was zwingend vor der Prüfsummenbildung passieren muss.

Installer und Updater prüfen Signatur (Thumbprint-Pin; keine Kettenvalidierung), Codesignatur-Eignung, Gültigkeit und KeyUsage des Signaturzertifikats sowie Dateiname, Länge und SHA-256-Hash vor jeder Änderung.

## 5. NodePilot installieren

Es gibt zwei Wege zur selben Installation.

### Variante A: GUI-Setup

`NodePilot-Server-Setup-<version>.exe` aus dem Release herunterladen und ausführen. Es bringt das
signierte Artefakt und die ASP.NET-Core-Runtime mit, prüft sämtliche Voraussetzungen aus den
Kapiteln 1 bis 4 **bevor** es etwas verändert, und zeigt jede als grün, gelb oder rot mit
kopierbarer Anleitung. Auf Wunsch installiert es die Runtime, legt SQL-Login und Datenbank an oder
erzeugt ein Laborzertifikat.

Dazu gehört die Vertrauensfrage aus Kapitel 4 — allerdings als **gelbe, optionale** Zeile: Das
Setup nennt den Thumbprint des Herausgebers `CN=NodePilot Release Signing` und bietet den Import
nach `LocalMachine\Root` an, angeboten und nicht vorangehakt. Die Installation braucht ihn nicht;
sie verifiziert die Signatur gegen den einkompilierten Thumbprint. Der Import bewirkt nur, dass
Windows die Authenticode-Signatur der Installer selbst validiert. **Rot** wird die Zeile dagegen
bei allem, was der Installer selbst ablehnt: falscher Thumbprint, abgelaufenes oder noch nicht
gültiges Zertifikat, fehlende Codesignatur-Eignung.

Für das Kestrel-Zertifikat verlangt es nur den Thumbprint — und bietet unter dem Eingabefeld die
Zertifikate aus `Cert:\LocalMachine\My` zur Auswahl an, sortiert nach Ablauf. Das gilt für ein
PKI-Zertifikat aus der eigenen CA genauso wie für ein selbstsigniertes: importieren, auswählen,
fertig. Ein Zertifikat ohne privaten Schlüssel steht mit entsprechender Markierung in der Liste,
statt kommentarlos zu fehlen.

Wer noch gar keins hat, lässt das Feld **leer**. Die Prüfseite meldet dann „No certificate
selected" und bietet an, ein selbstsigniertes zu erzeugen — angeboten, nicht vorangehakt, denn ein
Laborzertifikat entsteht auf Ansage. Es gilt zwei Jahre und wird bewusst **nicht** in den
Root-Store importiert; für Produktion bleibt ein Zertifikat aus der eigenen PKI der Weg.
Unbeaufsichtigt entspricht das einem leeren `certificate.thumbprint` plus
`"provisioning": { "generateSelfSignedCertificate": true }`.

Die Abschlussseite zeigt alles, was für den ersten Zugriff nötig ist: Adresse, Setup-Token für die
erste Anmeldung, External-Trigger-API-Key, Zertifikats-Thumbprint sowie Dienstname und Pfade. Der
API-Key erscheint **nur dort** — er ist danach nicht mehr rekonstruierbar. Der Text ist markierbar,
und „Save this summary…" legt ihn als Datei ab.

**Schlüsselfertig ohne Token-Eingabe.** Zwei Wege, die sich gegenseitig ausschließen:

- **`bootstrap`-Gruppe mit `adminUsername`** — das Setup legt den ersten Administrator selbst an.
  Kennwort pro Maschine zufällig erzeugt und in einer ACL-geschützten Datei unter
  `<DataPath>\bootstrap-admin.json` hinterlegt (nur SYSTEM und Administratoren). Feste
  Standard-Zugangsdaten gibt es bewusst nicht: sie wären über alle Maschinen gleich und würden
  gefunden statt geraten.
- **`seed`-Gruppe mit `backupPath` und `passphrase`** — eine Referenzmaschine einmal einrichten,
  `np backup export`, und jede weitere Installation spielt diesen Stand beim ersten Start ein:
  Benutzer, Workflows, Maschinen, Credentials und Einstellungen. Dann entsteht gar kein Token. Die
  Passphrase landet im Dienstschlüssel, nie in der Konfigurationsdatei; die Seed-Datei wird nach dem
  Einspielen gelöscht.

Der Seed gewinnt: bringt er Benutzer mit, gibt es nichts einzulösen. Er füllt außerdem **nur** eine
leere Instanz — eine Maschine im Betrieb behält alles, was sie hat. Und er ist fail-closed: eine
falsche Passphrase lässt den Dienst nicht starten, statt eine scheinbar provisionierte, in Wahrheit
leere Instanz zu hinterlassen.

Unbeaufsichtigt für SCCM oder GPO:

```powershell
NodePilot-Server-Setup-1.2.0.exe /VERYSILENT /SUPPRESSMSGBOXES /ANSWERFILE=C:\prod\answers.json
```

Ein erneuter Lauf erkennt eine vorhandene Installation und bietet drei Wege an: per Default ein
**Update**, alternativ **neu aufsetzen** — das erzeugt allerdings einen **neuen
External-Trigger-API-Key**, der alte ist nicht rekonstruierbar — oder **entfernen**. Die dritte
Option übergibt an denselben Uninstaller, den auch „Apps & Features" startet; sie fragt nichts
doppelt, sondern lässt ihn seine eine Frage stellen: Datenverzeichnis behalten oder löschen. Die
**Datenbank bleibt in jedem Fall unberührt** — dieses Setup hat sie nicht angelegt und entfernt sie
nicht. `/FULLREINSTALL` überspringt die Auswahl und setzt direkt neu auf. Schema der Antwortdatei,
Schalter und Exit-Codes stehen in `deploy/server/README.md`.

### Variante B: Skripte

Der Weg, den das Setup intern selbst geht, und der richtige für Automatisierung. Die
Installationsbefehle laufen als lokaler Administrator auf dem Zielserver.

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
6. Dateisystem- und Zertifikats-ACLs setzen. Das Installationsverzeichnis bekommt dabei eine geschützte ACL: SYSTEM und Administratoren `FullControl`, das Dienstkonto nur `ReadAndExecute` — es führt die Binaries aus, es überschreibt sie nie. Ein abweichender `-InstallPath` wird vorher geprüft (lokal, NTFS oder ReFS, keine Junctions im Pfad) und nach dem Kopieren erneut verifiziert; sonst würde er die Rechte seines übergeordneten Verzeichnisses erben, was auf einem eigenen Volume schreibenden Zugriff für alle Benutzer bedeuten kann.
7. HTTPS-Firewallregel anlegen.
8. Windows-Dienst mit Auto Start und Recovery Actions registrieren (bei einem gMSA zusätzlich abhängig von Netlogon, damit der Logon nicht vor dem DC-Kontakt scheitert). Der Dienst startet also ohne feste Verzögerung und wartet stattdessen selbst auf die Datenbank — Obergrenze `Database:StartupWaitSeconds`, Standard 120 Sekunden.
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

### Verhalten bei Datenbank-Ausfall im laufenden Betrieb

Bei einem Laufzeit-Ausfall bleibt der Dienst oben; DB-abhängige HTTP-Aufrufe antworten schnell mit
dem gemeinsamen `503 DATABASE_UNAVAILABLE`-Vertrag, SignalR-Aufrufe mit demselben Fehlercode. Die UI
bleibt erreichbar, zeigt Banner und Status-Ampel und verbindet sich nach erfolgreicher
`SELECT 1`-Recovery automatisch neu — inklusive SignalR-Gruppen, ohne Dienst-Neustart.
`RejectedByServer` bedeutet dagegen falsche
Zugangsdaten, Datenbankauswahl oder TLS-Konfiguration (`retryable: false`): Hier muss ein
Administrator die Konfiguration korrigieren. Wurden NodePilot-Verbindungsdaten geändert, ist danach
ein Dienst-Neustart erforderlich; serverseitige Korrekturen erkennt die Sonde automatisch.

| Endpoint | Verhalten im Ausfall |
|---|---|
| `/healthz/live` | HTTP 200; kein Prozess-Restart |
| `/healthz/ready` | schneller HTTP 503; Traffic wegrouten |
| `/healthz/database` | HTTP 200 mit `status` und `reason`; Statusbericht statt Gate |

Laufende Workflows warten an der dauerhaften Step-Grenze. Nur von aktiven Trigger-Sources beobachtete
Fires werden gezählt verworfen und nicht nachgeholt. Notifications sind at-least-once; Empfänger
sollten mit `eventKey` aus JSON beziehungsweise `X-NodePilot-Event-Key` deduplizieren. Timeout- und
Probe-Defaults stehen unter [Datenbank-Provider](../configuration/database), das genaue HTTP-Schema
unter [API-Endpunkte](../api/endpoints#health).

## 8. Ersten Admin-Account anlegen

Der Installer zeigt den einmaligen Setup-Token aus `C:\ProgramData\NodePilot\admin-setup.token` an. Im Browser mit Wunsch-Benutzername und Passwort anmelden: Beim ersten Versuch blendet die Login-Seite ein **Setup-Token-Feld** ein — Token einfügen, erneut anmelden. Danach ist das Admin-Konto angelegt und der Token gelöscht.

Konnte der Installer den Token nicht anzeigen: Die Datei ist per Owner-only-ACL auf das Dienstkonto beschränkt (auch für Administratoren nicht direkt lesbar — Absicht). Ohne ACL-Änderung per Backup-Semantik lesen:

```powershell
robocopy C:\ProgramData\NodePilot $env:TEMP admin-setup.token /B | Out-Null
Get-Content "$env:TEMP\admin-setup.token"
Remove-Item "$env:TEMP\admin-setup.token"   # nach dem ersten Login
```

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
- wartet nach dem Dienststopp bis zu 30 Sekunden auf das Ende von Prozessen aus dem Installationsverzeichnis und beendet Verbliebene erzwungen; nur wenn das nicht greift, bricht der Updater mit Prozessname und PID **vor der ersten Dateilöschung** ab (ein gestoppter Dienst genügt nicht: verwaiste Worker halten ihre DLLs weiterhin gemappt),
- erhält Datenbank, Dienstkonto und Produktionskonfiguration,
- startet den Dienst neu,
- prüft den Health-Endpunkt auf dem Port aus der installierten Konfiguration (`-HttpsPort` ist nur zum Überschreiben nötig),
- stellt bei einem fehlgeschlagenen Health-Check die vorherigen Binaries wieder her und lässt den Dienst im Zustand vor dem Update.

Ein **erfolgreicher** Update lässt den Dienst immer **laufen**, unabhängig davon, ob er vorher gestoppt war. Nur ein fehlgeschlagener Update stellt den Ausgangszustand wieder her.

Workflow-History wird beim Start absichtlich noch nicht umgeschlüsselt, damit der unmittelbare
Health-Check-Rollback sicher bleibt. Während eines gemischten HA-Upgrades Workflow-Edits und
Rollbacks pausieren (oder nach dem ersten Schreibzugriff eines neuen Knotens keinen Failback auf
alte Knoten mehr zulassen): neue Knoten schreiben neue History-Snapshots sofort als `np:wfv:v1:`,
das alte Binary kann diese Zeilen nicht lesen. Sobald der Health-Check erfolgreich war und **alle
HA-Knoten** die neue Version ausführen, nach dem regulären DB-Backup
`np secrets reencrypt --yes` (oder die Aktion unter Admin-Einstellungen → Security) ausführen.
Danach sind auch alte `WorkflowVersions` als `np:wfv:v1:` geschützt. Nach dem ersten neuen
History-Write oder diesem Cutover darf nicht mehr auf eine Binärversion ohne Format-Unterstützung
zurückgerollt werden.

Das Binärbackup enthält keine secret-haltige `appsettings.Production.json`. Sie wird beim Austausch deshalb als Letztes ersetzt, damit ein Abbruch sie nicht zerstört.

## Deinstallation

```powershell
.\deploy\Uninstall-NodePilot.ps1
```

Entfernt werden Dienst, Dienst-Binaries, Firewall-Regeln, der Installations-Marker und der
Registry-Environment-Eintrag (in dem das Postgres-Passwort liegt). Logs und Konfiguration bleiben
erhalten. Vollständige Entfernung der lokalen Betriebsdaten:

```powershell
.\deploy\Uninstall-NodePilot.ps1 -PurgeData
```

Nach einer Installation über das GUI-Setup geht dasselbe über „Apps & Features" oder direkt:

```powershell
& 'C:\Program Files\NodePilot\unins000.exe' /VERYSILENT /SUPPRESSMSGBOXES /PURGEDATA=1
```

**Die Datenbank wird nie entfernt, und es gibt dafür keine Option.** NodePilot legt sie nicht an —
sie wurde in Kapitel 2 separat bereitgestellt, hat oft ein eigenes Backup- und
Replikationsregime, und in einem Active/Passive-Cluster teilen sich beide Knoten dieselbe. Aus
demselben Grund bleiben das „Log on as a service"-Recht des gMSA und die Lese-ACE auf dem Private
Key des TLS-Zertifikats stehen; beide können mit einem anderen Dienst geteilt sein. Der
Uninstaller benennt alle drei am Ende seines Laufs.

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
