# Antiviren-Ausschlüsse für NodePilot

Übergabedokument für die Antiviren-/Endpoint-Security-Abteilung. Es listet auf, welche Ordner, Prozesse und Dateimuster NodePilot im Betrieb anfasst, warum ein Ausschluss nötig ist und welches Restrisiko er erzeugt.

Das Dokument ist **produktneutral** — es enthält bewusst keine `Add-MpPreference`-Zeilen oder Skripte. Die konkrete Umsetzung (GPO, Intune, Konsole des jeweiligen Herstellers) bleibt bei der AV-Abteilung.

**Geltungsbereich**

| Rolle | Enthalten |
|---|---|
| Produktions-Server (Windows-Dienst, `deploy/`-Installer) | ja → [Teil A](#teil-a--produktions-server) |
| Desktop-App (Offline-Installer, `deploy/desktop/`) | ja → [Teil B](#teil-b--desktop-app) |
| Beide Rollen gemeinsam (PowerShell-Ausführung) | ja → [Teil C](#teil-c--powershell-ausführung-beide-rollen) |
| Per WinRM orchestrierte Ziel-Maschinen | nein — dort läuft keine NodePilot-Software; siehe [Hinweis](#nicht-im-geltungsbereich-ziel-maschinen) |
| Entwickler-Arbeitsplätze | nein |

---

## Warum Ausschlüsse nötig sind

NodePilot ist eine Workflow-Orchestrierung: Der Kern der Anwendung besteht darin, PowerShell auszuführen und Prozesse zu starten. Fünf davon abgeleitete Verhaltensweisen kollidieren regelmäßig mit Standard-Heuristiken:

1. **Ein Dienst unter `LocalSystem` schreibt ein Skript nach `%TEMP%` und führt es aus.**
   Für die Ausführungsarten „isolierter Prozess" und „expliziter PowerShell-Host" schreibt NodePilot das Workflow-Skript als `nodepilot_<32-Hex>.ps1` in das Temp-Verzeichnis, härtet dessen ACL auf Besitzer-Vollzugriff (alle vererbten Rechte werden entfernt) und startet es mit `-NoProfile -NonInteractive -ExecutionPolicy Bypass -File`. Datei-Erzeugung, ACL-Härtung und sofortige Ausführung im Temp-Verzeichnis ist die stärkste Heuristik-Signatur im gesamten Produkt.

2. **Die Prozess-Isolation nutzt Low-Level-Windows-APIs.**
   Mit `config.isolated: true` startet NodePilot den PowerShell-Host nicht über die .NET-Standardwege, sondern über `CreateProcessW` mit `PROC_THREAD_ATTRIBUTE_JOB_LIST`/`PROC_THREAD_ATTRIBUTE_HANDLE_LIST`, vererbbaren Anonymous-Pipes und einem Job Object mit `KILL_ON_JOB_CLOSE`. Zweck ist Crash- und Leak-Containment (der Job räumt verwaiste Kindprozesse zuverlässig ab). Verhaltensanalysen lesen dieselbe API-Kombination als Injector- bzw. Launcher-Muster.

3. **Der Installer entpackt ein signiertes Artefakt und tauscht das Programmverzeichnis.**
   Installation und Update entpacken nach `%TEMP%\nodepilot-artifact-<GUID>\`, verschieben das alte Programmverzeichnis nach `…NodePilot.rollback.<Zeitstempel>` bzw. `…NodePilot.backup.<Zeitstempel>` und legen das neue an derselben Stelle ab. Ein Echtzeit-Scanner, der währenddessen ein Dateihandle hält, lässt Verschiebe- oder Löschoperationen fehlschlagen — der Installer bricht dann mitten im Tausch ab und rollt zurück.

4. **PostgreSQL erzeugt dauerhaftes, hochfrequentes Datei-I/O** (nur Desktop-Rolle).
   `postgres.exe` schreibt kontinuierlich in `pgdata\base\` und `pgdata\pg_wal\`. Echtzeit-Scanning dieser Verzeichnisse kostet spürbar Durchsatz; im schlechteren Fall blockiert ein Scanner-Handle einen WAL-Write und die Datenbank quittiert mit einem Schreibfehler.

5. **Die Desktop-Oberfläche ist eine Electron-Anwendung** (nur Desktop-Rolle).
   `NodePilot.exe` startet Kindprozesse **desselben Binärnamens** (`--type=renderer`, `--type=gpu-process`, `--type=utility`) und liefert Nativ-Bibliotheken mit, die häufig Heuristik-Treffer erzeugen (`vk_swiftshader.dll`, `ffmpeg.dll`, `dxcompiler.dll`, `libEGL.dll`, `libGLESv2.dll`).

---

## Grundsätze für die Umsetzung

- **Rangfolge der Ausschlussarten: Signatur/Publisher → Prozess → Pfad.**
  Das Server-Artefakt ist signiert und der Installer prüft die Signatur gegen einen festgelegten Signer-Thumbprint, bevor er irgendetwas entpackt. Wo das AV-Produkt Publisher-basierte Regeln kennt, ist das der pfadärmste und am schwersten missbrauchbare Weg. Pfad-Ausschlüsse sind die letzte Wahl.
- **Ein Ausschluss soll die Echtzeit-Prüfung entlasten, nicht die Telemetrie abschalten.**
  Wo das Produkt zwischen Scan-Ausschluss und EDR-Sichtbarkeit trennt, sollte die Verhaltens-/Telemetrie-Erfassung aktiv bleiben. Alle Einträge unten sind als Scan-/Blockier-Ausnahmen gemeint.
- **Ausschlüsse gelten pro Rolle, nicht flächendeckend.**
  Teil A gehört auf die Orchestrator-Server, Teil B auf die Desktop-Installationen. Eine gemeinsame Policy für alle Windows-Systeme der Domäne wäre eine unnötige Ausweitung.
- **Spalte „Priorität"**
  *Pflicht* = ohne diesen Ausschluss ist mit Funktionsausfall oder Abbruch zu rechnen.
  *Empfohlen* = ohne ihn arbeitet NodePilot, aber mit messbarem Durchsatzverlust oder wiederkehrenden Fehlalarmen.
- **Pfade sind Standardwerte.** Weichen `-InstallPath`/`-DataPath` bei der Installation ab, sind die Einträge entsprechend anzupassen — siehe [unten](#wann-diese-liste-neu-geprüft-werden-muss).

---

## Teil A — Produktions-Server

Rolle: Windows-Server mit dem Dienst **`NodePilot`** (Anzeigename `NodePilot Orchestrator`), ausgeführt als `LocalSystem` oder als gruppenverwaltetes Dienstkonto (gMSA, `DOMAIN\svc-nodepilot$`). Die Datenbank liegt auf einem **anderen** Host — der Server-Installer bringt kein lokales PostgreSQL mit.

### A.1 Ordner

| Pfad | Inhalt | Warum nötig | Priorität | Restrisiko |
|---|---|---|---|---|
| `C:\Program Files\NodePilot\` | Programmverzeichnis: `NodePilot.Api.exe`, ~mehrere hundert verwaltete DLLs, `wwwroot\` (SPA), `PSModules\`, `knowledge\` | Wird beim Update komplett getauscht; ein gehaltenes Scanner-Handle lässt Verschieben/Löschen fehlschlagen. Enthält außerdem `powershell.config.json` — siehe Warnung unter [A.4](#a4-verhaltensregeln-asr-controlled-folder-access) | Pflicht | Schreibrechte hat nur SYSTEM/Administratoren; ein Angreifer mit diesen Rechten hat das System ohnehin. Restrisiko: eine dort abgelegte Fremd-DLL würde nicht mehr gescannt — kompensierbar über Publisher-Regel statt Pfad und über Integritätsüberwachung des Verzeichnisses |
| `C:\ProgramData\NodePilot\` | Laufzeitdaten: `logs\`, `archive\`, `jwt-secret.key`, `data-protection-keys\`, `admin-setup.token`, `appsettings.runtime.json`, `install-report.txt`, `postgres-root-ca.pem` | Dauerhaftes Schreiben (Rolling Logs, atomare Config-Writes über `.tmp` + `File.Replace`, gzip-Archive). Scanner-Handles auf der Zieldatei lassen den atomaren Ersetzungsschritt scheitern | Pflicht | Verzeichnis ist per ACL auf das Dienstkonto + Administratoren beschränkt. Es liegen dort **Schlüsselmaterial und Tokens** — der Ausschluss verhindert nicht deren Diebstahl (dafür ist der Dateizugriff zuständig), er reduziert nur die Erkennung einer dort abgelegten Schaddatei |
| `C:\Program Files\NodePilot.rollback.*`<br>`C:\Program Files\NodePilot.backup.*` | Zeitgestempelte Kopien des vorherigen Programmverzeichnisses (drei werden aufbewahrt) | Entstehen nur während Installation/Update; enthalten dieselben Binärdateien wie oben | Empfohlen | Wie Programmverzeichnis. Kann auf ein Wartungsfenster befristet werden |
| `%TEMP%\nodepilot-artifact-*`<br>(Dienst-Kontext: `C:\Windows\Temp\nodepilot-artifact-*`) | Staging des signierten Artefakts: Installer **und** Updater entpacken das Zip zuerst hierher — rund **2900 Dateien**, davon ~2650 kleiner als 64 KB, und prüfen anschließend jede einzelne gegen das signierte Manifest | Dies ist die **teuerste Stelle des gesamten Updates** und der Grund, warum ein Upgrade minutenlang auf einer Stelle zu stehen scheint: Nicht die Datenmenge (114 MB) kostet, sondern die Dateianzahl. Ein Echtzeit-Scan prüft jede Erzeugung einzeln und vervielfacht die Laufzeit; ein gehaltenes Handle lässt zusätzlich das Aufräumen des Staging-Ordners scheitern | Empfohlen | Der Ordner trägt bereits eine restriktive DACL (SYSTEM + Administratoren + aufrufender Benutzer, atomar bei der Erzeugung gesetzt), und sein Inhalt wird unmittelbar nach dem Entpacken **gegen das signierte Manifest** verifiziert — Datei für Datei, mit Längen- und SHA-256-Vergleich. Der Ausschluss senkt die Erkennung also genau dort, wo bereits kryptografisch geprüft wird. Kann auf ein Wartungsfenster befristet werden |

Ausdrücklich **nicht** enthalten: Ordner, in die Workflows schreiben. Siehe [Was nicht ausgeschlossen werden soll](#was-ausdrücklich-nicht-ausgeschlossen-werden-soll).

### A.2 Prozesse

| Prozess | Pfad | Rolle | Warum nötig | Priorität | Restrisiko |
|---|---|---|---|---|---|
| `NodePilot.Api.exe` | `C:\Program Files\NodePilot\NodePilot.Api.exe` | Der Dienst selbst. Enthält die Workflow-Engine, den In-Process-PowerShell-Runspace-Pool und den WinRM-Client | Startet Kindprozesse, öffnet vererbbare Pipes, legt Job Objects an, liest/schreibt permanent unter `ProgramData` | Pflicht | Der Prozess führt konstruktionsbedingt beliebigen, vom Workflow-Autor bestimmten PowerShell-Code aus. Ein Prozess-Ausschluss macht dessen Datei-Zugriffe unsichtbar. **Kompensierende Kontrolle:** NodePilot hat eigene Rollen-/Ordner-Rechte und ein vollständiges Audit-Log für jede Workflow-Änderung und -Ausführung |
| `pwsh.exe` | `C:\Program Files\PowerShell\7\pwsh.exe` | PowerShell 7, bevorzugter Host für isolierte und explizit prozessbasierte Schritte | Wird mit `-ExecutionPolicy Bypass -File <Temp-Skript>` gestartet | Pflicht | Ein Prozess-Ausschluss für einen generischen Skript-Host ist die weitreichendste Regel in dieser Liste. **Nach Möglichkeit einschränken:** nur, wenn der Elternprozess `NodePilot.Api.exe` ist, oder nur in Kombination mit dem Dateimuster aus [C.1](#c1-temporäre-skript--und-transcript-dateien) |
| `powershell.exe` | `C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe` | Fallback-Host, wenn PowerShell 7 nicht installiert ist | wie oben | Pflicht, sofern PowerShell 7 nicht garantiert vorhanden ist | wie oben |
| `where.exe` | `C:\Windows\System32\where.exe` | Wird beim Start der Engine **einmalig** aufgerufen, um den PowerShell-Host zu lokalisieren | Aufruf durch einen Dienst kann als Discovery-Verhalten gewertet werden | Empfohlen | Sehr gering — reine Pfadauflösung ohne Schreibzugriff |

Aus generiertem PowerShell heraus werden je nach Workflow zusätzlich Windows-Bordmittel aufgerufen: `sc.exe` (Dienstverwaltung), `shutdown.exe` und `cmd.exe /c shutdown /a` (Energieverwaltung), die WMI-/CIM-Infrastruktur (`WmiPrvSE.exe`) und die Aufgabenplanung. Diese laufen in der Regel auf der **Ziel-Maschine**, nicht auf dem Orchestrator, und brauchen dort keinen NodePilot-spezifischen Ausschluss. Sie sind hier nur genannt, damit ein Alarm auf `NodePilot.Api.exe → powershell.exe → sc.exe` als erwartet eingeordnet werden kann.

### A.3 Temporäre Dateimuster

Siehe [Teil C](#teil-c--powershell-ausführung-beide-rollen) — die Muster sind für beide Rollen identisch. Für einen Dienst unter `LocalSystem` löst `%TEMP%` nach `C:\Windows\Temp\` auf.

### A.4 Verhaltensregeln, ASR, Controlled Folder Access

| Regel/Mechanismus | Konflikt | Empfehlung |
|---|---|---|
| Verhaltensregeln nach dem Muster **„Dienst oder Office-Prozess startet einen Skript-Host"** (bei Microsoft Defender: die ASR-Regeln zu Prozesserzeugung aus PSExec/WMI bzw. zu verschleierten Skripten) | Genau das ist die Kernfunktion von NodePilot: `NodePilot.Api.exe` (Dienst, `LocalSystem`) startet `pwsh.exe`/`powershell.exe` | Ausnahme für `NodePilot.Api.exe` als Elternprozess. Regel selbst **nicht** global deaktivieren |
| **Controlled Folder Access / Ordnerschutz** | Blockiert das Schreiben in `C:\ProgramData\NodePilot\` und lässt den Dienst-Start scheitern, weil weder Log noch Schlüsseldatei angelegt werden können | `NodePilot.Api.exe` als vertrauenswürdige Anwendung eintragen |
| **Quarantäne einzelner Dateien im Programmverzeichnis** | Wird `powershell.config.json` (liegt neben `System.Management.Automation.dll`) entfernt, fällt eine bewusst gesetzte Kompatibilitätssperre weg. Die PowerShell-SDK startet dann **pro Runspace im Pool** ein zusätzliches `powershell.exe -Version 5.1 -s`, das nicht beendet wird — der Dienst hat in einem realen Fall dadurch mehrere Gigabyte Arbeitsspeicher belegt | Datei-Löschquarantäne auf `C:\Program Files\NodePilot\` unterbinden; Funde melden statt entfernen |
| **Skript-Scanning / AMSI** | Unproblematisch und ausdrücklich erwünscht | Aktiv lassen |

### A.5 Netzwerk (informativ, kein Ausschluss)

Kein Ausschluss nötig — nur zur Einordnung, falls die AV-/Firewall-Seite dieselbe Konsole bedient.

| Richtung | Port | Zweck |
|---|---|---|
| eingehend | TCP 443 | Weboberfläche + REST-API + SignalR (`/hubs/execution`, kein eigener Port), Bindung auf alle Adressen |
| eingehend | TCP 80 | Weiterleitung auf HTTPS; per Konfiguration abschaltbar |
| ausgehend | TCP 5985 / 5986 | WinRM zu den Ziel-Maschinen (HTTP/HTTPS, je Maschine konfiguriert) |
| ausgehend | TCP 1433 bzw. 5432 | SQL Server bzw. PostgreSQL |

Der Installer legt zwei Firewall-Regeln an: `NodePilot NodePilot HTTPS` und — sofern HTTP gebunden ist — `NodePilot NodePilot HTTP-Redirect` (beide Profil *Domäne*).

---

## Teil B — Desktop-App

Rolle: Einzelplatz-Installation aus dem Offline-Installer. **Zwei** Windows-Dienste, ein mitgeliefertes PostgreSQL und eine Electron-Oberfläche. Alle Netzwerkdienste sind auf Loopback beschränkt; es wird **keine** Firewall-Regel angelegt.

> **Wichtiger Unterschied zu Teil A:** Beide Rollen nutzen `C:\Program Files\NodePilot`, aber die API liegt in der Desktop-Installation eine Ebene tiefer — `…\NodePilot\app\NodePilot.Api.exe` statt `…\NodePilot\NodePilot.Api.exe`. Ein auf die Server-Variante gemünzter Pfad-Ausschluss greift hier nicht.

### B.1 Dienste

| Dienstname | Anzeigename | Programm | Konto |
|---|---|---|---|
| `NodePilot` | `NodePilot` | `C:\Program Files\NodePilot\app\NodePilot.Api.exe` | `LocalSystem` |
| `NodePilotDb` | `NodePilot Database` | `C:\Program Files\NodePilot\pgsql\bin\pg_ctl.exe` (startet `postgres.exe`) | `NT AUTHORITY\NetworkService` |

### B.2 Ordner

| Pfad | Inhalt | Warum nötig | Priorität | Restrisiko |
|---|---|---|---|---|
| `C:\Program Files\NodePilot\app\` | API-Dienst und dessen Abhängigkeiten | wie Teil A: Update tauscht das Verzeichnis; enthält `powershell.config.json` | Pflicht | wie Teil A |
| `C:\Program Files\NodePilot\desktop\` | Electron-Oberfläche: `NodePilot.exe` plus Nativ-DLLs (`vk_swiftshader.dll`, `ffmpeg.dll`, `dxcompiler.dll`, `dxil.dll`, `libEGL.dll`, `libGLESv2.dll`, `vulkan-1.dll`), `resources\app.asar`, `*.pak`, `icudtl.dat`, `snapshot_blob.bin` | Chromium-Nativbibliotheken erzeugen regelmäßig generische Heuristik-Treffer; ein einzelner Quarantäne-Fund macht die Oberfläche startunfähig | Pflicht | Statischer Programmcode, wird nur durch Installer/Update verändert. Restrisiko entspricht dem jedes ausgeschlossenen Programmverzeichnisses |
| `C:\Program Files\NodePilot\pgsql\` | Mitgeliefertes PostgreSQL (Binärdateien, `lib`, `share`) | Wird von `pg_ctl.exe`/`postgres.exe` beim Start vollständig gelesen | Empfohlen | Enthält 43 Programme, von denen NodePilot nur sechs benutzt. Wer die Fläche klein halten will, schließt statt des Ordners nur die sechs Prozesse aus [B.3](#b3-prozesse) aus |
| `C:\ProgramData\NodePilot\` | `pgdata\` (Datenbank-Cluster), `secrets\`, `logs\`, `backups\`, `rollback\`, `archive\`, `desktop.json`, `jwt-secret.key`, `data-protection-keys\`, `appsettings.runtime.json` | Dauerhaftes Datenbank- und WAL-I/O plus alle Laufzeitschreibvorgänge der API | Pflicht | wie Teil A. Zusätzlich liegen hier die Datenbank-Passwörter unter `secrets\` — der Ausschluss ändert nichts an deren ACL-Schutz |
| `%APPDATA%\NodePilot\` (je Benutzer) | Chromium-Profil der Oberfläche: `Cache`, `GPUCache`, `Code Cache`, Cookies | Hochfrequentes Cache-I/O beim Bedienen der Oberfläche | Empfohlen | Nur Browser-Cache-Daten; kein ausführbarer Code |
| `%LOCALAPPDATA%\NodePilot\` (je Benutzer) | `admin-setup.handoff` — Einmal-Token für den Erstlogin | Wird beim ersten Start gelesen und gelöscht | Empfohlen | Einzelne kurzlebige Textdatei |

### B.3 Prozesse

| Prozess | Pfad | Warum nötig | Priorität | Restrisiko |
|---|---|---|---|---|
| `NodePilot.Api.exe` | `C:\Program Files\NodePilot\app\` | wie Teil A | Pflicht | wie Teil A |
| `NodePilot.exe` | `C:\Program Files\NodePilot\desktop\` | Electron-Oberfläche. Startet Kindprozesse **desselben Namens** mit `--type=renderer/gpu-process/utility`; über das Tray-Menü zusätzlich `powershell.exe` mit UAC-Anhebung, um den API-Dienst neu zu starten | Pflicht | Die Selbst-Aufruf-Kette und die Rechteanhebung aus einer GUI heraus sind für sich genommen auffällig. Der Ausschluss sollte auf den Pfad im Programmverzeichnis eingeschränkt werden, nicht auf den bloßen Dateinamen `NodePilot.exe` |
| `postgres.exe` | `C:\Program Files\NodePilot\pgsql\bin\` | Datenbank-Serverprozess; dauerhaftes I/O in `pgdata\` | Pflicht | Bindet ausschließlich an `127.0.0.1`, TLS ist aus, Port aus dem Bereich 47100–47149 |
| `pg_ctl.exe` | `C:\Program Files\NodePilot\pgsql\bin\` | Dienst-Host, Start/Stopp des Clusters | Pflicht | gering |
| `initdb.exe` | `C:\Program Files\NodePilot\pgsql\bin\` | Legt den Cluster an — läuft **nur** bei der Erstinstallation | Empfohlen | gering |
| `psql.exe` | `C:\Program Files\NodePilot\pgsql\bin\` | Legt Rolle und Datenbank an — nur bei Installation | Empfohlen | gering |
| `pg_dump.exe` / `pg_restore.exe` | `C:\Program Files\NodePilot\pgsql\bin\` | Sicherung vor jedem Update bzw. Rückrollen | Empfohlen | gering; laufen nur im Wartungsfenster |
| `powershell.exe` / `pwsh.exe` | Systempfade | Workflow-Ausführung — identisch zu Teil A | Pflicht | siehe [A.2](#a2-prozesse) |

Die übrigen 37 Programme in `pgsql\bin` (`pgbench.exe`, `pg_upgrade.exe`, `stackbuilder.exe`, …) werden von NodePilot **nicht** aufgerufen und brauchen keinen Ausschluss.

### B.4 Netzwerk (informativ)

| Richtung | Port | Zweck |
|---|---|---|
| Loopback | TCP 47000–47049 (ein freier Port wird bei der Installation gewählt und festgehalten) | Weboberfläche + API, ausschließlich `localhost` |
| Loopback | TCP 47100–47149 (dito) | PostgreSQL, gebunden an `127.0.0.1` |

Keine eingehenden Verbindungen von außen, keine Firewall-Regel.

---

## Teil C — PowerShell-Ausführung (beide Rollen)

Dieser Teil betrifft **den ausführenden Host**, also den Orchestrator-Server bzw. die Desktop-Installation.

### C.1 Temporäre Skript- und Transcript-Dateien

| Muster | Entsteht wann | Warum nötig | Priorität | Restrisiko |
|---|---|---|---|---|
| `%TEMP%\nodepilot_*.ps1`<br>bei `LocalSystem`: `C:\Windows\Temp\nodepilot_*.ps1` | Bei jedem Workflow-Schritt, der isoliert oder mit explizit gewähltem PowerShell-Host läuft. Wird nach dem Lauf wieder gelöscht | Datei wird angelegt, ACL-gehärtet und sofort mit `-ExecutionPolicy Bypass` ausgeführt — das häufigste Blockier-Muster | Pflicht | **Der weitreichendste Eintrag der Liste.** `C:\Windows\Temp` ist von vielen Prozessen beschreibbar; ein Angreifer, der dort eine Datei nach diesem Namensschema ablegt, entginge dem Scanner. **Deshalb: ausschließlich das Namensmuster ausschließen, niemals das gesamte Temp-Verzeichnis**, und wo möglich zusätzlich auf den Elternprozess `NodePilot.Api.exe` einschränken |
| `%TEMP%\NodePilot-Transcript-*.log` | Nur bei Schritten mit aktivierter Mitschrift (`transcript`); räumt sich nach 24 h selbst auf | Wird während des Laufs geschrieben und danach zurückgelesen | Empfohlen | Reine Textdatei ohne Ausführungspfad |

> **Häufiger Fehler:** Ein Ausschluss „alles unterhalb von `C:\Program Files\NodePilot` und `C:\ProgramData\NodePilot`" wirkt vollständig, lässt aber genau diese Skriptdatei ungeschützt — sie liegt außerhalb jedes NodePilot-benannten Pfads.

### C.2 Der Standardfall braucht nichts davon

Die Standard-Ausführungsart schreibt **keine** temporäre Datei und startet **keinen** Kindprozess: Skripte laufen in einem prozessinternen Runspace-Pool innerhalb von `NodePilot.Api.exe`. Temp-Dateien und PowerShell-Kindprozesse entstehen nur, wenn ein Schritt ausdrücklich Isolation oder einen bestimmten PowerShell-Host anfordert. Wenn diese Ausführungsarten in der Umgebung nicht genutzt werden, entfallen die Einträge aus [C.1](#c1-temporäre-skript--und-transcript-dateien) und die Skript-Host-Prozesse aus [A.2](#a2-prozesse).

### C.3 Ordner-Überwachung durch NodePilot selbst

Der Dateiwächter-Trigger überwacht ein vom Betrieb gewähltes Verzeichnis auf Änderungen. Wenn AV-Software dort Dateien verschiebt, umbenennt oder in Quarantäne nimmt, löst das echte Trigger-Ereignisse aus und startet Workflows. Das ist kein Ausschlussbedarf, aber ein bekannter Wechselwirkungspunkt bei der Fehlersuche.

---

## Nur während Installation und Update

Diese Einträge lassen sich auf ein Wartungsfenster befristen.

| Pfad | Rolle | Zweck | Priorität | Restrisiko |
|---|---|---|---|---|
| `%TEMP%\nodepilot-artifact-*\` | Server | Entpacktes, signaturgeprüftes Installationsartefakt | Pflicht während der Installation | Der Inhalt wurde vor dem Entpacken gegen einen festgelegten Signer-Thumbprint geprüft. Befristung empfohlen |
| `%TEMP%\nodepilot-provision.log` | Desktop | Mitschrift der Einrichtung, für die Fehlersuche | Empfohlen | Reine Textdatei |
| `NodePilot-Desktop-Setup-*.exe` | Desktop | Der Offline-Installer | Empfohlen | Signiertes Setup; Publisher-Regel bevorzugen |
| `unins000.exe` in `C:\Program Files\NodePilot\` | Desktop | Deinstallationsroutine | Empfohlen | gering |
| `C:\ProgramData\NodePilot\backups\pre-update-*.dump`<br>`C:\ProgramData\NodePilot\rollback\` | Desktop | Datenbank-Sicherung und Binär-Rückrollstand vor jedem Update | Empfohlen | Enthält Datenbankinhalte; ACL-geschützt |

---

## Symptome bei fehlenden Ausschlüssen

Zur schnellen Zuordnung, falls die Ausschlüsse unvollständig gesetzt wurden.

| Symptom | Wahrscheinlich fehlender Ausschluss |
|---|---|
| Workflow-Schritt bleibt dauerhaft im Zustand *Running* und läuft in den Timeout | `%TEMP%\nodepilot_*.ps1` oder der Skript-Host-Prozess |
| Schritt schlägt sofort mit einem Datei-Zugriffsfehler auf eine `.ps1` unter `C:\Windows\Temp` fehl | `%TEMP%\nodepilot_*.ps1` |
| Dienst startet nach einem Update nicht mehr, Programmverzeichnis unvollständig | Programmverzeichnis (Handle während des Verzeichnistauschs) |
| Dienst startet, aber es entsteht keine Logdatei; kein Erstlogin möglich | `C:\ProgramData\NodePilot\` bzw. Ordnerschutz/Controlled Folder Access |
| Arbeitsspeicherverbrauch des Dienstes wächst über Stunden in den Gigabyte-Bereich, viele `powershell.exe`-Prozesse | `powershell.config.json` wurde aus dem Programmverzeichnis entfernt (Quarantäne) |
| Desktop: Oberfläche startet nicht oder zeigt ein leeres Fenster | `C:\Program Files\NodePilot\desktop\` (Nativ-DLL in Quarantäne) |
| Desktop: Dienst `NodePilotDb` startet nicht bzw. läuft in einen Timeout | `C:\ProgramData\NodePilot\pgdata\` oder `postgres.exe`/`pg_ctl.exe` |
| Speichern in den Admin-Einstellungen schlägt fehl oder wird nicht wirksam | `C:\ProgramData\NodePilot\` (atomarer Ersetzungsvorgang auf `appsettings.runtime.json`) |
| Sporadische Fehler beim Archivieren alter Ausführungen oder Audit-Einträge | `C:\ProgramData\NodePilot\archive\` |

---

## Was ausdrücklich **nicht** ausgeschlossen werden soll

| Nicht ausschließen | Begründung |
|---|---|
| `C:\Windows\Temp\` als Ganzes | Für viele Prozesse beschreibbar. Nur das Muster `nodepilot_*.ps1` ausschließen |
| Pfade, in die Workflows schreiben | Die Datei-, Ordner-, Textdatei-, ZIP- und Registry-Aktivitäten schreiben an frei konfigurierbare Ziele — potenziell überall. Genau diese Schreibvorgänge sollen weiterhin geprüft werden. NodePilot deckt diesen Bereich über eigene Rollen, Ordner-Berechtigungen, Pfad-Prüfungen und das Audit-Log ab |
| Der gesamte Ordner `pgsql\bin` | 37 der 43 Programme werden nie aufgerufen. Es genügen die sechs aus [B.3](#b3-prozesse) |
| Benutzerprofile (`C:\Users\…`) | NodePilot schreibt dort nur `%APPDATA%\NodePilot` und `%LOCALAPPDATA%\NodePilot` — diese beiden Unterordner reichen |
| `pwsh.exe`/`powershell.exe` **systemweit ohne Elternprozess-Einschränkung** | Wo das AV-Produkt eine Einschränkung auf den Elternprozess `NodePilot.Api.exe` unterstützt, ist sie deutlich enger und sollte genutzt werden |
| Skript-Scanning / AMSI abschalten | Wird von NodePilot nicht behindert und soll aktiv bleiben |
| `*.npbackup`-Dateien | Das Ziel des Sicherungs-Exports wählt der Administrator frei — ein pauschaler Dateityp-Ausschluss wäre eine unnötig breite Regel |

---

## Nicht im Geltungsbereich: Ziel-Maschinen

Auf den per WinRM orchestrierten Windows-Hosts wird **keine NodePilot-Software installiert** — die Orchestrierung ist agentenlos. Dort führt der Windows-eigene WinRM-Dienst die Schritte in `wsmprovhost.exe` aus; WMI-Abfragen laufen über `WmiPrvSE.exe`. Ob dafür Anpassungen nötig sind, richtet sich nach der bestehenden Richtlinie für administrative Remote-Ausführung und ist bewusst nicht Teil dieses Dokuments.

Umgekehrt gilt: Der Orchestrator selbst startet für WinRM **keinen** Kindprozess. Die Remote-Verbindung läuft vollständig innerhalb von `NodePilot.Api.exe`.

---

## Wann diese Liste neu geprüft werden muss

- Die Installation weicht von `-InstallPath` = `C:\Program Files\NodePilot` oder `-DataPath` = `C:\ProgramData\NodePilot` ab.
- `Logging:File:Path`, `Logging:SupportLog:Path` oder `Retention:*:ArchivePath` wurden auf Verzeichnisse außerhalb des Datenverzeichnisses gesetzt.
- Der Dienstname wurde bei der Installation über `-ServiceName` geändert (betrifft auch die Namen der Firewall-Regeln).
- Desktop: die bei der Installation gewählten Loopback-Ports haben sich durch eine Neuinstallation verschoben.
- Eine neue Activity oder eine Custom Activity startet einen bisher nicht gelisteten Prozess.
- Ein NodePilot-Update ändert das Verzeichnislayout — die Freigabeinformationen nennen das dann ausdrücklich.

---

## Verwandte Dokumentation

- [deployment-guide.md](deployment-guide.md) — End-to-End-Produktions-Deployment
- [claude-reference.md](claude-reference.md) — Deployment-Architektur, gMSA, Kestrel-HTTPS, Konfigurationsschlüssel
- `deploy/README.md` — Server-Installer im Detail
- `deploy/desktop/README.md` — Desktop-Installer im Detail
