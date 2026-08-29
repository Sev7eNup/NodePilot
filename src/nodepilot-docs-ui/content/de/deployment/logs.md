# Logs & Diagnose

NodePilot schreibt zwei Anwendungslogs; die Installationsprogramme führen je ein eigenes Transkript. Diese Seite benennt alle Dateien mit Ablage und Aufbewahrung, ordnet den häufigen Störungsbildern die passende Quelle zu und listet die Artefakte, die einem Ticket beiliegen sollten. Welche Formate zur Auswahl stehen und wie sie gesetzt werden, steht unter [Logging](../configuration/logging).

## Welche Dateien es gibt

| Artefakt | Muster | Ablage | Inhalt | Aufbewahrung |
|---|---|---|---|---|
| Anwendungslog | `nodepilot-JJJJMMTT.log`, bei Größenrollover zusätzlich `_001`, `_002` … | `C:\ProgramData\NodePilot\logs\` (Server und Desktop); in einer Entwicklungsinstanz `src\NodePilot.Api\logs\` | Vollständige Diagnose: Boot, Konfiguration, HTTP, Engine, Datenbank, Stacktraces | **7 Dateien**, zusätzlicher Rollover bei 100 MB |
| Support-Log | `nodepilot-support-JJJJMMTT.log` (+ `_001` …) | derselbe Ordner | Kuratierter Auszug ohne Stacktraces: Boot-Banner, allowlistete Audit-Ereignisse, angewandte Migrationen, fehlgeschlagene Steps, Ausgaben der `log`-Activity | 90 Dateien, zusätzlicher Rollover bei 10 MiB |
| `SupportEvents` | Datenbanktabelle | Datenbank, gelesen über die Seite `/support-log` | Dieselben Ereignisse strukturiert, mit Filter, Cursor und Export | 90 Tage über `Retention:SupportEvents` |
| Server-Setup-Transkript | `nodepilot-server-setup.log` | `%TEMP%` | Vollständiger Mitschnitt eines Installations- oder Update-Laufs | Wird angehängt, nicht begrenzt |
| Desktop-Provisionierungslog | `nodepilot-provision.log` | `%TEMP%` | Mitschnitt der Provisionierung, endet am fehlgeschlagenen Schritt | Wird je Lauf überschrieben |
| Installationsbericht | `install-report.txt` | `C:\ProgramData\NodePilot\` | Ergebnis der letzten Serverinstallation, ohne Secrets | Wird je Installation überschrieben |
| Windows-Ereignisanzeige | Protokoll *Anwendung*, Quelle = Dienstname | Betriebssystem | Start-, Stopp- und Absturzereignisse des Dienstes, geschrieben vom SCM | Regel des Betriebssystems |

Von den Logs zu unterscheiden sind die Retention-**Archive** unter `C:\ProgramData\NodePilot\archive\`: `executions\executions-JJJJMMTT.ndjson` sowie `audit\audit-*.ndjson.gz` samt `.sha256`-Sidecar entstehen beim Abräumen der Historie, nicht beim Protokollieren. Details unter [Retention-Services](../configuration/retention).

## Was NodePilot nicht schreibt

Diese Stellen sind bewusst leer und müssen im Störfall nicht durchsucht werden:

- **Die Electron-Shell führt kein eigenes Log.** Meldet das Desktop-Fenster einen Fehler, liegt die Ursache im Anwendungslog oder im Provisionierungslog.
- **`np` und `nodepilot-mcp` protokollieren in keine Datei.** Unter `%APPDATA%\NodePilot\` liegen ausschließlich `config.json` und `session-<Profil>.dat` als Zustand. Der MCP-Server gibt seine Diagnose auf stderr aus, weil stdout dem Protokoll gehört.
- **Das mitgelieferte Desktop-PostgreSQL läuft ohne `logging_collector`** und legt daher keine eigene Logdatei an. Ein Ausfall von `NodePilotDb` wird über das Anwendungslog und `/healthz/database` sichtbar.
- **Der Dienst leitet keine Standardausgabe um.** Der Konsolen-Sink läuft im Dienstkontext ins Leere; maßgeblich sind die Dateien.
- **NodePilot schreibt keine eigenen Einträge in die Ereignisanzeige.** Was dort unter dem Dienstnamen erscheint, stammt vom Dienststeuerungs-Manager.

## Welches Log wann

| Situation | Quelle | Worauf zu achten ist |
|---|---|---|
| Serverinstallation oder Update bricht ab | `%TEMP%\nodepilot-server-setup.log`, danach `install-report.txt` | Das Transkript wird angehängt: Zeilen eines früheren Laufs stehen in derselben Datei, daher Zeitstempel prüfen |
| Desktop meldet „Setup abgeschlossen“, die App startet aber nicht | `%TEMP%\nodepilot-provision.log` | Die Datei endet am fehlgeschlagenen Schritt; Ursachentabelle im [Desktop-Troubleshooting](https://github.com/Sev7eNup/NodePilot/blob/main/docs/desktop-troubleshooting.md) |
| Dienst startet und stoppt sofort | Ereignisanzeige → *Anwendung*, Quelle = Dienstname; danach das Anwendungslog | Typisch sind Konfigurations- oder ACL-Fehler, die vor dem ersten Schreibvorgang ins Log auftreten |
| `/healthz/ready` bleibt 503 | Zuerst `/healthz/database` — antwortet immer HTTP 200 mit `status` und `reason` —, dann das Anwendungslog | `RejectedByServer` bedeutet falsche Zugangsdaten, Datenbankauswahl oder TLS-Konfiguration und klärt sich nicht durch Warten |
| Ein einzelner Workflow-Step ist rot | Execution-Detail in der Oberfläche | Step-Ausgaben stammen aus der Datenbank, nicht aus den Logdateien; das Support-Log führt nur die redigierte Kurzfassung |
| „Was ist auf dem System passiert?“ | Seite `/support-log` (Admin), Umschalter Tabelle/Klartext | Die Tabellenansicht filtert und exportiert nach CSV oder NDJSON, die Klartextansicht zeigt die Datei |
| „Wer hat was geändert?“ | Seite `/audit`, siehe [Audit-Log](../security/audit-log) | Konfigurations- und Datenänderungen stehen dort, nicht im Anwendungslog |
| Auswertung über mehrere Hosts hinweg | `Logging:Format=ecs-json`, siehe [SIEM-Logging](../enterprise/siem-logging) | Ein Sammler liest dieselben Dateien; NodePilot versendet selbst nichts |

## Zugriff und Fallstricke

- **`C:\ProgramData\NodePilot` ist nur für Administratoren lesbar.** Im Explorer erscheint als Standardbenutzer „Zugriff verweigert“ — das ist die vorgesehene ACL, kein Schaden. Eine elevierte Konsole verwenden.
- **`%TEMP%` gehört dem Konto, das den Installer eleviert hat.** Wurde am UAC-Dialog ein anderes Administratorkonto angegeben, liegt das Transkript in dessen Temp-Verzeichnis.
- **Das Anwendungslog hält nur sieben Dateien.** Auf einem gesprächigen System — oder nach mehreren Größenrollovern an einem Tag — reicht es entsprechend wenige Tage zurück. Für einen Vorfall, der länger zurückliegt, sind die Dateien vorab zu sichern; Support-Log und `SupportEvents` reichen mit 90 Einheiten deutlich weiter.
- **Ausgeliefert wird `Logging:Format=cmtrace`,** nicht der Code-Default `text`. Die Dateien bleiben in jedem Editor lesbar; CMTrace.exe stellt sie zusätzlich mit Spalten und Farben dar.
- **Die Sektion `Logging` ist nicht hot-reloadbar.** Ein Formatwechsel wird erst nach einem Dienstneustart wirksam.

Eine Tagesdatei des Support-Logs lässt sich ohne Dateizugriff auf dem Server abholen — als Administrator angemeldet, gegen die eigene Installation:

```powershell
Invoke-WebRequest 'https://nodepilot.contoso.local/api/diagnostics/support-log/download?date=2026-08-29' -OutFile support.log
```

Erwartetes Ergebnis ist die vollständige Klartextdatei des angefragten Tages; existiert sie nicht, antwortet der Endpunkt mit HTTP 404. Ohne Datumsangabe liefert `GET /api/diagnostics/support-log` die letzten Zeilen der heutigen Datei (Standard 200, Obergrenze 1000). Beide Endpunkte sind Administratoren vorbehalten, und der Download wird im Audit-Log vermerkt.

## Was einem Ticket beiliegen sollte

Für eine Serverinstallation:

1. Produktversion und Betriebsart (Server oder Desktop).
2. Der Abschnitt des Anwendungslogs um den Zeitpunkt des Vorfalls.
3. Die Support-Log-Tagesdatei des Vorfallstages.
4. Die Ausgabe von `Get-Service NodePilot` und die Antwort von `/healthz/database`.
5. Bei einer gescheiterten Installation zusätzlich `%TEMP%\nodepilot-server-setup.log`.

Für die Desktop-App gilt dieselbe Liste mit `%TEMP%\nodepilot-provision.log` anstelle des Setup-Transkripts und `Get-Service NodePilot, NodePilotDb`.

**Logs vor dem Versand sichten.** Die Ausgaberedaktion maskiert erkannte Secrets, aber Skriptausgaben, Hostnamen und Pfade bleiben im Klartext.
