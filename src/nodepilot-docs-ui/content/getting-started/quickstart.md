# Quickstart

Voraussetzung: Backend und Frontend laufen (siehe [Installation](./installation)).

## 1. Erster Login

Im Frontend (`http://localhost:5173`) mit dem Initial-Admin einloggen. Bei leerer DB wird beim ersten Login der Admin-Account erstellt. Dev-Default: `admin` / `admin123`.

## 2. Maschine & Credential anlegen (optional)

> **Diesen Schritt kannst du überspringen.** Für den Quickstart-Workflow in Schritt 3 brauchst du weder eine Maschine noch ein Credential: Lässt du bei einem `runScript`-Step das Feld *Maschine* leer, führt NodePilot das Skript **lokal im API-Prozess** aus — kein WinRM, kein Login. Genau richtig für den ersten Test.

Nötig wird beides erst, wenn ein Step **auf einem anderen Windows-Host** laufen soll — also sobald du `runScript` auf eine Zielmaschine legst oder eine der reinen Remote-Activities (`fileOperation`, `serviceManagement`, `registryOperation`, `wmiQuery`, …) verwendest. Ohne Maschine bricht so ein Step mit `No target machine specified` ab.

- **Machine** — das WinRM-Ziel (`targetMachineId`). Anlegen unter **Infrastruktur → Maschinen**, Button **Maschine hinzufügen**.
- **Credential** — der DPAPI-verschlüsselte Login-Datensatz. Anlegen unter **Administration → Einstellungen**, Abschnitt **Credentials**, Button **Credential hinzufügen**. Am Node ist es optional: ohne Credential nutzt WinRM die Prozess-Identität von NodePilot.

Beides geht auch über die API:

```http
POST /api/machines
POST /api/credentials
```

Für lokale Prozess-Ausführung ohne Credential gibt es zusätzlich den Localhost-Bypass (siehe [Security](../security/overview)).

## 3. Workflow bauen

### So kommst du in den Designer

1. In der **Seitenleiste** links die Gruppe **Arbeitsbereich** → Eintrag **Workflows** (Route `/workflows`).
2. Oben rechts auf **Neuer Workflow**. Der Button ist nur für **Admin** und **Operator** sichtbar — als Viewer hast du nur Leserechte.
3. Es klappt ein Feld auf: **Namen eingeben** und mit **Anlegen** bestätigen (oder Enter drücken).
4. NodePilot legt den leeren Workflow an und springt **direkt in den Designer** (`/workflows/{id}`). Ein bestehender Workflow öffnet sich genauso — per Klick auf seine Zeile in der Liste.

> Hast du links einen **Ordner** ausgewählt, wird der neue Workflow darin angelegt. Alternativ erzeugt der Button **Neuer KI-Workflow** daneben einen Entwurf per Prompt (siehe [KI-Features](../ai-features)).

Was dich im Designer erwartet — Canvas, Palette, Properties-Panel: [Designer](../designer/overview).

### Der erste Workflow

Im Designer ziehst du zwei Nodes auf die Fläche und verbindest sie mit einer Edge:

1. Einen **Manual-Trigger** — jeder Workflow braucht einen Trigger als Startpunkt. Ohne aktiven Trigger findet die Engine keinen Einstieg und der Lauf schlägt sofort fehl.
2. Eine **`runScript`**-Activity mit dem Skript `$env:COMPUTERNAME`. Das Feld *Maschine* bleibt leer (siehe Schritt 2), unter *Output-Variable* trägst du `hostInfo` ein.

Als JSON — das schreibt normalerweise der Designer für dich:

```json
{
  "nodes": [
    {
      "id": "trigger-1",
      "type": "activity",
      "position": { "x": 200, "y": 80 },
      "data": {
        "label": "Manuell starten",
        "activityType": "manualTrigger",
        "config": { "parameters": [] }
      }
    },
    {
      "id": "step-1",
      "type": "activity",
      "position": { "x": 200, "y": 220 },
      "data": {
        "label": "Host auslesen",
        "activityType": "runScript",
        "outputVariable": "hostInfo",
        "config": { "script": "$env:COMPUTERNAME", "timeoutSeconds": 30 }
      }
    }
  ],
  "edges": [
    {
      "id": "e-trigger-step",
      "source": "trigger-1",
      "target": "step-1",
      "type": "labeled",
      "data": { "label": "Always" }
    }
  ]
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