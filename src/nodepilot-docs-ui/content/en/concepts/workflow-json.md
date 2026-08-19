# Workflow JSON format

NodePilot stores a workflow's representation as JSON. `nodes` contains triggers and activities. `edges` contains the connections between the nodes.

The following example matches the workflow from the [Quick start](../getting-started/quickstart):

```json
{
  "nodes": [
    {
      "id": "manual-trigger",
      "type": "activity",
      "position": { "x": 80, "y": 120 },
      "data": {
        "label": "Manual start",
        "activityType": "manualTrigger",
        "config": {
          "title": "Check host",
          "parameters": []
        }
      }
    },
    {
      "id": "read-hostname",
      "type": "activity",
      "position": { "x": 360, "y": 120 },
      "data": {
        "label": "Read host name",
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

## Node fields

| Field | Meaning |
|---|---|
| `id` | Unique ID within the workflow |
| `position` | Position on the canvas |
| `data.label` | Displayed name |
| `data.activityType` | Trigger or activity type |
| `data.outputVariable` | Name for later variable access |
| `data.config` | The activity's settings |
| `data.targetMachineId` | Optional target machine for remote activities |
| `data.credentialId` | Optional credential |
| `data.disabled` | Disables the node |

## Edge fields

| Field | Meaning |
|---|---|
| `id` | Unique ID within the workflow |
| `source` | ID of the source node |
| `target` | ID of the target node |
| `data.label` | Displayed caption |
| `data.condition` | Optional success or failure condition |
| `data.disabled` | Disables the connection |

For an import through the web UI, the workflow definition is embedded in an export envelope. A complete importable example is at the end of the [Quick start](../getting-started/quickstart).
