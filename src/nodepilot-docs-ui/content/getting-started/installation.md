# Installation

NodePilot kann als Desktop-App, als Windows-Server-Dienst oder direkt aus dem Quellcode installiert werden. Die Auswahl hängt davon ab, ob ein lokaler Einzelplatz, ein zentraler Team-Server oder eine Entwicklungsumgebung benötigt wird.

## Installationsart auswählen

| Anforderung | Installation | Ergebnis |
|---|---|---|
| NodePilot lokal auf einem Windows-11-System verwenden | **Desktop-App** | Electron-App mit eigener PostgreSQL-Datenbank und Hintergrunddiensten |
| NodePilot zentral für mehrere Personen betreiben | **Windows-Server** | Windows-Dienst mit HTTPS und externer Datenbank |
| NodePilot entwickeln oder aus dem Quellcode testen | **Installation aus Quellcode** | PostgreSQL, API und React-Oberfläche als getrennte Entwicklungsprozesse |

Für den schnellsten lokalen Einstieg ist die Desktop-App vorgesehen. Für Netzwerkzugriff, Webhooks, zentrale Anmeldung oder Hochverfügbarkeit ist das Windows-Server-Deployment erforderlich.

## Variante 1: Desktop-App

Die Desktop-App richtet alle benötigten Komponenten auf einem Windows-11-x64-System ein:

- NodePilot API als Windows-Dienst
- PostgreSQL 16 als lokaler Windows-Dienst
- Produktoberfläche in einer Electron-Shell
- lokale HTTPS-Verbindung mit Zertifikat-Pinning

Der Zugriff ist ausschließlich auf dem installierten System möglich. Eingehende Webhooks, externe API-Clients, zentrale Anmeldung und Hochverfügbarkeit stehen in dieser Betriebsart nicht zur Verfügung.

### Installer beziehen

