# Einführung

**NodePilot** ist ein schlanker Ersatz für Microsoft System Center Orchestrator (SCOrch). Du baust Abläufe grafisch aus Bausteinen zusammen, NodePilot führt sie aus — auf Windows-Maschinen im Netz, **agentless** über WinRM. Auf den Zielmaschinen muss nichts installiert werden.

## Was ein Workflow ist

Ein Workflow ist ein Ablaufplan auf einer Canvas. **Nodes** sind die Arbeitsschritte — ein PowerShell-Skript ausführen, einen Dienst neu starten, eine REST-API aufrufen, eine Mail verschicken. **Edges** verbinden sie und entscheiden, wie es weitergeht, etwa „nur wenn der Schritt davor erfolgreich war".

Ein typisches Beispiel: *jede Nacht um 2 Uhr auf zwölf Servern den freien Platz auf `C:` prüfen und bei unter 10 % eine Mail ans Team schicken.* Ein Trigger startet den Lauf, ein Schritt sammelt die Werte, eine Bedingung an der Edge entscheidet, ob der Mail-Schritt überhaupt läuft.

## Die vier Teile

- **Designer** — die Oberfläche, in der du Workflows baust. Jede Activity hat ihren eigenen Node-Typ mit Icon und Eigenschaften-Panel.
- **Engine** — führt die Workflows aus: mehrere Schritte parallel, Wiederholung bei Fehlern, Timeouts und ein Debugger mit Breakpoints.
- **Trigger** — starten Läufe von selbst: Zeitplan, neue Datei, Datenbankzeile, Windows-Eventlog, eingehender HTTP-Aufruf. Oder du drückst auf Start.
- **API & CLI** — alles, was die Oberfläche kann, geht auch über die REST-API oder das Kommandozeilen-Tool `np`.

## Wo ein Schritt läuft

Jeder Schritt läuft entweder **auf einer Zielmaschine** (über WinRM — Dienste steuern, Registry lesen, Dateien anfassen) oder **im NodePilot-Prozess selbst** (REST-Aufrufe, SQL, E-Mail, Verzweigungen). Zwei Activities können beides, je nach Konfiguration. Welche wohin gehört: [Activity-Typen & Scopes](../concepts/activities).

## Wie Daten weiterfließen

Jeder Schritt legt sein Ergebnis im **Datenbus** ab. Spätere Schritte greifen per Platzhalter darauf zu:

```
{{hostInfo.output}}    # Ausgabe des Schritts "hostInfo"
{{hostInfo.success}}   # "true" / "false"
{{globals.NAME}}       # globale Variable
```

Mehr dazu: [Datenbus & Variablen](../concepts/data-bus).

## Wie du NodePilot betreibst

Drei Betriebsarten — die Engine kann in allen dasselbe, der Unterschied ist, **wer zugreifen kann** und **wie viel du selbst installierst**:

- **Desktop-App** — ein `.exe`-Installer bringt Datenbank und Laufzeit mit und richtet alles als Dienste ein. Erreichbar nur auf dieser einen Maschine. Der schnellste Weg zu einem produktiven Einzelplatz.
- **Server-Deployment** — Windows-Dienst mit externer Datenbank, erreichbar fürs ganze Team, eingehende Webhooks funktionieren.
- **Dev-Setup** — Backend und Frontend von Hand starten. Zum Entwickeln am Produkt selbst.

Der vollständige Vergleich mit allen Alltags-Konsequenzen: [Betriebsarten im Überblick](../deployment/overview).

## Wo es weitergeht

- [Quickstart](./quickstart) — in wenigen Minuten zum ersten laufenden Workflow.
- [Installation](./installation) — das Dev-Setup Schritt für Schritt.
- [Architektur](./architecture) — Solution-Struktur, Dep-Graph, Execution-Modell.
- [Konzepte](../concepts/workflows) — Workflows, Activities, Trigger und Datenbus im Detail.
