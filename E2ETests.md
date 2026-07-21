# NodePilot E2E Tests — Workflow Designer

Umfassende Test-Anleitung für den Workflow Designer mit Playwright MCP. Diese Datei dokumentiert alle zu testenden Szenarien. Beim Ausführen der Tests diese Checkliste durcharbeiten.

**Verwendung:** `guck bitte in E2ETests.md und führe die E2E-Tests durch`

---

## Vorbereitung & Setup

### 1. Umgebung starten

```bash
# Terminal 1: Backend starten
cd src/NodePilot.Api && dotnet run --urls "http://localhost:5000"

# Terminal 2: Frontend starten
cd src/nodepilot-ui && npm run dev
```

**Prüfpunkte:**
- [ ] Backend läuft auf `http://localhost:5000`
- [ ] Frontend läuft auf `http://localhost:5173`
- [ ] API ist erreichbar: `GET http://localhost:5000/healthz/live` → 200
- [ ] Frontend lädt ohne Fehler

### 2. Browser starten & Login

1. Öffne `http://localhost:5173`
2. Erstes Mal: Admin-Account wird erstellt (E-Mail + Passwort)
3. Login mit Admin-Credentials
4. Dashboard sollte leer oder mit Demo-Workflows geladen sein

**Prüfpunkte:**
- [ ] Login erfolgreich
- [ ] Dashboard ist sichtbar
- [ ] "New Workflow" Button ist sichtbar
- [ ] Keine Console-Fehler (F12 → Console)

---

## Teil 1: Workflow-Management

### Test 1.1 — Neuen Workflow erstellen

**Schritte:**
1. Klick auf "New Workflow"
2. Gib Namen ein: `E2E_Basic_Test`
3. Optionale Beschreibung: `Automated E2E test workflow`
4. Klick "Create"

**Prüfpunkte:**
- [ ] Editor öffnet sich
- [ ] Canvas ist leer (keine Nodes)
- [ ] Workflow-Name wird in der Toolbar angezeigt
- [ ] Console: keine Fehler

**Erwartung:** Leere Workflow-Datei im Designer

---

### Test 1.2 — Workflow speichern (ohne Nodes)

**Schritte:**
1. Klick auf "Save" (oder Ctrl+S)
2. Bestätigung sollte erfolgen

**Prüfpunkte:**
- [ ] Speicher-Button wird deaktiviert (grayed out)
- [ ] "Workflow saved" Toast/Nachricht erscheint
- [ ] Keine Fehler in Console

**Erwartung:** Leerer Workflow ist persistiert

---

### Test 1.3 — Workflow umbenennen

**Schritte:**
1. Klick auf Workflow-Name in der Toolbar
2. Edit-Modus aktiviert sich
3. Ändere Namen zu `E2E_Renamed_Test`
4. Drücke Enter oder klick außerhalb

**Prüfpunkte:**
- [ ] Name wird aktualisiert in der Toolbar
- [ ] Save-Button wird aktiviert
- [ ] Speichern erfolgt automatisch nach Timeout oder manuell

**Erwartung:** Workflow-Name wird geändert

---

### Test 1.4 — Zur Workflow-Liste zurück & öffnen

**Schritte:**
1. Klick auf "Workflows" oder Breadcrumb-Link
2. Wechsel zur Workflow-Liste
3. Suche nach `E2E_Renamed_Test`
4. Klick auf Workflow zum Öffnen

**Prüfpunkte:**
- [ ] Workflow-Liste lädt
- [ ] Neuer Workflow erscheint in der Liste
- [ ] Workflow öffnet sich im Editor
- [ ] Name und Zustand werden korrekt angezeigt

**Erwartung:** Workflow kann gespeichert und wieder geöffnet werden

---

## Teil 2: Node-Management — Activity-Typen

Für jeden Activity-Typ: **Node hinzufügen → Properties setzen → Speichern → Prüfpunkte**

### Test 2.1 — `delay` Activity (Engine-local, einfachste)

**Schritte:**
1. Im Editor: Rechtsklick auf Canvas → "Add Node" oder Toolbar-Button
2. Wähle Activity-Typ: `delay`
3. Node erscheint auf Canvas mit Label "Delay"
4. Properties-Panel öffnet sich rechts
5. Setze `seconds` auf `5`
6. Speichern

**Prüfpunkte:**
- [ ] Node wird auf Canvas gerendert
- [ ] Node hat blaues Icon (Engine-local Activity)
- [ ] Properties-Panel zeigt `seconds` Eingabe
- [ ] Wert wird gespeichert
- [ ] Kein Validation-Error

**Erwartung:** Einfache Engine-local Activity funktioniert

---

### Test 2.2 — `runScript` Activity (Remote, mit Credential)

**Schritte:**
1. Rechtsklick → Add Node: `runScript`
2. Properties öffnen sich
3. Setze folgende Werte:
   - **Script:** `Write-Host "Hello World"`
   - **Engine:** `auto`
   - **Timeout:** `30`
4. Setze **Target Machine** (benötigt vorher erstellte Machine):
   - Falls keine Machine existiert: Navigiere zu Settings → Machines, erstelle eine Dummy-Machine `localhost` oder ähnlich
   - Wähle diese Machine im Node aus
5. Setze **Credential** (optional, für echte WinRM-Tests mit echten Machines)
6. Setze **Output Variable:** `scriptOutput`
7. Speichern

**Prüfpunkte:**
- [ ] Node wird gerendert
- [ ] Node hat rotes Icon (Remote Activity)
- [ ] Properties-Panel zeigt Script-Editor
- [ ] Script-Text wird korrekt gespeichert
- [ ] Target Machine Dropdown lädt Machines
- [ ] Timeout ist validiert (Zahl, > 0)
- [ ] Output Variable wird als Eingabe-Feld angeboten

**Erwartung:** Remote-Activity mit Machine und Script-Config funktioniert

---

### Test 2.2b — `runScript` Prozess-Isolation & `successExitCodes`

**Schritte:**
1. `runScript`-Node ohne Target Machine anlegen (lokal)
2. Properties öffnen
3. **Prozess-Isolation**-Checkbox aktivieren
4. Prüfen ob Cap-Felder erscheinen
5. **Speicherlimit (MB)** und **Max. Prozesse** Werte eintragen
6. **Erfolg bei Exit-Codes** (`successExitCodes`) auf `0,1` setzen
7. Speichern

**Prüfpunkte:**
- [ ] Prozess-Isolation-Checkbox sichtbar und aktivierbar
- [ ] Nach Aktivierung erscheinen optionale Felder „Speicherlimit (MB)" und „Max. Prozesse"
- [ ] `successExitCodes`-Eingabe vorhanden (leer = nur fehler-basiert)
- [ ] Gespeicherte Werte: `config.isolated: true`, `config.successExitCodes: "0,1"`, `config.memoryLimitMb` und `config.maxProcesses` korrekt

**Erwartung:** Prozess-Isolation und Exit-Code-Gate werden korrekt in der Config persistiert

---

### Test 2.2c — `runScript` Isolation bei Remote-Target deaktiviert

**Schritte:**
1. `runScript`-Node anlegen, **Target Machine** setzen
2. Properties öffnen

**Prüfpunkte:**
- [ ] **Prozess-Isolation**-Checkbox erscheint ausgegraut / disabled
- [ ] Tooltip oder Hinweis erklärt, dass Isolation nur für lokale Ausführung gilt

**Erwartung:** Isolation ist ein reines Local-Feature und bei Remote-Ziel nicht konfigurierbar

---

### Test 2.3 — `fileOperation` + `folderOperation` Activities

**Schritte (fileOperation):**
1. Add Node: `fileOperation`
2. Properties:
   - **Operation:** `copy`
   - **File path:** `C:\source\file.txt`
   - **Destination:** `C:\dest\file.txt`
   - **Target Machine:** (wähle Machine)
   - **Output Variable:** `fileOpResult`
3. Speichern
4. Ändere Operation zu `move` → Speichern
5. Ändere Operation zu `delete` → Speichern (nur `path`)
6. Ändere Operation zu `exists` → Speichern (nur `path`, prüft `-PathType Leaf`)
7. Ändere Operation zu `create` → Path = `C:\Temp\NewFile.txt` → Speichern (legt leere Datei an, refused wenn am Pfad ein Folder steht)
8. Ändere Operation zu `rename` → Path = `C:\Temp\old.txt`, New name = `new.txt` → Speichern

**Schritte (folderOperation):**
1. Add Node: `folderOperation`
2. Operation = `create`, Folder path = `C:\Temp\NewFolder` → Speichern
3. Operation = `list`, Folder path = `C:\Temp` → Speichern
4. Operation = `delete`, Folder path = `C:\Temp\NewFolder` → Speichern

**Prüfpunkte:**
- [ ] `fileOperation`-Dropdown zeigt 6 Optionen: copy, move, rename, delete, exists, create
- [ ] `folderOperation`-Dropdown zeigt 7 Optionen: copy, move, rename, delete, exists, list, create
- [ ] Properties ändern sich dynamisch basierend auf Operation
   - `copy/move`: Path + Destination
   - `rename`: Path + New name
   - `delete/exists/list/create`: nur Path
- [ ] Validation: Pfade sind nicht leer
- [ ] `folderOperation.create` legt Folder via `New-Item -ItemType Directory` an (Idempotent: `-Force`)
- [ ] `fileOperation.delete` schlägt mit "Not a file" fehl, wenn der Pfad ein Folder ist
- [ ] `folderOperation.delete` schlägt mit "Not a directory" fehl, wenn der Pfad ein File ist
- [ ] `rename` führt `Rename-Item -Path … -NewName …` aus
- [ ] Speichern erfolgt bei jeder Änderung

**Erwartung:** Dynamische Properties je nach Operation

---

### Test 2.4 — `restApi` Activity

**Schritte:**
1. Add Node: `restApi`
2. Properties:
   - **URL:** `https://httpbin.org/get`
   - **Method:** `GET`
   - **Timeout:** `10`
3. Speichern
4. Ändere Method zu `POST`
5. Setze **Body:** `{"key": "value"}`
6. Setze **Headers:** `Content-Type: application/json`
7. Setze **Output Variable:** `apiResponse`
8. Speichern

