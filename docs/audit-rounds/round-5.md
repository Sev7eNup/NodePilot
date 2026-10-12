# Runde 5 – Fortsetzung mit Fokus auf praktische Auswirkungen

Start: 12. Oktober 2026, Branch `audit/practical-impact-2026-10-12`, Basis `de71058`.
Status: **abgeschlossen mit acht bestätigten und behobenen Medium-Funden**.
Die unabhängige Nachprüfung folgt in [Runde 6](round-6.md).

Der Nutzer hat das pausierte Goal wiederaufgenommen und den Umfang eingeschränkt:
Critical und High sowie Medium mit konkreter Auswirkung auf die tägliche Nutzung
von NodePilot. Reine Edge Cases müssen nicht behoben werden. Ein Finding braucht
einen nachvollziehbaren normalen Bedien-/Betriebsweg oder realistischen Angriff,
Gegenprüfung und einen sinnvollen Fix ohne Funktionsverlust. Große Dateien,
Duplikation oder fehlende Abstraktion allein reichen nicht.

Beide Skills werden erneut angewandt: `improve-codebase-architecture` und
`audit-ai-slop-code`. Zusätzliche Strukturprüfung ist eingeschlossen. Die vorhandene
Skill-Konfiguration in `CLAUDE.md` und `docs/agents/` wird weiterverwendet.
Die frühere Aufforderung zur eigenständigen Behebung ersetzt die interaktive
Kandidatenauswahl des Architektur-Skills.

## Abdeckung und Zuständigkeit

| Umfang | Nachweis |
| --- | --- |
| API und Core, Server-Autorisierung, Konfiguration, DTO-/Client-Verträge | [API/Core](round-5-api-core.md) |
| Engine, Scheduler, Data, Remote, Telemetry, AI und deren Verträge | [Runtime/AI](round-5-runtime-ai.md) |
| CLI, MCP, Switcher, Desktop, Deployment, Skripte, CI/Build, Grafana, LoadHarness | [Clients/Delivery](round-5-clients-delivery.md) |
| SPA, Designer-Restumfang, Demo, Doku-Website und Browsergrenzen | [UI/Docs](round-5-ui-docs.md) |
| Ergänzende Composition-Root-/Hosting-/Konfigurationsprüfung | [Hosting/Configuration](round-5-hosting-configuration.md) |
| Ergänzende Agent-Laufzeit-/Toolgrenzenprüfung | [Engine/Agents](round-5-engine-agents.md) |

Die Prüfung umfasst die aktuellen Module einschließlich der Änderungen seit
`318ae01`. Frühere Reports dienen zur Fortsetzung; sie ersetzen keine frische
Prüfung. Coverage-Angaben unterscheiden Inventar, gezielte Quellpfadprüfung und
ausgeführte Tests. Diese Runde ist wegen ihrer Funde keine Nullfundrunde.

## Übergabepunkte

- #133 (Low, alte Paging-Testfixture-Kompatibilität): außerhalb des neuen Scopes.
- #134 (Schritttest-Kontext/Ergebniszuordnung) behoben; kein RBAC-Bypass behauptet.
- #135–#141 nach Gegenprüfung behoben; alle gezielten Regressionen bestanden.
- Die bisherige [Fixliste](fix-list-de.md) bleibt erhalten und wird um bestätigte
  Korrekturen mit einfacher Erklärung ihrer möglichen Alltagsfolgen ergänzt.

Keine echten Installer, Dienstwechsel, Restore-, WinRM- oder Lastaktionen werden
aus diesem Auditauftrag abgeleitet. Builds und Tests bleiben lokal und gezielt;
.NET-Builds werden wegen gemeinsamer `bin`-/`obj`-Verzeichnisse koordiniert.

Das [Quellinventar](round-5-inventory.json) hält 3.148 Textdateien der Commitbasis
`de7105810af83cd21d20e32fc86bfbd3b4e280f4` fest, ohne Auditreports und Binärassets.
SHA-256 der sortierten Pfad-/Gitblob-Liste:
`1027c4e23bcb5e113bfc5bb7ec4ceaee4d03ccc9f39382ff3a1b25dc5b818009`.
Dies macht den Ausgangsstand nachvollziehbar; es behauptet keine manuelle
Zeile-für-Zeile-Prüfung jeder Datei. Vertiefte Pfade und Grenzen stehen in den Reports.

## Ergebnis und Tests

Keine neuen Critical-/High-Funde bestätigt. Die acht praktischen Medium-Korrekturen
sind #134–#141: Schritttestkontext, API-Bereitschaft beim Systemwechsel,
Schrittparameterherkunft, Agent-Konfigurationszustand, MCP-Teilansichtsredaktion,
Verbindungsbedingungen, begrenzte Historienabfragen und Dateiwächter-Deadlines.
Problem, Änderung und Alltagsfolge stehen in der [deutschen Fixliste](fix-list-de.md).

Ausgeführt: 70 unterschiedliche betroffene UI-Tests, TypeScript und fokussierter ESLint;
132 Switcher-Tests und 12 CLI-Basisfälle; später 136 CLI- und 215 MCP-Fälle;
110 Engine-Variablen-/Skriptfälle sowie 102 FileWatcher-/Orchestratorfälle.
Überlappende Testläufe werden nicht zu einer scheinbaren Gesamtabdeckung addiert.
Die Detailberichte nennen Reproduktionen, Logs und nicht ausgeführte Liveprüfungen.
