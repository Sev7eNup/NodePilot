# Technical Reference

This reference records the runtime contracts to keep in mind when changing NodePilot.
Working rules are in [CLAUDE.md](../CLAUDE.md), decisions in the [ADRs](adr/), and
usage in the [Designer overview](workflow-designer-features.md).

## Activities and script execution

Activity types and their inputs are defined in the [configuration catalog](../src/NodePilot.Core/Activities/Embedded/activity-config-reference.json).
The [activity reference](../src/nodepilot-docs-ui/content/en/activities-reference.md)
describes their use. New types are discovered through `IActivityExecutor`.

`runScript`, `waitForCondition`, `xmlQuery` and `jsonQuery` are hybrid: without a target machine they run
on the API host, with a target machine over WinRM. For `xmlQuery` and `jsonQuery` the file is read where
the step executes; UNC paths stay rejected everywhere.

### runScript: execution location

With a non-local machine or a credential, `runScript` uses the managed WinRM path.
Without a machine, or on loopback without a credential, it runs locally on the API
host. `engine: auto` starts user scripts in Windows PowerShell 5.1. Built-in
activities use the in-process pool locally.

`Engine:MaxCapturedOutputChars` caps the output collected during one script execution
across all streams at 16,777,216 characters in total (positive value, restart required).
The default leaves room for the two `startProgram` outputs, each capped at 1 MiB,
including JSON escaping. Smaller values limit that activity accordingly as well.
The shared limit applies to process, runspace and WinRM execution. Exceeding it
ends the step with an output-limit error; later truncation of stored results is
independent of this. Process pipes keep being drained after the limit is reached.

Remoting that a local script sets up itself lies outside the managed WinRM
connection. It loses that connection's credential binding, target mapping,
telemetry and transport hardening. The script must manage and clean up its
connections itself.

The in-process pool uses the PowerShell SDK with bundled modules. Implicit
Windows PowerShell compatibility is disabled. Compatibility sessions the script
creates explicitly belong to the script. With `engine: runspace`, PowerShell
processes started through `cmd`, `Start-Process` or `Process` can inherit the
module path modified by the SDK. A direct `& powershell.exe` call handles that path itself.

`config.isolated: true` runs local scripts in their own process with a Windows
Job Object. `memoryLimitMb` and `maxProcesses` limit its resources.
Remote execution does not use this local isolation.

Error records decide script success. `successExitCodes` adds an exit-code check.
A script's own `exit N` is observable on the process path, not in the runspace
pool. Parse errors stay errors in both cases.

## Variables and control flow

A step sees only the results of its graph ancestors. Missing results stay
unresolved, even if another parallel branch has already finished.
A new variable namespace needs its own parser and resolver support.

| Expression | Meaning |
|---|---|
| `{{step.output}}`, `{{step.error}}` | Standard and error output. |
| `{{step.success}}` | Success as a string. |
| `{{step.param.name}}` | Published parameter of this step. |
| `{{globals.NAME}}` | Global variable. |
| `{{manual.NAME}}` | Trigger or call parameter. |

`outputVariable` can replace the step ID. An unqualified `$name` in the script
is bound only when there is exactly one publisher. Variables the script assigns itself
appear as `param.*`; passed-through variables and PowerShell automatic variables do not.
Templates are already quoted inside scripts: use `$x = {{step.output}}`.

Only active triggers are workflow roots. Missing triggers make the execution
fail. Multiple incoming edges require a junction. Invalid conditions are rejected
on save and publish. A missing variable value satisfies no condition, not even
under NOT. Details: [ADR 0006](adr/0006-trigger-only-workflow-roots.md),
[ADR 0013](adr/0013-explicit-junction-fan-in.md) and [ADR 0015](adr/0015-fail-closed-edge-conditions.md).

## Triggers and child workflows

Trigger configuration is defined in Core and shared by Engine and Scheduler.
Event data appears under `manual.*` and as parameters of the trigger.
`databaseTrigger` reacts to a change of the sentinel value. The trigger scan runs
every 5 seconds. Downtime is not replayed as events afterwards.

`startWorkflow` can call any enabled workflow. `parameters` become `manual.*`.
With `waitForCompletion=true` the caller waits and takes over the
`returnData` output. The maximum call depth is 10, including `forEach`.
The shared slot management is described in [ADR 0018](adr/0018-active-subworkflow-capacity.md).

The derived contract considers active `manualTrigger` and `returnData` nodes.
Multiple return nodes do not guarantee every declared output: the last complete
JSON value written wins. Conflicts between input declarations are flagged,
`required` is merged. Reserved outputs such as `__executionId` and `__status`
are set by the engine itself.

Without folder Edit, input defaults are masked in the contract view; at runtime
the real values apply. Name resolution prefers an exact-case match, then a unique
case-insensitive match. Ambiguous names are rejected. Security-protected external
routes can hide this rejection as a 404.

## Execution and concurrency

`Workflow.MaxConcurrentExecutions` allows 1 to 1000, or `null` for unlimited.
All start paths share the counter. Dispatch jobs stay `Pending` at the limit,
synchronous child calls wait. `PUT /concurrency-limit` requires the field
`maxConcurrentExecutions`, even when it is `null`. The limit is part of neither
the graph save nor versioning, and a graph rollback does not change it.