**Prüfpunkte:**
- [ ] URL wird validiert (muss mit http:// oder https:// beginnen)
- [ ] Method-Dropdown: GET, POST, PUT, DELETE, PATCH, HEAD
- [ ] Body-Editor zeigt sich bei POST/PUT/PATCH
- [ ] Headers-Editor funktioniert (Key: Value)
- [ ] Output Variable ist optional aber akzeptiert
- [ ] Kein Fehler beim Speichern

**Erwartung:** HTTP-Request-Config funktioniert

---

### Test 2.5 — `sql` Activity (SQLite)

**Schritte:**
1. Add Node: `sql`
2. Properties:
   - **Provider:** `sqlite`
   - **Connection String:** `Data Source=nodepilot.db`
   - **Query:** `SELECT COUNT(*) as count FROM Workflows;`
   - **Timeout:** `10`
   - **Output Variable:** `queryResult`
3. Speichern

**Prüfpunkte:**
- [ ] Provider-Dropdown: sqlserver, sqlite
- [ ] Connection String wird akzeptiert
- [ ] Query-Editor (Textarea) funktioniert
- [ ] Timeout ist Zahl > 0
- [ ] Output Variable ist optional

**Erwartung:** SQL-Activity konfigurierbar

---

### Test 2.6 — `emailNotification` Activity

**Schritte:**
1. Add Node: `emailNotification`
2. Properties:
   - **To:** `test@example.com`
   - **Subject:** `Test Email from {{step.param.name}}`
   - **Body:** `Hello {{globals.USERNAME}}`
   - **Is HTML:** `false`
3. Speichern
4. Ändere **Is HTML** zu `true`
5. Speichern

**Prüfpunkte:**
- [ ] To-Feld wird validiert (muss E-Mail sein)
- [ ] Subject und Body akzeptieren Template-Syntax `{{...}}`
- [ ] Is HTML Toggle funktioniert
- [ ] Kein Output Variable Feld (Email hat keine Output)
- [ ] Speichern erfolgt

**Erwartung:** Email-Notification konfigurierbar

---

### Test 2.7 — `startWorkflow` Activity (Sub-Workflow)

**Schritte:**
1. Erstelle vorher zwei Workflows: `Parent_WF` und `Child_WF`
2. Im Parent: Add Node: `startWorkflow`
3. Properties:
   - **Workflow Name or ID:** `Child_WF`
   - **Wait for Completion:** `true`
   - **Timeout:** `60`
   - **Parameters:** (object mit {})
   - **Output Variable:** `childResult`
4. Speichern
5. Ändere **Wait for Completion** zu `false`
6. Speichern

**Prüfpunkte:**
- [ ] Workflow-Dropdown zeigt verfügbare Workflows
- [ ] Wait for Completion Toggle funktioniert
- [ ] Timeout wird validiert
- [ ] Parameters-Editor ist JSON-fähig
- [ ] Output Variable nur sichtbar wenn Wait=true

**Erwartung:** Sub-Workflow Call konfigurierbar

---

### Test 2.8 — `junction` Activity

**Schritte:**
1. Add Node: `junction`
2. Properties:
   - **Mode:** `waitAll`
   - (bei `waitNofM`: zusätzlich **Required Count**)
3. Speichern
4. Ändere Mode zu `waitAny`
5. Speichern
6. Ändere Mode zu `waitNofM`
7. Setze **Required Count:** `2`
8. Speichern

**Prüfpunkte:**
- [ ] Mode-Dropdown: waitAll, waitAny, waitNofM
- [ ] Bei waitNofM: Required Count Feld erscheint
- [ ] Bei waitAll/waitAny: Required Count ist hidden
- [ ] Validation: Required Count > 0 und ≤ Anzahl Eingabe-Edges (später prüfen)

**Erwartung:** Junction-Modi konfigurierbar

---

### Test 2.9 — `returnData` Activity

**Schritte:**
1. Add Node: `returnData`
2. Properties:
   - **Data:** JSON-Editor mit `{"result": "{{step.output}}", "timestamp": "{{globals.NOW}}"}`
3. Speichern

**Prüfpunkte:**
- [ ] Data-Editor ist JSON-fähig
- [ ] JSON wird validiert (muss valides JSON sein)
- [ ] Template-Variablen werden akzeptiert
- [ ] Speichern erfolgt

**Erwartung:** Return Data mit Template-Syntax funktioniert

---

### Test 2.10 — `log` Activity

**Schritte:**
1. Add Node: `log`
2. Properties:
   - **Level:** `info`
   - **Message:** `Starting process with {{step.param.processId}}`
3. Speichern
4. Ändere Level zu `warning`, dann `error`
5. Speichern

**Prüfpunkte:**
- [ ] Level-Dropdown: info, warning, error
- [ ] Message wird akzeptiert
- [ ] Template-Variablen sind erlaubt
- [ ] Speichern erfolgt

**Erwartung:** Logging-Activity funktioniert

---

### Test 2.11 — `jsonQuery` Activity

**Schritte:**
1. Add Node: `jsonQuery`
2. Properties:
   - **Source:** `step`
   - **Path:** `previousStep`
   - **JSONPath:** `$.data.property`
   - **Result Mode:** `first`
   - **Output Variable:** `jsonExtracted`
3. Speichern
4. Ändere **Result Mode** zu `all`
5. Speichern

**Prüfpunkte:**
- [ ] Source-Dropdown: step, literal
- [ ] JSONPath wird akzeptiert (z.B. `$.data[0].name`)
- [ ] Result Mode: first, all
- [ ] Output Variable wird gespeichert
- [ ] Kein Validation-Error bei leerer JSONPath

**Erwartung:** JSON-Query funktioniert

---

### Test 2.12 — `xmlQuery` Activity

**Schritte:**
1. Add Node: `xmlQuery`
2. Properties:
   - **Source:** `literal`
   - **Content:** `<root><item>test</item></root>`
   - **XPath:** `/root/item`
   - **Result Mode:** `first`
   - **Namespaces:** (leer lassen oder optionales Format)
3. Speichern

**Prüfpunkte:**
- [ ] Source-Dropdown: step, literal
- [ ] Content Textarea zeigt XML
- [ ] XPath wird akzeptiert
- [ ] Result Mode: first, all
- [ ] Output Variable wird gespeichert

**Erwartung:** XML-Query funktioniert

---

### Test 2.13 — Remote Activities: `serviceManagement`, `registryOperation`, `wmiQuery`, `startProgram`, `powerManagement`

Für jede Activity:

#### `serviceManagement`
- **Service Name:** `Spooler`
- **Action:** `stop` (auch testen: start, restart, status)
- Speichern

#### `registryOperation`
- **Operation:** `read`
- **Key Path:** `HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion`
- **Value Name:** `ProductName`
- Speichern

#### `wmiQuery`
- **Class Name:** `Win32_ComputerSystem`
- **Namespace:** `root\cimv2`
- **Filter:** (leer)
- Speichern

#### `startProgram`
- **File Path:** `notepad.exe`
- **Arguments:** `C:\test.txt`
- **Wait for Exit:** `true`
- **Timeout:** `30`
- **Success Exit Codes:** `0`
- Speichern

#### `powerManagement`
- **Action:** `shutdown`
- **Delay Seconds:** `60`
- **Force:** `false`
- **Message:** `Scheduled maintenance shutdown`
- Speichern

**Prüfpunkte (für alle 5):**
- [ ] Node wird gerendert
- [ ] Alle Properties werden akzeptiert
- [ ] Keine Validation-Fehler
- [ ] Speichern erfolgt
- [ ] Output Variable (wo sinnvoll) wird gespeichert

**Erwartung:** Alle Remote-Activities konfigurierbar

---

## Teil 3: Node-Operationen & Designer-Interaktionen

### Test 3.1 — Node duplizieren

**Schritte:**
1. Erstelle einen Node (z.B. `runScript`)
2. Rechtsklick auf Node → "Duplicate" oder "Copy"
3. Node wird dupliziert (neuer Node neben dem Original)
4. Speichern

**Prüfpunkte:**
- [ ] Duplizierter Node hat andere ID (z.B. `step-123-copy`)
- [ ] Properties sind kopiert
- [ ] Neue Node-ID ist eindeutig
- [ ] Speichern erfolgt

**Erwartung:** Nodes können dupliziert werden

---

### Test 3.2 — Node löschen

**Schritte:**
1. Klick auf Node
2. Rechtsklick → "Delete" oder Taste "Delete" drücken
3. Node verschwindet
4. Speichern

**Prüfpunkte:**
- [ ] Node wird aus Canvas entfernt
- [ ] Edges zu/von diesem Node werden auch entfernt
- [ ] Keine Console-Fehler
- [ ] Speichern erfolgt

**Erwartung:** Nodes können gelöscht werden

---

### Test 3.3 — Node verschieben (Drag & Drop)

**Schritte:**
1. Erstelle mindestens 2 Nodes
2. Drag Node 1 zu neuer Position
3. Speichern
4. Reload Seite und prüfe, ob Position erhalten bleibt

**Prüfpunkte:**
- [ ] Node-Position ändert sich während Drag
- [ ] Edges folgen dem Node (werden neu gezeichnet)
- [ ] Nach Speichern und Reload: Position ist gleich
- [ ] Keine Snap-to-Grid Fehler

**Erwartung:** Node-Position wird gespeichert

---

### Test 3.4 — Canvas zoomen & pannen

**Schritte:**
1. Scroll-Wheel zum Zoomen (oder Ctrl+Scroll)
2. Pan mit Mittelmaus-Taste oder Space+Drag
3. Zoom Out komplett (alle Nodes sichtbar)
4. Zoom In
5. Speichern

**Prüfpunkte:**
- [ ] Zoom funktioniert (Canvas wird vergrößert/verkleinert)
- [ ] Pan funktioniert (Canvas-Versatz)
- [ ] UI bleibt responsiv
- [ ] Nach Speichern und Reload: Zoom-Level erhalten?

**Erwartung:** Canvas Navigation funktioniert

---

### Test 3.5 — Multiple Nodes auswählen (Marquee)

**Schritte:**
1. Erstelle 3+ Nodes in Rechteck-Anordnung
2. Drag-Box ziehen, um mehrere Nodes auszuwählen
3. Alle Nodes sollten selected sein (Highlight)
4. Rechtsklick auf ausgewählte Nodes → Optionen (z.B. Delete All, Copy)

**Prüfpunkte:**
- [ ] Marquee-Selection funktioniert
- [ ] Mehrere Nodes werden highlighted
- [ ] Bulk-Operations sind möglich
- [ ] Kein Crash bei Mehrfach-Selektion

**Erwartung:** Multi-Select funktioniert

---

## Teil 4: Edges (Verbindungen) & Bedingungen

### Test 4.1 — Einfache Edge erstellen (immer/bedingungslos)

**Schritte:**
1. Erstelle 2 Nodes: `delay` und `runScript`
2. Ziehe Connector vom Output-Handle des 1. Nodes zum Input des 2. Nodes
3. Edge wird gezeichnet mit Standard-Label "On Success"
4. Speichern

**Prüfpunkte:**
- [ ] Edge wird visuell gerendert
- [ ] Edge hat Label "On Success" oder ähnlich
- [ ] Edge ist selektierbar
- [ ] Speichern erfolgt

**Erwartung:** Basic Edge-Erstellung funktioniert

---

### Test 4.2 — Edge mit Bedingung: Comparison Operators

Erstelle folgende Edges mit Comparison-Bedingungen:

#### Operator: `==` (Equals)
1. Edge von Node 1 zu Node 2
2. Klick auf Edge → Properties öffnen
3. **Condition Type:** `comparison`
4. **Left Operand:** `step` + `{{step1.param.result}}`
5. **Operator:** `==`
6. **Right Operand:** `literal` + `"success"`
7. Speichern

#### Operator: `!=` (Not Equals)
- Linke Seite: `{{step.param.code}}`
- Operator: `!=`
- Rechte Seite: `"error"`

#### Operator: `<` (Less Than)
- Linke Seite: `{{step.param.count}}`
- Operator: `<`
- Rechte Seite: `10`

#### Operator: `>` (Greater Than)
- Linke Seite: `{{step.param.percentage}}`
- Operator: `>`
- Rechte Seite: `50`

#### Operator: `<=` (Less or Equal)
- Linke Seite: `{{step.param.age}}`
- Operator: `<=`
- Rechte Seite: `18`

#### Operator: `>=` (Greater or Equal)
- Linke Seite: `{{step.param.version}}`
- Operator: `>=`
- Rechte Seite: `3`

#### Operator: `contains`
- Linke Seite: `{{step.param.text}}`
- Operator: `contains`
- Rechte Seite: `"localhost"`

#### Operator: `startsWith`
- Linke Seite: `{{step.param.filename}}`
- Operator: `startsWith`
- Rechte Seite: `"error_"`

#### Operator: `endsWith`
- Linke Seite: `{{step.param.filename}}`
- Operator: `endsWith`
- Rechte Seite: `".log"`

#### Operator: `matches` (Regex)
- Linke Seite: `{{step.param.email}}`
- Operator: `matches`
- Rechte Seite: `"^[a-zA-Z0-9+_.-]+@[a-zA-Z0-9.-]+$"`

#### Operator: `isEmpty`
- Linke Seite: `{{step.param.value}}`
- Operator: `isEmpty`
- Rechte Seite: (nicht sichtbar / ignoriert)

#### Operator: `isNotEmpty`
- Linke Seite: `{{step.param.value}}`
- Operator: `isNotEmpty`
- Rechte Seite: (nicht sichtbar / ignoriert)

#### Operator: `isTrue`
- Linke Seite: `{{step.param.success}}`
- Operator: `isTrue`
- Rechte Seite: (nicht sichtbar / ignoriert)

#### Operator: `isFalse`
- Linke Seite: `{{step.param.success}}`
- Operator: `isFalse`
- Rechte Seite: (nicht sichtbar / ignoriert)

**Prüfpunkte (für alle Operators):**
- [ ] Operator wird in Dropdown angeboten
- [ ] Operanden werden akzeptiert
- [ ] Bei unären Operatoren (isEmpty, isTrue, etc.): Rechte Seite ist hidden
- [ ] Speichern erfolgt
- [ ] Edge-Label aktualisiert sich semantisch

**Erwartung:** Alle 14 Comparison-Operatoren funktionieren

---

### Test 4.3 — Edge mit Bedingung: Logische Verknüpfung (AND/OR/NOT)

#### Test AND
1. Erstelle Edge mit **Condition Type:** `group`
2. **Operator:** `AND`
3. Füge zwei Conditions hinzu:
   - Condition 1: `{{step.param.env}}` == `"prod"`
   - Condition 2: `{{step.param.debug}}` == `false`
4. Speichern
5. Edge sollte nur true sein wenn BEIDE erfüllt

#### Test OR
1. Neue Edge mit **Group Operator:** `OR`
2. Conditions:
   - Condition 1: `{{step.param.status}}` == `"retry"`
   - Condition 2: `{{step.param.status}}` == `"pending"`
3. Speichern
4. Edge sollte true sein wenn MINDESTENS eine erfüllt

#### Test NOT
1. Neue Edge mit **NOT** Checkbox aktivieren
2. Base-Condition: `{{step.param.isDev}}` == `true`
3. Speichern
4. Edge sollte true sein wenn Condition FALSE ist

#### Test verschachtelte AND/OR
1. Edge mit AND-Gruppe:
   - Subgroup 1 (OR):
     - `env` == `"prod"` ODER `env` == `"staging"`
   - AND
   - Condition: `enabled` == `true`
2. Speichern

**Prüfpunkte:**
- [ ] AND/OR Operator wird angeboten
- [ ] NOT Checkbox funktioniert
- [ ] Conditions können hinzugefügt/entfernt werden
- [ ] Verschachtelte Gruppen sind möglich
- [ ] Speichern erfolgt
- [ ] Edge-Label wird semantisch aktualisiert

**Erwartung:** Logische Verknüpfungen funktionieren

---

### Test 4.4 — Edge disablen

**Schritte:**
1. Klick auf Edge
2. Properties-Panel rechts
3. Toggle **Disabled** zu `true`
4. Speichern
5. Edge sollte visuell deaktiviert aussehen (z.B. gestrichelt)

**Prüfpunkte:**
- [ ] Edge wird visuell als disabled markiert
- [ ] Disabled Flag wird gespeichert
- [ ] Bei Ausführung: Edge wird ignoriert

**Erwartung:** Edges können deaktiviert werden

---

### Test 4.5 — Edge löschen

**Schritte:**
1. Klick auf Edge
2. Rechtsklick → "Delete" oder Taste "Delete"
3. Edge verschwindet
4. Speichern

**Prüfpunkte:**
- [ ] Edge wird entfernt
- [ ] Nodes bleiben bestehen
- [ ] Speichern erfolgt

**Erwartung:** Edges können gelöscht werden

---

## Teil 5: Properties Panel & Variablen

### Test 5.1 — Output Variable setzen & umbenennen

**Schritte:**
1. Erstelle Node: `runScript`
2. Setze **Output Variable:** `myScriptOutput`
3. Speichern
4. Ändere Variable zu `myScriptOutput_v2`
5. Speichern

**Prüfpunkte:**
- [ ] Output Variable wird gespeichert
- [ ] Name kann beliebig geändert werden
- [ ] Keine Duplikate erzwungen (mehrere Steps gleiche Variable OK)

**Erwartung:** Output-Variablen werden korrekt verwaltet

---

### Test 5.2 — Template-Variablen in Script/Query

**Schritte:**
1. Erstelle Nodes:
   - Node 1: `delay` mit Output-Variable `delayOutput`
   - Node 2: `runScript` mit Script:
     ```powershell
     $prevStep = '{{delayOutput.output}}'
     Write-Host "Previous: $prevStep"
     ```
2. Speichern
3. Im Properties-Panel sollte Autocompletions-Vorschlag für `{{delayOutput` erscheinen
4. Klick auf Suggestion → wird eingefügt
5. Speichern

**Prüfpunkte:**
- [ ] Template-Syntax wird akzeptiert
- [ ] Variable-Dropdown zeigt verfügbare Variablen
- [ ] Autocompletion funktioniert
- [ ] `output`, `error`, `param.*`, `success` Suffixe werden angeboten
- [ ] Speichern erfolgt

**Erwartung:** Template-Variablen mit Autocomplete funktionieren

---

### Test 5.3 — Global-Variablen verwenden

**Schritte:**
1. Admin-Seite: Settings → Global Variables
2. Erstelle Global-Variable:
   - **Name:** `ADMIN_EMAIL`
   - **Value:** `admin@example.com`
   - **Is Secret:** `false`
3. Speichern
4. Im Workflow: Erstelle Node `emailNotification`
5. **To:** `{{globals.ADMIN_EMAIL}}`
6. Speichern

**Prüfpunkte:**
- [ ] Global-Variable wird erstellt
- [ ] In Workflow ist `{{globals.ADMIN_EMAIL}}` verfügbar
- [ ] Autocompletion schlägt `globals.ADMIN_EMAIL` vor
- [ ] Speichern erfolgt

**Erwartung:** Global-Variablen sind nutzbar

---

### Test 5.4 — Structured Output (Parameter)

**Schritte:**
1. Erstelle Node `runScript`:
   - **Script:**
     ```powershell
     $hostName = 'SERVER01'
     $version = '1.0.5'
     Write-Host "Name=$hostName, Version=$version"
     ```
2. Speichern
3. Downstream Node: Erstelle `returnData`
   - **Data:**
     ```json
     {
       "host": "{{step.param.hostName}}",
       "version": "{{step.param.version}}"
     }
     ```
4. Speichern

**Prüfpunkte:**
- [ ] Script wird akzeptiert (mit $var Assignments)
- [ ] Autocompletion schlägt `step.param.hostName`, `step.param.version` vor
- [ ] Properties-Panel zeigt diese Params
- [ ] JSON wird korrekt gespeichert

**Erwartung:** Strukturierter Output mit Parametern funktioniert

---

## Teil 6: Workflow-Ausführung & Debugging

### Test 6.1 — Einfacher Workflow ausführen

**Schritte:**
1. Erstelle Workflow mit 3 Nodes:
   - Node 1: `log` (message: `"Starting"`)
   - Node 2: `delay` (5 Sekunden)
   - Node 3: `log` (message: `"Done"`)
2. Verbinde mit Edges: 1 → 2 → 3
3. Speichern
4. Klick "Execute" oder `POST /api/workflows/{id}/execute`
5. Execution startet

**Prüfpunkte:**
- [ ] Execute-Button ist sichtbar/aktiv
- [ ] Execution-Panel öffnet sich (rechts oder Modal)
- [ ] Status wechselt zu "Running"
- [ ] Nach ~5s: Status wechselt zu "Completed"
- [ ] Keine Fehler in Console oder Execution Log

**Erwartung:** Workflow läuft durch

---

### Test 6.2 — Workflow mit Parameters ausführen

**Schritte:**
1. Erstelle Workflow mit **Manual Trigger**:
   - Trigger Node: `manualTrigger`
   - **Parameters:**
     ```json
     [
       { "name": "environmentType", "type": "string", "required": true, "default": "dev" },
       { "name": "deployCount", "type": "integer", "required": false, "default": 1 }
     ]
     ```
2. Downstream Node `log`:
   - **Message:** `"Deploying to {{param.environmentType}} (count: {{param.deployCount}})"`
3. Speichern
4. Execute-Dialog öffnet sich
5. Parameter-Inputs zeigen (environmentType, deployCount)
6. Setze: environmentType = `"prod"`, deployCount = `3`
7. Klick "Execute"

**Prüfpunkte:**
- [ ] Execute-Dialog zeigt Parameter-Felder
- [ ] Felder sind optional/required wie definiert
- [ ] Default-Werte werden angezeigt
- [ ] Eingegebene Werte werden in Workflow verfügbar (`{{param.*}}`)
- [ ] Execution läuft mit Parametern

**Erwartung:** Manueller Trigger mit Parametern funktioniert

---

### Test 6.3 — Bedingter Workflow (mehrere Branches)

**Schritte:**
1. Erstelle Workflow:
   - Trigger: `manualTrigger` mit Parameter `result` (string)
   - Node 1: `log` message `"Processing"`
   - Node 2 (Success): `log` message `"Success!"`
   - Node 3 (Error): `log` message `"Failed!"`
   - Node 4 (Fallback): `log` message `"Unknown"`
2. Edges:
   - Trigger → Node 1 (immer)
   - Node 1 → Node 2 (Condition: `{{param.result}}` == `"success"`)
   - Node 1 → Node 3 (Condition: `{{param.result}}` == `"error"`)
   - Node 1 → Node 4 (Condition: `{{param.result}}` != `"success"` AND != `"error"`)
3. Speichern
4. Execute mit `result = "success"` → nur Node 2 sollte laufen
5. Execute mit `result = "error"` → nur Node 3 sollte laufen
6. Execute mit `result = "unknown"` → nur Node 4 sollte laufen

**Prüfpunkte:**
- [ ] Edges mit Bedingungen werden gerendert
- [ ] Execution-Timeline zeigt nur relevante Nodes als "Completed"
- [ ] Andere Nodes zeigen "Skipped"
- [ ] Logs enthalten korrekte Messages

**Erwartung:** Bedingte Verzweigung funktioniert korrekt

---

### Test 6.4 — Parallel Branches (Fan-Out)

**Schritte:**
1. Erstelle Workflow:
   - Trigger: `manualTrigger`
   - Node 1: `log` message `"Starting parallel tasks"`
   - Node 2A: `delay` 3s (Output: `task_a`)
   - Node 2B: `delay` 5s (Output: `task_b`)
   - Node 2C: `delay` 2s (Output: `task_c`)
   - Node 3: `junction` mode `waitAll` (wartet auf alle 3)
   - Node 4: `log` message `"All tasks done"`
2. Edges:
   - Trigger → Node 1
   - Node 1 → Node 2A, Node 2B, Node 2C
   - Node 2A → Node 3
   - Node 2B → Node 3
   - Node 2C → Node 3
   - Node 3 → Node 4
3. Speichern
4. Execute
5. Timeline sollte zeigen, dass 2A, 2B, 2C parallel laufen
6. Gesamtzeit sollte ~5s sein (längster Branch)

**Prüfpunkte:**
- [ ] Alle 3 Branches starten parallel
- [ ] Junction wartet auf alle drei
- [ ] Gesamtzeit ≈ max(3s, 5s, 2s) + Overhead = ~5-6s
- [ ] Timeline zeigt Parallelität

**Erwartung:** Parallele Branches funktionieren

---

### Test 6.5 — Workflow Debug-Modus (Breakpoints)

**Schritte:**
1. Erstelle Workflow:
   - Node 1: `log` message `"Step 1"`
   - Node 2: `delay` 5s
   - Node 3: `log` message `"Step 3"`
2. Edges: 1 → 2 → 3
3. Speichern
4. Setze Breakpoint auf Node 2:
   - Rechtsklick auf Node 2 → "Toggle Breakpoint" oder Checkbox in Properties
   - Node sollte visuell markiert sein (z.B. roter Kreis)
5. Execute mit **Debug:** `true`
6. Execution läuft, pausiert vor Node 2
7. Execution-Panel zeigt "Paused at Node 2"
8. Variable-Inspector zeigt verfügbare Variablen
9. Klick "Continue" oder "Step Over"
10. Execution fährt fort

**Prüfpunkte:**
- [ ] Breakpoint-Toggle funktioniert
- [ ] Debug-Flag in Execute-Dialog sichtbar
- [ ] Execution pausiert bei Breakpoint
- [ ] Variable-Inspector zeigt Werte
- [ ] "Continue", "Step Over", "Stop" Buttons funktionieren
- [ ] Execution läuft nach Resume weiter

**Erwartung:** Debug-Modus funktioniert

---

### Test 6.6 — Execution Cancel

**Schritte:**
1. Erstelle Workflow mit 5× `delay` 10s (insgesamt 50s)
2. Speichern
3. Execute
4. Nach ~15s: Klick "Cancel" Button
5. Execution sollte abgebrochen werden

**Prüfpunkte:**
- [ ] Cancel-Button ist sichtbar während Execution
- [ ] Status wechselt zu "Cancelled"
- [ ] In-Flight Nodes werden abgebrochen
- [ ] Timeline zeigt Cancelled-Status

**Erwartung:** Execution kann abgebrochen werden

---

### Test 6.7 — Execution Retry

**Schritte:**
1. Erstelle Workflow mit Node `runScript`:
   - Script: `Write-Host "failed"; exit 1`
   - (simuliert Fehler)
2. Speichern
3. Execute → Workflow schlägt fehl
4. Im Execution-Panel: Klick "Retry"
5. Neue Execution startet mit gleichen Parametern

**Prüfpunkte:**
- [ ] Retry-Button ist sichtbar nach Fehler
- [ ] Neue Execution wird mit gleichen Parametern erstellt
- [ ] Neue Execution-ID wird generiert
- [ ] Alte Execution bleibt unverändert

**Erwartung:** Execution kann wiederholt werden

---

## Teil 7: Fehlerbehandlung & Edge Cases

### Test 7.1 — Ungültige Template-Syntax

**Schritte:**
1. Erstelle Node `log` mit Message:
   ```
   {{unknownVariable.output}}
   ```
2. Speichern
3. Execute
4. Bei Runtime sollte Variable-Resolution fehlschlagen oder Fehlermeldung zeigen

**Prüfpunkte:**
- [ ] Workflow läuft, aber zeigt Fehler bei Variable-Resolution
- [ ] Fehler wird in Logs dokumentiert
- [ ] Execution-Status ist `Failed`
- [ ] Error-Message ist aussagekräftig

**Erwartung:** Fehlerhafte Templates werden erkannt

---

### Test 7.2 — Ungültige Bedingung (Syntax-Fehler)

**Schritte:**
1. Erstelle Edge mit Condition:
   - Left: `{{step.param.value}}`
   - Operator: `==`
   - Right: `{{` (unvollständig, Syntax-Fehler)
2. Speichern-Versuch sollte möglicherweise warnen

**Prüfpunkte:**
- [ ] UI zeigt Validation-Error (rotes Highlight)
- [ ] Speichern wird blockiert oder warnt
- [ ] Fehlermeldung ist klar

**Erwartung:** Syntax-Fehler werden validiert

---

### Test 7.3 — Trigger-loser / Cycle-only Workflow zeigt `no-trigger` Lint-Fehler

**Schritte:**
1. Erstelle Nodes: A (`log`), B (`log`), C (`log`)
2. Edges: A → B → C → A (Zirkel, kein Trigger-Node)
3. Speichern

**Prüfpunkte:**
- [ ] Lint-Pill erscheint mit mindestens 1 Fehler
- [ ] Lint-Panel öffnen → Eintrag mit Code `no-trigger` sichtbar
- [ ] Meldung enthält "keinen Trigger" / "Einstiegspunkt"
- [ ] Publish-Button ist deaktiviert (Fehler blockiert Publish)
- [ ] Kein separates Cycle-Banner (das wurde durch den Lint-Fehler ersetzt)

**Erwartung:** Ein Workflow ohne aktiven Trigger-Node wird vom Lint als fehlerhaft markiert. Die Engine würde ohne Trigger 0 Roots ermitteln und die Execution sofort als `Failed` beenden.

---

### Test 7.4 — Isolierte Nodes (Orphans) zeigen Lint-Fehler

**Schritte:**
1. Erstelle Workflow ohne Trigger-Nodes:
   - Node 1: `log` (keine Edges)
   - Node 2: `log` (keine Edges)
2. Speichern

**Prüfpunkte:**
- [ ] Lint-Panel zeigt `isolated-node` ERROR für jeden isolierten Node (×2)
- [ ] Lint-Panel zeigt zusätzlich `no-trigger` ERROR (kein Trigger vorhanden)
- [ ] Publish-Button ist deaktiviert
- [ ] Kein `inDegree==0`-Fallback: Nodes werden **nicht** als Roots ausgeführt

**Erwartung:** Isolierte Nodes ohne eingehende Edges und ohne Trigger-Node erzeugen Lint-Fehler. Die Engine würde die Nodes als `Skipped` markieren (sie sind keine Roots), da ausschließlich aktive Trigger-Nodes als Einstiegspunkte gelten.

---

### Test 7.5 — Disabled Nodes & Edges

**Schritte:**
1. Erstelle Workflow: A → B → C
2. Setze Node B `disabled: true`
3. Speichern
4. Execute

**Prüfpunkte:**
- [ ] Node B wird übersprungen (`Skipped`)
- [ ] Node C wird nicht ausgeführt (weil B nicht lief)
- [ ] Execution endet erfolgreich (nicht fehler)

**Erwartung:** Disabled Nodes werden korrekt übersprungen

---

## Teil 8: Speichern & Persistierung

### Test 8.1 — Automatisches Speichern

**Schritte:**
1. Erstelle Workflow mit Nodes & Edges
2. Beobachte den Speicher-Button
3. Ändere einen Node-Wert
4. Speicher-Button wird aktiv (z.B. Highlight)
5. Warte ~2s → Button wird wieder grayed out (autosave?)
6. Reload Seite (F5)
7. Änderungen sollten erhalten sein

**Prüfpunkte:**
- [ ] Manuelles Speichern funktioniert (Ctrl+S oder Button)
- [ ] Änderungen werden persistiert
- [ ] Nach Reload: Workflow-State ist identisch
- [ ] Kein Datenverlust

**Erwartung:** Speichern funktioniert zuverlässig

---

### Test 8.2 — Workflow-Versioning

**Schritte:**
1. Erstelle Workflow mit Node 1
2. Speichern
3. Ändere zu Node 1 + Node 2
4. Speichern
5. Admin-Dashboard → Workflows → [Workflow wählen]
6. "Versions" Tab
7. Sollte 2+ Versionen zeigen

**Prüfpunkte:**
- [ ] Versions-Historie wird aufgebaut
- [ ] Jeder Save erzeugt neue Version (oder nur bei signifikanten Änderungen)
- [ ] Rollback ist möglich
- [ ] Alte Versionen sind abrufbar

**Erwartung:** Versions-Tracking funktioniert

---

## Teil 9: Spezielle Szenarien

### Test 9.1 — Sub-Workflow mit Parameters

**Schritte:**
1. Erstelle Parent-Workflow: `Parent`
   - Trigger: `manualTrigger` mit Parameter `env` (string)
   - Node: `startWorkflow` (Child: `Child`, Parameters: `{"environment": "{{param.env}}"}`)
2. Erstelle Child-Workflow: `Child`
   - Trigger: `manualTrigger` mit Parameter `environment`
   - Node: `log` message `"Running in {{param.environment}}"`
3. Speichern beide
4. Execute Parent mit `env = "prod"`
5. Child sollte mit `environment = "prod"` starten

**Prüfpunkte:**
- [ ] Parameter werden von Parent zu Child übergeben
- [ ] Child kann auf Parameter zugreifen
- [ ] Execution-Timeline zeigt Parent + Child
- [ ] Parent pausiert bis Child fertig ist (wenn `waitForCompletion: true`)

**Erwartung:** Sub-Workflow Parameter-Passing funktioniert

---

### Test 9.2 — Multiple Triggers im selben Workflow

**Schritte:**
1. Erstelle Workflow mit 2 Trigger-Nodes:
   - Trigger 1: `manualTrigger` (mit Start-Button)
   - Trigger 2: `scheduleTrigger` (mit Cron `0 * * * * ? *` = jede Stunde)
   - Downstream Node: `log` message `"Execution triggered"`
2. Speichern
3. Teste manuellen Trigger (Execute Button)
4. Teste Schedule-Trigger (warte oder manipuliere Zeit/Cron-Evaluierung)

**Prüfpunkte:**
- [ ] Beide Trigger sind sichtbar im Designer
- [ ] Manueller Trigger funktioniert
- [ ] Schedule-Trigger startet nach Cron-Zeit
- [ ] Beide können unabhängig Executions starten

**Erwartung:** Mehrere Trigger im Workflow funktionieren

---

### Test 9.3 — Großer Workflow (50+ Nodes, 100+ Edges)

**Schritte:**
1. Importiere oder erstelle großen Workflow (siehe [tech-demo/main.json](scripts/tech-demo/main.json) mit 39 Nodes)
2. Öffne im Designer
3. Teste Performance:
   - Zoom/Pan
   - Node-Auswahl
   - Speichern
   - Execute

**Prüfpunkte:**
- [ ] Designer bleibt responsiv
- [ ] Zoom/Pan ist flüssig
- [ ] Kein Lag bei Node-Auswahl
- [ ] Speichern erfolgt innerhalb <5s
- [ ] Execution startet ohne Timeout
- [ ] Memory-Leak: Browser-Tab belastet sich nicht über Zeit

**Erwartung:** Performance bei großen Workflows ist akzeptabel

---

### Test 9.4 — Workflow Export & Import

**Schritte:**
1. Erstelle Workflow mit 5 Nodes & Edges
2. Klick "Export" → JSON wird heruntergeladen
3. Öffne JSON in Editor, validiere Schema
4. Lösche Workflow aus DB
5. Klick "Import" → wähle JSON-Datei
6. Neuer Workflow wird importiert (ggf. mit Suffix "(Imported 1)")

**Prüfpunkte:**
- [ ] Export erzeugt valides JSON
- [ ] JSON enthält alle Nodes & Edges & Properties
- [ ] Import rekonstruiert Workflow korrekt
- [ ] Importierter Workflow funktioniert wie Original
- [ ] Namenskollisionen werden handhaben (Suffix)

**Erwartung:** Export/Import funktioniert lossless

---

## Teil 10: UI/UX Tests

### Test 10.1 — Properties-Panel Responsiveness

**Schritte:**
1. Wähle Node im Designer
2. Properties-Panel öffnet sich rechts
3. Ändere mehrere Felder schnell nacheinander
4. Alle Änderungen sollten sofort reflektiert werden

**Prüfpunkte:**
- [ ] Keine Verzögerung bei Input
- [ ] Keine verloren Eingaben
- [ ] UI bleibt responsiv

**Erwartung:** Properties-Panel ist flüssig

---

### Test 10.2 — Mobile Responsiveness (Smartphone/Tablet)

> Automatisiert in `e2e/mobile-responsive.spec.ts` (Kern-Guard: kein horizontaler Overflow bei 390px auf jeder Listen-Route + read-only Graph rendert Edges). Desktop-Layout bleibt ab `lg` unverändert.

**Schritte:**
1. Öffne die SPA im Mobile-View (DevTools: 390px width)
2. Toggle die Sidebar über das Hamburger-Menü in der TopBar (Drawer + abgedunkelter Backdrop, schließt automatisch bei Navigation)
3. Öffne die Listen-Seiten (Workflows, Executions, Machines, Users, Global Variables, Maintenance Windows, Audit) — sie sollen als gestapelte Cards erscheinen, nicht als breite Tabelle
4. Öffne einen Workflow → es erscheint die **read-only** Mobile-Graph-Ansicht (`MobileWorkflowView`); teste Pan/Pinch-Zoom

**Prüfpunkte:**
- [ ] Sidebar-Drawer öffnet/schließt korrekt (Hamburger + Backdrop, Auto-Close bei Navigation)
- [ ] Kein horizontaler Scroll/Overflow auf den Listen-Seiten bei 390px (Cards statt Tabelle)
- [ ] Workflow öffnet als read-only Graph mit Live-Execution-Status (kein Editieren/Node-Erstellung auf Mobile)
- [ ] Pan/Pinch-Zoom funktioniert; kein Crash

**Erwartung:** SPA voll nutzbar auf dem Smartphone; Designer-Editing bleibt Desktop-only (≥ `lg`).

---

### Test 10.3 — Accessibility (A11y) — Basic

**Schritte:**
1. Öffne Developer Tools
2. Nutze Accessibility Inspector / axe DevTools
3. Prüfe:
   - Alle Buttons haben Labels / aria-labels
   - Form-Felder haben Labels
   - Kontrast ist ausreichend
   - Keyboard-Navigation funktioniert (Tab, Enter, Escape)

**Prüfpunkte:**
- [ ] Keine kritischen Accessibility-Fehler
- [ ] Buttons sind keyboard-fokussierbar
- [ ] Fehlermeldungen sind für Screen-Reader lesbar
- [ ] Farbkontraste sind ausreichend (WCAG AA mindestens)

**Erwartung:** Basics der Barrierefreiheit sind erfüllt

---

## Teil 11: Dashboard

### Test 11.1 — Dashboard-Stats laden

**Schritte:**
1. Navigiere zu `/` (Dashboard)
2. Warte bis Stats geladen sind

**Prüfpunkte:**
- [ ] Stat-Cards sichtbar: Total Workflows, Running Executions, Success Rate, Machines
- [ ] 24h Execution-Chart rendert
- [ ] "Top Workflows" Liste zeigt nach Aktivität sortiert
- [ ] "Recent Executions" Liste ist aktuell
- [ ] Keine "loading"-States hängen

**Erwartung:** Dashboard zeigt aggregierte Daten korrekt

---

### Test 11.2 — Dashboard-Navigation

**Schritte:**
1. Klick auf Stat-Card "Total Workflows" → sollte zu `/workflows` navigieren
2. Klick auf einen Eintrag in "Top Workflows" → sollte direkt den Workflow öffnen
3. Klick auf einen Eintrag in "Recent Executions" → sollte Execution-Details öffnen

**Prüfpunkte:**
- [ ] Deep-Links funktionieren
- [ ] Zurück-Navigation (Browser-Back) funktioniert
- [ ] URL ändert sich korrekt

**Erwartung:** Dashboard ist voll navigierbar

---

### Test 11.3 — Telemetry/Observability Links

**Schritte:**
1. Bei aktiviertem OpenTelemetry: Execution ausführen
2. Im Execution-Detail: TraceId-Link klicken
3. Externer Tracer (Jaeger/Tempo) öffnet sich (wenn konfiguriert)

**Prüfpunkte:**
- [ ] TraceId wird pro Execution angezeigt
- [ ] Link öffnet externes Tool oder zeigt sinnvolle Fehlermeldung
- [ ] Bei `OpenTelemetry:Enabled: false`: Link ist ausgeblendet

**Erwartung:** Observability-Integration funktioniert opt-in

---

## Teil 12: Keyboard-Shortcuts & Productivity

### Test 12.1 — Undo/Redo

**Schritte:**
1. Öffne Workflow im Designer
2. Füge einen Node hinzu
3. Drücke **Ctrl+Z** → Node verschwindet
4. Drücke **Ctrl+Y** oder **Ctrl+Shift+Z** → Node erscheint wieder
5. Mache 10+ Änderungen → Undo alle mit Ctrl+Z

**Prüfpunkte:**
- [ ] Ctrl+Z macht letzte Aktion rückgängig
- [ ] Ctrl+Y / Ctrl+Shift+Z macht Redo
- [ ] History reicht bis zu 50 Schritte zurück
- [ ] Undo/Redo funktioniert für: Node add/delete/move, Edge add/delete, Property-Änderungen
- [ ] Kein State-Korruption

**Erwartung:** Undo/Redo ist zuverlässig

---

### Test 12.2 — Copy/Paste/Duplicate (Ctrl+C, Ctrl+V, Ctrl+D)

**Schritte:**
1. Wähle einen oder mehrere Nodes aus
2. Drücke **Ctrl+C** (Copy)
3. Drücke **Ctrl+V** (Paste) → neue Nodes erscheinen mit Offset (+40px)
4. Wähle einen Node → **Ctrl+D** (Duplicate) → direktes Duplizieren

**Prüfpunkte:**
- [ ] Copy speichert in sessionStorage (`np_clipboard`)
- [ ] Paste fügt Nodes mit neuen IDs ein
- [ ] Paste erhält Edges zwischen kopierten Nodes
- [ ] Ctrl+D dupliziert inline
- [ ] Cross-Tab-Paste möglich (gleicher Browser)

**Erwartung:** Clipboard-Operationen funktionieren

---

### Test 12.3 — Grouping (Ctrl+G)

**Schritte:**
1. Wähle 3+ Nodes mit Marquee-Select
2. Drücke **Ctrl+G**
3. Group-Node erscheint und umschließt die Auswahl
4. Wähle Group → Rechtsklick → "Ungroup"

**Prüfpunkte:**
- [ ] Group-Node wird erstellt
- [ ] Group kann collapsed/expanded werden
- [ ] Nodes innerhalb Group behalten ihre Positionen
- [ ] Ungroup stellt Einzel-Nodes wieder her
- [ ] Group hat eigenes Label (editierbar)

**Erwartung:** Node-Grouping funktioniert

---

### Test 12.4 — Help-Overlay (`?`)

**Schritte:**
1. Drücke Taste **?** (Shift+/)
2. Help-Modal erscheint mit allen Shortcuts
3. Schließen mit **Escape**

**Prüfpunkte:**
- [ ] Help zeigt alle Shortcuts an
- [ ] Modal ist dismissible (ESC, X-Button, Backdrop)
- [ ] Shortcuts sind aktuell (entsprechen tatsächlichem Verhalten)

**Erwartung:** Help-Overlay ist nutzerfreundlich

---

### Test 12.5 — Search-Overlay (Ctrl+F)

**Schritte:**
1. Öffne Workflow mit vielen Nodes
2. Drücke **Ctrl+F**
3. Search-Overlay erscheint
4. Tippe Node-Name oder Activity-Typ
5. Ergebnisse zeigen Preview
6. Klick auf Ergebnis → Canvas pannt zum Node & highlightet ihn

**Prüfpunkte:**
- [ ] Search-Overlay öffnet sich mit Ctrl+F
- [ ] Fuzzy-Search funktioniert (auch Activity-Type, Label, Script-Content)
- [ ] Preview zeigt Node-Details
- [ ] Canvas fokussiert den gewählten Node
- [ ] ESC schließt Overlay

**Erwartung:** Search ist schnell und akkurat

---

### Test 12.6 — Canvas-Shortcuts

| Shortcut | Erwartete Aktion |
|---|---|
| `Ctrl+S` | Workflow speichern |
| `Delete` / `Backspace` | Ausgewählte Nodes/Edges löschen |
| `Ctrl+A` | Alle Nodes auswählen |
| `Escape` | Auswahl aufheben / Dialog schließen |
| `Space+Drag` | Canvas pannen |
| `Ctrl+Scroll` | Zoom in/out |
| `Ctrl+0` | Fit-to-View / Zoom reset |

**Prüfpunkte:**
- [ ] Alle aufgelisteten Shortcuts funktionieren
- [ ] Keine Konflikte mit Browser-Shortcuts
- [ ] Shortcuts funktionieren auch mit gewähltem Text-Input (oder werden abgefangen)

**Erwartung:** Standard-Shortcuts sind durchgängig implementiert

---

## Teil 13: Spezielle Node-Types

### Test 13.1 — Sticky Note Node

**Schritte:**
1. Add Node → Wähle "Sticky Note" Typ
2. Sticky Note erscheint (gelbe Notiz-Optik)
3. Doppel-Klick → Text editierbar
4. Gib Text ein: `"Diese Region macht X"`
5. Ändere Farbe (gelb → blau → grün)
6. Resize die Note per Drag an der Ecke
7. Speichern

**Prüfpunkte:**
- [ ] Sticky Note ist visuell von Activity-Nodes unterscheidbar
- [ ] Text ist editierbar (Rich-Text oder Plain)
- [ ] Farben-Palette funktioniert
- [ ] Resize funktioniert
- [ ] Sticky Note wird NICHT in Execution einbezogen
- [ ] Sticky Note kann keine Edges haben (oder werden ignoriert)

**Erwartung:** Sticky Notes sind Dokumentations-only

---

### Test 13.2 — Group Node

**Schritte:**
1. Erstelle 3 Activity-Nodes
2. Wähle alle → **Ctrl+G** → Group Node erstellt
3. Group hat Label "Group" (editierbar auf z.B. "Data Processing")
4. Klick auf Collapse-Icon → Group kollabiert, Child-Nodes ausgeblendet
5. Expand wieder
6. Drag Group → alle Child-Nodes werden mitbewegt
7. Lösche Group → Optionen: nur Group löschen oder mit Kindern

**Prüfpunkte:**
- [ ] Group umschließt Nodes visuell
- [ ] Collapse/Expand funktioniert
- [ ] Drag-Parent bewegt Children
- [ ] Labels sind editierbar
- [ ] Delete-Dialog fragt nach Policy (nur Wrapper vs. komplettes Delete)

**Erwartung:** Groups helfen bei Strukturierung

---

## Teil 14: Alle Trigger-Typen

### Test 14.1 — `scheduleTrigger` (Cron)

**Schritte:**
1. Add Trigger-Node: `scheduleTrigger`
2. Properties:
   - **Cron Expression:** `0 */5 * * * ? *` (alle 5 Minuten)
   - **Timezone:** `UTC` (optional)
3. Speichern + Enable Workflow
4. Warte bis nächster Fire-Zeitpunkt
5. Execution sollte automatisch starten

**Prüfpunkte:**
- [ ] Cron-Expression wird validiert (7-Field Quartz-Syntax)
- [ ] Ungültige Cron-Expressions werden abgelehnt
- [ ] Next Fire Time wird in UI angezeigt
- [ ] Execution startet zum geplanten Zeitpunkt
- [ ] `trigger.schedule.firedAt` und `.nextFireAt` sind in Variablen verfügbar
- [ ] Disabled Workflow → kein Fire

**Erwartung:** Schedule-Trigger funktioniert

---

### Test 14.2 — `webhookTrigger` (HTTP)

**Schritte:**
1. Add Trigger-Node: `webhookTrigger`
2. Properties:
   - **Path:** `my-webhook-test`
   - **Method:** `POST`
   - **Secret:** `my-secret-123` (optional)
3. Speichern + Enable Workflow
4. Sende HTTP-Request:
   ```bash
   curl -X POST http://localhost:5000/api/webhooks/{workflowName}/my-webhook-test \
     -H "X-Webhook-Secret: my-secret-123" \
     -H "Content-Type: application/json" \
     -d '{"key": "value"}'
   ```
5. Execution sollte starten

**Prüfpunkte:**
- [ ] Webhook-URL wird in UI angezeigt (kopier-bar)
- [ ] Request ohne Secret → 401
- [ ] Request mit falschem Secret → 401
- [ ] Request mit korrektem Secret → 202
- [ ] `webhook.body`, `webhook.query.*`, `webhook.header.*` sind verfügbar
- [ ] Rate-Limit aktiv (60/Min per IP)

**Erwartung:** Webhook-Trigger funktioniert sicher

---

### Test 14.3 — `fileWatcherTrigger`

**Schritte:**
1. Add Trigger-Node: `fileWatcherTrigger`
2. Properties:
   - **Directory:** `C:\temp\watch` (muss existieren)
   - **Filter:** `*.txt`
   - **Watch Type:** `Created`
   - **Include Subdirectories:** `true`
3. Speichern + Enable Workflow
4. Erstelle Datei: `C:\temp\watch\test.txt`
5. Execution sollte starten

**Prüfpunkte:**
- [ ] Directory-Validation (existiert/berechtigung)
- [ ] Filter-Glob akzeptiert Wildcards
- [ ] Watch-Types: Created, Modified, Deleted, Renamed
- [ ] `trigger.file.action`, `.path`, `.name` sind verfügbar
- [ ] Mehrere Dateien → mehrere Executions

**Erwartung:** FileWatcher funktioniert

---

### Test 14.4 — `databaseTrigger`

**Schritte:**
1. Add Trigger-Node: `databaseTrigger`
2. Properties:
   - **Provider:** `sqlite`
   - **Connection String:** `Data Source=test.db`
   - **Query:** `SELECT id, status FROM tasks WHERE status='new'`
   - **Interval Seconds:** `5`
3. Speichern + Enable Workflow
4. Füge Zeile in DB ein: `INSERT INTO tasks (id, status) VALUES (1, 'new')`
5. Execution sollte nach ≤5s starten

**Prüfpunkte:**
- [ ] Connection wird validiert
- [ ] Query wird periodisch ausgeführt
- [ ] `trigger.db.sentinel` und `.previous` verfügbar
- [ ] Keine Duplikate (nur neue Rows triggern)
- [ ] Bei DB-Fehler: Warning, kein Crash

**Erwartung:** DB-Trigger pollt korrekt

---

### Test 14.5 — `eventLogTrigger` (Windows)

**Schritte:**
1. Add Trigger-Node: `eventLogTrigger`
2. Properties:
   - **Log Name:** `Application`
   - **Source:** `Application Error` (optional)
   - **Entry Type:** `Error`
   - **Message Pattern:** `.*crash.*` (regex, optional)
3. Speichern + Enable Workflow
4. Schreibe Test-Event ins Log: `eventcreate /T ERROR /ID 100 /L APPLICATION /SO "TestSource" /D "application crash"`
5. Execution sollte starten

**Prüfpunkte:**
- [ ] Log-Name-Dropdown zeigt verfügbare Logs
- [ ] Entry-Type Filter funktioniert
- [ ] Message-Pattern-Regex wird angewendet
- [ ] `trigger.eventlog.*` verfügbar
- [ ] Nur Windows (andere OS → Trigger disabled)

**Erwartung:** EventLog-Trigger funktioniert auf Windows

---

## Teil 15: Admin & User-Management

### Test 15.1 — User erstellen (Admin)

**Schritte:**
1. Login als Admin
2. Navigiere zu `/users`
3. Klick "New User"
4. Felder:
   - **Username:** `operator1`
   - **Email:** `op1@example.com`
   - **Role:** `Operator`
   - **Password:** `SecurePass123!`
5. Klick "Create"

**Prüfpunkte:**
- [ ] User wird erstellt
- [ ] Liste zeigt neuen User
- [ ] Passwort ist gehasht (BCrypt)
- [ ] Audit-Log-Eintrag `USER_CREATED` vorhanden

**Erwartung:** User-Creation funktioniert

---

### Test 15.2 — User-Rollen & Berechtigungen (RBAC)

**Schritte:**
1. Erstelle User mit Rolle `Viewer`
2. Logout → Login als Viewer
3. Versuche: Workflow erstellen → **403 Forbidden**
4. Versuche: Execution cancel → **403**
5. Versuche: Workflow ansehen → **200 OK**
6. Wiederhole für `Operator`:
   - Workflow erstellen → OK
   - Workflow löschen → **403**
7. Wiederhole für `Admin`:
   - Alle Aktionen → OK

**Prüfpunkte:**
- [ ] Viewer: Nur GET-Endpoints
- [ ] Operator: CRUD auf Workflows/Machines/Credentials, kein Delete
- [ ] Admin: Volle Rechte inkl. Delete
- [ ] UI-Buttons werden bei fehlenden Rechten ausgeblendet oder grayed out
- [ ] API-Fehler (403) werden im UI als verständliche Meldung gezeigt

**Erwartung:** RBAC ist konsequent durchgesetzt

---

### Test 15.3 — Passwort-Reset (Admin für User)

**Schritte:**
1. Admin: Öffne User-Detail
2. Klick "Reset Password"
3. Neues Passwort eingeben
4. User wird informiert (E-Mail optional)
5. Logout → User-Login mit neuem Passwort

**Prüfpunkte:**
- [ ] Admin kann Passwort zurücksetzen
- [ ] User kann sich mit neuem Passwort einloggen
- [ ] Altes Passwort funktioniert nicht mehr
- [ ] Audit-Log-Eintrag

**Erwartung:** Password-Reset funktioniert

---

### Test 15.4 — User deaktivieren/löschen

**Schritte:**
1. Admin: Öffne User-Liste
2. Wähle User → "Deactivate"
3. User kann sich nicht mehr einloggen
4. Wähle anderen User → "Delete"
5. User wird aus DB entfernt

**Prüfpunkte:**
- [ ] Deaktivieren: Login schlägt fehl, User bleibt in DB
- [ ] Löschen: User wird entfernt, Audit bleibt erhalten
- [ ] Eigener Account kann nicht gelöscht werden (Safety)
- [ ] Letzter Admin kann nicht gelöscht werden

**Erwartung:** User-Lifecycle ist sicher

---

## Teil 16: Audit Log

### Test 16.1 — Audit-Log ansehen

**Schritte:**
1. Login als Admin
2. Navigiere zu `/audit`
3. Audit-Log lädt

**Prüfpunkte:**
- [ ] Liste zeigt letzte 100 Einträge
- [ ] Spalten: Timestamp, Username, Action, Resource, ResourceId, Details
- [ ] Einträge sind chronologisch sortiert (neueste oben)
- [ ] Live-Reload alle 15s

**Erwartung:** Audit-Log ist zugänglich

---

### Test 16.2 — Audit-Log filtern

**Schritte:**
1. Filter nach Action: `WORKFLOW_CREATED`
2. Filter nach ResourceType: `Workflow`
3. Filter nach User: `admin`
4. Filter nach Zeitraum: letzte 24h

**Prüfpunkte:**
- [ ] Filter-Felder sind verfügbar
- [ ] Filter-Logik: AND zwischen verschiedenen Filtern
- [ ] Max. 500 Einträge pro Abfrage
- [ ] URL wird als Query-Parameter aktualisiert (deep-linkable)

**Erwartung:** Filter sind performant

---

### Test 16.3 — Audit für sensitive Aktionen

**Schritte:**
1. Ausführen der folgenden Aktionen:
   - Workflow erstellen
   - Workflow löschen
   - Credential erstellen
   - Login fehlgeschlagen
2. Im Audit-Log: Alle 4 Aktionen sollten erscheinen

**Prüfpunkte:**
- [ ] `WORKFLOW_CREATED` / `WORKFLOW_DELETED` sichtbar
- [ ] `CREDENTIAL_CREATED` sichtbar (aber KEIN Passwort/Secret)
- [ ] `LOGIN_FAILED` mit Username, aber nicht Passwort
- [ ] Details-JSON ist lesbar

**Erwartung:** Audit dokumentiert alle Mutationen

---

## Teil 17: Theme & UX-Features

### Test 17.1 — Dark/Light Mode Toggle

**Schritte:**
1. Öffne App
2. Setting-Menu → Theme
3. Optionen: `System` + 7 Skins — hell: `Light`, `Light Grey`, `Light Bank`; dunkel: `Dark`, `Dark Lilac`, `Dark Bank`, `Nebula`
4. Wähle `Dark` → UI wechselt sofort zu dunklem Theme
5. Wähle `Light` → UI wechselt zurück
6. Wähle `System` → folgt OS-Präferenz
7. Wähle `Nebula` → futuristischer Cyan-Deep-Space-Skin (Glas-Cards, Cyan-Glow, Mesh-Backdrop); Canvas-Nodes bleiben neutral

**Prüfpunkte:**
- [ ] Theme-Change ist instant (kein Flicker)
- [ ] Preference bleibt über Reload erhalten (localStorage)
- [ ] Canvas-Hintergrund, Nodes, Edges folgen dem Theme
- [ ] Properties-Panel ebenfalls
- [ ] Kontraste in Dark Mode sind ausreichend

**Erwartung:** Theme-Switching ist seamless

---

### Test 17.2 — Mini-Map

**Schritte:**
1. Öffne großen Workflow
2. Mini-Map unten rechts sichtbar
3. Klick in Mini-Map → Viewport springt zu gewählter Position
4. Drag in Mini-Map → Viewport pannt kontinuierlich

**Prüfpunkte:**
- [ ] Mini-Map zeigt alle Nodes als Thumbnails
- [ ] Viewport-Rectangle ist sichtbar
- [ ] Click-to-Navigate funktioniert
- [ ] Mini-Map kann ein-/ausgeblendet werden
- [ ] Mini-Map lag-frei bei großen Workflows

**Erwartung:** Mini-Map ist nutzbar

---

### Test 17.3 — Node Context-Menu

**Schritte:**
1. Rechtsklick auf Node
2. Menu zeigt Optionen:
   - Duplicate
   - Enable/Disable
   - Toggle Breakpoint
   - Copy / Cut
   - Delete
3. Klick auf jede Option → Aktion wird ausgeführt

**Prüfpunkte:**
- [ ] Menu erscheint an Mausposition
- [ ] Alle Optionen funktionieren
- [ ] Menu schließt bei Outside-Click
- [ ] Menu schließt bei ESC

**Erwartung:** Context-Menu ist intuitiv

---

### Test 17.4 — Drag-Drop aus Activity-Sidebar

**Schritte:**
1. Activity-Sidebar links öffnen (wenn vorhanden)
2. Liste der Activity-Typen
3. Drag `runScript` → drop auf Canvas
4. Node wird an Drop-Position erstellt
5. Test für mehrere Activity-Typen

**Prüfpunkte:**
- [ ] Sidebar zeigt alle kategorisierten Activities
- [ ] Drag-Ghost ist sichtbar
- [ ] Drop-Position = Canvas-Koordinate
- [ ] Neue Node hat Default-Config

**Erwartung:** Drag-Drop ist flüssig

---

## Teil 18: Workflow-Organisation

### Test 18.1 — Workflow-Ordner (Folder Hierarchy)

**Schritte:**
1. Workflow-Liste öffnen
2. Erstelle Ordner: `Production`, `Development`
3. Drag Workflow `WF1` auf Ordner `Production`
4. Workflow ist nun unter `Production`
5. Erstelle Sub-Ordner `Production/Web`
6. Drag Workflow dorthin

**Prüfpunkte:**
- [ ] Ordner erstellbar
- [ ] Drag-Drop zwischen Ordnern funktioniert
- [ ] Hierarchie wird persistiert
- [ ] Breadcrumb-Navigation
- [ ] Ordner können gelöscht werden (Warnung wenn nicht leer)

**Erwartung:** Workflows sind organisierbar

---

### Test 18.2 — Bulk-Operations auf Workflows

**Schritte:**
1. Workflow-Liste: Select Multiple (Checkbox oder Shift-Click)
2. Aktionen-Menu zeigt: Bulk Duplicate, Bulk Delete, Bulk Enable/Disable, Bulk Export
3. Teste Bulk-Delete: Confirm-Dialog → Alle markierten werden gelöscht

**Prüfpunkte:**
- [ ] Multi-Select funktioniert
- [ ] Bulk-Actions sind sichtbar
- [ ] Confirm-Dialog vor destruktiven Aktionen
- [ ] Progress-Feedback bei Langläufern (viele Workflows)

**Erwartung:** Bulk-Operations sparen Zeit

---

### Test 18.3 — Workflow-Suche & Filter (Liste)

**Schritte:**
1. Workflow-Liste: Such-Feld oben
2. Tippe Teil-Name
3. Liste filtert live
4. Zusätzlich: Filter nach Status (Enabled/Disabled), Tags, Owner

**Prüfpunkte:**
- [ ] Live-Search funktioniert (debounced)
- [ ] Filter sind kombinierbar
- [ ] Clear-Button setzt Filter zurück
- [ ] URL-State für Filter

**Erwartung:** Workflow-Liste ist durchsuchbar

---

## Teil 19: Workflow-Diff / Version-Compare

### Test 19.1 — Versions-Historie ansehen

**Schritte:**
1. Öffne Workflow
2. Klick "Versions" Tab oder ähnlich
3. Liste aller Versionen: V1, V2, V3, ...
4. Jede Version: Timestamp, User, Change-Summary

**Prüfpunkte:**
- [ ] Versions werden chronologisch angezeigt
- [ ] Metadaten pro Version (User, Comment?)
- [ ] "Current"-Markierung auf aktiver Version

**Erwartung:** Version-Historie ist zugänglich

---

### Test 19.2 — Workflow-Diff-Modal

**Schritte:**
1. Klick "Compare" zwischen V2 und V4
2. Diff-Modal öffnet sich:
   - Added Nodes (grün)
   - Removed Nodes (rot)
   - Modified Nodes (gelb)
3. Details pro Änderung

**Prüfpunkte:**
- [ ] Side-by-side oder unified Diff
- [ ] Farb-Coding ist konsistent
- [ ] Diff-Stats: Nodes/Edges added/removed/modified
- [ ] Modal schließbar
- [ ] Exportierbar?

**Erwartung:** Diff ist verständlich

---

### Test 19.3 — Rollback auf alte Version

**Schritte:**
1. Wähle V2 in Versions-Liste
2. Klick "Rollback to this version"
3. Bestätigungs-Dialog
4. Nach Confirm: Workflow entspricht V2
5. V5 wird erstellt (Rollback ist append-only Event)

**Prüfpunkte:**
- [ ] Rollback erstellt neue Version (keine Löschung)
- [ ] UI zeigt neue aktive Version
- [ ] Audit-Log-Eintrag `WORKFLOW_ROLLED_BACK`
- [ ] Execution läuft nach Rollback gegen neue Definition

**Erwartung:** Rollback ist sicher & nachvollziehbar

---

## Teil 20: Machines & Credentials

### Test 20.1 — Machine hinzufügen & testen

**Schritte:**
1. Navigiere zu `/machines`
2. Klick "New Machine"
3. Felder:
   - **Name:** `Test-Server-01`
   - **Hostname:** `server01.local`
   - **Port:** `5985` (WinRM default)
   - **Use SSL:** `false` (oder `true` für 5986)
   - **Default Credential:** (wähle aus Dropdown)
4. Speichern
5. Klick "Test Connection" → WinRM-Check

**Prüfpunkte:**
- [ ] Machine wird erstellt
- [ ] Test-Connection gibt Feedback (Success/Error)
- [ ] Bei Error: Verständliche Meldung (Timeout, Auth, Cert)
- [ ] Machine erscheint in Dropdown bei Remote-Activities

**Erwartung:** Machine-Management funktioniert

---

### Test 20.2 — Credential hinzufügen

**Schritte:**
1. Settings → Credentials → "New Credential"
2. Felder:
   - **Name:** `DomainAdmin`
   - **Username:** `DOMAIN\admin`
   - **Password:** `secret123`
   - **Is Secret:** `true`
3. Speichern

**Prüfpunkte:**
- [ ] Credential wird gespeichert
- [ ] Passwort ist DPAPI-verschlüsselt in DB
- [ ] UI zeigt `***` statt Passwort
- [ ] Credential in Machine-Dropdown verfügbar
- [ ] Audit-Eintrag `CREDENTIAL_CREATED`

**Erwartung:** Credentials sind sicher

---

### Test 20.3 — Credential löschen mit Abhängigkeiten

**Schritte:**
1. Credential `C1` ist von Machine `M1` referenziert
2. Versuch: Credential löschen
3. Warnung: "In Use by 1 Machine"
4. Optionen: Cancel / Force-Delete (mit Clear-Referenz)

**Prüfpunkte:**
- [ ] Abhängigkeits-Check vor Delete
- [ ] Warnung mit Liste der Referenzen
- [ ] Force-Delete entfernt Referenz aus Machines

**Erwartung:** Sicheres Löschen mit Dependency-Check

---

## Teil 21: Global Variables

### Test 21.1 — Global Variable erstellen (Admin-only)

**Schritte:**
1. Navigiere zu `/global-variables`
2. Admin sieht volle Liste
3. Klick "New Variable"
4. Felder:
   - **Name:** `API_BASE_URL`
   - **Value:** `https://api.example.com`
   - **Is Secret:** `false`
5. Speichern

**Prüfpunkte:**
- [ ] Variable wird erstellt
- [ ] Name-Validation: UPPER_SNAKE_CASE empfohlen
- [ ] In Workflow-Templates verfügbar: `{{globals.API_BASE_URL}}`
- [ ] Operator: Read-Only (kann lesen in Workflows, nicht mutieren via UI)

**Erwartung:** Global Variables sind zentral verwaltet

---

### Test 21.2 — Secret Global Variable

**Schritte:**
1. Erstelle Variable `API_KEY`, Value `sk-secret-xyz`, **Is Secret:** `true`
2. In UI: Wert wird als `***` angezeigt
3. In Workflow verwenden: `{{globals.API_KEY}}`
4. Execution-Log sollte Wert redacten

**Prüfpunkte:**
- [ ] Value in UI maskiert
- [ ] Wert DPAPI-verschlüsselt in DB
- [ ] Execution-Output redacted (über OutputRedactor)
- [ ] In Audit-Log: Value NICHT enthalten

**Erwartung:** Secrets bleiben geheim

---

## Teil 22: SCOrch Import (SCORCH-XML)

### Test 22.1 — SCOrch Runbook Import

**Schritte:**
1. Workflows-Liste → "Import SCOrch Runbook"
2. Upload `*.ois_export` oder XML-Datei
3. Import-Dialog zeigt erkannte Runbooks
4. Auswahl + "Import"

**Prüfpunkte:**
- [ ] XML bis 50 MiB akzeptiert
- [ ] Heuristics-Konvertierung zeigt Activity-Typen-Mapping
- [ ] Nicht-mappbare Activities → `runScript` Fallback mit Warning
- [ ] Import-Report: Success/Warnings/Errors pro Runbook
- [ ] Audit-Log `WORKFLOW_IMPORTED_SCORCH`

**Erwartung:** SCOrch-Migration ist machbar

---

### Test 22.2 — SCOrch Detailed Results

**Schritte:**
1. Import großer SCOrch-Datei mit 20+ Runbooks
2. Report zeigt pro Runbook:
   - Imported Nodes Count
   - Fallback-Activities (Count + Details)
   - Warnings

**Prüfpunkte:**
- [ ] Detailed Report ist übersichtlich
- [ ] Pro Runbook: Expand/Collapse
- [ ] Export-Button für Report (JSON/CSV)

**Erwartung:** Report hilft bei Migration-Review

---

## Teil 23: External Trigger API & Idempotency

### Test 23.1 — External Trigger via API-Key

**Schritte:**
1. Config: `ExternalTrigger:ApiKey: "my-api-key-xyz"`
2. Call:
   ```bash
   curl -X POST http://localhost:5000/api/trigger/MyWorkflow \
     -H "X-Api-Key: my-api-key-xyz" \
     -H "Content-Type: application/json" \
     -d '{"parameters": {"env": "prod"}}'
   ```
3. Response: 202 Accepted + ExecutionId

**Prüfpunkte:**
- [ ] Ohne Config → 503
- [ ] Ohne API-Key → 401
- [ ] Falscher Key → 401
- [ ] Korrekter Key → 202
- [ ] Execution wird erstellt

**Erwartung:** External Trigger ist sicher

---

### Test 23.2 — Idempotency-Key

**Schritte:**
1. Call mit `Idempotency-Key: unique-123`
2. Wiederhole Call mit gleichem Key
3. Response-Header `Idempotent-Replayed: true`
4. Gleiche ExecutionId wie erste Response

**Prüfpunkte:**
- [ ] Zweite Call liefert Original-Execution
- [ ] Header `Idempotent-Replayed: true`
- [ ] TTL 24h (danach neu)
- [ ] Verschiedene Keys → verschiedene Executions

**Erwartung:** Idempotency verhindert Duplikate

---

## Teil 24: Real-time SignalR

### Test 24.1 — Live Execution-Updates

**Schritte:**
1. Öffne Workflow in Tab A
2. Öffne Executions-Page in Tab B
3. In Tab A: Execute Workflow
4. In Tab B: Execution erscheint live
5. Status-Updates erfolgen ohne Reload

**Prüfpunkte:**
- [ ] SignalR-Connection aktiv (DevTools → Network → WS)
- [ ] Step-Status-Updates sind instant (<1s)
- [ ] StepStarted, StepCompleted, StepFailed Events
- [ ] Bei Debug: StepPaused Event
- [ ] Reconnect nach Connection-Loss

**Erwartung:** Live-Updates funktionieren zuverlässig

---

### Test 24.2 — Multi-User Live-Editing

**Schritte:**
1. User A und User B öffnen gleichen Workflow
2. User A ändert Node
3. User B: Sieht Änderung? (optional, abhängig von Implementierung)

**Prüfpunkte:**
- [ ] Entweder: Live-Sync oder Warning ("modified by another user")
- [ ] Keine Datenverluste durch Race-Condition
- [ ] Save-Conflict-Dialog bei gleichzeitigem Save

**Erwartung:** Multi-User-Szenario hat sinnvolles Verhalten

---

## Teil 25: Authentication-Lifecycle (Bootstrap, JWT, CSRF, Lockout)

### Test 25.1 — Bootstrap-Token-Flow auf frischer DB

**Setup:** App-DB ohne User. `admin-setup.token` existiert in ContentRoot (`{ContentRoot}/admin-setup.token` oder `Security:AdminSetupTokenPath`).

**Schritte:**
1. Browser auf `/login`.
2. Login-Form mit Username `admin`, Passwort `Admin#2025!`, Setup-Token aus Datei kopieren.
3. Submit.

**Prüfpunkte:**
- [ ] Login-Erfolg, Redirect auf Dashboard.
- [ ] `admin-setup.token` ist nach Konsum gelöscht (nicht nur leer).
- [ ] `Audit-Log` enthält `USER_CREATED_BOOTSTRAP`.
- [ ] Cookie `np_auth` gesetzt mit `HttpOnly`, `SameSite=Lax`.
- [ ] Cookie `np_csrf` ebenfalls gesetzt.

**Erwartung:** First-Login auf leerer DB legt Admin an, Token-File ist verbraucht.

---

### Test 25.2 — Account-Lockout nach Brute-Force

**Schritte:**
1. Mit `admin` 4× falsches Passwort eingeben.
2. 5. Versuch ebenfalls falsch.
3. 6. Versuch jetzt mit korrektem Passwort.

**Prüfpunkte:**
- [ ] 5. Versuch bekommt 423 (oder UI-Banner "Account gesperrt").
- [ ] Audit-Log enthält 4× `LOGIN_FAILED` und 1× `LOGIN_LOCKED`.
- [ ] 6. Versuch (korrekt) wird trotzdem abgewiesen — Lockout greift.
- [ ] Nach Wartezeit (oder Admin-Reset) ist Login wieder möglich.

**Erwartung:** Brute-Force-Schwelle hard-greift, auch korrekte Credentials werden während Lockout abgewiesen.

---

### Test 25.3 — Login Rate-Limit (5/min)

**Schritte:** 6× POST `/api/auth/login` innerhalb 60s mit beliebigen Daten.

**Prüfpunkte:**
- [ ] 6. Request → HTTP 429 mit `Retry-After`-Header.
- [ ] Pro IP separat (anderes Gerät bleibt nicht betroffen).

---

### Test 25.4 — JWT-Cookie hat httpOnly + Secure-Flags

**Schritte:** Nach Login DevTools → Application → Cookies.

**Prüfpunkte:**
- [ ] `np_auth`: `HttpOnly = true`, `SameSite = Lax`.
- [ ] In Production-Env zusätzlich `Secure = true`.
- [ ] Nicht via `document.cookie` lesbar (Console-Test).

---

### Test 25.5 — CSRF Double-Submit greift bei Cookie-Auth

**Schritte:**
1. Login (Cookie-Auth aktiv).
2. Aus Browser-DevTools-Konsole: `fetch('/api/workflows', {method:'POST', body:'{}', credentials:'include'})` (ohne `X-NodePilot-Csrf`-Header).
3. Anschließend gleichen Request **mit** Header (Wert aus `np_csrf`-Cookie).

**Prüfpunkte:**
- [ ] Erster Request → 403 mit `code: "CSRF_TOKEN_MISSING"` o.ä.
- [ ] Zweiter Request → erfolgreich (oder 400 wegen Body, aber nicht 403).

---

### Test 25.6 — Logout invalidiert Token sofort

**Schritte:**
1. JWT als Bearer kopieren (z.B. via `np auth login` CLI).
2. `POST /api/auth/logout` über Browser.
3. Sofort danach: `curl -H "Authorization: Bearer <jwt>" http://localhost:5000/api/auth/me`.

**Prüfpunkte:**
- [ ] Logout → 204, Cookie expired (`Max-Age=0`).
- [ ] Bearer-Replay → 401 (`TokenValidityMiddleware` prüft RevokedTokens).
- [ ] Audit-Log `LOGOUT`.

---

### Test 25.7 — User-Disable invalidiert offene Sessions

**Setup:** `operator1` ist im Browser eingeloggt.

**Schritte:**
1. Admin disabled `operator1` über Admin-UI.
2. operator1 macht beliebigen API-Call.

**Prüfpunkte:**
- [ ] Nächster Call → 401.
- [ ] UI redirected zu `/login`.

---

## Teil 26: RBAC — Rollen-Crossings

### Test 26.1 — Viewer kann nicht schreiben

**Setup:** Login als `viewer1`.

**Schritte:**
1. UI öffnen, Workflow-Liste anschauen.
2. Versuche "New Workflow" zu klicken.
3. Direkt-API: `POST /api/workflows` per curl mit Viewer-JWT.

**Prüfpunkte:**
- [ ] "New Workflow"-Button hidden / disabled.
- [ ] Kein "Bearbeiten" / "Disable" / "Save" auf Workflow-Cards.
- [ ] API → 403.

---

### Test 26.2 — Operator kann editieren, aber nicht löschen

**Schritte:**
1. Login als `operator1`. Workflow erstellen ✓.
2. Workflow editieren (PUT) ✓.
3. Workflow DELETE → erwartet Failure.

**Prüfpunkte:**
- [ ] DELETE → 403.
- [ ] UI zeigt keinen Delete-Button für Operator.
- [ ] Credential-DELETE und Machine-DELETE ebenfalls 403.

---

### Test 26.3 — `disable` ignoriert Lock-by-other (Incident-Kill-Switch)

**Setup:** `operator2` hat Workflow `WF1` gelockt (gerade in Bearbeitung).

**Schritte:** `operator1` (anderer User mit Operator-Rolle) klickt **Disable** auf WF1.

**Prüfpunkte:**
- [ ] Disable-Aktion erfolgreich (200), trotz fremdem Lock.
- [ ] WF1 ist `IsEnabled: false`.
- [ ] Lock von operator2 unverändert.
- [ ] Audit `WORKFLOW_DISABLED`.

**Erwartung:** Incident-Response (Kill-Switch) hat Vorrang vor Edit-Lock — by design (CLAUDE.md).

---

### Test 26.4 — Force-Unlock nur Admin

**Setup:** operator1 hat WF1 gelockt.

**Schritte:**
1. operator2 (Operator) öffnet WF1 → sieht Lock-Banner mit Owner-Name.
2. operator2 sucht "Force Unlock" → nicht sichtbar.
3. Admin öffnet WF1 → "Force Unlock" sichtbar → klickt.

**Prüfpunkte:**
- [ ] Operator hat keinen Force-Unlock-Button.
- [ ] Admin-Force-Unlock gibt 200, Lock weg, `IsEnabled` unverändert.
- [ ] Audit `WORKFLOW_FORCE_UNLOCKED` mit `previousLockOwnerId`.

---

### Test 26.5 — DB-Admin Viewer nur Admin

**Schritte:** Operator und Viewer öffnen `/db-admin` (oder klicken Sidebar-Link).

**Prüfpunkte:**
- [ ] Sidebar-Link für Operator/Viewer hidden.
- [ ] Direkt-Navigation → Redirect auf 403 / Login.
- [ ] API `GET /api/dbadmin/tables` → 403 für Operator.

---

## Teil 27: Edit-Lock-Lifecycle (4-States-Toggle)

Pflicht-Lese: CLAUDE.md "Edit-Lifecycle (SCOrch-style Edit-Lock)" — die 4-States-Tabelle ist die Wahrheit.

### Test 27.1 — State A: Productive (kein Lock)

**Setup:** `WF1` ist `IsEnabled=true`, kein Lock.

**Schritte:** Designer öffnen.

**Prüfpunkte:**
- [ ] Banner "läuft produktiv".
- [ ] Toolbar zeigt **"Bearbeiten"** + **"Disable"** (rot).
- [ ] Save-Button hidden.
- [ ] Nodes sind nicht draggable (`canWrite: false`).

---

### Test 27.2 — Bearbeiten → State B (Lock-by-me + Disabled)

**Schritte:** State A → Klick **Bearbeiten**.

**Prüfpunkte:**
- [ ] Workflow ist atomar `IsEnabled: false`, `CheckedOutByUserId: meineId`.
- [ ] Toolbar zeigt **"Save"** + **"Publish"** + **"Beenden"**.
- [ ] Nodes sind draggable.
- [ ] Audit `WORKFLOW_LOCKED`.

---

### Test 27.3 — Save mehrfach in State B (kein Status-Flip)

**Setup:** State B.

**Schritte:** 3× Edit + Save in Folge.

**Prüfpunkte:**
- [ ] Jeder Save grün, neue Version in `WorkflowVersions`-Tabelle.
- [ ] `IsEnabled` bleibt false.
- [ ] Lock unverändert.

---

### Test 27.4 — Publish → State A (atomar Save+Enable+Unlock)

**Schritte:** State B → Klick **Publish**.

**Prüfpunkte:**
- [ ] Atomare Transaktion: Definition gespeichert, `IsEnabled: true`, Lock weg.
- [ ] Toolbar springt zurück zu State A.
- [ ] Audit `WORKFLOW_PUBLISHED`.
- [ ] Bei eingebauten Lint-Warnings öffnet sich vorher Pre-Publish-Checklist-Modal.

---

### Test 27.5 — Beenden → State C (kein Lock, disabled, NICHT auto-enable)

**Schritte:** State B → Klick **Beenden**.

**Prüfpunkte:**
- [ ] Lock weg (`CheckedOutByUserId: null`).
- [ ] `IsEnabled` bleibt **false** (kein Auto-Enable).
- [ ] Toolbar zeigt **"Bearbeiten"** + **"Publish"**, wobei Publish jetzt → `/enable` ruft (nicht `/publish`).
- [ ] Audit `WORKFLOW_UNLOCKED`.

---

### Test 27.6 — State C Publish ruft `/enable`

**Setup:** State C.

**Schritte:** Klick **Publish**.

**Prüfpunkte:**
- [ ] Network-Tab zeigt `POST /api/workflows/{id}/enable` (nicht `/publish`).
- [ ] Workflow → State A.
- [ ] Audit `WORKFLOW_ENABLED`.

---

### Test 27.7 — State D: Lock-by-other (Read-Only-View)

**Setup:** operator2 hat Lock auf WF1.

**Schritte:** operator1 öffnet WF1.

**Prüfpunkte:**
- [ ] Designer ist Read-Only (Drag deaktiviert).
- [ ] Banner zeigt "operator2 bearbeitet diesen Workflow seit HH:MM".
- [ ] Publish-Button disabled mit Tooltip nennt Lock-Owner.
- [ ] "Force Unlock" Button hidden für Operator, sichtbar für Admin.

---

### Test 27.8 — Two-User Race auf Lock

**Schritte:** operator1 + operator2 simultan auf "Bearbeiten" klicken (Browser-Tabs).

**Prüfpunkte:**
- [ ] Einer kriegt 200 + Lock.
- [ ] Anderer kriegt 409, UI zeigt Konflikt-Toast mit Lock-Owner-Name.

---

## Teil 28: Step-Test mit Kontext

Pflicht-Lese: CLAUDE.md "Step-Test mit Kontext".

### Test 28.1 — Step-Test ohne Kontext

**Setup:** runScript-Node selektiert, Workflow gelockt.

**Schritte:** Properties → "Step Test" → Run (ohne MockVariables).

**Prüfpunkte:**
- [ ] Modal zeigt Live-Output.
- [ ] **Kein** `WorkflowExecution`-Eintrag in DB erstellt (per Direct-DB-Inspection prüfen).
- [ ] Output wird durch OutputRedactor maskiert (Test mit Script `Write-Output "password=hunter2"` → Output zeigt `***`).

---

### Test 28.2 — MockVariables einsetzen

**Setup:** Step referenziert `{{checkDisk.param.freeGb}}` und `{{checkDisk.output}}`.

**Schritte:** Step-Test-Modal → MockVariables: `checkDisk.param.freeGb=7`, `checkDisk.output=ok` → Run.

**Prüfpunkte:**
- [ ] Engine resolved Werte korrekt.
- [ ] Stdout enthält die eingesetzten Werte.

---

### Test 28.3 — configOverride mit Live-Editor-Stand

**Setup:** runScript hat `script: "echo OLD"` in DB. Im Editor (ohne Save) auf `echo NEW` ändern.

**Schritte:** Step-Test ohne Save.

**Prüfpunkte:**
- [ ] Test verwendet `echo NEW` (configOverride).
- [ ] DB-Stand bleibt `echo OLD` unverändert.

---

### Test 28.4 — "Letzter Run-Kontext" auto-populiert MockVariables

**Setup:** Workflow lief erfolgreich vor 5min. Step `step3` hat Ancestors `step1`, `step2`.

**Schritte:** Step-Test für `step3` → Toggle "Letzten Run benutzen" → Dropdown wählt aktuelle Execution.

**Prüfpunkte:**
- [ ] MockVariables auto-populiert mit Outputs von step1+step2 aus dieser Execution.
- [ ] Globals werden nicht in MockVariables gezeigt — Engine pulled sie direkt aus IGlobalVariableStore.

---

### Test 28.5 — Run-Auswahl zeigt "stepRan: false" disabled

**Setup:** Step3 lief in Run R1, aber **nicht** in Run R2 (z.B. Branch-Skip).

**Schritte:** Run-Dropdown öffnen.

**Prüfpunkte:**
- [ ] R2 ist gegrayed-out / disabled, Tooltip "dieser Step lief in R2 nicht".
- [ ] R1 ist auswählbar.

---

### Test 28.6 — Step-Test als Viewer blockiert

**Schritte:** Viewer öffnet Workflow, klickt "Step Test".

**Prüfpunkte:**
- [ ] Button hidden oder API gibt 403.

---

## Teil 29: Sub-Workflow Contracts (V1)

Pflicht-Lese: CLAUDE.md "Sub-Workflows", `docs/testing/sub-workflow-contracts-test-cases.md`.

### Test 29.1 — Contract auto-derived bei startWorkflow-Node

**Setup:** Child-Workflow "Patch-Server" mit:
- manualTrigger mit Parametern `{serverName: required string, reboot: bool default=false, maxDurationMin: int default=30}`.
- returnData mit `data: {patched, summary}`.

**Schritte:**
1. Parent-Workflow erstellen mit startWorkflow-Node.
2. In Properties Workflow-Feld auf `Patch-Server` setzen.

**Prüfpunkte:**
- [ ] ContractMappingTable erscheint anstelle der freien ParameterTable.
- [ ] Header "Inputs erwartet von 'Patch-Server'" mit 3 Einträgen.
- [ ] `serverName` hat rotes Sternchen, Type-Badge `string`.
- [ ] `reboot` zeigt "Default: false" rechts.
- [ ] Outputs-Section: 4 System-Outputs (`__executionId`, `__status`, `__workflowId`, `__workflowName`) + 2 user-Outputs (`patched`, `summary`).

---

### Test 29.2 — Required-Validation + leeres Pflichtfeld

**Setup:** 29.1.

**Schritte:**
1. `serverName`-Feld leer lassen.
2. Save-Button.

**Prüfpunkte:**
- [ ] Inline-Border rot um `serverName`.
- [ ] Save → Lint-Panel zeigt "serverName ist required".
- [ ] Pre-Publish-Modal blockiert oder warnt.

---

### Test 29.3 — Empty-out entfernt Key (statt "" zu setzen)

**Setup:** 29.1, `reboot`-Feld auf `"true"` gesetzt.

**Schritte:** Wert komplett löschen (nicht nur "").

**Prüfpunkte:**
- [ ] Workflow-JSON: Key `reboot` ist nicht mehr im Parameter-Dict (Child nimmt seinen Default).
- [ ] DB-Save bestätigt das.

---

### Test 29.4 — Stale-Key-Warning

**Setup:** Parent-Workflow hat im JSON `parameters: {serverName: "...", oldField: "x"}`. Child-Contract hat `oldField` nicht mehr.

**Schritte:** Parent-Workflow im Designer öffnen.

**Prüfpunkte:**
- [ ] Stale-Key wird mit Warning + Remove-Button gerendert.
- [ ] Engine-Run würde `oldField` ignorieren — kein Crash.

---

### Test 29.5 — Multi-Return-Node Warning

**Setup:** Child mit zwei `returnData`-Nodes (Decision-Pfad).

**Schritte:** Parent öffnen.

**Prüfpunkte:**
- [ ] Warning-Badge "letztes returnData gewinnt — pro Run sind nicht alle Outputs garantiert".
- [ ] `HasMultipleReturnDataNodes: true` im Contract.

---

### Test 29.6 — By-Name-Lookup: exact-case gewinnt, sonst case-insensitive

**Setup:** Child heißt "Patch-Server".

**Schritte:** Parent setzt `workflowNameOrId: "patch-server"` (lowercase).

**Prüfpunkte:**
- [ ] Contract WIRD gefunden (case-insensitive Fallback; Engine löst identisch auf).
- [ ] Existieren zwei Workflows, die sich nur in Groß-/Kleinschreibung unterscheiden ("Daily" + "DAILY"), liefert `by-name` 409 und der Run-Step schlägt mit „multiple workflows … disambiguate with the GUID" fehl; exakte Schreibweise trifft weiterhin eindeutig.

---

### Test 29.7 — Recursion-Guard (self-call rejected)

**Setup:** Workflow A hat `startWorkflow → A`.

**Schritte:** Run.

**Prüfpunkte:**
- [ ] Step-Output: "self-invocation is not allowed".
- [ ] Status `Failed`.

---

### Test 29.8 — Call-Depth-Cap (10 Levels)

**Setup:** Kette A→B→C→…→K (11 Level deep).

**Schritte:** Run A.

**Prüfpunkte:**
- [ ] 11. Aufruf scheitert mit `MaxCallDepth exceeded`.
- [ ] Metric `nodepilot.subworkflow.depth_exceeded` incrementiert (Prometheus).

---

### Test 29.9 — Sub-Workflow-Concurrency-Cap

**Setup:** Stress-Test: Parent triggert via `forEach` 200 parallel laufende Children.

**Schritte:** Run.

**Prüfpunkte:**
- [ ] Erste 128 starten gleichzeitig (siehe `InMemorySubWorkflowGate.DefaultCapacity`).
- [ ] Restliche warten oder bekommen "engine is at sub-workflow concurrency limit".

---

## Teil 30: Coverage Heatmap

Pflicht-Lese: CLAUDE.md "Coverage Heatmap".

### Test 30.1 — Heatmap-Toggle ein/aus

**Setup:** Workflow mit ≥1 Execution in den letzten 30d.

**Schritte:**
1. Designer öffnen → Toolbar → Target-Icon klicken.
2. Erneut klicken zum Aus.

**Prüfpunkte:**
- [ ] Nodes werden tinted: `never` (grayscale + 40% opacity), `rare` (<25% Total, 80% opacity), `common` (≥25%, normal).
- [ ] Toggle off → Tinting weg.

---

### Test 30.2 — Hover Counts

**Schritte:** Heatmap an, Hover über Node.

**Prüfpunkte:**
- [ ] Tooltip zeigt: "executed N", "failed F", "skipped S".
- [ ] Bei `executedCount = 0`: "never run in last 30 days".

---

### Test 30.3 — Window-Days-Setting

**Schritte:** Designer-Settings → coverageWindowDays auf 7.

**Prüfpunkte:**
- [ ] API-Call: `?windowDays=7`.
- [ ] Counts ändern sich entsprechend (kürzeres Window = niedrigere Counts).

---

### Test 30.4 — Window-Days Cap

**Schritte:** API-Call mit `windowDays=999`.

**Prüfpunkte:**
- [ ] Server cappt auf 365.
- [ ] Response-`oldestExecutionInWindow` reflektiert max 365d.

---

### Test 30.5 — Cap auf 900 Executions

**Setup:** Workflow mit ≥1500 Executions im 30d-Window.

**Schritte:** Endpoint öffnen.

**Prüfpunkte:**
- [ ] `executedCount` basiert auf 900 letzten (CLAUDE.md).
- [ ] `oldestExecutionInWindow` zeigt das.

---

## Teil 31: DB-Admin Viewer

Pflicht-Lese: CLAUDE.md "DB-Admin"-Audit-Codes.

### Test 31.1 — Tabellen-Liste

**Setup:** Admin-Login.

**Schritte:** Sidebar → "DB Admin".

**Prüfpunkte:**
- [ ] Liste aller EF-Entities (Workflows, Users, Audit, etc.).
- [ ] Cascade-Annotationen sichtbar.

---

### Test 31.2 — Browse mit Pagination

**Schritte:** Tabelle "Workflows" → Browse.

**Prüfpunkte:**
- [ ] Server-side Pagination funktioniert.
- [ ] Sortierbar.
- [ ] Filter (z.B. `Name contains "Patch"`) liefert nur Matches.

---

### Test 31.3 — Cell-Edit

**Schritte:** Doppelklick auf Cell → Wert ändern → Save.

**Prüfpunkte:**
- [ ] Update committed.
- [ ] Audit `DBADMIN_ROW_UPDATED` mit `details: {table, pk, column, oldValue, newValue}`.
- [ ] Bei Concurrency-Konflikt (zwei Tabs editieren dieselbe Cell) → 409 mit Resolution-Dialog.

---

### Test 31.4 — Row Delete mit Cascade-Hint

**Setup:** Workflow-Row mit assoziierten WorkflowExecutions.

**Schritte:** Rechtsklick Row → Delete.

**Prüfpunkte:**
- [ ] Confirm-Modal listet Cascade-Targets.
- [ ] Nach Confirm: DELETE committed.
- [ ] Audit `DBADMIN_ROW_DELETED`.

---

### Test 31.5 — Operator-Zugriff blockiert

**Schritte:** operator1 → URL `/db-admin` direkt eingeben.

**Prüfpunkte:**
- [ ] 403 oder Redirect.
- [ ] API-Endpoints `/api/dbadmin/*` → 403.

---

## Teil 32: KI-Features (LLM)

Pflicht-Lese: CLAUDE.md "KI-Features", `docs/ai-features.md`.

### Test 32.1 — Generate-Script (Sparkles-Button)

**Setup:** `Llm:Enabled: true`, `Llm:BaseUrl: http://127.0.0.1:11434/v1` (lokales Ollama oder LM Studio).

**Schritte:**
1. runScript-Node selektieren → Properties.
2. Sparkles-Button neben Script-Editor → Prompt: "Get free disk space on C:".
3. Submit.

**Prüfpunkte:**
- [ ] Script-Vorschlag erscheint.
- [ ] UI fügt am Cursor ein (nicht ersetzt) — Default-Verhalten.
- [ ] Toggle "Replace all" ist explizit, nicht default.
- [ ] Audit `AI_SCRIPT_GENERATED` (Details: Modell, Token-Counts, Dauer; **kein** Prompt-Text).

---

### Test 32.2 — Generate-Workflow

**Schritte:** WorkflowsPage → "KI generieren" → Prompt: "Patch alle Server der Gruppe X, neustarten falls Pflicht-Update".

**Prüfpunkte:**
- [ ] Stats-Preview vor Anlegen (nodes count, edges count).
- [ ] Confirm legt neuen Workflow an.
- [ ] Audit `AI_WORKFLOW_GENERATED`.

---

### Test 32.3 — LLM Disabled → 503

**Setup:** `Llm:Enabled: false`.

**Schritte:** Sparkles-Button.

**Prüfpunkte:**
- [ ] HTTP 503 mit `code: "LLM_DISABLED"`.
- [ ] UI zeigt klare Hinweis-Toast.

---

### Test 32.4 — AI Rate-Limit (20/min)

**Schritte:** 21× Generate-Script in 60s.

**Prüfpunkte:**
- [ ] Ab 21. Request → 429.

---

### Test 32.5 — SSRF-Block bei Cloud-Metadata-IP

**Setup:** `Llm:BaseUrl: http://169.254.169.254/v1` in appsettings.

**Schritte:** Backend starten.

**Prüfpunkte:**
- [ ] Startup-Failure mit klarer Error-Message (siehe `LlmServiceCollectionExtensions`).
- [ ] Backend startet **nicht**.

---

### Test 32.6 — Plaintext-API-Key Warning

**Setup:** `Llm:ApiKey: "sk-…"` als Klartext in appsettings.

**Schritte:** Backend starten.

**Prüfpunkte:**
- [ ] Startup-Log zeigt Warning analog `Smtp:Password`-Pattern.
- [ ] Backend startet trotzdem (Warning, kein Hard-Fail).

---

## Teil 33: Hardening-Flags

Pflicht-Lese: CLAUDE.md "Opt-in Hardening-Flags".

### Test 33.1 — `Remote:RequireWinRmSsl=true` blockt Plain-WinRM

**Setup:** Machine ohne SSL-Transport, Hardening-Flag aktiv.

**Schritte:** Workflow mit Remote-Step → Run.

**Prüfpunkte:**
- [ ] Step Failed mit klarer Error-Meldung.
- [ ] Mit SSL-Transport (winrm-ssl): Erfolg.

---

### Test 33.2 — `RestApi:BlockPrivateNetworks=true` blockt RFC1918

**Setup:** Hardening-Flag aktiv.

**Schritte:** restApi-Step `GET http://192.168.1.1/foo`.

**Prüfpunkte:**
- [ ] Failed mit "private network blocked".
- [ ] Auch `127.0.0.1` blockiert.

---

### Test 33.3 — `RestApi:Proxy:Enabled=false` ignoriert Windows-System-Proxy

**Setup:** System-Proxy in Windows gesetzt.

**Schritte:** restApi-Step → externe URL.

**Prüfpunkte:**
- [ ] Direct-Connection (kein Proxy in Network-Trace).
- [ ] Bei `Enabled=true` und `Address` gesetzt → Proxy benutzt.

---

### Test 33.4 — `FileSystemOperation:RejectTraversal` blockt `..`

**Schritte:** fileOperation copy mit `path: "C:\Temp\..\Windows\System32\drivers\etc\hosts"`.

**Prüfpunkte:**
- [ ] Failed mit klarer Path-Traversal-Error.
- [ ] Bei Flag=false: würde durchlaufen (Standard-Dev-Verhalten).

---

### Test 33.5 — `SqlActivity:RequireConnectionRef=true` lehnt inline-Connection ab

**Schritte:** sql-Step mit raw `connectionString` (inline).

**Prüfpunkte:**
- [ ] Failed mit "RequireConnectionRef ist gesetzt — bitte connectionRef verwenden".
- [ ] Mit `connectionRef: "OpsDB"`: läuft.

---

### Test 33.6 — `OpenTelemetry:Exporters:PrometheusScrapeAllowAnonymous=false` schützt /metrics

**Schritte:** `GET /metrics` ohne JWT.

**Prüfpunkte:**
- [ ] 401.
- [ ] Mit JWT: 200 mit Prometheus-Format.

---

## Teil 34: Migration-Drift + Backend-Smoke (Backend-only)

### Test 34.1 — Provider-Wechsel Postgres ↔ SQL Server

**Setup:** App-DB ist auf Postgres. Stoppe Backend.

**Schritte:**
1. `appsettings.Production.json`: `Database:Provider` auf `sqlserver` setzen + `DefaultConnection`.
2. SQL Server bereit (lokales Express o.ä.).
3. Backend starten.

**Prüfpunkte:**
- [ ] `MigrationBootstrapper` läuft das gleiche Set durch — keine Provider-spezifischen Type-Errors.
- [ ] Migrations-Drift-Tests in CI grün.
- [ ] Schema in beiden DBs identisch (nur Provider-Mapping unterschiedlich, Guid → uniqueidentifier vs. uuid).

---

### Test 34.2 — Health-Endpoints

**Schritte:**
1. `GET /healthz/live` → 200.
2. Postgres stoppen.
3. `GET /healthz/ready` → 503.
4. `GET /healthz/live` → weiterhin 200 (Liveness ignoriert DB-State).
5. Postgres wieder up → `/healthz/ready` → 200.

**Prüfpunkte:**
- [ ] Wie oben. Liveness/Readiness-Trennung sauber.

---

### Test 34.3 — Output-Redaction in Persisted Logs

**Setup:** Step gibt `password=hunter2` und `Bearer abc123` aus.

**Schritte:** Run, dann ExecutionsController GET → Step Output. Auch File-Log lesen.

**Prüfpunkte:**
- [ ] DB-Spalte `Output` enthält `***`-Maskierung.
- [ ] SignalR-Push ebenfalls maskiert.
- [ ] Audit-Log keine Klartext-Secrets.
- [ ] File-Log-Zeile maskiert.

---

## Teil 35: Secrets Re-Encryption (SecretsController)

### Test 35.1 — Bulk-Re-Encrypt — alle Credentials erfolgreich

**Voraussetzung:** Mind. 1 Credential + 1 Secret-Global-Variable existieren.

**Schritte:** `POST /api/secrets/reencrypt` mit Admin-Bearer.

**Prüfpunkte:**
- [ ] 200 OK. Response enthält `succeeded` (Anzahl ≥ 0). `skipped` ist leeres Array.
- [ ] Credentials sind danach weiterhin nutzbar.

---

### Test 35.2 — Re-Encrypt — Partial Success (207)

**Setup:** DB enthält korrumpierten Credential-Wert (falscher DPAPI-Ciphertext per DB-Admin).

**Prüfpunkte:**
- [ ] 207 Multi-Status. `skipped`-Array enthält fehlerhaften Eintrag mit `reason`. Valide Einträge neu verschlüsselt.

---

### Test 35.3 — Re-Encrypt — Operator → 403

**Prüfpunkte:** `POST /api/secrets/reencrypt` als Operator → 403 Forbidden.

---

## Teil 36: Activity Catalog

### Test 36.1 — Katalog abrufbar

**Schritte:** `GET /api/activity-catalog` mit Admin-Bearer.

**Prüfpunkte:**
- [ ] 200 OK. Response enthält mind.: `log`, `runScript`, `restApi`, `sql`, `delay`, `junction`, `startWorkflow`, `returnData`, `emailNotification`, alle Remote-Types.

---

### Test 36.2 — Katalog auch für Viewer lesbar

**Prüfpunkte:** Als Viewer → 200 OK (read-only Endpoint).

---

## Teil 37: Diagnostics / Support-Log

### Test 37.1 — Support-Events abrufbar

**Voraussetzung:** Mind. 1 Execution durchgeführt.

**Prüfpunkte:**
- [ ] `GET /api/diagnostics/support-events` → 200. Events haben `eventType`, `workflowName`, `traceId`, `createdAt`.

---

### Test 37.2 — Filter nach EventType

**Prüfpunkte:** `?eventType=EXECUTION_STARTED` → Nur passende Events.

---

### Test 37.3 — Cursor-Pagination

**Prüfpunkte:** `?limit=5` → max 5. Zweiter Aufruf mit Cursor → nächste Seite (keine Duplikate).

---

### Test 37.4 — Viewer → 403

**Prüfpunkte:** Als Viewer `GET /api/diagnostics/support-events` → 403.

---

## Teil 38: Admin Settings

### Test 38.1 — Settings-Status

**Prüfpunkte:** `GET /api/admin/settings/status` → 200. Enthält aktiven DB-Provider + Remote-Provider.

---

### Test 38.2 — SMTP-Probe

**Prüfpunkte:** `POST /api/admin/settings/test/smtp` mit Test-Payload → 200 (kein 500). `success: false` wenn Server nicht erreichbar. Persistierter Stand unverändert.

---

### Test 38.3 — LLM-Probe

**Prüfpunkte:** `POST /api/admin/settings/test/llm` mit Dummy-Payload → strukturierter Response (kein 500).

---

### Test 38.4 — Settings-Roundtrip ETag

**Schritte:** GET → ETag merken → PUT mit `If-Match` → GET prüfen → zweites PUT mit altem ETag → 412.

**Prüfpunkte:**
- [ ] Änderung reflektiert. Zweites PUT: 412 Precondition Failed.

---

### Test 38.5 — Settings — Operator → 403

**Prüfpunkte:** Als Operator `GET /api/admin/settings/status` → 403.

---

## Teil 39: Debug — Variable Overrides

### Test 39.1 — Resume mit Override (What-If)

**Voraussetzung:** 2-Step-Workflow, Breakpoint auf Step 2 (liest `{{stepA.output}}`).

**Schritte:**
1. Debug-Execution starten.
2. `GET /api/executions/{id}/paused-steps` → Step 2 gelistet.
3. `POST /api/executions/{id}/resume` mit `{"mode":"continue","overrides":{"stepA.output":"mocked"}}`.

**Prüfpunkte:**
- [ ] Step 2 verwendet `mocked` statt echtem Output von Step A.

---

### Test 39.2 — stop-Mode beendet Execution

**Prüfpunkte:** `mode: "stop"` → Execution endet; Step 3 bleibt Skipped/nicht ausgeführt.

---

## Teil 40: Edit-Lock — Force-Unlock Audit

### Test 40.1 — Force-Unlock durch Admin

**Voraussetzung:** Operator A hat Workflow gelockt.

**Prüfpunkte:**
- [ ] `POST /api/workflows/{id}/force-unlock` als Admin → 200. Workflow entsperrt.
- [ ] Audit `WORKFLOW_FORCE_UNLOCKED` enthält `previousLockOwnerId` = Operator-A-ID.
- [ ] Als Operator force-unlock → 403.

---

## Teil 41: Rollback mit Reason

### Test 41.1 — Rollback mit Reason-Body

**Schritte:** `POST /api/workflows/{id}/rollback/2` mit `{"reason":"Revert broken URL"}`.

**Prüfpunkte:**
- [ ] 200 OK. Definition entspricht v2. Audit-Details enthalten `reason`.

---

### Test 41.2 — Rollback ohne Body

**Prüfpunkte:** 200 OK. Audit-Event vorhanden (reason null/leer).

---

## Teil 42: Coverage Heatmap — Erweiterte Parameter

### Test 42.1 — windowDays=365

**Prüfpunkte:** `?windowDays=365` → 200 OK. `oldestExecutionInWindow` sinnvoll befüllt.

---

### Test 42.2 — windowDays=366 → 400

**Prüfpunkte:** 400. Fehlermeldung nennt Maximum 365.

---

### Test 42.3 — oldestExecutionInWindow im Response

**Prüfpunkte:** `?windowDays=30` → Response enthält `oldestExecutionInWindow` (ISO-Timestamp).

---

## Teil 43: Observability Query-Endpoints

### Test 43.1 — Instant-Query

**Voraussetzung:** `OpenTelemetry:Enabled=true`.

**Prüfpunkte:** `GET /api/observability/query?expr=nodepilot_executions_total&time=<ts>` → 200. `result` mit Metric-Wert.

---

### Test 43.2 — Range-Query

**Prüfpunkte:** `GET /api/observability/query_range?expr=...&start=...&end=...&step=60` → 200. Matrix-Format mit mehreren Datenpunkten.

---

### Test 43.3 — Operator → 403

**Prüfpunkte:** Als Operator → 403.

---

## Teil 44: Rate-Limiting Headers

### Test 44.1 — Login-Rate-Limit (5/Min)

**Schritte:** 6 fehlgeschlagene Logins in unter einer Minute.

**Prüfpunkte:**
- [ ] Requests 1–5: 401. Request 6: 429. Response enthält `Retry-After` o. ä.

---

### Test 44.2 — Webhook-Rate-Limit (60/Min)

**Prüfpunkte:** 61. Webhook-POST → 429. Header `RateLimit-Limit: 60`.

---

### Test 44.3 — AI-Rate-Limit (20/Min, nur bei aktivem LLM)

**Prüfpunkte:** 21. `POST /api/ai/generate-script` → 429.

---

## Teil 45: Shared Folder Permissions — Grant / Revoke

### Test 45.1 — Permission Grant

**Schritte:** `POST /api/shared-folders/{id}/permissions/grant` mit `{userId, role:"Viewer"}`.

**Prüfpunkte:** Operator B sieht Folder danach (vorher nicht sichtbar).

---

### Test 45.2 — Permission Revoke

**Schritte:** `POST /api/shared-folders/{id}/permissions/revoke`.

**Prüfpunkte:** Operator B kann Folder nicht mehr sehen.

---

### Test 45.3 — Permissions auflisten

**Prüfpunkte:** `GET /api/shared-folders/{id}/permissions` → 200. Liste mit `userId`, `role`, `grantedAt`.

---

## Teil 46: Unresolved Template Variables (Fehlverhalten)

### Test 46.1 — Unbekannte Step-Referenz → Step Failed

**Setup:** log-Activity mit `message: "{{nonExistentStep.output}}"`.

**Prüfpunkte:**
- [ ] Execution `status: Failed`. `errorOutput` enthält `"Unresolved template variable(s): {{nonExistentStep.output}}"`.

---

### Test 46.2 — Unbekannte Referenz im URL-Feld (restApi)

**Setup:** restApi mit `url: "https://api.example.com/{{missingStep.param.id}}"`.

**Prüfpunkte:** Step Failed. errorOutput: `"Unresolved template variable(s): {{missingStep.param.id}}"`.

---

### Test 46.3 — SQL `.query`-Feld ist geschützt (kein False-Positive)

**Setup:** sql mit `query: "SELECT * FROM jobs WHERE id = {{manual.id}}"`.

**Prüfpunkte:** Kein Unresolved-Guard-Fehler (query ist protected field). Step schlägt ggf. aus anderem Grund fehl.

---

### Test 46.4 — `runScript` ist exempt

**Setup:** runScript mit `script: "$out = {{missingStep.output}}"`.

**Prüfpunkte:** Kein `"Unresolved template variable(s)"`-Fehler vom Guard (runScript löst intern auf).

---

## Teil 47: External Trigger — Idempotency-Key TTL

### Test 47.1 — Replay innerhalb TTL → Idempotent-Replayed

**Prüfpunkte:** Zweiter POST mit gleichem Key → `Idempotent-Replayed: true`-Header. Kein neuer Execution-Row.

---

### Test 47.2 — Replay nach TTL-Ablauf → neuer Run

**Setup:** DB: `IdempotencyKeys.CreatedAt` auf NOW()-25h setzen.

**Prüfpunkte:** Dritter POST: kein `Idempotent-Replayed`. Neuer Execution-Row.

---

## Teil 48: Output-Redaction — Viewer-Perspektive

### Test 48.1 — Viewer sieht redacted Step-Output

**Setup:** runScript gibt `password=hunter2` aus. Als Viewer `GET /api/executions/{id}/steps`.

**Prüfpunkte:**
- [ ] `output` enthält `***` statt `hunter2` (unabhängig von Rolle).

---

### Test 48.2 — SignalR-Event ebenfalls redacted

**Prüfpunkte:** `StepCompleted`-Event im WS-Stream: `output` enthält `***`.

---

## Teil 49: Execution Retry & Cancel-All

### Test 49.1 — Execution Retry

**Voraussetzung:** Terminale Execution (Failed/Succeeded).

**Prüfpunkte:**
- [ ] `POST /api/executions/{id}/retry` → 202. Neue `executionId`. `trigger` enthält `retry:<originalId>`. Audit `EXECUTION_RETRIED`.

---

### Test 49.2 — Retry auf laufende Execution → 400

**Prüfpunkte:** `POST /api/executions/{id}/retry` auf Running → 400. Fehlermeldung nennt den Status.

---

### Test 49.3 — Cancel-All

**Voraussetzung:** ≥2 gleichzeitig laufende Executions eines Workflows.

**Prüfpunkte:**
- [ ] `POST /api/workflows/{id}/cancel-all` → 200. `cancelledCount` ≥ 1. Alle → `Cancelled`. Audit `WORKFLOW_CANCEL_ALL`.

---

### Test 49.4 — Cancel-All ohne laufende Executions → 0

**Prüfpunkte:** 200. `cancelledCount: 0`.

---

## Teil 50: Workflow Duplicate, By-Name-Lookup & Bulk-Export

### Test 50.1 — Duplicate (API)

**Prüfpunkte:**
- [ ] `POST /api/workflows/{id}/duplicate` → 201. Neue ID + Name (Suffix "(2)"). Definition identisch. Audit `WORKFLOW_DUPLICATED`.

---

### Test 50.2 — By-Name-Lookup (exakte Schreibweise)

**Prüfpunkte:** `GET /api/workflows/by-name/E2E_Basic_Test` → 200. Korrekter Workflow.

---

### Test 50.3 — By-Name — falsche Schreibweise → 404

**Prüfpunkte:** `GET /api/workflows/by-name/e2e_basic_test` → 404.

---

### Test 50.4 — By-Name Contract

**Prüfpunkte:** `GET /api/workflows/by-name/E2E_Basic_Test/contract` → 200. `inputs`, `outputs`, `hasManualTrigger` vorhanden.

---

### Test 50.5 — Bulk-Export (alle Workflows)

**Prüfpunkte:**
- [ ] `GET /api/workflows/export` → 200. `"schema":"nodepilot-workflow-export/v1"` + `"workflows":[...]`. Audit `WORKFLOW_EXPORTED_BULK`.

---

### Test 50.6 — Bulk-Export Berechtigungen

**Prüfpunkte:** Operator → 200. Viewer → 403.

---

## Teil 51: Step Stats & Step Health

### Test 51.1 — Step Stats

**Voraussetzung:** Mind. 1 abgeschlossene Execution.

**Prüfpunkte:**
- [ ] `GET /api/workflows/{id}/step-stats` → 200. Einträge haben `executedCount`, `failedCount`, `skippedCount`.

---

### Test 51.2 — Step Health

**Prüfpunkte:**
- [ ] `GET /api/workflows/{id}/step-health` → 200. Health-Scores pro Step.

---

### Test 51.3 — Viewer-Zugriff

**Prüfpunkte:** Viewer → 200 auf beiden Endpoints (read-only Analytics).

---

## Teil 52: Folder Move

### Test 52.1 — Shared Folder verschieben

**Prüfpunkte:**
- [ ] `POST /api/shared-folders/{childId}/move` mit `{"targetParentId":"..."}` → 200. Folder erscheint unter Parent.

---

### Test 52.2 — Zirkelreferenz → 400

**Prüfpunkte:** Folder in eigenen Nachfahren verschieben → 400. "Cannot move a folder into its own descendant".

---

### Test 52.3 — Nicht-leeren Folder löschen → 409

**Prüfpunkte:** `DELETE` auf Folder mit Inhalt → 409. "Folder is not empty".

---

### Test 52.4 — Workflow in Folder verschieben

**Prüfpunkte:**
- [ ] `POST /api/workflows/{id}/move-folder` → 200. Workflow hat neues `folderId`.

---

## Teil 53: Schedule Next-Fires & AI Generate-Workflow

### Test 53.1 — Next-Fires

**Voraussetzung:** Aktiver Schedule-Trigger-Workflow.

**Prüfpunkte:**
- [ ] `GET /api/triggers/schedule/next-fires` → 200. ISO-Timestamps in der Zukunft pro aktivem Trigger.

---

### Test 53.2 — Next-Fires ohne aktive Trigger → `[]`

**Prüfpunkte:** 200. Leeres Array (kein 404).

---

### Test 53.3 — AI Generate-Workflow (LLM aktiv)

**Prüfpunkte:**
- [ ] `POST /api/ai/generate-workflow` mit Prompt → 200. `definitionJson` mit `nodes`+`edges`. Audit `AI_WORKFLOW_GENERATED`.

---

### Test 53.4 — AI Generate-Workflow (LLM disabled) → 503

**Prüfpunkte:** 503. `code: "LLM_DISABLED"`.

---

## Teil 54: Designer-Overlays — Command Palette, Quick Switcher, Find & Replace

### Test 54.1 — Command Palette (Ctrl+Shift+P)

**Schritte:** Ctrl+Shift+P → Palette öffnet. Tippen filtert fuzzy. Enter führt aus.

**Prüfpunkte:**
- [ ] Commands nach Kategorie gruppiert. Shortcut-Hints sichtbar. Disabled Commands ausgegraut. Escape schließt.

---

### Test 54.2 — Quick Switcher (Ctrl+P)

**Schritte:** Ctrl+P → Overlay. Tippe Workflow-Namen → Enter navigiert.

**Prüfpunkte:**
- [ ] Recent Workflows zuerst. Fuzzy-Match. Bei dirty Workflow: Warnung.

---

### Test 54.3 — Find & Replace (Ctrl+H)

**Schritte:** Ctrl+H → Overlay mit Find+Replace-Feldern. "Replace All" auf `"hello"` → `"world"`.

**Prüfpunkte:**
- [ ] Match-Counter "X of Y". Replace einzeln + Replace All. Leeres Search → kein Crash. Escape schließt.

---

## Teil 55: Erweiterte Keyboard-Shortcuts

### Test 55.1 — Tidy Layout (Ctrl+Shift+T)

**Schritte:** Nodes chaotisch platzieren → Ctrl+Shift+T.

**Prüfpunkte:**
- [ ] Nodes werden neu arrangiert. Keine Überlappung. Undo setzt zurück.

---

### Test 55.2 — Node-Größe & Edge-Breite

**Schritte:** Node selektieren → `>` / `<`. Edge selektieren → `]` / `[`.

**Prüfpunkte:**
- [ ] Node größer/kleiner. Edge dicker/dünner. Undo funktioniert.

---

### Test 55.3 — Label-Schriftgröße (Alt+. / Alt+,)

**Prüfpunkte:** Node-Label wird größer/kleiner. Persisistiert nach Speichern + Reload.

---

### Test 55.4 — Seiten-Navigation (Ctrl+Shift+1–5)

**Prüfpunkte:**
- [ ] 1 → Workflows, 2 → Executions, 3 → Machines, 4 → Global Variables, 5 → Audit Log. Browser-History intakt.

---

## Teil 56: Error-Notifications & Empty States

### Test 56.1 — Import-Fehler bei invalidem JSON

**Prüfpunkte:**
- [ ] Fehlermeldung (Toast/Dialog) mit Dateiname. Valide Dateien im selben Batch trotzdem importiert.

---

### Test 56.2 — API-Fehler-Toast (Netzwerkausfall beim Speichern)

**Setup:** DevTools → Offline → Ctrl+S im Designer.

**Prüfpunkte:**
- [ ] Fehler-Toast erscheint (rot/orange). Nach Reconnect: Speichern gelingt.

---

### Test 56.3 — Leere Workflows-Liste (Empty State)

**Prüfpunkte:**
- [ ] Sinnvoller Empty-State-Text (nicht leere Tabelle). "New Workflow"-Button sichtbar.

---

### Test 56.4 — Leere Executions-Liste (Empty State)

**Prüfpunkte:** "No executions yet"-Message. Kein Crash.

---

## Teil 57: Gantt-Chart & Execution-Timeline

### Test 57.1 — Gantt-Chart in LiveTimeline

**Schritte:** Workflow mit ≥3 Steps ausführen → ExecutionPanel → Timeline-Tab.

**Prüfpunkte:**
- [ ] Horizontale Balken proportional zur Laufzeit. Hover zeigt Tooltip. Klick auf Balken selektiert Node auf Canvas.

---

### Test 57.2 — Step-Detail-Expansion in ExecutionsPage

**Schritte:** ExecutionsPage → Execution-Row klicken.

**Prüfpunkte:**
- [ ] Row expandiert in-place. Step-Liste mit Status + Duration. Klick auf Step zeigt vollständigen Output/ErrorOutput.

---

## Teil 58: Variable Autocomplete & Preview Tooltip

### Test 58.1 — Variable Autocomplete ({{-Tippen)

**Schritte:** Input-Feld in Properties → `{{` tippen.

**Prüfpunkte:**
- [ ] Dropdown erscheint mit Upstream-Variablen. Filterung case-insensitive. Tab/Klick schließt `}}` korrekt ab.

---

### Test 58.2 — Variable Preview Tooltip (Hover)

**Voraussetzung:** Mind. 1 abgeschlossene Execution.

**Prüfpunkte:**
- [ ] Hover über `{{stepA.output}}` im Properties Panel → Tooltip mit letztem Wert + Datentyp.

---

## Teil 59: PausedVariablesInspector

### Test 59.1 — Inspector beim Breakpoint

**Prüfpunkte:**
- [ ] Alle aktuellen Variablen (Outputs, Globals) angezeigt. JSON-Objekte expandierbar. "Resume"-Button sichtbar.

---

### Test 59.2 — Variable im Inspector überschreiben

**Schritte:** Inspector → Variable-Wert editieren → Resume.

**Prüfpunkte:**
- [ ] Geänderter Wert wird in nachfolgendem Step verwendet.

---

## Teil 60: BulkEditPanel & ActivityTypeFilter

### Test 60.1 — BulkEditPanel (Mehrere Nodes bulk bearbeiten)

**Schritte:** Mehrere Remote-Activity-Nodes selektieren → BulkEditPanel erscheint → Machine ändern.

**Prüfpunkte:**
- [ ] BulkEditPanel erscheint bei Multi-Select. Änderung greift auf alle selektierten Nodes.

---

### Test 60.2 — ActivityTypeFilter (Node-Typen auf Canvas ausblenden)

**Schritte:** EditorHeader → Filter-Icon → Activity-Typ deaktivieren.

**Prüfpunkte:**
- [ ] Deaktivierte Nodes ausgeblendet/gefadet. Badge zeigt Anzahl. "Clear All" setzt zurück.

---

## Teil 61: Edge-Reshape Handles

### Test 61.1 — Edge manuell biegen

**Schritte:** Edge selektieren → Handle ziehen.

**Prüfpunkte:**
- [ ] Handles erscheinen bei selected Edge. Drag ändert Krümmung. Form nach Save+Reload erhalten. Nur bei Single-Segment-Edges.

---

### Test 61.2 — Edge-Form zurücksetzen

**Schritte:** Rechtsklick auf gebogene Edge → "Edge-Form zurücksetzen".

**Prüfpunkte:**
- [ ] Edge folgt Standard-Routing. Undo stellt gebogene Form wieder her.

---

## Teil 62: Designer Status-Banner

### Test 62.1 — Disabled-Workflow-Banner

**Prüfpunkte:**
- [ ] Banner "Workflow ist deaktiviert" + "Aktivieren"-Button (wenn lock-by-me). Klick → Banner verschwindet.

---

### Test 62.2 — Fremdlock-Banner

**Prüfpunkte:**
- [ ] Banner "Gesperrt von \<Username\>". Edit-Felder disabled. Admin sieht "Force Unlock"-Button mit Confirm-Dialog.

---

## Teil 63: Workflows-Listenansicht UI

### Test 63.1 — Spalten-Sortierung (Column Headers)

**Schritte:** Klick auf "Name"-Header → erneuter Klick → Klick auf "Geändert".

**Prüfpunkte:**
- [ ] Erst Klick: aufsteigend. Zweiter: absteigend. Chevron ↑/↓ sichtbar.

---

### Test 63.2 — Enable/Disable-Toggle in Listenzeile

**Prüfpunkte:**
- [ ] Icon wechselt sofort (optimistic update). `isEnabled` in DB geändert. Fehler → Toast + Rollback.

---

### Test 63.3 — Delete-Bestätigungs-Dialog

**Schritte:** Menü auf Workflow-Zeile → "Löschen" → Dialog → "Abbrechen" → erneut → "Bestätigen".

**Prüfpunkte:**
- [ ] Dialog erscheint. "Abbrechen" lässt Workflow unberührt. "Bestätigen" entfernt ihn. Audit `WORKFLOW_DELETED`.

---

## Teil 64: Audit-Log Pagination & Multi-Filter

### Test 64.1 — Cursor-Pagination

**Voraussetzung:** >100 Audit-Events.

**Prüfpunkte:**
- [ ] Erste Seite ≤100 Einträge. "Mehr laden" hängt an (kein Ersetzen). Button verschwindet auf letzter Seite.

---

### Test 64.2 — Multi-Filter

**Schritte:** Filter: `action=WORKFLOW_PUBLISHED` + `userName=admin` + Daterange.

**Prüfpunkte:**
- [ ] AND-kombiniert. Export-Button respektiert Filter. Reset leert alle Felder.

---

## Teil 65: Credential- & Machine-Picker im Properties Panel

### Test 65.1 — Credential-Picker

**Prüfpunkte:**
- [ ] Dropdown zeigt alle Credentials. Wahl wird gespeichert. "None" als Option vorhanden.

---

### Test 65.2 — Machine-Picker

**Prüfpunkte:**
- [ ] Dropdown zeigt alle Machines. Wahl wird gespeichert.

---

## Teil 66: SignalR-Verbindungsstatus

### Test 66.1 — Live-Indikator während Execution

**Prüfpunkte:**
- [ ] Grüner Live-Indikator sichtbar. Steps werden in Echtzeit ohne Reload aktualisiert.

---

### Test 66.2 — Reconnect nach Verbindungsabbruch

**Schritte:** DevTools → Offline 5s → Online.

**Prüfpunkte:**
- [ ] Offline: Indikator rot/orange. Nach Reconnect: grün. Verpasste Events nachgeladen.

---

## Teil 67: Import-Dialog mit Drag & Drop

### Test 67.1 — Drag & Drop Import

**Prüfpunkte:**
- [ ] Drag-Zone hebt sich hervor bei Drag-over. Drop startet Import. Ergebnis-Zusammenfassung erscheint.

---

### Test 67.2 — Multi-File Import

**Prüfpunkte:**
- [ ] Alle Dateien verarbeitet. Fehlerhafte Datei bricht andere nicht ab. Ergebnis pro Datei.

---

## Teil 68: Activity-Palette & Node-Kontext-Menü

### Test 68.1 — ActivityPickerGrid (Rechtsklick auf Canvas)

**Schritte:** Rechtsklick auf leere Canvas → ActivityPickerGrid erscheint.

**Prüfpunkte:**
- [ ] Grid nach Kategorie gruppiert. Klick fügt Node ein. Escape ohne Node.

---

### Test 68.2 — Node-Kontext-Menü (vollständig)

**Schritte:** Rechtsklick auf Activity-Node.

**Prüfpunkte:**
- [ ] Menü enthält: Duplicate, Delete, Enable/Disable, Breakpoint setzen/entfernen. Außen-Klick schließt. Alle Aktionen sofort wirksam.

---

### Test 68.3 — Bulk-Disable per D-Taste

**Schritte:** Mehrere Nodes selektieren → D.

**Prüfpunkte:**
- [ ] Alle selektierten Nodes disabled (ausgegraut). Erneutes D → wieder enabled. Execution überspringt disabled Nodes.

---

### Test 68.4 — Bulk-Breakpoint per B-Taste

**Schritte:** Mehrere Nodes selektieren → B.

**Prüfpunkte:**
- [ ] Alle erhalten Breakpoint-Markierung. Erneutes B entfernt alle. Debug-Execution pausiert bei allen.

---

## Teil 69: Editor-Toolbar View-Toggles

### Test 69.1 — Data Flow Overlay

**Prüfpunkte:**
- [ ] Button aktiv → Edges mit Variablen hervorgehoben, andere gedimmt. Zweites Klicken → normal.

---

### Test 69.2 — Machine Coloring Toggle

**Voraussetzung:** Nodes mit verschiedenen Machines.

**Prüfpunkte:**
- [ ] Nodes nach Machine eingefärbt. Deaktivieren → Standard-Farbe.

---

### Test 69.3 — Simulation ausführen

**Prüfpunkte:**
- [ ] Simulation-Modus startet (Status-Indikator). Canvas zeigt Mock-Execution. "Clear" → Normalzustand.

---

## Teil 70: Lint Panel

### Test 70.1 — Lint-Fehler werden angezeigt

**Voraussetzung:** Workflow mit Lint-Problemen (z. B. Node ohne eingehende Edge).

**Schritte:** EditorHeader → Lint-Button → LintPanel öffnet.

**Prüfpunkte:**
- [ ] Errors + Warnings gelistet. Klick auf Eintrag springt zum betroffenen Node. Schließen per X.

---

### Test 70.2 — Kein Lint-Problem bei validem Workflow

**Prüfpunkte:** LintPanel zeigt "No issues found". Lint-Button grün.

---

## Teil 71: LiveConsole — Filter & Pause

### Test 71.1 — Log-Filter

**Schritte:** Laufende Execution → LiveConsole → Begriff ins Filter-Feld tippen.

**Prüfpunkte:**
- [ ] Nur passende Zeilen sichtbar. Leeren → alle zurück.

---

### Test 71.2 — Errors-Only Toggle

**Prüfpunkte:**
- [ ] "Errors only"-Button → nur Fehlerzeilen. Fehler-Count-Badge. Erneutes Klicken → alle Zeilen.

---

### Test 71.3 — Auto-Scroll pausieren

**Prüfpunkte:**
- [ ] Pause-Toggle stoppt Auto-Scroll. Indikator wechselt auf "Pausiert". Erneutes Klicken aktiviert Auto-Scroll.

---

## Teil 72: WorkflowBreadcrumbs (Calls → Navigation)

### Test 72.1 — "Calls →" Leiste mit Child-Links

**Voraussetzung:** `startWorkflow`-Node mit statischem Workflow-Namen.

**Prüfpunkte:**
- [ ] Leiste unter Editor-Header erscheint. Klickbarer Pill. Klick navigiert zum Child-Workflow-Editor. Dynamische `{{`-Refs erscheinen nicht als Link.

---

### Test 72.2 — Broken Reference (Workflow nicht gefunden)

**Prüfpunkte:**
- [ ] Pill mit ⚠ + amber Farbe. Tooltip: "Workflow '\<name\>' not found". Nicht klickbar.

---

## Teil 73: Edge-Label Manuell Überschreiben

### Test 73.1 — Custom Edge-Label

**Schritte:** Edge selektieren → EdgePropertiesPanel → Label-Feld ausfüllen.

**Prüfpunkte:**
- [ ] Canvas-Edge zeigt Custom-Label sofort. Nach Speichern + Reload: bleibt.

---

### Test 73.2 — Custom vs. Auto-Label

**Prüfpunkte:**
- [ ] Custom-Label bleibt trotz Condition-Änderung. Auto-Label-Preview zeigt was ohne Custom wäre. Leeren → Auto-Label übernimmt.

---

## Teil 74: Workflow-Snippets (NodeLibrary)

### Test 74.1 — Snippet einfügen

**Schritte:** Linke Sidebar → Snippets-Sektion → Klick auf Snippet.

**Prüfpunkte:**
- [ ] Nodes+Edges des Snippets auf Canvas eingefügt. Neue IDs (kein Clash). Sofort verschiebbar. Viewer: Snippets deaktiviert.

---

## Teil 75: Quick-Interaktionen im Designer

### Test 75.1 — QuickEditPopup (Doppelklick auf Node)

**Schritte:** Node doppel-klicken.

**Prüfpunkte:**
- [ ] Popup mit primärem Feld (log → Message, restApi → URL, runScript → Script, sql → Query). Enter/Blur speichert. Escape verwirft.

---

### Test 75.2 — QuickConnectPicker (Drag von Handle auf Canvas)

**Schritte:** Von Node-Handle auf leere Canvas-Fläche ziehen → loslassen.

**Prüfpunkte:**
- [ ] QuickConnectPicker erscheint. Klick → Neuer Node + verbundene Edge. Escape → kein Node.

---

### Test 75.3 — EdgeInserter (Node in Edge einfügen)

**Schritte:** Doppelklick auf Edge (o. entsprechende Aktion).

**Prüfpunkte:**
- [ ] ActivityPickerGrid erscheint. Klick → Neuer Node in Edge-Mitte. Ursprüngliche Edge → zwei Edges ersetzt.

---

### Test 75.4 — SubWorkflowPreviewModal

**Voraussetzung:** `startWorkflow`-Node mit statischem Namen.

**Schritte:** Node-Body-Link klicken.

**Prüfpunkte:**
- [ ] Modal öffnet mit Mini-Designer des Child-Workflows (read-only). "Open in Editor" navigiert. Escape schließt. Nicht gefundener Workflow → ⚠-Meldung.

---

## Teil 76: Admin Settings UI (SettingsPage Sektionen)

### Test 76.1 — SettingsPage Navigation & Sektionen

**Schritte:** Als Admin → `/settings`.

**Prüfpunkte:**
- [ ] Sektion vorhanden: Authentifizierung, Sicherheit, SMTP, Integrationen (LLM), Logging & Telemetrie, Performance, Retention, System-Info. Viewer → 403/Redirect.

---

### Test 76.2 — SMTP-Konfiguration + TestProbeModal

**Schritte:** SMTP-Sektion → Host/Port/From ausfüllen → "Test" → "Speichern".

**Prüfpunkte:**
- [ ] TestProbeModal mit Erfolg/Fehler-Anzeige. Speichern persistiert. ETag-Konflikt → EtagConflictDialog.

---

### Test 76.3a — Retention-Einstellungen (Hot-Reload)

**Schritte:** Retention → `MaxAgeDays=14` → Speichern.

**Prüfpunkte:**
- [ ] Wert persistent. PUT läuft mit `If-Match` (ETag). Hot-Reload-Hinweis („sofort wirksam") auf der Karte — Retention ist hot-reloadable, kein RestartBanner für diese Sektion.

---

### Test 76.3b — RestartBanner für restart-pflichtige Sektion

**Schritte:** `/status` mit `restartRequired:true` für eine boot-feste Sektion (z. B. Logging) → System-Tab öffnen.

**Prüfpunkte:**
- [ ] Orangenes RestartBanner (role=alert) erscheint. Gilt für die restart-pflichtigen Sektionen (Authentication, Logging, OpenTelemetry, Security, RestApi, Remote, Engine, ExecutionDispatch); hot-reloadable Sektionen (Smtp, Llm, Retention, Stats, Threading, FileSystemOperation, SqlActivity, StartProgram, Webhook, ExternalTrigger, DbAdmin) zeigen stattdessen den Live-Hinweis.

---

### Test 76.4 — Support-Log Viewer (SupportEventsTable)

**Schritte:** Support-Log öffnen → Plain-Text-Toggle klicken.

**Prüfpunkte:**
- [ ] Tabellen-Ansicht mit EventType, WorkflowName usw. Toggle → Plain-Text-Rohdaten. Filter funktionieren.

---

### Test 76.5 — System-Info Sektion

**Prüfpunkte:**
- [ ] .NET-Version, DB-Provider, Remote-Provider, Assembly-Version sichtbar (Live vom Backend).

---

## Teil 77: KI-Workflow-Assistent + Streaming (Erklären + Bearbeiten)

> Voraussetzung: `Llm:Enabled=true` + erreichbarer LLM-Endpoint. Hermetisch in `e2e/ai-assistant.spec.ts` abgedeckt (alle APIs gemockt via `page.route`): Panel öffnen, fragen, Proposal-Apply-Gating, Stale-Schutz, SSE-End-State (77.1–77.3). Echtes Token-Chunking + Antwort-Qualität sind backend-/modellabhängig und nur manuell prüfbar. Tests 77.5 (benannte Threads) und 77.7 (Markdown-Export) sind ebenfalls hermetisch testbar (kein LLM nötig); 77.6/77.8/77.9 setzen LocalStorage-Persistenz, Audit-Schreibzugriff bzw. einen echten Tool-Calling-LLM voraus. `chat` + `generate-script` antworten als SSE (`text/event-stream`).

### Test 77.1 — Panel öffnen & Erklärung streamt

**Schritte:** Workflow im Designer öffnen → lila „KI-Assistent"-Button neben dem Standard/Experte-Toggle → Frage stellen („Was macht dieser Workflow?") → Senden.

**Prüfpunkte:**
- [ ] Angedocktes Chat-Panel rechts öffnet sich; Antwort erscheint **ab dem ersten Token** (blinkender Cursor) und wird als Markdown gerendert.
- [ ] Während des Streams erscheint ein **Stopp**-Button; Klick bricht ab, partielle Antwort bleibt, kein Fehler.
- [ ] Bei `Llm:Enabled=false` → Fehleranzeige `LLM_DISABLED` im Panel.

---

### Test 77.2 — Änderung vorschlagen & übernehmen (Admin/Operator)

**Schritte:** Im Bearbeiten-Modus (Lock-by-me) eine Änderung beauftragen („Füge nach dem Trigger einen Log-Schritt ein") → Prosa streamt, Proposal-Karte erscheint am Ende → Änderungen per Checkbox auswählen → „Übernehmen".

**Prüfpunkte:**
- [ ] Die Erklärung streamt live; die **Proposal-Karte** mit strukturiertem Changelog (hinzugefügt/entfernt/geändert, reine Layout-Moves separat markiert) erscheint erst am Stream-Ende.
- [ ] Checkboxen ermöglichen **selektives Übernehmen**: nur ausgewählte Änderungen landen auf dem Canvas; Kanten ohne Endpunkt werden übersprungen (Hinweis-Banner wenn vorhanden).
- [ ] „Übernehmen" lädt die Auswahl auf den Canvas (Positionen/Credentials/Conditions unveränderter Nodes bleiben erhalten); Workflow wird dirty → `Save`/`Publish`.
- [ ] Nach dem Apply: **Rückgängig**-Button macht die Änderung rückgängig; **Layout aufräumen** ordnet den Canvas.
- [ ] Ändert man den Canvas zwischen Frage und „Übernehmen", wird das Apply mit Stale-Hinweis blockiert.

---

### Test 77.3 — Viewer darf fragen, nicht ändern

**Schritte:** Als Viewer einen Workflow öffnen → Assistent öffnen → Änderung beauftragen.

**Prüfpunkte:**
- [ ] Button + Chat sind sichtbar; eine Antwort streamt, aber statt „Übernehmen" steht der Hinweis „Änderungen sind Operator/Admin vorbehalten".

---

### Test 77.4 — Script-Generierung streamt in den Editor

**Schritte:** runScript-Node öffnen (Properties-Panel **oder** Doppelklick) → „KI"-Button → Prompt → Generieren.

**Prüfpunkte:**
- [ ] Der Prompt-Dialog schließt beim ersten Token; das Skript **tippt sich live** in den Monaco-Editor (während des Streams read-only).
- [ ] Die ganze Generierung ist **ein** Undo-Schritt (`Ctrl+Z` macht sie komplett rückgängig).
- [ ] Bei „Komplett ersetzen" geht bei einem Fehler **vor** dem ersten Token der alte Inhalt nicht verloren.
- [ ] Dialog-Close während des Streams bricht sauber ab.

---

### Test 77.5 — Benannte Chat-Threads (wechseln / umbenennen / löschen)

**Voraussetzung:** Admin/Operator im Designer, `Llm:Enabled=true`.

**Schritte:** Chat-Panel öffnen → Thread-Picker öffnen → „Neuer Chat" erstellen → aktiven Thread umbenennen → weiteren Thread erstellen → zwischen Threads wechseln → einen Thread löschen.

**Prüfpunkte:**
- [ ] Jeder Workflow hat einen eigenen Thread-Verlauf; Wechsel im Picker zeigt den Verlauf des jeweils anderen Threads.
- [ ] Umbenennung des aktiven Threads wird sofort im Picker reflektiert.
- [ ] Gelöschter Thread ist aus dem Picker entfernt; aktiver Thread wechselt auf den nächsten verfügbaren.
- [ ] „Neuer Chat" startet mit leerem Verlauf.

---

### Test 77.6 — Persistenter Verlauf (localStorage)

**Schritte:** Im Chat-Panel mehrere Nachrichten senden → Seite neu laden → Panel erneut öffnen.

**Prüfpunkte:**
- [ ] Der Verlauf des aktiven Threads ist nach dem Reload vollständig erhalten (Prosa-Nachrichten).
- [ ] Canvas-Snapshots + Proposal-Definitionen sind **nicht** im gespeicherten Verlauf (redacted).
- [ ] Bei einem **ungespeicherten** Workflow wird kein Verlauf persistiert.
- [ ] Nach **Logout** ist der Verlauf gelöscht (kein cross-user Leak).

---

### Test 77.7 — Markdown-Export des Threads

**Schritte:** Thread mit mindestens zwei Nachrichten öffnen → „Als Markdown exportieren"-Button im Panel-Header klicken.

**Prüfpunkte:**
- [ ] Ein `.md`-Datei-Download startet im Browser (client-seitig, kein Backend-Call nötig).
- [ ] Der Export enthält alle sichtbaren Nachrichten des Threads als Markdown; Proposal-Definitions-JSON ist **nicht** enthalten.

---

### Test 77.8 — Workflow-scoped AI-Aktivitäts-Ansicht

**Voraussetzung:** Admin/Operator; mindestens ein Proposal wurde angewendet (`POST /api/ai/chat/applied`).

**Schritte:** Im Chat-Panel die „AI-Aktivität"-Ansicht öffnen (Panel-Header oder Tab).

**Prüfpunkte:**
- [ ] Die Ansicht listet `AI_WORKFLOW_EXPLAINED`- und `AI_PROPOSAL_APPLIED`-Einträge des Workflows, neueste zuerst.
- [ ] Viewer-Rolle sieht die Ansicht **nicht** (Endpoint ist Admin/Operator).
- [ ] Folder-RBAC greift: Operator ohne Read-Recht auf den Workflow-Ordner bekommt 403.

---

### Test 77.9 — Tool-Calling-Anzeige (opt-in `Llm:EnableToolCalling=true`)

**Voraussetzung:** `Llm:EnableToolCalling=true` in den Admin-Einstellungen.

**Schritte:** Frage stellen, die Tool-Calling auslöst → SSE-Stream sendet `building` → `tool_call` → `tool_result` → `delta` → `done`.

**Prüfpunkte:**
- [ ] `building`-Event → UI zeigt „Generiere Workflow-Änderung…" (oder ähnliche Status-Meldung).
- [ ] `tool_call`-Event → „analyze_workflow — running…"-Anzeige erscheint im Panel.
- [ ] `tool_result`-Event → die Anzeige wechselt zu „checked".
- [ ] Ist `Llm:EnableToolCalling=false` (Default), wird die Tool-Calling-Schleife nicht ausgeführt (keine `tool_call`/`tool_result`-Events).

---

## Teil 78: Alerting (Notification-Rules)

> Voraussetzung: Admin-Login. Seite **Alerting** (`/alerts`) in der Sidebar. Siehe `docs/alerting.md`.

### Test 78.1 — Regel anlegen
1. `/alerts` öffnen → „Neue Regel".
2. Name setzen, Ereignistyp **Ausführung fehlgeschlagen** wählen, Geltungsbereich **Global**.
3. Filter (optional): im Condition-Builder eine Bedingung `Workflow-Name == Prod` hinzufügen (Field-Modus).
4. Kanal **E-Mail** mit Empfänger hinzufügen; Cooldown z. B. 30 setzen.
5. Speichern → Regel erscheint in der Liste mit Events/Scope/Kanälen.
- [ ] Regel persistiert; Liste zeigt Name, „Aktiv", Events, Scope, Kanäle.

### Test 78.2 — Test-Fire
1. Regel bearbeiten → Button **Testbenachrichtigung**.
2. Ergebnis pro Kanal wird inline angezeigt (ok / fehlgeschlagen + Fehler bei z. B. fehlender SMTP-Konfiguration).
- [ ] Pro-Kanal-Ergebnis sichtbar; ein fehlgeschlagener Kanal zeigt die Fehlermeldung.

### Test 78.3 — Webhook-Route + Secret-Redaction
1. Regel mit Kanal **Webhook** (URL) + HMAC-Secret anlegen, speichern.
2. Regel erneut öffnen → das Secret-Feld zeigt „•••• (gespeichert)", nie den Klartext.
- [ ] Gespeichertes Secret wird nie im Klartext zurückgegeben; Sentinel erhält es über einen Edit.

### Test 78.4 — Rollen-Gating & Löschen
1. Als Viewer: kein „Neue Regel"-Button, keine Edit/Delete-Aktionen.
2. Als Admin: Regel löschen (Bestätigung) → verschwindet aus der Liste.
- [ ] Viewer read-only; Admin kann löschen.

### Test 78.5 — Zustell-Ledger (Deliveries-Modal)
1. `/alerts` öffnen → Button **Zustellungen** klicken → `DeliveriesModal` öffnet sich.
2. Liste zeigt letzte Zustellversuche mit Status (`Succeeded` / `Failed` / `Pending`), Kanal, Ziel und Zeitstempel.
3. Status-Filter (Dropdown) auf `Failed` setzen → Liste filtert entsprechend.
- [ ] Modal öffnet sich; Zustellliste wird angezeigt; Status-Filter funktioniert.

### Test 78.6 — Gauge-Regel + Adaptiver Feldkatalog + Scope-Gate
1. Neue Regel anlegen, Ereignistyp **Backlog hoch** (`BacklogHigh`) wählen.
2. Geltungsbereich-Selector prüfen → nur **Global** auswählbar (Ordner/Workflow-Optionen fehlen oder sind deaktiviert).
3. Filter öffnen → Feldkatalog zeigt nur Gauge-Felder (`signalValue`, `sourceKey`), keine Execution-Felder (`workflowName`, `status` usw.).
4. Bedingung `Messwert > 50` (`signalValue`) hinzufügen → speichern.
- [ ] Scope fest auf Global; Feldkatalog adaptiert sich auf Gauge-Felder; Regel persistiert.

### Test 78.7 — ExecutionRunningLong + cancelledBy-Filter
1. Neue Regel anlegen, Ereignistyp **Ausführung läuft lange** (`ExecutionRunningLong`) wählen.
2. Geltungsbereich auf **Workflow** einschränken → sollte erlaubt sein (ExecutionRunningLong ist execution-scoped, kein Gauge).
3. Speichern → Regel erscheint in der Liste.
4. Zweite Regel: Ereignistyp **Ausführung abgebrochen** (`ExecutionCancelled`), Filter `Abgebrochen von == user`.
5. Speichern → Filter korrekt angezeigt.
- [ ] ExecutionRunningLong akzeptiert Workflow-Scope; cancelledBy-Filter speichert und wird angezeigt.

### Test 78.8 — System-Alerts-Tab (ADR 0008)
1. `/alerts` öffnen → landet auf dem Tab **System-Alarme** (Default). Katalog-Karten nach Kategorie gruppiert.
2. Eine Quelle (z. B. **Execution-Backlog**) → **Policy hinzufügen** → Schwelle (`depth > N`), Dauer, Route setzen, aktivieren → Karte zeigt **Aktiv**.
3. **Aktuelle Werte prüfen** → Preview listet passende Instanzen (oder „keine Beobachtung trifft zu").
4. Nicht verfügbare Quelle (z. B. Maschine ohne Connectivity-Check) → Karte zeigt **Nicht verfügbar**.
5. Tab **Benutzerdefinierte Regeln** → die alte Tabelle; System-Policies erscheinen dort NICHT (Kind-Isolation).
- [ ] System-Tab rendert Katalog + Status; Policy anlegen/aktivieren/preview; Kind-Isolation zw. den Tabs.

> **Automatisierung:** vitest deckt `AlertingPage` (Liste/Empty/Rollen-Gating/Suche/Editor-Öffnen, Tab-Split),
> `SystemAlertsSection` (Katalog-Karten, Gruppierung, Status Aktiv/Nicht verfügbar/Nicht konfiguriert,
> Admin-vs-Viewer add-policy), den ConditionBuilder-Event-Source, Gauge-Scope-Gate, adaptiven Feldkatalog
> (inkl. `signalValue`), `cancelledBy`-Filter sowie `DeliveriesModal` (Status-Filter, leere Liste) ab.
> Ein hermetischer Playwright-Spec ist ein Follow-up.

---

## Teil 79: Toolbar-Layout-Umschalter (kompakt ⇄ klassisch)

**Voraussetzung:** Workflow im Editor geöffnet, Expert-Modus.

**Schritte:** EditorHeader → `Rows3`-Icon (`toggle-toolbar-layout`) klicken.

**Prüfpunkte:**
- [ ] Umschalten kompakt → klassisch: die gruppierten Popovers (Darstellung `canvas-settings-trigger`, Overlays `view-overlays-trigger`) verschwinden; alle Toggles/Tools stehen als **einzelne Inline-Buttons** in einer Reihe, Overlay-Toggles inline (`toggle-dataflow-overlay` u. a. mit erhaltenen `data-testid`s), Run ist icon-only Play, Name rechtsbündig. Zurückschalten stellt das kompakte Layout wieder her.
- [ ] **Persistenz:** Wahl überlebt Reload (persistiert in `designStore.toolbarLayout`); ein Alt-Profil ohne `toolbarLayout` startet auf `compact`.
- [ ] **Proximity-Glow** funktioniert in beiden Layouts (farbige Bloom-Unterlegung beim Annähern); aktive Overlays behalten ihre eigene Tönung (rot/amber/orange/primary).
- [ ] **Viewport 1024 & 1440 px:** klassische Reihe wrappt ganze Cluster mehrzeilig, **kein** horizontaler Overflow; der Layout-Umschalter bleibt sichtbar/erreichbar.
- [ ] Rollen-/Lifecycle-Verhalten (Run/Publish/Disable/Save) identisch in beiden Layouts (geteilte `RunControls`/`LifecycleControls`).

> Automatisiert: `e2e/toolbar-layout.spec.ts` (79.1–79.4, hermetisch).

---

## Checkliste für vollständigen E2E-Test-Run

```
[ ] Teil 1: Workflow-Management (1.1 — 1.4)
[ ] Teil 2: Activity-Typen (2.1 — 2.13, 2.2b, 2.2c)
[ ] Teil 3: Node-Operationen (3.1 — 3.5)
[ ] Teil 4: Edges & Bedingungen (4.1 — 4.5)
[ ] Teil 5: Properties & Variablen (5.1 — 5.4)
[ ] Teil 6: Workflow-Ausführung (6.1 — 6.7)
[ ] Teil 7: Fehlerbehandlung (7.1 — 7.5)
[ ] Teil 8: Speichern & Persistierung (8.1 — 8.2)
[ ] Teil 9: Spezielle Szenarien (9.1 — 9.4)
[ ] Teil 10: UI/UX Tests (10.1 — 10.3)
[ ] Teil 11: Dashboard (11.1 — 11.3)
[ ] Teil 12: Keyboard-Shortcuts (12.1 — 12.6)
[ ] Teil 13: Spezielle Node-Types (13.1 — 13.2)
[ ] Teil 14: Alle Trigger-Typen (14.1 — 14.5)
[ ] Teil 15: Admin & User-Management (15.1 — 15.4)
[ ] Teil 16: Audit Log (16.1 — 16.3)
[ ] Teil 17: Theme & UX (17.1 — 17.4)
[ ] Teil 18: Workflow-Organisation (18.1 — 18.3)
[ ] Teil 19: Workflow-Diff / Version-Compare (19.1 — 19.3)
[ ] Teil 20: Machines & Credentials (20.1 — 20.3)
[ ] Teil 21: Global Variables (21.1 — 21.2)
[ ] Teil 22: SCOrch Import (22.1 — 22.2)
[ ] Teil 23: External Trigger API (23.1 — 23.2)
[ ] Teil 24: Real-time SignalR (24.1 — 24.2)
[ ] Teil 25: Authentication-Lifecycle (25.1 — 25.7)
[ ] Teil 26: RBAC — Rollen-Crossings (26.1 — 26.5)
[ ] Teil 27: Edit-Lock-Lifecycle (27.1 — 27.8)
[ ] Teil 28: Step-Test mit Kontext (28.1 — 28.6)
[ ] Teil 29: Sub-Workflow Contracts V1 (29.1 — 29.9)
[ ] Teil 30: Coverage Heatmap (30.1 — 30.5)
[ ] Teil 31: DB-Admin Viewer (31.1 — 31.5)
[ ] Teil 32: KI-Features LLM (32.1 — 32.6)
[ ] Teil 33: Hardening-Flags (33.1 — 33.6)
[ ] Teil 34: Migration-Drift + Backend-Smoke (34.1 — 34.3)
[ ] Teil 35: Secrets Re-Encryption (35.1 — 35.3)
[ ] Teil 36: Activity Catalog (36.1 — 36.2)
[ ] Teil 37: Diagnostics / Support-Log (37.1 — 37.4)
[ ] Teil 38: Admin Settings API (38.1 — 38.5)
[ ] Teil 39: Debug — Variable Overrides (39.1 — 39.2)
[ ] Teil 40: Edit-Lock — Force-Unlock Audit (40.1)
[ ] Teil 41: Rollback mit Reason (41.1 — 41.2)
[ ] Teil 42: Coverage Heatmap — Erweiterte Parameter (42.1 — 42.3)
[ ] Teil 43: Observability Query-Endpoints (43.1 — 43.3)
[ ] Teil 44: Rate-Limiting Headers (44.1 — 44.3)
[ ] Teil 45: Shared Folder Permissions Grant/Revoke (45.1 — 45.3)
[ ] Teil 46: Unresolved Template Variables (46.1 — 46.4)
[ ] Teil 47: External Trigger — Idempotency-Key TTL (47.1 — 47.2)
[ ] Teil 48: Output-Redaction — Viewer-Perspektive (48.1 — 48.2)
[ ] Teil 49: Execution Retry & Cancel-All (49.1 — 49.4)
[ ] Teil 50: Workflow Duplicate, By-Name-Lookup & Bulk-Export (50.1 — 50.6)
[ ] Teil 51: Step Stats & Step Health (51.1 — 51.3)
[ ] Teil 52: Folder Move — Shared Folders & Workflow Move-Folder (52.1 — 52.4)
[ ] Teil 53: Schedule Next-Fires & AI Generate-Workflow (53.1 — 53.4)
[ ] Teil 54: Designer-Overlays — Command Palette, Quick Switcher, Find & Replace (54.1 — 54.3)
[ ] Teil 55: Erweiterte Keyboard-Shortcuts (55.1 — 55.4)
[ ] Teil 56: Error-Notifications & Empty States (56.1 — 56.4)
[ ] Teil 57: Gantt-Chart & Execution-Timeline (57.1 — 57.2)
[ ] Teil 58: Variable Autocomplete & Preview Tooltip (58.1 — 58.2)
[ ] Teil 59: PausedVariablesInspector (59.1 — 59.2)
[ ] Teil 60: BulkEditPanel & ActivityTypeFilter (60.1 — 60.2)
[ ] Teil 61: Edge-Reshape Handles (61.1 — 61.2)
[ ] Teil 62: Designer Status-Banner (62.1 — 62.2)
[ ] Teil 63: Workflows-Listenansicht UI (63.1 — 63.3)
[ ] Teil 64: Audit-Log Pagination & Multi-Filter (64.1 — 64.2)
[ ] Teil 65: Credential- & Machine-Picker (65.1 — 65.2)
[ ] Teil 66: SignalR-Verbindungsstatus (66.1 — 66.2)
[ ] Teil 67: Import-Dialog mit Drag & Drop (67.1 — 67.2)
[ ] Teil 68: Activity-Palette & Node-Kontext-Menü (68.1 — 68.4)
[ ] Teil 69: Editor-Toolbar View-Toggles (69.1 — 69.3)
[ ] Teil 70: Lint Panel (70.1 — 70.2)
[ ] Teil 71: LiveConsole — Filter & Pause (71.1 — 71.3)
[ ] Teil 72: WorkflowBreadcrumbs — Calls → Navigation (72.1 — 72.2)
[ ] Teil 73: Edge-Label manuell überschreiben (73.1 — 73.2)
[ ] Teil 74: Workflow-Snippets / NodeLibrary (74.1)
[ ] Teil 75: Quick-Interaktionen im Designer (75.1 — 75.4)
[ ] Teil 76: Admin Settings UI — SettingsPage Sektionen (76.1 — 76.5)
[ ] Teil 77: KI-Workflow-Assistent + Streaming (77.1 — 77.9)
[ ] Teil 78: Alerting (78.1 — 78.7)
```

---

## Bekannte Limitation & Gotchas

- **WinRM-Tests:** Remote-Activities erfordern echte Machines oder Mock-Setup. In Test-Environment: `Remote:Provider: noop` verwenden.
- **Scheduling:** Quartz-basierte Trigger benötigen Systemzeit-Stabilität. Für Tests: Mock-Clock oder Quartz-Testutils nutzen.
- **FileWatcher:** Erfordert echte Dateisystem-Änderungen. In CI/Sandbox: möglicherweise eingeschränkt.
- **Email-Notification:** SMTP muss konfiguriert sein. Sonst: Mock-SMTP oder Dummy-Implementation.
- **Database-Trigger:** SELECT-Polling kann in Tests langsam sein. Intervall auf 1s setzen für schnelle Tests.

---

## Weitere Ressourcen

- Workflow-Layout: [docs/workflow-styleguide.md](docs/workflow-styleguide.md)
- Tech-Demo: [scripts/tech-demo/main.json](scripts/tech-demo/main.json)
- Seed-Script: [scripts/tech-demo/seed.ps1](scripts/tech-demo/seed.ps1)
- Playwright Docs: https://playwright.dev/docs/intro
- React Flow Docs: https://reactflow.dev/

---

**Letzte Aktualisierung:** 2026-07-09 — Teile 77–78 erweitert (KI-Workflow-Assistent Threads/Persistenz/Export/Aktivität/Tool-Calling, Alerting Delivery-Ledger/Gauge-Scope-Gate/cancelledBy) und trigger-only/runScript-Isolation-Szenarien nachgezogen.
**Autor:** sev7enup
**Projekt:** NodePilot E2E Test Suite
