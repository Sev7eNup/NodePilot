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
| 11 | `delay` | 5 s |

Alle 6 `runScript`-Branches laufen **in-process** → die API erzeugt 6 gleichzeitige PowerShell-Runspaces, die eine Weile ~alle Cores beschäftigen.

## Import

Die Datei ist eine reine Workflow-Definition (nicht das Export-Envelope). Zwei Wege:

```powershell
# 1) Direkt via API (empfohlen — analog scripts/tech-demo/seed.ps1):
$def = Get-Content scripts/stress-test/main.json -Raw
$body = @{ name = "Stress-Test"; description = "Ad-hoc load"; definitionJson = $def; isEnabled = $true } | ConvertTo-Json -Depth 50
Invoke-RestMethod -Method POST -Uri http://localhost:5000/api/workflows `
  -Headers @{ Authorization = "Bearer $token"; "Content-Type" = "application/json" } `
  -Body $body
```

```
# 2) UI: "New Workflow" → Editor → "Import JSON" (falls vorhanden) oder in den
#    Designer die nodes/edges via Dev-Tools-Load einspielen.
```

Nach dem Import: **Run** klicken, optional Label eingeben. Laufzeit je nach CPU ~10–25 s. Live-Fortschritt via SignalR.

## Warnung

Die 6 CPU-Burn-Branches **setzen die API-Host-CPU unter Volllast**. Auf einem Dev-Rechner ist das unkritisch, aber in Produktion vorher mindestens `Engine:Debug:*`-Limits und `Retention:*`-Services im Blick behalten — und ein laufender Stress-Test blockiert kein anderes Workflow-Execute, aber Response-Latenzen des Backends steigen sichtbar.
