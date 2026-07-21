# Sub-Workflow Contracts — Manual Test Cases

Manuelle Test-Cases für Feature **Sub-Workflow Contracts V1**.
Branch: `feat/sub-workflow-contracts`.
Code-Tests (xUnit + vitest): `tests/NodePilot.Api.Tests/Services/WorkflowContractDeriverTests.cs` (16), `tests/NodePilot.Api.Tests/Controllers/WorkflowsControllerTests.cs::GetContract*` (5), `src/nodepilot-ui/src/__tests__/hooks/useWorkflowContract.test.tsx` (5), `src/nodepilot-ui/src/__tests__/components/properties/ContractMappingTable.test.tsx` (9). Diese Datei deckt die End-to-End-UX ab, die Code-Tests nicht erreichen.

## Setup

1. Postgres läuft (`pg_ctl status -D 'C:\NodePilot-Postgres\data'`).
2. API läuft auf `http://localhost:5000` (`dotnet run` in `src/NodePilot.Api`).
3. Frontend läuft auf `http://localhost:5173` (`npm run dev` in `src/nodepilot-ui`).
4. Eingeloggt als Admin oder Operator (Viewer kann den Endpoint zwar lesen, aber StartWorkflow nicht editieren).

## Vorbereitung — Test-Workflows

Lege diese drei Workflows einmal an, sie werden in mehreren Cases referenziert:

### "Patch-Server" (Child mit komplettem Contract)

```json
{
  "nodes": [
    {"id":"trigger","type":"trigger","position":{"x":80,"y":120},"data":{
      "activityType":"manualTrigger",
      "config":{"parameters":[
        {"name":"serverName","type":"string","required":true,"description":"Hostname or FQDN"},
        {"name":"reboot","type":"boolean","required":false,"default":"false"},
        {"name":"maxDurationMin","type":"int","required":false,"default":"30"}
      ]}
    }},
    {"id":"return","type":"activity","position":{"x":480,"y":120},"data":{
      "activityType":"returnData",
      "config":{"data":{"patched":"true","summary":"Patched {{trigger.param.serverName}}"}}
    }}
  ],
  "edges":[{"id":"e1","source":"trigger","target":"return"}]
}
```

### "Cleanup" (Child ohne returnData)

```json
{
  "nodes":[
    {"id":"trigger","type":"trigger","position":{"x":80,"y":120},"data":{
      "activityType":"manualTrigger",
      "config":{"parameters":[{"name":"path","type":"string","required":true}]}
    }}
  ],
  "edges":[]
}
```

### "Multi-Return" (Child mit zwei returnData-Nodes)

Zweistufiger Workflow mit Decision: bei Erfolg → returnData-A, bei Failure → returnData-B. Beide setzen `result` und `errorCode` mit unterschiedlichen Quellen.

---

## Test Cases

### TC-1 — Contract wird beim Eintippen geladen

**Setup:** Neuer Parent-Workflow mit einem `startWorkflow`-Step.

**Steps:**
1. Klick auf den `startWorkflow`-Node.
2. Im Properties-Panel rechts ins Feld „Workflow (Name oder GUID)" tippen: `Patch-Server`.

**Expected:**
- Innerhalb ~250ms erscheint die ContractMappingTable.
- Header: „Inputs erwartet von „Patch-Server"" mit 3 Eintraägen (`serverName *`, `reboot`, `maxDurationMin`).
- `serverName` hat Type-Badge `string`, ist required (rotes Sternchen), Description-Tooltip „Hostname or FQDN".
- `reboot` zeigt Default-Hint „Default: false" rechts.
- Outputs-Section listet 4 System-Outputs (blau) + 2 user-Outputs (`patched`, `summary`) mit Hint `{{<step>.param.X}}` rechts.

### TC-2 — Required-Validation greift bei leerem Pflicht-Feld

**Setup:** TC-1 abgeschlossen, ContractMappingTable für „Patch-Server" sichtbar.

**Steps:**
1. `serverName` leer lassen.
2. Andere Felder ausfüllen oder ignorieren.

**Expected:**
- Unter dem `serverName`-Input erscheint roter Border + Text „erforderlich — kein Default deklariert".
- `maxDurationMin` zeigt **keinen** Error obwohl required wäre, weil ein Default existiert.

### TC-3 — Empty-out entfernt den Key (kein leerer String)

**Setup:** TC-1, im `reboot`-Feld den Wert `true` eingegeben.

**Steps:**
1. DevTools öffnen, im React-Tab den `startWorkflow`-Node-State inspect-en. `data.config.parameters` zeigt `{ reboot: "true" }`.
2. Im UI das `reboot`-Feld komplett leer-machen (Backspace bis nichts drin steht).

