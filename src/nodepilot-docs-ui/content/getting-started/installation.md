# Installation

Diese Anleitung führt dich in ca. 10 Minuten zu einem laufenden NodePilot. Du brauchst nur eine Windows-Maschine und Admin-Rechte zum Installieren der Voraussetzungen.

NodePilot ist **Windows-only** (.NET 10, `net10.0-windows`) und besteht aus drei Komponenten: **PostgreSQL** (Datenbank), **Backend** (ASP.NET Core API, Port 5000) und **Frontend** (React SPA, Port 5173).

> **Reihenfolge zählt:** Erst PostgreSQL starten, **dann** das Backend. Das Backend bricht beim Boot ab, wenn die Datenbank nicht erreichbar ist.

## Voraussetzungen

Installiere diese drei Dinge einmalig (je ein Befehl, oder per grafischem Installer von den Hersteller-Seiten):

| Komponente | Winget-Aufruf (PowerShell/CMD) | Check danach |
|---|---|---|
| .NET 10 SDK | `winget install Microsoft.DotNet.SDK.10` | `dotnet --version` |
| Node.js 20+ | `winget install OpenJS.NodeJS.LTS` | `node -v` |
| PostgreSQL 16+ | `winget install PostgreSQL.PostgreSQL` | `"C:\Program Files\PostgreSQL\16\bin\pg_ctl.exe" --version` |

> Paket-IDs können sich ändern — notfalls per `winget search dotnet` / `winget search node` / `winget search postgres` die aktuelle ID suchen. Alternativ die Installer von <https://dotnet.microsoft.com/download>, <https://nodejs.org> und <https://www.postgresql.org/download/windows> holen.

Lade das NodePilot-Repo auf die Maschine und entpacke es, falls noch nicht geschehen:

```powershell
git clone https://github.com/Sev7eNup/NodePilot.git
cd NodePilot
```

