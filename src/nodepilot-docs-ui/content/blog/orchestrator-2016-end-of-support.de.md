# System Center 2016 Orchestrator: Das Supportende am 11. Januar 2027 planen

Am 11. Januar 2027 endet der erweiterte Support für System Center 2016 Orchestrator. Danach veröffentlicht Microsoft für diese Version keine Sicherheitsupdates mehr, und es gibt keinen kostenlosen oder bezahlten Support. Die Runbooks laufen an diesem Tag nicht plötzlich schlechter. Sie laufen aber auf einer Plattform, für die neu entdeckte Schwachstellen nicht mehr geschlossen werden. [Microsoft Lifecycle: System Center 2016 Orchestrator](https://learn.microsoft.com/de-de/lifecycle/products/system-center-2016-orchestrator).

Betroffen ist nur die Version 2016. System Center 2019 Orchestrator ist bis zum 9. April 2029 unterstützt, die Version 2022 bis zum 13. April 2032 und die Version 2025 bis zum 9. Januar 2035. Wer eine neuere Version betreibt, hat Zeit, sollte die Fragen unten aber trotzdem kennen.

## Drei Fragen vor jeder Entscheidung

Ob ein Upgrade oder ein anderes Werkzeug sinnvoll ist, lässt sich ohne Bestandsaufnahme nicht beantworten. Drei Fragen helfen, den tatsächlichen Umfang zu erkennen:

- **Welche Runbooks laufen wirklich?** Über Jahre sammeln sich Runbooks an, die niemand mehr startet. Die Ausführungshistorie zeigt, welche Abläufe regelmäßig laufen und welche nur noch im Ordnerbaum stehen.
- **Welche Integration Packs werden verwendet?** Standardaktivitäten lassen sich meist ersetzen. Ein Integration Pack für ein Fremdsystem bestimmt dagegen oft, welcher Weg überhaupt infrage kommt.
- **Unter welchen Konten und gegen welche Ziele?** Dienstkonten, gespeicherte Zugangsdaten und Firewall-Freigaben stehen nicht im Runbook-Diagramm, entscheiden aber über jeden Umzug.

Das Ergebnis ist eine Liste mit Priorität: Runbooks mit Schreibzugriff auf produktive Systeme verdienen mehr Prüfung als eine reine Statusabfrage.

## Die möglichen Wege

**Upgrade auf System Center 2025 Orchestrator.** Microsoft beschreibt Upgrades nur von der jeweils vorherigen Version: [2016 (ab Update Rollup 6) auf 2019, 2019 auf 2022 und 2022 auf 2025](https://learn.microsoft.com/en-us/system-center/orchestrator/upgrade-orch?view=sc-orch-2025). Eine 2016-Umgebung braucht daher drei Schritte. Jeder Schritt deinstalliert und installiert die Orchestrator-Komponenten neu und verlangt bei Bedarf ein neueres Betriebssystem oder weitere Software. Die Alternative ist eine Neuinstallation von 2022 oder 2025 neben der alten Umgebung, in die die Runbooks per Export und Import im Runbook Designer übernommen werden. Die Integration Packs sind auf beiden Wegen zu prüfen: Orchestrator 2022 und neuer sind 64-Bit-Anwendungen, und [laut Kelverion](https://www.kelverion.com/blog/migrating-from-orchestrator-2016-or-2019-to-orchestrator-2022) sind Packs für 2019 und älter nicht kompatibel. Lizenzierung und Betrieb der System-Center-Infrastruktur bleiben bestehen.

**Azure Automation.** Microsoft bietet ein Migrations-Toolkit an, das die eigene Anleitung als Beta führt. Es wandelt Runbooks in grafische Runbooks um. Monitor-Aktivitäten, Variablen und Verbindungen werden nicht übernommen und müssen neu angelegt oder ersetzt werden. Damit die Runbooks lokale Systeme erreichen, ist ein Hybrid Runbook Worker nötig. [Microsoft: Von Orchestrator zu Azure Automation migrieren](https://learn.microsoft.com/de-de/azure/automation/automation-orchestrator-migration).

**Service Management Automation (SMA).** SMA führt Runbooks im eigenen Rechenzentrum aus, unterstützt aber keine grafischen Runbooks. Laut derselben Microsoft-Anleitung müssen Orchestrator-Runbooks dafür von Hand in PowerShell neu geschrieben werden.

**Ein anderer Workflow-Orchestrator.** Werkzeuge wie NodePilot bleiben im eigenen Netz und arbeiten agentenlos über WinRM. NodePilot importiert Exporte im Format `.ois_export`, bildet unterstützte Aktivitäten ab und lässt nicht unterstützte als deaktivierte Platzhalter stehen. Gespeicherte Zugangsdaten werden nicht übernommen, und importierte Workflows sind zunächst deaktiviert. Wie das im Einzelnen aussieht, beschreibt der Beitrag [SCOrch-Runbooks importieren und das Ergebnis prüfen](blog/scorch-import/).

## Ein Migrationspfad in fünf Schritten

Unabhängig vom Ziel hat sich eine schrittweise Umstellung bewährt:

1. **Inventar erstellen:** aktive Runbooks, Integration Packs, Konten und Zeitpläne auflisten.
2. **Mit einem lesenden Runbook beginnen:** zum Beispiel eine tägliche Statusabfrage, die nichts verändert.
3. **Erwartungen vorher festhalten:** Ausgabe im Erfolgsfall, Verhalten bei einem Fehler, Verhalten bei einem nicht erreichbaren Ziel.
4. **Kontrolliert parallel betreiben:** Der neue Ablauf läuft zunächst manuell oder gegen Testziele. Zwei aktive Zeitpläne für denselben Auftrag würden ihn doppelt ausführen.
5. **Erst nach der Abnahme umschalten:** den Zeitplan im neuen Werkzeug aktivieren und das Runbook in SCOrch deaktivieren, aber noch nicht löschen.

Mit dieser Reihenfolge lässt sich bis zum Stichtag ein belastbarer Teil der Runbooks umstellen, ohne alles auf einmal zu bewegen. Was bis dahin nicht fertig ist, bleibt auf der alten Plattform, deren Risiko dann bewusst getragen wird.

Einen Überblick über Supportdaten, Wege und den Vergleich mit NodePilot bietet die Seite [Eine Open-Source-Alternative zu System Center Orchestrator prüfen](scorch-alternative/).
