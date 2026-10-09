# Runde 4 – vollständige Prüfung mit gewechselten Zuständigkeiten

Status: **auf Wunsch des Nutzers pausiert, kein Abschlussurteil**. Begonnene Korrekturen
werden zum überprüfbaren Zwischenstand abgeschlossen; es beginnt keine weitere Prüfrunde.
Fortsetzung und noch offene Bereiche: [Pausenstand](pause-2026-10-09.md).

Startstand: `audit/architecture-security-2026-10-09`, Basis `a96eb96`, einschließlich
aller korrigierten Runde-3-Befunde. Beide angeforderten Skills werden erneut angewandt.
Neue bestätigte Critical-/High-/Medium-Befunde erfordern nach Korrektur wiederum
eine neue vollständige Projektprüfung.

Das [Dateiinventar](round-4-inventory.json) vom **2026-10-09, 11:33:25 UTC** enthält
**3178 Dateien**. SHA-256 des sortierten Pfad-/Inhaltinventars:
`586be1aacd35e96be2906a90bb17a144def44e28778389473d959cf1db6a8e42`.
Verfahren und Ausschlüsse entsprechen Runde 3. Dies dokumentiert den Startstand,
nicht automatisch eine abgeschlossene Prüfung. Spätere Änderungen werden im Rundennachweis festgehalten.

| Zuständigkeit | Vollständiger Quellenumfang | Nachweis |
|---|---|---|
| Bisheriger Clientprüfer | API, Core und zugehörige Tests/Verträge | `round-4-api-core.md` |
| Bisheriger API/Core-Prüfer | Engine, Scheduler, Data, Remote, Telemetry, AI und zugehörige Tests/Verträge | `round-4-runtime-ai.md` |
| Bisheriger Runtime-Prüfer | CLI, MCP, Desktop, Switcher, LoadHarness, deploy, scripts, CI, Build, Grafana, Samples, TestCommons und Betriebsverträge | `round-4-clients-delivery.md` |
| Root | SPA, Browser-Demo, Docs-UI/Website, deren Builds, Tests, Client-Verträge und strukturelle Gegenprüfung | `round-4-ui-docs.md` |

Die Prüfung umfasst unveränderte Module. Pro Familie werden Architekturverantwortung,
Interface/Implementation, vorhandene Seams, Deletion Test und konkrete Eingabe-/Rechte-/
Ausgabewege geprüft. Große Dateien oder viele Helfer allein sind keine Befunde.
Kandidaten brauchen eine konkrete Auswirkung und Gegenprüfung; bloß vermutete Risiken
werden nicht in bestätigte mittlere Mängel umbenannt.

Tests laufen mit tatsächlicher Projekt-/Filterangabe. .NET-Builds bleiben seriell.
Externe Verzeichnisdienste, reale WinRM-Ziele, produktive Datenbankserver, Installer,
Restore und Release-Lab werden durch diese lokale Prüfung nicht als live abgenommen bezeichnet.
