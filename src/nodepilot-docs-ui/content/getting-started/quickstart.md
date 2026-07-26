# Quickstart

Ziel dieser Seite: **ein Workflow, der wirklich läuft** — ohne dass du vorher eine Zielmaschine oder Zugangsdaten anlegen musst.

## Voraussetzung: eine laufende Instanz

Zwei Wege führen dorthin:

| | **Desktop-Installer** | **Dev-Setup** |
|---|---|---|
| Was du tust | eine `.exe` ausführen | PostgreSQL, Backend und Frontend einzeln starten |
| Bringt mit | PostgreSQL, .NET-Laufzeit, alles als Windows-Dienste | nichts — Voraussetzungen installierst du selbst |
| Wofür | produktiver Einzelplatz, schnellster Weg | Entwickeln am Produkt |
| Anleitung | [Desktop-App](../deployment/desktop) | [Installation](./installation) |

> **Zur Verfügbarkeit des Installers:** Die `.exe` ist ein **Build-Ziel**, kein fertiger Download — am GitHub-Release hängt derzeit kein signiertes Artefakt. Wer sie einsetzen möchte, baut sie mit `deploy\desktop\Build-DesktopInstaller.ps1` und signiert sie vor der Verteilung per Authenticode. Voraussetzungen für den Build und die vollständige Aufrufsyntax: [Desktop-App](../deployment/desktop).

Danach ist NodePilot erreichbar — beim Dev-Setup unter `http://localhost:5173`, bei der Desktop-App im Electron-Fenster.

## 1. Erster Login

Bei leerer Datenbank führt dich die Oberfläche durch die Einrichtung des **ersten Admin-Accounts**. Benutzername und Passwort legst **du** dabei fest; es gibt kein voreingestelltes Konto.

Abgesichert ist das über einen Einmal-Token, den das Backend beim ersten Start schreibt (`admin-setup.token`). Die Oberfläche liest ihn automatisch, du musst nichts abtippen; nach dem Anlegen wird er gelöscht. Danach stehen die Rollen **Admin / Operator / Viewer** zur Verfügung.

## 2. Ersten Workflow bauen

Im **Designer** einen neuen Workflow anlegen. Für den ersten Lauf reichen zwei Nodes:

1. Ein **Manual-Trigger**. Jeder Workflow braucht einen Trigger als Startpunkt — ohne aktiven Trigger findet die Engine keinen Einstieg und der Lauf schlägt sofort mit einer entsprechenden Meldung fehl.
2. Eine **`runScript`**-Activity, per Edge mit dem Trigger verbunden. Als Skript genügt `$env:COMPUTERNAME`.

Trag bei der Activity unter `outputVariable` einen Namen ein, zum Beispiel `hostInfo` — darüber kommst du später an das Ergebnis.

> **Ohne Zielmaschine läuft der Schritt lokal.** Lässt du das Feld *Maschine* leer, führt NodePilot das Skript im eigenen Prozess aus — kein WinRM, keine Credential nötig. Genau richtig für den ersten Test.

Wie das Ganze als JSON aussieht (das schreibt normalerweise der Designer für dich): [Workflow-JSON](../concepts/workflow-json).

## 3. Veröffentlichen

Workflows sind beim Bearbeiten gesperrt und deaktiviert — der SCOrch-artige **Edit-Lock**:

1. **Edit** — sperrt den Workflow für dich und schaltet ihn ab.
2. **Save** — sichert einen Zwischenstand, die Sperre bleibt.
3. **Publish** — speichert, aktiviert und gibt die Sperre frei. Erst jetzt ist der Workflow produktiv.

Hält jemand anderes die Sperre, sind die Schaltflächen für dich deaktiviert. Details: [Workflow-Kontrollfluss](../api/workflow-control).

## 4. Ausführen und zuschauen

Auf **Run** klicken. Der Lauf startet asynchron, der Fortschritt erscheint live — die Schritte färben sich, während sie laufen. Das läuft über SignalR, du musst nichts neu laden.

Dasselbe über die API:

```http
POST /api/workflows/{id}/execute
Content-Type: application/json

{ "parameters": {}, "timeoutSeconds": 120 }
```

Antwort: `202` plus die `ExecutionId`.

## 5. Ergebnis weiterverwenden

Unter **Executions** siehst du den Lauf und die Ausgabe jedes Schritts. Ein nachfolgender Schritt greift per Platzhalter darauf zu:

```
{{hostInfo.output}}    # Stdout — hier der Computername
{{hostInfo.success}}   # "true" / "false"
```

NodePilot setzt `{{hostInfo.output}}` automatisch als einfach-gequoteten String ein. Im Skript schreibst du also `$x = {{hostInfo.output}}` und **nicht** `$x = '{{hostInfo.output}}'`. Mehr: [Datenbus & Variablen](../concepts/data-bus).

## 6. Auf echte Maschinen zugreifen

Sobald ein Schritt nicht mehr lokal, sondern **auf einem anderen Windows-Host** laufen soll:

- **Machine** anlegen — das WinRM-Ziel.
- **Credential** anlegen — die Zugangsdaten dazu, DPAPI-verschlüsselt gespeichert.
- Beides am Node auswählen.

In der Oberfläche unter den jeweiligen Bereichen, oder über `POST /api/machines` und `POST /api/credentials`.

## 7. Automatisch starten lassen

Den Manual-Trigger durch einen echten ersetzen oder ergänzen: Zeitplan (`scheduleTrigger`, Quartz-Cron), neue Datei (`fileWatcherTrigger`), Datenbankzeile (`databaseTrigger`), Windows-Eventlog (`eventLogTrigger`) oder eingehender HTTP-Aufruf (`webhookTrigger`).

Was der Trigger mitbringt, steht im Lauf als `{{manual.<name>}}` zur Verfügung. Details: [Trigger](../triggers).

> Im Desktop-Betrieb funktionieren **eingehende** Webhooks nicht — dort lauscht NodePilot ausschließlich auf Loopback. Zeitplan-, Datei-, Datenbank- und Eventlog-Trigger laufen normal, ebenso jede ausgehende Automatisierung. Siehe [Betriebsarten im Überblick](../deployment/overview).
