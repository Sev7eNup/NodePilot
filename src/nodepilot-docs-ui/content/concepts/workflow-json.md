# Workflow-JSON-Format

Workflows werden als JSON gespeichert und ausgetauscht. Ein Node ist eine Activity (oder ein Trigger), eine Edge trägt Condition-Daten und optionale Kontrollpunkte für manuelles Routing.

## Struktur

```json
{
  "nodes": [{
    "id": "step-123",
    "type": "activity",
    "position": { "x": 100, "y": 200 },
    "data": {
      "label": "Check Disk",
      "activityType": "runScript",
      "targetMachineId": "guid",
      "credentialId": null,
      "outputVariable": "diskCheck",
      "config": { "script": "Get-PSDrive C", "timeoutSeconds": 60 }
    }
  }],
  "edges": [{
    "id": "e1",
    "source": "step-123",
    "target": "step-456",
    "type": "labeled",
    "data": {
      "label": "On Success",
      "condition": "step-123.success",
      "disabled": false,
      "controlPoints": { "cp1x": 240, "cp1y": 200, "cp2x": 360, "cp2y": 200 }
    }
  }]
}
```

## Felder

### Node `data`

| Feld | Bedeutung |
|---|---|
| `label` | Anzeige-Name im Designer |
| `activityType` | Activity-Typ (siehe [Referenz](../activities-reference)) |
| `targetMachineId` | Zielmaschine (nur Remote-Activities) |
| `credentialId` | Optionale Credential; null = Localhost-Bypass |
| `outputVariable` | Name im Datenbus. Fehlt er → Step-ID wird verwendet (`{{step-123.output}}`) |
| `config` | Activity-spezifische Config-Keys |
| `disabled` | `true` → Node wird `Skipped` |

### Edge `data`

| Feld | Bedeutung |
|---|---|
| `label` | Anzeige-Label |
| `condition` | Shortcut `stepId.success` / `stepId.failed` |
| `conditionExpression` | Strukturierte AST-Bedingung (siehe [Edge-Bedingungen](./edge-conditions)) |
| `disabled` | `true` → Edge wird übersprungen |
| `controlPoints` | `{ cp1x, cp1y, cp2x, cp2y }` — überschreibt Auto-Routing |

## Control Points

`data.controlPoints` überschreibt das Auto-Routing der Edge. Fehlt es, greift das bestehende Routing. Damit lassen sich manuell saubere Kantenverläufe erzeugen — siehe `docs/workflow-styleguide.md` für Layout-Konventionen.

## Styleguide

Vor dem manuellen Bau von Workflow-JSONs **zuerst** `docs/workflow-styleguide.md` lesen (Layout-Regeln, Edge-Label-Konventionen, Engine-Gotchas). Referenz-Beispiel: `scripts/test-master-all-activities.json`.