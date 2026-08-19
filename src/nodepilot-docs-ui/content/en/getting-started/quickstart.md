# Quick start

This quick start creates and runs a minimal workflow:

```text
Manual trigger -> Run Script
```

No target machine and no credential are required.

## Prerequisites

- A running NodePilot instance
- An admin or operator account

Installation routes are under [Installation](./installation).

## 1. Create the workflow

1. Open **Workspace → Workflows**.
2. Choose **New workflow**.
3. Enter `Check host` as the name.
4. Choose **Create**, then **Edit**.

## 2. Add the nodes

1. Drag a **manual trigger** onto the canvas.
2. Add a **Run Script** activity.
3. Connect both nodes with an edge.
4. Enter the following script in **Run Script**:

```powershell
$env:COMPUTERNAME
```

5. Enter `hostInfo` as the **output variable**.
6. Leave **Machine** empty.

## 3. Publish and run

1. Choose **Publish**.
2. Choose **Run**.
3. Under **Executions**, open the output of the Run Script activity.

The output contains the name of the NodePilot host.

## Importing the example as JSON

The following file contains the same workflow in NodePilot's importable format.

In short:

1. Save the complete JSON block as `check-host.json` in UTF-8.
2. In the web UI, open **Workspace → Workflows**.
3. Optionally choose a target folder.
4. Choose **Import** and open `check-host.json`.
5. Open the imported workflow, publish it and run it.

```json
{
  "schema": "nodepilot-workflow-export/v1",
  "exportVersion": 1,
  "exportedAt": "2026-07-27T00:00:00Z",
  "workflows": [
    {
      "name": "Check host",
      "description": "Minimal example workflow for the quick start",
      "definition": {
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
    }
  ]
}
```
