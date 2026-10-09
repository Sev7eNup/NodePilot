# NodePilot Stress-Test Workflow

Künstlicher Last-Workflow: fächert von `log-start` auf **11 parallele Branches** auf, fängt sie mit `junction-all` (`waitAll`) wieder ein. Nutzt ausschließlich Engine-local Activities + `runScript` mit `targetMachineId: "localhost"` (In-Process-Bypass) — **kein WinRM-Target nötig**.

## Branches (alle parallel)

| # | Activity | Workload |
|---|---|---|
| 1 | `runScript` | Primzahl-Sieb bis 2·10⁶ |
| 2 | `runScript` | Σ√i für i=1..10⁷ |
| 3 | `runScript` | SHA-256 200 000× verkettet |
| 4 | `runScript` | 50 000 Regex-Matches auf generierten Log-Lines |
| 5 | `runScript` | 250×250 Matrix-Multiplikation |
| 6 | `runScript` | Sort 10⁶ Integers |
| 7 | `jsonQuery` | `$.items[?(@.val > 50)].id` auf 20-Item-Array |
| 8 | `jsonQuery` | `$..name` auf verschachtelter Org-Struktur |
| 9 | `xmlQuery` | `//host[@up='y']/@name` |
| 10 | `xmlQuery` | `sum(//host[@up='y']/@cpu)` (XPath-Aggregat) |
| 11 | `delay` | 55 s |

Alle 6 `runScript`-Branches laufen **in-process** → die API erzeugt 6 gleichzeitige PowerShell-Runspaces, die eine Weile ~alle Cores beschäftigen.

## Import

Die Datei ist eine reine Workflow-Definition (nicht das Export-Envelope). Der Launcher
meldet sich an, erstellt oder sperrt den Workflow und veröffentlicht die Definition,
bevor er die parallelen Ausführungen startet. Fremde Bearbeitungssperren und mehrdeutige
Workflow-Namen führen zum Abbruch.

```powershell
./scripts/stress-test/launch-40x.ps1 -BaseUrl http://localhost:5000 -User admin
# PowerShell fragt das erforderliche Passwort ab.
```

Alternativ: `python scripts/stress-test/launch-40x.py`. Die Python-Clients verwenden
`NODEPILOT_URL` (Standard `http://localhost:5000`), `NODEPILOT_USER` (Standard `admin`)
und `NODEPILOT_PASSWORD`; ohne Passwortvariable fragen sie interaktiv danach.
`launch-50-master.py` startet einen bereits veröffentlichten Workflow anhand
`NODEPILOT_STRESS_WORKFLOW`. Die Launcher liefern einen Fehler-Exitcode, wenn ein Start
oder eine Ausführung fehlschlägt oder bis zur Deadline kein Erfolg feststeht.

Die sechs CPU-Branches und die Verzögerung sind auf 55 Sekunden ausgelegt;
die Gesamtlaufzeit beträgt typischerweise etwa 55–60 Sekunden. Live-Fortschritt via SignalR.

## Warnung

Die 6 CPU-Burn-Branches **setzen die API-Host-CPU unter Volllast**. Auf einem Dev-Rechner ist das unkritisch, aber in Produktion vorher mindestens `Engine:Debug:*`-Limits und `Retention:*`-Services im Blick behalten — und ein laufender Stress-Test blockiert kein anderes Workflow-Execute, aber Response-Latenzen des Backends steigen sichtbar.
