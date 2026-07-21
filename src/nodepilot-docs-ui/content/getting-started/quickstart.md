# Quickstart

Voraussetzung: Backend und Frontend laufen (siehe [Installation](./installation)).

## 1. Erster Login

Im Frontend (`http://localhost:5173`) mit dem Initial-Admin einloggen. Bei leerer DB wird beim ersten Login der Admin-Account erstellt. Dev-Default: `admin` / `admin123`.

## 2. Maschine & Credential anlegen

Eine **Machine** ist ein WinRM-Ziel (`targetMachineId`). Eine **Credential** ist ein DPAPI-verschlüsselter Login-Datensatz. Beides im UI unter den jeweiligen Bereichen anlegen oder via API:

```http
POST /api/machines
POST /api/credentials
```

Für lokale Prozess-Ausführung ohne Credential gibt es den Localhost-Bypass (siehe [Security](../security/overview)).

## 3. Workflow bauen

Im **Designer** einen neuen Workflow erstellen. Ein Minimal-Workflow mit einem `runScript`-Step:

```json
{
  "nodes": [{
    "id": "step-1",
    "type": "activity",
    "position": { "x": 200, "y": 200 },
    "data": {
      "label": "Host auslesen",
      "activityType": "runScript",
      "targetMachineId": "<guid>",
      "outputVariable": "hostInfo",
      "config": { "script": "$env:COMPUTERNAME", "timeoutSeconds": 30 }
    }
  }],
  "edges": []
}
```

Details zum JSON-Format: [Workflow-JSON](../concepts/workflow-json).

## 4. Edit-Lifecycle

Workflows haben einen SCOrch-style **Edit-Lock**:

1. **Edit** → sperrt den Workflow (`lock`) und deaktiviert ihn.
2. Änderungen vornehmen → **Save** (Zwischenstand).
3. **Publish** → atomar Save + Enable + Unlock. Workflow ist produktiv.

`canWrite = role !== 'Viewer' && checkedOutByUserId === currentUserId`. Siehe [Workflow-Kontrollfluss](../api/workflow-control).

## 5. Ausführen

```http
POST /api/workflows/{id}/execute
Content-Type: application/json

{ "parameters": {}, "timeoutSeconds": 120, "debug": false }
```

Antwort: `202` + `ExecutionId`. Fortschritt landet via **SignalR** auf `/hubs/execution`.

## 6. Ergebnis & Variablen

Der Step-Output ist im Datenbus verfügbar:

```
{{hostInfo.output}}   # Stdout: der Computername
{{hostInfo.success}}  # "true" / "false"
```

Ein downstream-Step kann den Wert per Template referenzieren — NodePilot auto-quotet `{{hostInfo.output}}` als Single-Quoted String. Siehe [Datenbus & Variablen](../concepts/data-bus).

## 7. Trigger setzen (optional)

Einen `scheduleTrigger` (Quartz cron), `fileWatcherTrigger`, `databaseTrigger`, `eventLogTrigger` oder `webhookTrigger` als Root-Node ergänzen. Trigger-Daten landen als `{{manual.<name>}}` im Run. Siehe [Trigger](../triggers).