**Expected:**
- Nach dem Leeren verschwindet `reboot` aus dem Parameter-Dict komplett (nicht `{ reboot: "" }`).
- Wenn der Run später läuft, greift der Default `false` aus dem Child-manualTrigger.

### TC-4 — Stale-Key-Warning bei manuell hinzugefügtem Parameter

**Setup:** Parent-Workflow gespeichert mit `parameters = { serverName: "srv01", legacyParam: "old" }`. Child „Patch-Server" deklariert `legacyParam` nicht im Contract.

**Steps:**
1. Parent-Workflow im Editor öffnen, `startWorkflow`-Node anklicken.

**Expected:**
- Stale-Keys-Section unten mit orange Warning: „Parameter werden gesendet, sind aber nicht im Contract — Child wird sie ignorieren".
- Eintrag `legacyParam = old` mit Remove-Button.
- Klick auf Remove → Stale-Section verschwindet, Parent-Config hat `legacyParam` nicht mehr.

### TC-5 — Workflow ohne returnData zeigt nur System-Outputs

**Setup:** Neuer Parent, `startWorkflow` zeigt auf „Cleanup" (kein returnData).

**Expected:**
- Outputs-Section zeigt 4 System-Outputs.
- Hint unten: „Workflow hat kein returnData — nur die System-Outputs (oben) stehen downstream zur Verfügung."

### TC-6 — Multiple-returnData-Warning

**Setup:** Parent zeigt auf „Multi-Return" (zwei returnData-Nodes).

**Expected:**
- Outputs-Header rechts: orange Badge „mehrere Quellen".
- Hover-Tooltip: „Workflow hat mehrere returnData-Nodes. Pro Run gewinnt nur eine — Outputs sind „kann verfügbar sein", nicht garantiert."
- Output-Keys haben `Source = "multiple"`, in der Spaltennamen-Farbe als amber markiert.

### TC-7 — Workflow ohne manualTrigger fällt auf freie ParameterTable zurück (NICHT „nicht callable")

**Setup:** Erstelle einen Workflow „SchedOnly" mit nur `scheduleTrigger` (kein manualTrigger), enabled.

**Steps:**
1. Parent: `startWorkflow` mit Workflow-Name `SchedOnly`.

**Expected:**
- ContractMappingTable rendert KEINE Inputs-Tabelle.
- Stattdessen Info-Banner: „Kein deklarierter Input-Contract — „SchedOnly" hat keinen manualTrigger — alle Parameter unten gehen ungetypt durch".
- Es darf KEINE „nicht callable"-Fehlermeldung erscheinen — die freie ParameterTable taucht **nicht** auf, weil das Backend `HasManualTrigger=false` schickt aber der Frontend-Contract trotzdem da ist. Wenn Du eigene Parameter brauchst, musst Du den Workflow mit einem manualTrigger ausstatten.
- Zusätzlich: Der Run klappt trotzdem (probiere `Run`). Engine kümmert sich nicht um das Manual-Trigger-Vorhandensein.

### TC-8 — Variable-Expression (`{{globals.X}}`) deaktiviert Contract-Lookup

**Setup:** Globale Variable `WORKFLOW = "Patch-Server"` anlegen (Globals-Sidebar).

**Steps:**
1. Im `startWorkflow`-Node das Workflow-Feld auf `{{globals.WORKFLOW}}` setzen.

**Expected:**
- Kein Contract wird gefetcht (Network-Tab leer für `/contract`).
- Hint: „Workflow-Referenz ist dynamisch — Contract kann erst zur Laufzeit aufgelöst werden. Freie Parameter-Eingabe verfügbar."
- Freie ParameterTable ist sichtbar.

### TC-9 — Unbekannter Workflow → Warning + freie ParameterTable

**Setup:** Im Workflow-Feld einen Namen eintippen, der nicht existiert: `does-not-exist`.

**Expected:**
- Nach ~250ms: Amber-Banner „Workflow „does-not-exist" nicht gefunden — freie Parameter-Eingabe verfügbar".
- Freie ParameterTable rendert darunter.

### TC-10 — Case-Sensitivity (exact match)

**Setup:** Workflow heißt exakt `Daily-Report`.

**Steps:**
1. Im Workflow-Feld `daily-report` (lowercase) eingeben.

**Expected:**
- TC-9-ähnliches Verhalten: not-found-Banner, kein Contract.
- API direkt: `curl http://localhost:5000/api/workflows/by-name/daily-report/contract` → 404.
- `curl http://localhost:5000/api/workflows/by-name/Daily-Report/contract` → 200 + Body.

### TC-11 — Multi-manualTrigger Conflict-Badge

**Setup:** Child-Workflow mit zwei manualTrigger-Nodes:
- Trigger A: `parameters: [{name:"x", type:"string"}]`
- Trigger B: `parameters: [{name:"x", type:"int"}]`

