# Activity-Typen & Scopes

"Remote" = `targetMachineId` / WinRM. "Engine-local" = im API-Prozess. `runScript` / `waitForCondition` = hybrid. `(controlFlow)` = Kategorie `ControlFlow` im Backend-`ActivityCatalog` (Palette-Achse, unabhängig vom Scope).

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

## Neue Activity hinzufügen

Eine neue Activity ist ein reines Backend-+Frontend-Paar, ohne zentrale DI-Verdrahtung:

1. **Backend:** Klasse in `Engine/Activities/`, `IActivityExecutor` implementieren. Auto-Discovery via `AddNodePilotActivities()` scannt `NodePilot.Engine` — **keine** DI-Registrierung in `Program.cs`.
2. **Frontend-Katalog:** Eintrag in `library/activityCategories.ts` (`buildActivityCategories`).
3. **Properties-Komponente:** `*Config`-Komponente unter `properties/activities|triggers/` + Registrierung in `properties/activityConfigMap.ts` (eine Zeile — `PropertiesPanel.tsx` wird **nicht** editiert).
4. **Katalog-Spiegel:** `lib/activityCatalog.generated.ts` ergänzen (Mirror von `NodePilot.Core.Activities.ActivityCatalog`, von Hand gepflegt — kein Codegen). `isRemote`/Timeout-Flags speisen `REMOTE_ACTIVITY_TYPES` / `TIMEOUT_ACTIVITY_TYPES`. `ActivityCatalogFrontendSyncTests` erzwingt Gleichstand mit dem Backend-Katalog.
5. **Downstream-Outputs** in `describeNodeOutputs` in `lib/upstreamVariables.ts`.

Die volle Config-Keys- und Output-Referenz pro Activity: [Activity-Referenz](../activities-reference).