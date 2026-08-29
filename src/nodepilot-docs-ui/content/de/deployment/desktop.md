# Desktop-App

Die Desktop-App ist ein lokales NodePilot-Paket für Windows 11 x64. Ein Installer richtet API, Produktoberfläche, Electron-Shell und PostgreSQL ein. Externe Laufzeiten, eine externe Datenbank und Internetzugriff sind während der Installation nicht erforderlich.

## Einsatzbereich

Geeignet für:

- produktive Automatisierung auf einem einzelnen Windows-System
- lokale Zeitplan-, Datei-, Datenbank- und Eventlog-Trigger
- ausgehende WinRM-, REST-, SQL-, SMTP- und Alerting-Verbindungen
- Offline-Installation

Nicht geeignet für:

- Zugriff von anderen Systemen
- eingehende Webhooks
- externe REST- oder Trigger-API
- LDAP, Windows SSO, OIDC oder SCIM
- Hochverfügbarkeit

## Laufzeitarchitektur

```text
Electron-Shell
      |
      | HTTPS auf localhost, Zertifikat gepinnt
      v
Windows-Dienst "NodePilot"       LocalSystem
      |
      v
Windows-Dienst "NodePilotDb"     NetworkService
      |
      v
PostgreSQL 16 auf 127.0.0.1
```

Die Dienste starten beim Systemstart. Das Schließen des Electron-Fensters beendet keine Workflows oder Trigger.

Stoppt oder hängt `NodePilotDb` im laufenden Betrieb, bleibt die API erreichbar und die Oberfläche
zeigt einen Datenbank-Ausfall. Datenbankzugriffe antworten schnell mit 503, Workflows warten an
dauerhaften Schrittgrenzen und der Betrieb wird nach erfolgreicher PostgreSQL-Prüfung automatisch
fortgesetzt. Diagnose: `/healthz/ready` und `/healthz/database`. Details und Timeout-Einstellungen:
[Datenbank-Provider](../configuration/database).

Fenster- und Infobereichssymbol übernehmen die Farbe des in der Oberfläche gewählten Skins. Das Symbol der Programmdatei, des Installers und des Startmenüeintrags bleibt beim blauen Standard, da Windows diese Symbole aus der Datei selbst auflöst.

## Installierte Pfade

| Pfad | Inhalt |
|---|---|
| `C:\Program Files\NodePilot\app` | Self-contained API und Produktoberfläche |
| `C:\Program Files\NodePilot\desktop` | Electron-Shell |
| `C:\Program Files\NodePilot\pgsql` | PostgreSQL-Serverruntime |
| `C:\Program Files\NodePilot\tools\np` | `np` CLI (Operations-CLI, kein PATH-Eintrag beim Desktop-Paket) |
| `C:\Program Files\NodePilot\tools\mcp` | `nodepilot-mcp` — MCP-Server für KI-Agenten |
| `C:\ProgramData\NodePilot\pgdata` | PostgreSQL-Daten |
| `C:\ProgramData\NodePilot\logs` | Anwendungslogs |
| `C:\ProgramData\NodePilot\backups` | Update-Backups |
| `C:\ProgramData\NodePilot\desktop.json` | Verbindung zwischen Shell und Backend |

## Sicherheitsmodell

`Deployment:Mode=Desktop` setzt folgende Regeln:

- Kestrel bindet ausschließlich an Loopback.
- Es wird keine eingehende Firewallregel angelegt.
- PostgreSQL bindet ausschließlich an `127.0.0.1`.
- Ein self-signed Zertifikat schützt die lokale HTTPS-Verbindung.
- Electron prüft den SHA-256-Fingerprint des Zertifikats.
- Das Zertifikat wird nicht als globale Root-CA installiert.
- Electron verwendet `contextIsolation`, `sandbox` und `webSecurity`.
- Node-Integration, Preload-Bridge, externe Navigation, Pop-ups, Downloads und Berechtigungsanfragen sind deaktiviert.

Ein normaler Browser kann für die lokale URL eine Zertifikatswarnung anzeigen. Der unterstützte Zugriff erfolgt über die Electron-Shell.

## Rechte lokaler und entfernter Ausführung

Die API läuft als LocalSystem. Daraus folgen zwei unterschiedliche Fälle:

