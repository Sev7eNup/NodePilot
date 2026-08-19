# Activity types & scopes

An activity is an executable work step. The scope determines where it runs:

- **Remote:** executed on `targetMachineId` over WinRM.
- **Engine-local:** executed in the NodePilot API process.
- **Hybrid:** where it runs depends on the configuration.

`runScript` and `waitForCondition` are hybrid. `ControlFlow` is a functional category in the activity catalog, not an execution location.

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
