# Performance-Dimensionierung

NodePilot leitet seine Parallelitäts-Grenzen beim Start aus der erkannten Hardware ab. `Performance:ManualTuning` schaltet zwischen dieser automatischen Dimensionierung und den wörtlich konfigurierten Werten um.

| Key | Default | Wirkung |
|---|---|---|
| `Performance:ManualTuning` | `false` | `false` = Werte aus erkannter CPU + RAM ableiten. `true` = `Engine:Runspace:*`, `Engine:MaxConcurrentSteps`, `Threading:*` und `ExecutionDispatch:*` unverändert aus der Config nehmen. **Restart-pflichtig.** |

Der Schalter ist bewusst nicht hot-reloadbar: Runspace-Pool und Dispatch-Worker-Pool entstehen einmalig beim Boot. Der Plan wird deshalb genau einmal gebaut (`PerformancePlanFactory`) und von allen Consumern gelesen — sonst liefe der hot-reloadbare ThreadPool im einen Modus, während boot-feste Consumer noch im anderen stehen.

## Was der Schalter umfasst — und was nicht

Automatisch dimensioniert werden:

`Engine:Runspace:MinRunspaces` · `Engine:Runspace:MaxRunspaces` · `Engine:MaxConcurrentSteps` · `Threading:MinWorkerThreads` · `Threading:MinIoCompletionThreads` · `ExecutionDispatch:WorkerCount`

**Ausgenommen ist `Engine:MaxConcurrentExecutions:*`** (`Global` / `PerUser`). Das ist ein Sicherheits-Cap gegen pathologische Fälle — Trigger-Schleifen, Sub-Workflow-Kaskaden —, kein Durchsatz-Hebel. Wer einen Wert setzt, meint ihn; ihn aus der Hardware abzuleiten würde genau die Schranke entschärfen, die konfiguriert wurde. Der Cap gilt daher in **beiden** Modi.

Steht `ManualTuning` auf `false`, sind die Zahlen der genannten Sektionen in `appsettings.json` ein **inertes Preset**: sie bleiben lesbar stehen, wirken aber nicht.

## Was tatsächlich in Kraft ist

Weil die Config-Datei im Automatik-Modus nicht die Wahrheit sagt, gibt es dafür einen eigenen Endpoint. Er liefert je Wert zusätzlich den **Constraint**, der ihn erzeugt hat:

```
GET /api/admin/settings/effective-sizing
```

```powershell
np settings effective-sizing
```

| Constraint | Bedeutung |
|---|---|
| `Cpu` | Die CPU-Formel war der kleinste Kandidat |
| `Ram` | Das Speicher-Teilbudget war der kleinste Kandidat |
| `Floor` | Ergebnis lag unter dem minimal sinnvollen Wert |
| `Ceiling` | Ergebnis lag über dem gemessen abgesicherten Bereich |
| `Manual` | Wörtlich aus der Config (`ManualTuning: true`) |

Die Antwort nennt außerdem den gebooteten Modus **und** den gespeicherten. Weichen beide ab, wurde der Schalter nach dem Start umgelegt und wirkt erst nach einem Neustart.

## Modell

**CPU-Dimension.** `MaxRunspaces` = Cores × 4, `MaxConcurrentSteps` = Cores × 32, `Threading:*` = max(200, Cores × 16), `ExecutionDispatch:WorkerCount` = Cores × 3.

**Speicher-Dimension.** Vom erkannten Speicher wird ein fixer Grundbedarf von 512 MB abgezogen (Runtime, EF-Modell, Caches, Telemetry — gemessener Idle-Footprint 383–444 MB, aufgerundet). Vom Rest beansprucht NodePilot **60 %** im Server-Modus und **25 %** bei `Deployment:Mode=Desktop`, weil das Desktop-Paket sich die Maschine mit Postgres, der Electron-Shell und den Anwendungen des Nutzers teilt. Dieses App-Budget wird als **ein Haushalt** aufgeteilt — Runspaces 50 %, Steps 25 %, der Rest ist bewusste Reserve für GC-Spitzen. Jeden Wert einzeln gegen das volle Budget zu rechnen würde denselben Speicher mehrfach ausgeben. Wartender Dispatch liegt in der Datenbank-Outbox und hat deshalb keine In-Memory-Queue-Kapazität mehr.

Der **kleinere** der beiden Kandidaten gewinnt, danach greifen Floor und Ceiling. Die Speicher-Dimension kann einen Plan damit nur verkleinern, nie vergrößern.

**Erkennung fehlgeschlagen.** Meldet die Plattform weniger als 1 GB, gilt das als fehlgeschlagene Messung und nicht als kleine Maschine — kein unterstützter Host läuft unter 1 GB. Die Dimensionierung fällt dann auf die reine CPU-Formel zurück; `effective-sizing` weist den Speicher als nicht erkannt aus.

**Grenzen.** Floors halten eine 2-Core-/4-GB-Maschine lauffähig; Ceilings stoppen die Extrapolation am Rand des gemessenen Bereichs.

| Wert | Floor | Ceiling |
|---|---|---|
| `MinRunspaces` | 1 | 8 |
| `MaxRunspaces` | 8 | 64 |
| `MaxConcurrentSteps` | 32 | 600 |
| `Threading:*` | 64 | 768 |
| `ExecutionDispatch:WorkerCount` | 20 | 200 |

`MinRunspaces` bleibt im Automatik-Modus immer **1**: `RunspacePool.Open()` materialisiert das Minimum sofort, und eifriges Vorwärmen ist ein gemessener Anti-Pattern (28 % Regression). Der Pool wächst unter echter Last ohnehin.

## Wann sich der Schalter lohnt

Die Automatik liefert einen **sicheren, monoton skalierenden Default mit begrenztem Ressourcenrisiko** — kein universelles Optimum. Das Optimum hängt zusätzlich an Workflow-Anzahl, Activity-Mix, Step-Laufzeit, Remote-Latenz und DB-Provider; nichts davon ist beim Boot bekannt. Die Automatik zielt deshalb auf leichte bis mittlere Last.

Das gemessene Hochlast-Profil — 768 Runspaces für 500 parallele Workflows auf 20 Cores — bleibt bewusst dem manuellen Modus vorbehalten, damit die Automatik es nie stillschweigend herunterstuft. Wer dieses Profil fährt, setzt `Performance:ManualTuning: true` und übernimmt die Werte aus der Lastprofil-Tabelle in [`docs/performance-improvements.md`](https://github.com/Sev7eNup/NodePilot/blob/main/docs/performance-improvements.md).