`NodePilot-Desktop-Setup-<version>.exe` liegt als Asset am [aktuellen Release](https://github.com/Sev7eNup/NodePilot/releases/latest). Herunterladen, gegen `SHA256SUMS.txt` prüfen, ausführen — der Installer richtet Datenbank, Zertifikat und beide Dienste ein und übergibt das Setup-Token direkt an die Anmeldemaske.

Alternativ selbst bauen: `deploy\desktop\Build-DesktopInstaller.ps1` benötigt zusätzlich Inno Setup 6 und die PostgreSQL-16-Binaries. Eine selbst gebaute `.exe` ist **unsigniert** und wird von SmartScreen angemeldet, bis sie mit einem eigenen Authenticode-Zertifikat signiert wird.

Build-Voraussetzungen, vollständiger Befehl, Installation, Update und Deinstallation stehen unter [Desktop-App](../deployment/desktop).

## Variante 2: Windows-Server

Das Windows-Server-Deployment ist für den zentralen Produktivbetrieb vorgesehen.

Unterstützte Kombinationen:

- SQL Server 2022 oder PostgreSQL 16+
- LocalSystem oder gMSA als Dienstidentität
- Single-Node oder Active/Passive-Cluster
- Kestrel-HTTPS mit Zertifikat aus `LocalMachine\My`

Es gibt zwei Wege zur selben Installation.

### GUI-Setup

`NodePilot-Server-Setup-<version>.exe` liegt als Asset am [aktuellen Release](https://github.com/Sev7eNup/NodePilot/releases/latest). Es bringt das signierte Artefakt und die ASP.NET-Core-Runtime mit und prüft sämtliche Voraussetzungen, **bevor** es etwas verändert. Auf Wunsch legt es SQL-Login und Datenbank beziehungsweise PostgreSQL-Rolle und -Datenbank selbst an; das Kestrel-Zertifikat wird aus einer Liste der Zertifikate in `Cert:\LocalMachine\My` ausgewählt statt als Thumbprint eingetippt. Unbeaufsichtigt für SCCM oder GPO: `NodePilot-Server-Setup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /ANSWERFILE=answers.json`.

Das ist der kürzeste Weg: eine Datei statt fünf, und kein manueller Abgleich des Publisher-Thumbprints.

### PowerShell-Skripte

Dasselbe, was das Setup ausführt, und für Automatisierung der direktere Weg: Das Repository erzeugt ein signiertes ZIP-Artefakt, `deploy\Install-NodePilot.ps1` installiert daraus den Windows-Dienst, setzt ACLs und Firewallregeln und prüft den Health-Endpunkt.

Voraussetzungen und vollständige Installationsbefehle für beide Wege stehen unter [Windows-Server-Deployment](../deployment/production).

## Variante 3: Installation aus Quellcode

Diese Variante startet Datenbank, Backend und Produktoberfläche getrennt und dient der Entwicklung sowie technischen Tests. Für den dauerhaften Produktivbetrieb sind Desktop-App oder Windows-Server vorgesehen.

### Ergebnis

Nach Abschluss laufen folgende Komponenten:

| Komponente | Adresse |
|---|---|
| PostgreSQL | `127.0.0.1:5432` |
| NodePilot API | `http://localhost:5000` |
| Produktoberfläche | `http://localhost:5173` |

Die Produktoberfläche leitet API-, Health- und SignalR-Aufrufe an Port 5000 weiter.

### Voraussetzungen

- Windows
- Git
- .NET 10 SDK — das akzeptierte SDK-Band steht in `global.json`
- Node.js — die Mindestversion ist im `engines`-Feld der `package.json`-Dateien deklariert (react-router 8 setzt die Untergrenze); `npm` warnt bei einer älteren Version
- PostgreSQL 16 oder neuer
- Lokale Administratorrechte für die Installation der Voraussetzungen

Beispielinstallation mit `winget`:

```powershell
winget install Microsoft.DotNet.SDK.10
winget install OpenJS.NodeJS.LTS
winget install PostgreSQL.PostgreSQL
```

Prüfung:

```powershell
git --version
dotnet --version
node --version
npm --version
& "C:\Program Files\PostgreSQL\16\bin\psql.exe" --version
```

Falls eine Paket-ID nicht verfügbar ist, kann `winget search <name>` die aktuelle ID ermitteln. Alternativ stehen die Installationspakete bei den jeweiligen Herstellern bereit.

### 1. Repository bereitstellen

```powershell
git clone https://github.com/Sev7eNup/NodePilot.git
Set-Location NodePilot
```

Alle weiteren Befehle verwenden den Repository-Root als Ausgangspunkt.

### 2. PostgreSQL-Datenbank anlegen

Ein Entwicklungsbenutzer und eine leere Datenbank werden einmalig angelegt:

```powershell
$pgClient = "C:\Program Files\PostgreSQL\16\bin\psql.exe"
& $pgClient -U postgres -c "CREATE ROLE nodepilot WITH LOGIN PASSWORD 'ChangeMe!';"
& $pgClient -U postgres -c "CREATE DATABASE nodepilot OWNER nodepilot;"
```

`ChangeMe!` ist ausschließlich ein lokaler Beispielwert. Für gemeinsam genutzte oder erreichbare Datenbanken ist ein eigenes starkes Passwort erforderlich.

PostgreSQL läuft nach der Standardinstallation als Windows-Dienst. Der Dienststatus lässt sich wie folgt prüfen:

```powershell
Get-Service -Name "postgresql*"
```

### 3. Datenbankverbindung konfigurieren

Die Verbindungszeichenkette wird im Terminal gesetzt, in dem anschließend das Backend startet:

```powershell
$env:ConnectionStrings__Postgres = "Host=127.0.0.1;Port=5432;Database=nodepilot;Username=nodepilot;Password=ChangeMe!"
```

Der doppelte Unterstrich bildet die .NET-Konfiguration `ConnectionStrings:Postgres` ab. Die Umgebungsvariable gilt nur für das aktuelle Terminal und vermeidet ein Passwort in einer Repository-Datei.

### 4. Backend starten

Im selben Terminal:

```powershell
Set-Location src\NodePilot.Api
dotnet run --urls "http://localhost:5000"
```

Der erste Start führt Paket-Restore, Build und Datenbankmigrationen aus. Die API ist bereit, sobald folgende Meldung erscheint:

```text
Now listening on: http://localhost:5000
```

Health-Prüfung in einem zweiten Terminal:

```powershell
Invoke-RestMethod http://localhost:5000/healthz/live
Invoke-RestMethod http://localhost:5000/healthz/ready
```

`live` bestätigt den laufenden Prozess. `ready` bestätigt zusätzlich die erreichbare Datenbank.

### 5. Produktoberfläche starten

In einem zweiten Terminal aus dem Repository-Root:

```powershell
Set-Location src\nodepilot-ui
npm install
npm run dev
```

`npm install` ist nach Änderungen an `package-lock.json` erneut erforderlich. Vite startet standardmäßig unter `http://localhost:5173`.

### 6. Ersten Admin-Account anlegen

1. `http://localhost:5173` im Browser öffnen.
2. Wunsch-Benutzername und Passwort eingeben und anmelden — beim ersten Versuch blendet die Login-Seite ein **Setup-Token-Feld** ein.
3. Token aus `src\NodePilot.Api\admin-setup.token` einfügen und erneut anmelden.

Bei einer leeren Datenbank erzeugt das Backend diese Token-Datei beim Start. Nach erfolgreichem Setup wird sie gelöscht. Es existiert kein voreingestelltes Konto.

Der nächste Schritt ist der [Schnelleinstieg](./quickstart).

### Stoppen und erneut starten

- Frontend: `Ctrl+C` im Vite-Terminal
- Backend: `Ctrl+C` im API-Terminal
- Neustart: zuerst PostgreSQL prüfen, danach Backend und Frontend starten

Die Daten bleiben in PostgreSQL erhalten.

### Fehlerdiagnose

| Symptom | Prüfung | Lösung |
|---|---|---|
| Backend beendet sich beim Start | `/healthz/live` ist nicht erreichbar; Log enthält Datenbankfehler | PostgreSQL-Dienst und Verbindungszeichenkette prüfen |
| `password authentication failed` | Passwort in `ConnectionStrings__Postgres` stimmt nicht mit der Rolle überein | Passwort korrigieren oder Rolle in PostgreSQL ändern |
| Port 5000 ist belegt | `Get-NetTCPConnection -LocalPort 5000` | Belegenden Prozess beenden oder anderen API-Port konfigurieren |
| `MSB3027` beim Build | Laufender API-Prozess hält eine DLL geöffnet | API stoppen, Build erneut ausführen |
| Port 5173 ist belegt | `Get-NetTCPConnection -LocalPort 5173` | Prozess beenden oder den von Vite gemeldeten Ersatzport verwenden |
| Frontend-Abhängigkeiten fehlen | `npm run dev` meldet fehlende Module | `npm install` erneut ausführen |

### Grenzen der Quellcode-Installation

Die Quellcode-Installation besitzt keinen Windows-Dienst, keinen Autostart und keine produktive TLS-Konfiguration. Für produktive Systeme stehen die beiden oben beschriebenen Installationsvarianten bereit:

- [Windows-Server-Deployment](../deployment/production) für Teamzugriff, APIs, Webhooks und Hochverfügbarkeit
- [Desktop-App](../deployment/desktop) für einen lokalen Einzelplatz

Der Vergleich steht unter [Betriebsarten](../deployment/overview).
