namespace NodePilot.Core.Enums;

public enum ExecutionStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped,
    Cancelled,
    /// <summary>
    /// The step is paused at a breakpoint and waits for a resume command from the debugger.
    /// Sits between Running and the terminal states. Set only when the execution started with
    /// debugEnabled=true and the current node has data.breakpoint=true, or StepOverArmed is
    /// still active from an earlier step-over resume.
    /// </summary>
    Paused,
}