| Activity-Ziel | Identität |
|---|---|
| Lokale `runScript`-Activity ohne Machine | `NT AUTHORITY\SYSTEM` |
| Remote-Ausführung mit Machine | Hinterlegtes Credential |

Credential-lose Kerberos-Delegation ist im Desktop-Modus nicht vorgesehen.

## Installer-Verfügbarkeit

`NodePilot-Desktop-Setup-<version>.exe` hängt als Asset am [aktuellen Release](https://github.com/Sev7eNup/NodePilot/releases/latest); die zugehörigen Prüfsummen stehen in `NodePilot-<version>.SHA256SUMS.txt`.

Auch der signierte Installer löst beim ersten Start SmartScreen aus, sobald er heruntergeladen wurde — das Zertifikat ist selbstsigniert und trägt keine Reputation. Erklärung und Vorgehen: [Beim ersten Start: das blaue SmartScreen-Fenster](./production#beim-ersten-start-das-blaue-smartscreen-fenster).

Der Installer bleibt daneben ein Build-Ziel des Repositorys — der Abschnitt unten beschreibt, wie er selbst erzeugt wird. Eine selbst gebaute `.exe` ist zusätzlich **unsigniert**, bis sie mit einem eigenen Authenticode-Zertifikat signiert wird.

## Installer bauen

### Voraussetzungen auf dem Build-System

- .NET 10 SDK
- Node.js und npm
- Inno Setup 6 mit `ISCC.exe`
- PostgreSQL-16-Binärverzeichnis `pgsql`
- Authenticode-Zertifikat für die Verteilung

Build aus `deploy\desktop`:

```powershell
Set-Location deploy\desktop
.\Build-DesktopInstaller.ps1 `
  -PgBinariesPath "C:\Packages\pgsql" `
  -Version 1.0.0
```

Ergebnis:

```text
out\NodePilot-Desktop-Setup-1.0.0.exe
```

Der Build:

1. veröffentlicht die API self-contained für `win-x64`,
2. veröffentlicht die Operator-Clients (`np`, `nodepilot-mcp`) self-contained nach `tools\np` und `tools\mcp`,
3. baut die React-Oberfläche,
4. kopiert erforderliche PowerShell-Module,
5. paketiert die Electron-Shell,
6. übernimmt den benötigten PostgreSQL-Teil,
7. erzeugt den Inno-Setup-Installer.

Der erzeugte Installer muss vor der Verteilung mit Authenticode signiert werden.

## Installation

1. Signierten Installer auf das Windows-11-Zielsystem übertragen.
2. Installer mit UAC-Bestätigung starten.
3. Provisionierung vollständig abschließen lassen.
4. Electron-Shell starten.
5. Lokalen Admin-Account im Setup-Dialog anlegen.

Der Installer:

- installiert Dateien,
- initialisiert PostgreSQL,
- registriert `NodePilotDb` und `NodePilot`,
- erzeugt das Loopback-Zertifikat,
- schreibt die Produktionskonfiguration,
- setzt ACLs,
- erstellt `desktop.json`,
- übergibt den einmaligen Setup-Token geschützt an die Electron-Shell.

Der Setup-Token geht in das Profil des **interaktiven** Benutzers — also des Benutzers, unter dem die Shell anschließend läuft. Das ist nicht zwingend derjenige, der den Installer startet: Der Installer läuft eleviert, und gibt ein Standardbenutzer am UAC-Dialog die Anmeldedaten eines *anderen* Administratorkontos ein, sind das zwei verschiedene Profile. Der Installer ermittelt den interaktiven Benutzer deshalb ausdrücklich, statt vom eigenen Profil auszugehen.

Scheitert die Provisionierung, meldet der Installer das mit einem Verweis auf sein Log unter `%TEMP%\nodepilot-provision.log` und startet die Shell nicht — statt eine erfolgreiche Installation zu melden, die anschließend nicht startet. Ursachen und Abhilfen stehen im [Desktop-Troubleshooting](https://github.com/Sev7eNup/NodePilot/blob/main/docs/desktop-troubleshooting.md), eine Übersicht aller Logdateien unter [Logs & Diagnose](logs).

## Dokumentation auf dem Gerät

Das Paket bringt diese Dokumentation mit; die API liefert sie unter `/docs` aus. Das Fragezeichen
in der Kopfzeile der Anwendung öffnet sie in einem eigenen Fenster — ohne Anmeldung und ohne
Internetzugang, passend zur installierten Version. Links aus der Dokumentation nach außen
(GitHub, Releases) öffnet die Shell im Systembrowser; sie stellt selbst keine fremden Inhalte dar.

Sie ist verfügbar, solange der Dienst `NodePilot` läuft — auch dann noch, wenn die Datenbank
gerade nicht erreichbar ist. Startet der Dienst gar nicht, hilft nur das Provisionierungslog.

## Installation prüfen

```powershell
Get-Service NodePilotDb, NodePilot
Get-Content "$env:ProgramData\NodePilot\desktop.json"
```

Erwartete Ergebnisse:

| Prüfung | Erwartung |
|---|---|
| `NodePilotDb` | `Running` |
| `NodePilot` | `Running` |
| Electron-Shell | Produktoberfläche ohne Zertifikatsdialog |
| Admin-Setup | Lokales Konto kann angelegt werden |
| Neustart | Beide Dienste starten automatisch |

Der Origin aus `desktop.json` kann lokal geprüft werden:

```powershell
$desktop = Get-Content "$env:ProgramData\NodePilot\desktop.json" | ConvertFrom-Json
Invoke-WebRequest "$($desktop.origin)/healthz/ready" -SkipCertificateCheck
```

`-SkipCertificateCheck` steht in PowerShell 7 zur Verfügung und ist hier nur für die lokale Diagnose des self-signed, durch Electron gepinnten Zertifikats vorgesehen.

## Update

Ein neuer signierter Installer kann über die bestehende Installation ausgeführt werden.

Update-Ablauf:

1. ACL-geschütztes `pg_dump` erstellen.
2. Dienste stoppen.
3. Binaries ersetzen.
4. Vorhandenes PostgreSQL-Datenverzeichnis weiterverwenden.
5. Dienste neu provisionieren.
6. Health-Endpunkt prüfen.

`Update-Desktop.ps1` bietet zusätzlich ein gestuftes Update mit Rollback für Binaries, Konfiguration und Datenbank.

PostgreSQL-Major-Upgrades und Electron-Auto-Update sind nicht Bestandteil der aktuellen Desktop-Version.

## Deinstallation

Die normale Deinstallation entfernt:

- beide Windows-Dienste,
- Loopback-Zertifikat,
- Dateien unter `C:\Program Files\NodePilot`.

Die Daten unter `C:\ProgramData\NodePilot`, einschließlich `pgdata`, bleiben standardmäßig erhalten.

Vollständige Entfernung:

Das Skript liegt in der Installation, nicht im aktuellen Verzeichnis, und `-InstallPath` ist ein
Pflichtparameter:

```powershell
& 'C:\Program Files\NodePilot\deploy\Uninstall-Desktop.ps1' `
    -InstallPath 'C:\Program Files\NodePilot' -PurgeData
```

`-PurgeData` löscht die lokale Datenbank und ist nicht rückgängig zu machen. Vorher ist ein Backup erforderlich.

## Backup und Systemwechsel

Für den Schutz der Konfiguration:

1. System-Configuration-Backup in NodePilot erstellen.
2. Backup-Datei und Passphrase getrennt sichern.
3. Für vollständige Historie zusätzlich PostgreSQL sichern.

Ein Kopieren von `pgdata` auf einen anderen Rechner ist kein unterstützter Migrationsweg. Credentials sind bei DPAPI-Nutzung an die Maschine gebunden. Der unterstützte Wechsel verwendet das System-Configuration-Backup, damit Secrets im Zielsystem neu verschlüsselt werden.

## Bekannte Grenzen

- Installer muss im Release-Prozess signiert werden.
- PostgreSQL-Major-Upgrades sind nicht automatisiert.
- Electron besitzt keinen Auto-Updater.
- Installation und Rollback benötigen einen Test auf einer sauberen Windows-11-VM.
- Desktop-Modus ist absichtlich auf einen lokalen Einzelplatz begrenzt.

Die vollständigen Build- und Validierungsdetails stehen in `deploy\desktop\README.md`.