(ab hier laufen alle Befehle aus dem Repo-Root, also dem Ordner, in dem `src\` liegt)

## 1. PostgreSQL starten & anlegen

Wenn du PostgreSQL gerade frisch installiert hast, existieren noch keine NodePilot-Datenbank und kein Benutzer. Lege beides einmalig an — mit der `psql`-Konsole, die mit PostgreSQL mitkommt:

**PowerShell**

```powershell
$pg = "C:\Program Files\PostgreSQL\16\bin\psql.exe"
& $pg -U postgres -c "CREATE ROLE nodepilot WITH LOGIN PASSWORD 'ChangeMe!';"
& $pg -U postgres -c "CREATE DATABASE nodepilot OWNER nodepilot;"
```

**CMD**

```cmd
set PG="C:\Program Files\PostgreSQL\16\bin\psql.exe"
%PG% -U postgres -c "CREATE ROLE nodepilot WITH LOGIN PASSWORD 'ChangeMe!';"
%PG% -U postgres -c "CREATE DATABASE nodepilot OWNER nodepilot;"
```

> `ChangeMe!` ist dein DB-Passwort — notiere es, du brauchst es im nächsten Schritt. Falls `psql` nach einem Passwort für `postgres` fragt, gib das Passwort an, das du während der PostgreSQL-Installation vergeben hast.

PostgreSQL läuft nach der Installation i.d.R. bereits als Windows-Dienst. Wer ein lokales Dev-Cluster von Hand startet (Beispielpfad aus dem Repo):

```powershell
& 'C:\NodePilot-Postgres\pgsql\bin\pg_ctl.exe' start -D 'C:\NodePilot-Postgres\data' -l 'C:\NodePilot-Postgres\data\postgres.log' -w
```

## 2. Verbindung konfigurieren

Das Backend braucht die Verbindungszeichenkette zur Datenbank. Am einfachsten als Umgebungsvariable im selben Shell-Fenster, bevor du das Backend startest (so musst du keine Config-Datei anfassen):

**PowerShell**

```powershell
$env:ConnectionStrings__Postgres = "Host=127.0.0.1;Port=5432;Database=nodepilot;Username=nodepilot;Password=ChangeMe!"
```

**CMD**

```cmd
set ConnectionStrings__Postgres=Host=127.0.0.1;Port=5432;Database=nodepilot;Username=nodepilot;Password=ChangeMe!
```

> `__` (Doppel-Unterstrich) trennt Config-Sektionen in Umgebungsvariablen — `ConnectionStrings__Postgres` mappt auf `ConnectionStrings:Postgres`. Wer es lieber fest einträgt: in `src\NodePilot.Api\appsettings.json` den `Postgres`-Wert setzen (Passwort dann zwingend ergänzen).

## 3. Backend starten

Aus dem Repo-Root, im selben Fenster, in dem du die Umgebungsvariable gesetzt hast:

**PowerShell**

```powershell
cd src\NodePilot.Api
dotnet run --urls "http://localhost:5000"
```

**CMD**

```cmd
cd src\NodePilot.Api
dotnet run --urls "http://localhost:5000"
```

Der erste Start dauert länger (Restore + Build). Wenn `Now listening on: http://localhost:5000` erscheint, läuft die API. Das Datenbankschema wird beim ersten Start automatisch per EF-Migration angelegt — du musst nichts weiter tun.

Test:

```powershell
curl http://localhost:5000/healthz/live   # → Healthy
```

## 4. Frontend starten

Öffne ein **zweites** Terminal-Fenster (das Backend-Fenster muss offen bleiben), im Repo-Root:

**PowerShell**

```powershell
cd src\nodepilot-ui
npm install
npm run dev
```

**CMD**

```cmd
cd src\nodepilot-ui
npm install
npm run dev
```

`npm install` einmalig; danach reicht `npm run dev`. Vite startet auf **http://localhost:5173** und leitet API-Aufrufe ans Backend auf `localhost:5000` weiter.

## 5. Erster Login

Im Browser **http://localhost:5173** öffnen. Bei leerer Datenbank führt die UI dich durch die Einrichtung des ersten Admin-Accounts. Den dazu nötigen One-Shot-Token legt das Backend beim ersten Start in `src\NodePilot.Api\admin-setup.token` ab — die UI nutzt ihn automatisch, du musst ihn nicht von Hand eintragen.

Danach stehen die Rollen **Admin / Operator / Viewer** zur Verfügung. Für eine schnelle Dev-Übung kannst du als Admin `admin` / `admin123` verwenden (falls du dieses Passwort beim Setup vergibst).

## Häufige Probleme

| Symptom | Ursache & Lösung |
|---|---|
| Backend bricht sofort beim Boot ab | PostgreSQL nicht erreichbar. Erst DB starten/prüfen (`curl` auf `/healthz/live` schlägt fehl, solange die API nicht läuft), dann Backend neu starten. |
| `Authenticating to ... failed` im Backend-Log | Falsches DB-Passwort. `ConnectionStrings__Postgres` erneut setzen (achte auf `ChangeMe!`) und Backend neu starten. |
| Port 5000 ist bereits belegt | Anderer Prozess horcht auf 5000. PID finden und beenden: `Get-NetTCPConnection -LocalPort 5000` (PowerShell) → `Stop-Process -Id <PID>`, dann neu `dotnet run`. |
| `MSB3027`/Datei gesperrt beim Rebuild | DLL-Lock durch die laufende API. Ist normal — API stoppen, neu bauen, neu starten. |
| Port 5173 belegt / Frontend startet nicht | Meist fehlendes `npm install`. Wenn kaputt: `node_modules` löschen und erneut `npm install`. Alternativer Port wird von Vite automatisch gewählt (Meldung im Terminal beachten). |

## Produktivbetrieb

Das obige Setup ist der Dev/Quick-Path. Für einen produktiven Rollout als Windows-Service (gMSA oder LocalSystem, Kestrel-HTTPS, ACLs, Firewall, Zertifikate) liegen fertige Installer-Skripte unter `deploy\` (`Build-Artifact.ps1`, `Install-NodePilot.ps1`, `Update-NodePilot.ps1`, `Uninstall-NodePilot.ps1`). Der Installer braucht zwingend mehrere Parameter (Artifact-Pfad, Zertifikat-Thumbprint, DB-Host/User/Passwort als SecureString) — die komplette Aufrufsyntax, Parameterliste und Stolperfallen stehen in [Deployment → Produktions-Rollout](../deployment/production) sowie in `deploy\README.md`. Fürs Dev-Setup brauchst du diese Skripte **nicht**.