# Schnelleinstieg

Dieser Schnelleinstieg erstellt und startet einen minimalen Workflow:

```text
Manual-Trigger -> Run Script
```

Eine Zielmaschine und ein Credential sind nicht erforderlich.

## Voraussetzungen

- laufende NodePilot-Instanz
- Admin- oder Operator-Account

Installationswege stehen unter [Installation](./installation).

## 1. Workflow anlegen

1. **Arbeitsbereich → Workflows** öffnen.
2. **Neuer Workflow** auswählen.
3. `Host prüfen` als Namen eintragen.
4. **Anlegen** und anschließend **Edit** auswählen.

## 2. Nodes hinzufügen

1. Einen **Manual-Trigger** auf die Canvas ziehen.
2. Eine **Run Script**-Activity hinzufügen.
3. Beide Nodes mit einer Edge verbinden.
4. In **Run Script** folgendes Skript eintragen:

```powershell
$env:COMPUTERNAME
```

5. `hostInfo` als **Output Variable** eintragen.
6. **Maschine** leer lassen.

## 3. Veröffentlichen und starten

1. **Publish** auswählen.
2. **Run** auswählen.
3. Unter **Executions** die Ausgabe der Run-Script-Activity öffnen.

Die Ausgabe enthält den Namen des NodePilot-Hosts.

## Beispiel als JSON importieren

Die folgende Datei enthält denselben Workflow im importierbaren NodePilot-Format.

Kurzanleitung:

1. Den vollständigen JSON-Block als `host-pruefen.json` in UTF-8 speichern.
2. In der Web-UI **Arbeitsbereich → Workflows** öffnen.
3. Optional den Zielordner auswählen.
4. **Importieren** auswählen und `host-pruefen.json` öffnen.
5. Den importierten Workflow öffnen, veröffentlichen und starten.

```json
{
  "schema": "nodepilot-workflow-export/v1",
  "exportVersion": 1,
  "exportedAt": "2026-07-27T00:00:00Z",
  "workflows": [
    {
      "name": "Host prüfen",
      "description": "Minimaler Beispiel-Workflow für den Schnelleinstieg",
      "definition": {
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
    }
  ]
}
```
