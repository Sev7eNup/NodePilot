# Automation needs a way to stop

The job has been cancelled. On the target server, the installer it started is still running.

This state is possible once the orchestration layer has handed work to another process or a remote system. Whether cancellation reaches that work, and what effect it has, depends on the implementation. Operations therefore needs to know exactly what a stop control promises.

| Intervention | Intended effect | What still needs clarification |
|---|---|---|
| Block new starts | Admit no further jobs | Work already running or waiting |
| Cancel a run | End further execution of that run | Behavior of the current step and external processes |
| Reverse a change | Restore a defined previous state | Consequences already created and subsequent changes |

Software deployment provides a useful exercise. The following scenario is a planning example: a faulty package has been approved for twenty test servers. Four installations are running, with more jobs waiting. After the first unexpected result, operations decides to halt deployment.

**First, prevent further starts.** The schedule may not be the only trigger. Manual invocations, webhooks, or parent workflows can initiate the same work. The block must cover the relevant start mechanisms. Then check whether a job already accepted can still move from a queue into execution.

**Next, identify the active executions.** For each of the four servers, establish which step has been reached. An installation that has not begun permits different treatment from an installer already running. A cancellation signal to the controller is initially just a signal. Confirmation that a target process has ended requires a separate observation.

**Record the state actually reached.** Where completion cannot be established, the state remains unresolved. Check the software version and application state on the target. Restarting before this assessment could trigger the same installation twice or interfere with a repair already in progress.

**Decide separately how to reverse changes.** Uninstalling a package does not automatically reverse its installation. If the new version has changed data, an older version must still be able to use that data. Recovery may be necessary, and its effects also require assessment. Microsoft describes these application-specific counteractions in the [Compensating Transaction pattern](https://learn.microsoft.com/en-us/azure/architecture/patterns/compensating-transaction).

The on-call team should have exercised this procedure before the first real incident. A limited test deployment, interrupted at known points, is sufficient to investigate it. Record how long interruption takes and which processes remain active afterwards. This provides evidence of the cancellation boundary.

Returning to normal operation needs an owner too. Removing a block can release waiting work. Before that happens, decide which jobs remain valid and which are superseded by the corrected deployment. Fixing the original package defect does not answer that question.

NodePilot distinguishes between disabling a workflow and cancelling its active executions. Its documentation describes combining `disable` and `cancel-all` to stop further execution. Changes already initiated on target systems still require the state checks described above and, where necessary, separate corrective actions. [NodePilot: Workflow control](https://www.nodepilot.run/docs/en/api/workflow-control/).
