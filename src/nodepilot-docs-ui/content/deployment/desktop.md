# Desktop-App (Electron)

NodePilot als **lokale Desktop-App** für **Windows 11 x64**: ein signierter `.exe`-Installer, der App, gebündelte .NET-10-Runtime und einen **lokalen PostgreSQL**-Server mitbringt und alles als Hintergrund-Dienste betreibt — **offline**, ohne Prerequisites. Vollständige Doku im Repo unter `deploy/desktop/README.md`; die `deploy/`-Skripte werden im Dev-Mode **nicht** ausgeführt.

Abgrenzung zum Server-Rollout (`deploy/README.md`): dort domain-joined Windows **Server** als Dienst hinter Kestrel-TLS mit **externer** DB. Hier eine Maschine-mit-sich-selbst.

## Topologie

- **Dienst `NodePilotDb`** — gebündeltes PostgreSQL 16 als **NetworkService**, gebunden an `127.0.0.1`, Boot-Start.
- **Dienst `NodePilot`** — self-contained `NodePilot.Api.exe` als **LocalSystem**, `https://127.0.0.1:<port>`, Boot-Start, `depend= NodePilotDb`.
- **Electron-Shell** (Chromium + Node, in voller Größe gebündelt, kein WebView2) — dünner Viewer, lädt die vom Backend **same-origin** ausgelieferte SPA. Kein Auto-Update; Updates laufen über einen neuen signierten Installer.

Weil NodePilot ein **Orchestrator** ist (Schedule-/FileWatcher-/Webhook-Trigger), laufen die Dienste im Hintergrund weiter, auch wenn das Fenster geschlossen ist.

## `Deployment:Mode=Desktop`

Die Desktop-App läuft mit `ASPNETCORE_ENVIRONMENT=Production` (volle Härtung: Security-Header, Swagger aus, Inline-Password-Guard) plus der neuen Posture `Deployment:Mode=Desktop`. Desktop relaxiert **nur** das Maschine-mit-sich-selbst-Sinnvolle:

- `Database:AllowInsecureTls=true` wird **nur** bei Loopback-DB **und** Desktop akzeptiert (127.0.0.1-Postgres ohne PKI). Remote-Hosts bleiben fail-closed.
- Kestrel bindet **nur Loopback** (`ListenLocalhost`).
- Vor dem Migration-Bootstrap wartet die API bis zu **120 s** auf Postgres-Konnektivität (nur Erreichbarkeit wird retried).

Default ist `Server`; ein unbekannter Wert ist ein Boot-Fehler.

## Handoff: desktop.json

`%ProgramData%\NodePilot\desktop.json` (kein Secret) sagt der Shell, was zu laden/vertrauen ist:

```json
{ "schemaVersion": 1, "origin": "https://localhost:47000",
  "certificateSha256": "<hex>", "serviceName": "NodePilot" }
```

Das DB-Passwort steht ausschließlich im ACL-geschützten Service-Env `ConnectionStrings__Postgres`.

## Sicherheit

- **API als LocalSystem** → lokale (`localhost`) `runScript`-Activities laufen mit **SYSTEM**-Rechten (bewusste v1-Entscheidung).
- **Loopback-TLS per Pinning statt Root-CA:** self-signed `localhost`-Cert in `LocalMachine\My`, von der Electron-Session per SHA-256 gepinnt. Kein systemweiter Trust — normaler Browserzugriff **darf warnen**; Electron ist der unterstützte Zugang.
- **Electron-Härtung:** SPA-Fenster mit `contextIsolation`/`sandbox`/`webSecurity`, ohne `nodeIntegration`, **ohne Preload/IPC**; Navigation off-origin, Popups, Downloads und Berechtigungen blockiert.
- **Erststart-Token nie im Renderer:** der elevierte Installer legt den One-Shot-Token als ACL-geschützte Handoff-Datei ins Profil des installierenden Users; die Setupseite hat nur `completeAdminSetup({username,password})`, der Main-Prozess sendet den Token als `X-Setup-Token` an `/api/auth/login`, teilt die Cookies und löscht beide Token-Kopien.

## Build / Install / Update / Uninstall

```powershell
./Build-DesktopInstaller.ps1 -PgBinariesPath 'C:\pfad\pgsql' -Version 1.0.0
# -> out\NodePilot-Desktop-Setup-1.0.0.exe  (vor Verteilung Authenticode-signieren)
```

- **Install:** `.exe` als lokaler Admin (UAC) → Dateien + `Provision-LocalDb.ps1` (Cluster/Dienste/Cert/Config/Handoff) + Shell-Start.
- **Update:** neuer Installer → ACL-geschütztes `pg_dump`, Binär-Swap, idempotentes Re-Provision; `Update-Desktop.ps1` bietet zusätzlich ein voll-transaktionales Update mit Rollback (Binaries + Config + DB). Keine PG-Major-Upgrades in v1.
- **Uninstall:** Dienste + Cert weg; **ProgramData/`pgdata` bleiben** (außer `-PurgeData`).

Voraussetzungen für den Build: .NET-10-SDK, Node/npm, Inno Setup 6, ein PostgreSQL-16-`pgsql`-Verzeichnis.
