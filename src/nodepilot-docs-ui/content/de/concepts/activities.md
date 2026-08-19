# Activity-Typen & Scopes

Eine Activity ist ein ausführbarer Arbeitsschritt. Der Scope legt den Ausführungsort fest:

- **Remote:** Ausführung auf `targetMachineId` über WinRM.
- **Engine-local:** Ausführung im NodePilot-API-Prozess.
- **Hybrid:** Ausführungsort hängt von der Konfiguration ab.

`runScript` und `waitForCondition` sind hybrid. `ControlFlow` ist eine fachliche Kategorie im Activity-Katalog und kein Ausführungsort.

| Type | Scope |
|---|---|
| `runScript` | Hybrid |
| `fileOperation` | Remote |
| `folderOperation` | Remote |
| `textFileEdit` | Remote |
| `serviceManagement` | Remote |
| `registryOperation` | Remote |
| `wmiQuery` | Remote |
| `startProgram` | Remote |
| `powerManagement` | Remote |
| `scheduledTask` | Remote |
| `fileHash` | Remote |
| `zipOperation` | Remote |
| `restApi` | Engine-local |
| `sql` | Engine-local |
| `emailNotification` | Engine-local |
| `delay` | Engine-local |
| `junction` | Engine-local (controlFlow) |
| `forEach` | Engine-local (controlFlow) |
| `decision` | Engine-local (controlFlow) |
| `startWorkflow` | Engine-local (controlFlow) |
| `returnData` | Engine-local (controlFlow) |
| `xmlQuery` | Engine-local |
| `jsonQuery` | Engine-local |
| `log` | Engine-local |
| `generateText` | Engine-local |
| `llmQuery` | Engine-local |
| `waitForCondition` | Hybrid |