**Steps:**
1. Parent zeigt auf diesen Child.

**Expected:**
- Input `x` zeigt amber Badge „Konflikt".
- Hover: „Mehrere manualTrigger deklarieren diesen Parameter mit unterschiedlichem Type oder Default. Erste Deklaration gewinnt."
- Type-Badge zeigt `string` (erste Deklaration).

### TC-12 — Disabled Trigger wird ignoriert

**Setup:** Child mit zwei manualTrigger:
- Trigger A: aktiv, `parameters: [{name:"a"}]`
- Trigger B: `disabled: true`, `parameters: [{name:"b", required: true}]`

**Steps:**
1. Parent zeigt auf diesen Child.

**Expected:**
- Inputs-Tabelle zeigt nur `a`.
- `b` taucht NICHT auf, weil sein Trigger deaktiviert ist.

### TC-13 — Reserved-Key wird gefiltert

**Setup:** Child mit `returnData.config.data = { "__status": "fake", "myKey": "x" }`.

**Steps:**
1. Parent-Contract anschauen.

**Expected:**
- Outputs-Section zeigt `__status` nur **einmal**, mit Source=`system` (blau).
- `myKey` taucht zusätzlich auf mit Source=`single` (indigo).
- User-Deklaration des reserved-Keys wird stillschweigend ignoriert — keine Warning im V1 (das wäre Lint-Concern).

### TC-14 — Wechsel zwischen Workflows aktualisiert Contract live

**Setup:** Parent-Workflow geöffnet, `startWorkflow`-Node referenziert „Patch-Server".

**Steps:**
1. Workflow-Namen ändern auf „Cleanup".

**Expected:**
- Inputs ändern sich live von 3 (`serverName`, `reboot`, `maxDurationMin`) auf 1 (`path`).
- Outputs-Section ändert sich von 6 (4 system + 2 user) auf 4 (system only) + noReturnDataHint.
- Stale-Section taucht eventuell auf, wenn frühere Werte (z.B. `serverName`) gesetzt waren — normaler Stale-Workflow.

### TC-15 — Output-Hint nutzt outputVariable des Parent-Steps

**Setup:** Parent-Step hat `outputVariable = "patchResult"` gesetzt.

**Expected:**
- Output-Hints lauten `{{patchResult.param.patched}}`, nicht `{{<step-id>.param.patched}}`.
- Wenn `outputVariable` leer ist, fällt es auf die Step-ID zurück.

### TC-16 — Required-Validation blockiert Save NICHT

**Setup:** TC-2-State (required-Field leer mit roter Warning).

**Steps:**
1. „Save" im Editor klicken.

**Expected:**
- Save geht durch, Warning ist Soft-Validation.
- Beim tatsächlichen Run scheitert der Step erst zur Laufzeit (Engine wirft beim Mounting des Child-Manual-Triggers, weil `serverName` weder gesetzt noch ein Default hat).
- V1 hat bewusst KEINEN Publish-Block für Contract-Violations — Folge-Feature.

### TC-17 — Viewer-Role kann Contract lesen, nicht editieren

**Setup:** Login als Viewer-User.

**Steps:**
1. `startWorkflow`-Node im read-only-Modus inspekten.
2. API direkt: `curl -b cookies.txt http://localhost:5000/api/workflows/<id>/contract`.

**Expected:**
- API liefert 200 + Contract-Body (`[Authorize]` ohne Rolle reicht).
- UI zeigt ContractMappingTable, alle Inputs sind disabled (durch das fieldset-Wrap im Properties-Panel).
- Stale-Keys-Remove-Button ist disabled.

### TC-18 — Malformed Workflow JSON liefert leeren Contract, kein Crash

**Setup:** Workflow mit absichtlich kaputtem `DefinitionJson` (z.B. via direktem DB-Update: `UPDATE Workflows SET DefinitionJson = '{ broken' WHERE Name = 'Test'`).

**Steps:**
1. Parent zeigt auf diesen Workflow.

**Expected:**
- Contract-Endpoint liefert 200 (kein 500) mit `{ hasManualTrigger: false, hasReturnData: false, inputs: [], outputs: [4 system outputs] }`.
- UI zeigt das No-Manual-Trigger-Banner.
- Kein Frontend-Error in der Console.

---

## Smoke-Test (Quick-Pass nach Änderungen)

Wenn eine Änderung am Feature gemacht wurde, mindestens:
- TC-1 (Contract lädt)
- TC-2 (Required-Validation)
- TC-3 (Empty-out entfernt Key)
- TC-9 (404 fällt zurück auf freie Tabelle)

Wenn die Änderung am Backend war: API direkt mit `curl` gegen alle drei Test-Workflows aus der Vorbereitung pingen.
