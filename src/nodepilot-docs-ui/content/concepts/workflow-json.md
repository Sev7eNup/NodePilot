# Workflow-JSON-Format

NodePilot speichert die Darstellung eines Workflows als JSON. `nodes` enthält Trigger und Activities. `edges` enthält die Verbindungen zwischen den Nodes.

Das folgende Beispiel entspricht dem Workflow aus dem [Schnelleinstieg](../getting-started/quickstart):

```json
{
  "nodes": [
    {
      "id": "manual-trigger",
      "type": "activity",
      "position": { "x": 80, "y": 120 },
      "data": {
        "label": "Manueller Start",
        "activityType": "manualTrigger",
        "config": {
          "title": "Host prüfen",
          "parameters": []
        }
      }
    },
    {
      "id": "read-hostname",
      "type": "activity",
      "position": { "x": 360, "y": 120 },
      "data": {
        "label": "Hostnamen lesen",
        "activityType": "runScript",
        "outputVariable": "hostInfo",
        "config": {
          "engine": "auto",
          "timeoutSeconds": 30,
          "script": "$env:COMPUTERNAME"
        }
      }
    }
  ],
  "edges": [
    {
      "id": "manual-to-script",
      "source": "manual-trigger",
      "target": "read-hostname",
      "type": "labeled",
      "data": {
        "label": "Always",
        "disabled": false
      }
    }
  ]
}
```

## Node-Felder

| Feld | Bedeutung |
|---|---|
| `id` | Eindeutige ID innerhalb des Workflows |
| `position` | Position auf der Canvas |
| `data.label` | Angezeigter Name |
| `data.activityType` | Trigger- oder Activity-Typ |
| `data.outputVariable` | Name für spätere Variablenzugriffe |
| `data.config` | Einstellungen der Activity |
| `data.targetMachineId` | Optionale Zielmaschine für Remote-Activities |
| `data.credentialId` | Optionales Credential |
| `data.disabled` | Deaktiviert den Node |

## Edge-Felder

| Feld | Bedeutung |
|---|---|
| `id` | Eindeutige ID innerhalb des Workflows |
| `source` | ID des Ausgangs-Nodes |
| `target` | ID des Ziel-Nodes |
| `data.label` | Angezeigte Beschriftung |
| `data.condition` | Optionale Erfolgs- oder Fehlerbedingung |
| `data.disabled` | Deaktiviert die Verbindung |

Für den Import über die Web-UI wird die Workflow-Definition in ein Export-Envelope eingebettet. Ein vollständiges importierbares Beispiel steht am Ende des [Schnelleinstiegs](../getting-started/quickstart).
