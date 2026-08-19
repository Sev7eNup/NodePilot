# Einführung

NodePilot ist eine Workflow-Orchestrierung für Windows-Umgebungen. Arbeitsabläufe werden grafisch modelliert und automatisch ausgeführt. Für die Ausführung auf entfernten Windows-Systemen verwendet NodePilot WinRM. Auf den Zielsystemen ist kein zusätzlicher Agent erforderlich.

## Grundbegriffe

| Begriff | Bedeutung |
|---|---|
| **Workflow** | Vollständiger Ablauf aus Startpunkt, Arbeitsschritten und Verbindungen |
| **Trigger** | Startpunkt eines Workflows, zum Beispiel ein Zeitplan oder ein manueller Start |
| **Activity** | Einzelner Arbeitsschritt, zum Beispiel ein PowerShell-Skript oder ein REST-Aufruf |
| **Node** | Darstellung eines Triggers oder einer Activity im Designer |
| **Edge** | Verbindung zwischen zwei Nodes; kann eine Bedingung enthalten |
| **Execution** | Ein einzelner Lauf eines Workflows |

Beispiel: Ein Workflow prüft jede Nacht den freien Speicherplatz auf mehreren Servern. Ein Zeitplan startet den Lauf. Eine Activity liest den Speicherplatz. Eine Edge-Bedingung leitet nur kritische Ergebnisse an eine E-Mail-Activity weiter.

## Hauptkomponenten

- **Designer:** Grafische Erstellung und Bearbeitung von Workflows.
- **Engine:** Ausführung von Activities, parallelen Pfaden, Wiederholungen, Timeouts und Sub-Workflows.
- **Trigger-System:** Automatischer Start durch Zeitplan, Dateiänderung, Datenbankabfrage, Windows-Eventlog oder HTTP-Aufruf.
- **API und CLI:** Automatisierung der Verwaltungs- und Ausführungsfunktionen über REST oder das Kommandozeilenwerkzeug `np`.
- **Datenbank:** Speicherung von Workflows, Konfiguration, Ausführungen und Audit-Daten.

## Ausführungsorte

Eine Activity läuft an einem von zwei Orten:

| Ausführungsort | Beispiele |
|---|---|
| **NodePilot-Host** | REST, SQL, E-Mail, Bedingungen, lokale PowerShell-Ausführung |
| **Remote-Windows-System** | Dienste, Registry, Dateien, WMI und PowerShell über WinRM |

Einige Activities unterstützen beide Ausführungsorte. Die Zuordnung aller Typen steht unter [Activity-Typen und Scopes](../concepts/activities).

## Daten zwischen Schritten

Jede Activity kann ein Ergebnis im Datenbus ablegen. Spätere Activities greifen über Variablen darauf zu:

```text
{{hostInfo.output}}    # Ausgabe der Activity mit outputVariable "hostInfo"
{{hostInfo.success}}   # Ausführungsstatus als "true" oder "false"
{{globals.NAME}}       # globale Variable
```

Weitere Regeln und Beispiele enthält [Datenbus und Variablen](../concepts/data-bus).

## Unterstützte Betriebsarten

NodePilot besitzt drei unterstützte Betriebsarten:

| Betriebsart | Zweck |
|---|---|
| **Installation aus Quellcode** | Entwicklung und Test aus dem Repository |
| **Windows-Server-Deployment** | Produktivbetrieb für Teams, APIs, Webhooks und optional Hochverfügbarkeit |
| **Desktop-App** | Produktiver Einzelplatz auf Windows 11, ausschließlich lokal erreichbar |

Die Workflow-Engine ist in allen Betriebsarten gleich. Unterschiede bestehen bei Installation, Netzwerkzugriff, Dienstkonto, Datenbank, Authentifizierung und Hochverfügbarkeit. Der vollständige Vergleich steht unter [Betriebsarten](../deployment/overview).

## Empfohlener Einstieg

1. [Betriebsart auswählen](../deployment/overview).
2. [Installation](./installation) öffnen und die passende Variante ausführen.
3. [Ersten Workflow ausführen](./quickstart).
4. [Architektur](./architecture) und [Konzepte](../concepts/workflows) nach Bedarf vertiefen.