A step row is persisted before the activity executes. Terminal executions
end any remaining running or paused steps. `StepsTotal` counts the rows
materialised so far and is not a reliable progress denominator while a workflow
runs. Loops and later branches can create further steps.

`WorkflowExecution.ErrorMessage` is a masked, bounded summary for lists and
notifications. Error analysis uses `StepExecution.ErrorOutput`.
With multiple errors, the order by `StartedAt` and `Id` decides.

## Step test

`POST /api/workflows/{id}/steps/{stepId}/test` executes a single activity
without creating a WorkflowExecution. `mockVariables` supplies synthetic
step results. `stepOverride` passes the complete current `node.data` state,
including activity type, configuration, machine, credential and output variable.
The three test actions in the Designer use this consistent snapshot.
Alternatively, `configOverride` replaces only `data.config` of the saved node.
The two overrides are mutually exclusive and require an active edit lock of your own.
A selected machine target that no longer exists is rejected.

`test-context` returns ancestor values from a chosen run or the last terminal
run. For ancestors that did not execute, only the schema remains. Globals come from
the GlobalVariableStore and must not be sent back as mocks.
Implementation: [StepTester.cs](../src/NodePilot.Engine/StepTester.cs).

## SCOrch import

`POST /api/workflows/import-scorch` creates disabled workflows and returns their
IDs in the import report. Enable them only after a functional review. The limit is
300 MiB per request and 500 imported workflows and variables combined.
Large XML files need considerably more memory while parsing than their file size.

`MaxParallelRequests` is carried over as the workflow limit, including `1`.
Missing values mean no limit; unusable values produce a warning.
Runbooks without a trigger get a manual trigger. Target names are resolved through
the MachineResolver. Connection strings are not carried over.

The import must preserve execution intent and data flow. Report semantics that
cannot be mapped instead of producing a replacement that only appears to work.
`Run Program` stays `startProgram`; embedded scripts are mapped separately.
Child references use folder path and name, so renames during import do not
point at the wrong workflow. External or ambiguous references are reported.

Format handling and synthetic fixtures are in
[ScorchImporter.cs](../src/NodePilot.Engine/Scorch/ScorchImporter.cs),
[ScorchActivityMapper.cs](../src/NodePilot.Engine/Scorch/ScorchActivityMapper.cs) and
[the import tests](../tests/NodePilot.Engine.Tests/Scorch/).
Real customer data does not belong in fixtures.

## Settings, statistics and operations

Settings saves write atomically to `appsettings.runtime.json`. The
`IsHotReloadable` flag in [SettingsSchema.cs](../src/NodePilot.Api/Configuration/SettingsSchema.cs)
decides the restart notice. Mixed sections stay restart-required when part of
their values is bound at boot.

| Behaviour | Sections |
|---|---|
| Hot reload | `Smtp`, `Llm`, `AiKnowledge`, `Retention`, `Stats`, `Threading`, `FileSystemOperation`, `WaitForCondition`, `SqlActivity`, `StartProgram`, `Webhook`, `ExternalTrigger`, `DbAdmin` |
| Restart | `Authentication`, `Logging`, `OpenTelemetry`, `Security`, `RestApi`, `Remote`, `Performance`, `Engine`, `ExecutionDispatch`, `Agents` |

`Threading` takes changes only when sizing is set to manual.
`Agents` contains a run limit bound at boot, despite some values being read live.
Effective sizing: [Performance](performance-improvements.md).
Restarting the service from the UI requires SCM recovery with restart actions only.

The dashboard and workflow lists read precomputed statistics. By default
`WorkflowStatsRefresher` refreshes a 7-day window every 5 minutes and uses
at most 1000 duration samples per workflow. Live Ops keeps running executions
visible regardless of the chosen history window. Capped aggregates mark missing
coverage; they must not be shown as complete totals.

The support log exists as a file and as the `SupportEvents` projection. Both have
90 days of retention by default. The full diagnostic log rotates daily and
at 100 MB, keeping at most 7 files. Save it early when handling an incident.

## Further references

| Topic | Authoritative source |
|---|---|
| Edit lock, publish and canvas | [Designer](workflow-designer-features.md) |
| AI and tool access | [AI features](ai-features.md), [agents](ai-agents.md) |
| Authentication and hardening | [Threat model](threat-model.md), [LDAP/SSO](ldap-windows-sso.md) |
| Audit codes | [AuditActions.cs](../src/NodePilot.Core/Audit/AuditActions.cs) |
| Deployment and troubleshooting | [Deployment guide](deployment-guide.md), [Desktop](../deploy/desktop/README.md) |
| Configuration backup | [ADR 0001](adr/0001-system-configuration-backup-restore.md), plus native DB and key backups for a full restore |
| HA and maintenance | [Enterprise features](enterprise-features.md), [HA](ha-active-passive.md) |
| Coverage | [coverage.runsettings](../coverage.runsettings), [CI](../.github/workflows/ci.yml) |

When merging coverage, include the assemblies `np` and `nodepilot-mcp` and
normalise the differing `src/` prefixes. Migrations and code marked with a
justification are excluded through the runsettings. Scheduler, Remote and Telemetry tests
live in Engine.Tests and Api.Tests respectively.
