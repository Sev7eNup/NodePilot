# Performance und Dimensionierung

NodePilot dimensioniert Runspaces, parallele Steps, ThreadPool und Dispatch-Worker
beim Start anhand der erkannten CPU und des verfügbaren Speichers.
`Performance:ManualTuning` ist standardmäßig `false`. Bei `true` gelten die gespeicherten
Werte unverändert. Der Wechsel benötigt einen Dienstneustart.

## Wirksame Einstellungen

Die Admin-Oberfläche unterscheidet gewählten und laufenden Modus. Die tatsächlich
aktiven Werte und ihre begrenzende Größe liefert der Prozess selbst:

```powershell
np settings effective-sizing
```

Der zugehörige Endpoint ist `GET /api/admin/settings/effective-sizing`.
Unter automatischer Dimensionierung sind die gespeicherten Tuning-Werte inaktiv.
`Threading` lässt sich nur im manuell gestarteten Modus zur Laufzeit ändern.

`Engine:MaxConcurrentExecutions:Global` und `PerUser` bleiben in beiden Modi
konfigurierte Sicherheitsgrenzen. Die ausgelieferten Werte sind 5000 und 2000,
die Code-Fallbacks bei fehlenden Schlüsseln 500 und 200.

## Automatische Dimensionierung

Die CPU-Formel wird durch das Speicherbudget sowie Unter- und Obergrenzen begrenzt.
Die Berechnung liegt in `PerformanceSizing`, die Erkennung in `PerformancePlanFactory`.

| Einstellung | CPU-Formel | Untergrenze | Auto-Obergrenze |
|---|---|---:|---:|
| `Engine:Runspace:MaxRunspaces` | `min(64, max(8, Kerne * 4))` | 8 | 64 |
| `Engine:Runspace:MinRunspaces` | `MaxRunspaces / 8` | 1 | 8 |
| `Engine:MaxConcurrentSteps` | `Kerne * 32` | 32 | 600 |
| `Threading:MinWorkerThreads` und `MinIoCompletionThreads` | `max(200, Kerne * 16)` | 64 | 768 |
| `ExecutionDispatch:WorkerCount` | `Kerne * 3` | 20 | 200 |

Das Speicherbudget verwendet 60 % des erkannten Speichers im Serverbetrieb und 25 %
im Desktopbetrieb, abzüglich 512 MB Sockel. Davon entfallen 50 % auf Runspaces,
25 % auf laufende Steps und 25 % auf Reserve. Die Kostenannahmen sind konservative
Schätzwerte und keine Messung des konkreten Workloads.

Die Erkennung berücksichtigt Prozess- und Job-Limits. Werte unter 1 GB gelten als
fehlgeschlagene Speichererkennung und führen zur CPU-basierten Berechnung.
Der serverseitige DB-Pool gehört nicht zu diesem Speicherbudget.

## Manuelles Tuning

Das konfigurierte Hochlast-Preset wurde für 20 Kerne und bis zu 500 parallele
Workflows gemessen. Es ist kein allgemeiner Standard für kleinere Systeme.
Die aktuellen Vorgaben stehen in [appsettings.json](../src/NodePilot.Api/appsettings.json)
und den [Deployment-Templates](../deploy/templates/).

| Einstellung | Konfiguriertes Preset |
|---|---:|
| Dispatch-Worker | 600 |
| Parallele Steps | 600 |
| Runspaces, Minimum / Maximum | 256 / 768 |
| Worker- und I/O-Threads, Minimum | Jeweils 768 |
| DB-Verbindungspool, Minimum / Maximum | 40 / 800 |

CPU, Activity-Mix, Remote-Latenz und Datenbank bestimmen den Durchsatz gemeinsam.
Vor Änderungen deshalb den begrenzenden Bereich messen. Größere Pools können
Wartezeiten und Speicherverbrauch erhöhen, ohne mehr Arbeit abzuschließen.

## Laufzeitverhalten

Wartende Starts liegen dauerhaft in der DB-Outbox. `ExecutionDispatch:Capacity`
ist entfernt, `WorkerCount` bestimmt die gleichzeitig arbeitenden Dispatcher.
Workflowbezogene Limits werden vor dem Start berücksichtigt.

`startWorkflow` und `forEach` teilen standardmäßig 128 Slots für aktive Kind-Workflows.
Vollständig auf Kinder wartende Vorfahren geben ihren Slot bis zur Fortsetzung frei.
Aktive Geschwister halten ihn weiter. Der globale Execution-Cap zählt auch wartende
Vorfahren, siehe [ADR 0018](adr/0018-active-subworkflow-capacity.md).

Dashboard und Listen verwenden vorberechnete Statistiken. Der Refresh-Stand kann
daher hinter der Live-Ausführung liegen. Bei Messungen zwischen Datenbankabfrage,
API-Laufzeit und Darstellung unterscheiden.

Der In-Process-Runspace-Pool lädt keine implizite Windows-PowerShell-Kompatibilität.
Eager Pre-Warm und gemeinsam genutzte FormatTables wurden verworfen. Die
[Roadmap](roadmap.md) führt die verbindlichen Sperrvermerke für erneut vorgeschlagene
Tuning-Ansätze.

## Messnachweise

Datierte Lasttests, verworfene Varianten und Ursachenanalysen stehen im
[Archiv der Performance-Messungen](archived/performance-measurements.md).
Die dortigen Konfigurationen gelten für den jeweiligen Messstand.
Neue Vergleiche benötigen denselben Workload, vergleichbare Datenbestände und
mehrere Läufe. Ein begrenzter Heap-Ausschnitt allein belegt keine Speicherursache.
