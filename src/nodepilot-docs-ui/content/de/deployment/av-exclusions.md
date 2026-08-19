# Antiviren-Ausschlüsse

NodePilot führt PowerShell aus und startet Prozesse. Beides kollidiert mit Standard-Heuristiken von Endpoint-Security-Produkten. Diese Seite listet die dafür erforderlichen Ausschlüsse, jeweils mit Begründung und Restrisiko, und ist als Übergabegrundlage für eine AV-Abteilung gedacht.

Betroffen sind die Betriebsarten [Windows-Server](./production) und [Desktop-App](./desktop). Auf den per WinRM orchestrierten Ziel-Maschinen wird keine NodePilot-Software installiert; sie sind nicht Gegenstand dieser Seite.

**Nicht gemeint ist SmartScreen.** Der blaue Dialog beim Start eines heruntergeladenen Installers kommt von einem getrennten Reputationsdienst, der Ausschlusslisten ignoriert — kein Eintrag dieser Seite beeinflusst ihn. Siehe [Beim ersten Start: das blaue SmartScreen-Fenster](./production#beim-ersten-start-das-blaue-smartscreen-fenster).

## Auslöser

| Verhalten | Betriebsart | Typische Reaktion des Scanners |
|---|---|---|
| Dienst unter LocalSystem schreibt `nodepilot_<hex>.ps1` nach `%TEMP%`, härtet die ACL und führt die Datei mit `-ExecutionPolicy Bypass` aus | beide | Blockade der Skriptdatei, Schritt läuft in den Timeout |
| Prozess-Isolation startet den PowerShell-Host über `CreateProcessW` mit Attribute-List, vererbbaren Pipes und Job Object | beide | Verhaltenserkennung als Launcher-Muster |
| Installer entpackt ein signaturgeprüftes Artefakt nach `%TEMP%` und tauscht das Programmverzeichnis | Server | Dateihandle verhindert Verschieben, Update bricht ab |
| `postgres.exe` schreibt dauerhaft in `pgdata\base` und `pgdata\pg_wal` | Desktop | Durchsatzverlust, im Extremfall Schreibfehler |
| Electron startet Kindprozesse desselben Binärnamens und liefert Chromium-Nativ-DLLs | Desktop | Generischer Heuristik-Treffer, Oberfläche startet nicht |

## Grundsätze

- Rangfolge der Ausschlussarten: **Signatur/Publisher → Prozess → Pfad**. Das Server-Artefakt ist signiert und wird vor dem Entpacken gegen einen festgelegten Signer-Thumbprint geprüft; eine Publisher-Regel ist deshalb der pfadärmste Weg.
- Ausschlüsse betreffen die Echtzeit-Prüfung, nicht die EDR-Telemetrie. Wo das Produkt beides trennt, bleibt die Verhaltenserfassung aktiv.
- Ausschlüsse gelten pro Rolle. Eine domänenweite Policy wäre eine unnötige Ausweitung.
- Alle Pfade sind Standardwerte. Abweichende `-InstallPath`/`-DataPath` oder gesetzte `Logging:File:Path`/`Retention:*:ArchivePath` verschieben die Einträge entsprechend.

## Windows-Server

Ein Dienst: `NodePilot` (Anzeigename `NodePilot Orchestrator`), ausgeführt als LocalSystem oder gMSA. Die Datenbank liegt auf einem anderen Host.

### Ordner

| Pfad | Inhalt | Priorität | Restrisiko |
|---|---|---|---|
| `C:\Program Files\NodePilot` | Programmverzeichnis, wird beim Update vollständig getauscht | Pflicht | Nur SYSTEM und Administratoren haben Schreibrechte. Publisher-Regel statt Pfad bevorzugen |
| `C:\ProgramData\NodePilot` | Logs, Archive, Schlüsselmaterial, Laufzeitkonfiguration | Pflicht | Enthält Schlüssel und Tokens; deren Schutz liegt bei der ACL, nicht beim Scanner |
| `C:\Program Files\NodePilot.rollback.*`, `…NodePilot.backup.*` | Zeitgestempelte Vorgängerstände (drei werden aufbewahrt) | Empfohlen | Auf das Wartungsfenster befristbar |
| `%TEMP%\nodepilot-artifact-*` (Dienst: `C:\Windows\Temp\…`) | Staging des signierten Artefakts — ~2900 Dateien, davon ~2650 unter 64 KB. Die **teuerste Stelle eines Updates**: nicht die 114 MB kosten, sondern die Dateianzahl, und ein Echtzeit-Scan prüft jede Erzeugung einzeln | Empfohlen | Restriktive DACL bei der Erzeugung; der Inhalt wird direkt danach Datei für Datei gegen das signierte Manifest geprüft. Auf das Wartungsfenster befristbar |

### Prozesse

| Prozess | Priorität | Restrisiko |
|---|---|---|
| `C:\Program Files\NodePilot\NodePilot.Api.exe` | Pflicht | Führt konstruktionsbedingt Workflow-PowerShell aus. Kompensiert durch NodePilot-eigene Rollen, Folder-RBAC und Audit-Log |
| `pwsh.exe` (`C:\Program Files\PowerShell\7`) | Pflicht | Weitreichend. Wo möglich auf den Elternprozess `NodePilot.Api.exe` einschränken |
| `powershell.exe` (`System32\WindowsPowerShell\v1.0`) | Pflicht, falls PowerShell 7 nicht garantiert vorhanden | wie oben |
| `where.exe` | Empfohlen | Einmalige Pfadauflösung beim Engine-Start, kein Schreibzugriff |

### Verhaltensregeln

| Mechanismus | Konflikt | Empfehlung |
|---|---|---|
| Regeln „Dienst startet Skript-Host" (u. a. ASR-Regeln zu Prozesserzeugung und verschleierten Skripten) | Kernfunktion von NodePilot | Ausnahme für den Elternprozess, Regel nicht global deaktivieren |
| Controlled Folder Access | Blockiert Schreiben nach `C:\ProgramData\NodePilot`, Dienststart schlägt fehl | `NodePilot.Api.exe` als vertrauenswürdig eintragen |
| Löschquarantäne im Programmverzeichnis | Entfernt der Scanner `powershell.config.json`, startet die PowerShell-SDK pro Runspace ein zusätzliches `powershell.exe -Version 5.1 -s`, das nicht beendet wird — der Dienst belegt dann Gigabyte an Arbeitsspeicher | Funde melden statt entfernen |
| Skript-Scanning / AMSI | keiner | aktiv lassen |

Ports (informativ, kein Ausschluss): eingehend 443 und optional 80; ausgehend 5985/5986 für WinRM sowie 1433 bzw. 5432 für die Datenbank. Der Installer legt die Firewall-Regeln `NodePilot NodePilot HTTPS` und `NodePilot NodePilot HTTP-Redirect` an.

## Desktop-App

Zwei Dienste: `NodePilot` (API, LocalSystem) und `NodePilotDb` (PostgreSQL über `pg_ctl.exe`, NetworkService). Alle Ports sind auf Loopback beschränkt, es wird keine Firewall-Regel angelegt.

Die API liegt hier eine Ebene tiefer als beim Server — `…\NodePilot\app\NodePilot.Api.exe`. Ein auf die Server-Variante gemünzter Pfad-Ausschluss greift nicht.

### Ordner

| Pfad | Inhalt | Priorität | Restrisiko |
|---|---|---|---|
| `C:\Program Files\NodePilot\app` | API-Dienst | Pflicht | wie Server-Programmverzeichnis |
| `C:\Program Files\NodePilot\desktop` | Electron-Shell samt Chromium-Nativ-DLLs (`vk_swiftshader.dll`, `ffmpeg.dll`, `dxcompiler.dll`, `libEGL.dll`, `libGLESv2.dll`) | Pflicht | Statischer Programmcode; ein einzelner Quarantäne-Fund macht die Oberfläche startunfähig |
| `C:\Program Files\NodePilot\pgsql` | Mitgeliefertes PostgreSQL | Empfohlen | Alternativ nur die sechs genutzten Prozesse ausschließen statt des ganzen Ordners |
| `C:\ProgramData\NodePilot` | `pgdata`, `secrets`, `logs`, `backups`, `rollback`, `archive`, `desktop.json` | Pflicht | Enthält Datenbank-Passwörter; deren Schutz liegt bei der ACL |
| `%APPDATA%\NodePilot` | Chromium-Profil und Caches der Shell | Empfohlen | Nur Cache-Daten, kein ausführbarer Code |
| `%LOCALAPPDATA%\NodePilot` | `admin-setup.handoff`, Einmal-Token für den Erstlogin | Empfohlen | Kurzlebige Textdatei |

### Prozesse

| Prozess (in `C:\Program Files\NodePilot`) | Priorität | Restrisiko |
|---|---|---|
| `app\NodePilot.Api.exe` | Pflicht | wie Server |
| `desktop\NodePilot.exe` | Pflicht | Startet Kindprozesse desselben Namens und über das Infobereichsmenü ein UAC-angehobenes `powershell.exe` für den Dienst-Neustart. Ausschluss auf den Pfad einschränken, nicht auf den Dateinamen |
| `pgsql\bin\postgres.exe`, `pgsql\bin\pg_ctl.exe` | Pflicht | Bindung ausschließlich an `127.0.0.1` |
| `pgsql\bin\initdb.exe`, `psql.exe`, `pg_dump.exe`, `pg_restore.exe` | Empfohlen | Laufen nur bei Installation und Update |
| `powershell.exe` / `pwsh.exe` | Pflicht | wie Server |

Die übrigen 37 Programme in `pgsql\bin` werden nicht aufgerufen und brauchen keinen Ausschluss.

Ports (informativ): Loopback 47000–47049 für die API, 47100–47149 für PostgreSQL; je ein freier Port wird bei der Installation gewählt und festgehalten.

## Gemeinsam: temporäre Skriptdateien

| Muster | Entsteht | Priorität | Restrisiko |
|---|---|---|---|
| `%TEMP%\nodepilot_*.ps1` — bei LocalSystem `C:\Windows\Temp\nodepilot_*.ps1` | Bei jedem isolierten oder prozessbasierten `runScript`-Schritt; wird danach gelöscht | Pflicht | Der weitreichendste Eintrag. Ausschließlich das Namensmuster ausschließen, niemals das gesamte Temp-Verzeichnis, und wo möglich auf den Elternprozess einschränken |
| `%TEMP%\NodePilot-Transcript-*.log` | Nur bei aktivierter Mitschrift; räumt sich nach 24 Stunden selbst auf | Empfohlen | Reine Textdatei |

Ein Ausschluss „alles unterhalb der beiden NodePilot-Verzeichnisse" wirkt vollständig, lässt aber genau diese Skriptdatei ungeschützt — sie liegt außerhalb jedes NodePilot-benannten Pfads.

Die **Standard-Ausführungsart schreibt keine temporäre Datei und startet keinen Kindprozess**: Skripte laufen in einem prozessinternen Runspace-Pool innerhalb von `NodePilot.Api.exe`. Werden Isolation und explizite PowerShell-Hosts in der Umgebung nicht genutzt, entfallen die Einträge dieses Abschnitts und die Skript-Host-Prozesse.

## Nur bei Installation und Update

| Pfad | Betriebsart | Priorität |
|---|---|---|
| `%TEMP%\nodepilot-artifact-*` (Installer **und** Updater, siehe A.1) | Server | Empfohlen, für Installation und Update |
| `%TEMP%\nodepilot-provision.log` | Desktop | Empfohlen |
| `NodePilot-Desktop-Setup-*.exe`, `unins000.exe` | Desktop | Empfohlen |
| `C:\ProgramData\NodePilot\backups\pre-update-*.dump`, `…\rollback` | Desktop | Empfohlen |

Diese Einträge lassen sich auf ein Wartungsfenster befristen.

## Nicht ausschließen

| Nicht ausschließen | Begründung |
|---|---|
| `C:\Windows\Temp` als Ganzes | Für viele Prozesse beschreibbar; nur das Muster `nodepilot_*.ps1` ausschließen |
| Zielpfade von Workflows | Datei-, Ordner-, Textdatei-, ZIP- und Registry-Activities schreiben an frei konfigurierbare Ziele. Genau diese Schreibvorgänge sollen weiter geprüft werden |
| Der gesamte Ordner `pgsql\bin` | 37 der 43 Programme werden nie aufgerufen |
| Benutzerprofile | Es genügen `%APPDATA%\NodePilot` und `%LOCALAPPDATA%\NodePilot` |
| `pwsh.exe`/`powershell.exe` systemweit ohne Elternprozess-Einschränkung | Die engere Regel ist vorzuziehen, wo das Produkt sie unterstützt |
| Skript-Scanning / AMSI abschalten | Wird von NodePilot nicht behindert |
| Dateityp `*.npbackup` | Das Ziel des Sicherungs-Exports wählt der Administrator frei |

## Symptome bei fehlenden Ausschlüssen

| Symptom | Wahrscheinlich fehlender Ausschluss |
|---|---|
| Schritt bleibt in `Running` und läuft in den Timeout | `%TEMP%\nodepilot_*.ps1` oder der Skript-Host-Prozess |
| Dienst startet nach einem Update nicht, Programmverzeichnis unvollständig | Programmverzeichnis |
| Dienst startet, aber es entsteht keine Logdatei; kein Erstlogin möglich | `C:\ProgramData\NodePilot` oder Controlled Folder Access |
| Arbeitsspeicher des Dienstes wächst über Stunden, viele `powershell.exe`-Prozesse | `powershell.config.json` in Quarantäne |
| Desktop: Oberfläche startet nicht oder bleibt leer | `C:\Program Files\NodePilot\desktop` |
| Desktop: `NodePilotDb` startet nicht | `C:\ProgramData\NodePilot\pgdata` oder `postgres.exe`/`pg_ctl.exe` |
| Speichern in den Admin-Einstellungen bleibt wirkungslos | `C:\ProgramData\NodePilot` (atomarer Ersetzungsvorgang) |

Die ausführliche Fassung mit allen Pfaden und Belegen steht im Repository unter `docs/av-exclusions.md`.
