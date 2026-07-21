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
    /// The step is paused at a breakpoint, waiting for a resume command from the debugger.
    /// Sits between Running and the terminal states — only set when the execution was started
    /// with debugEnabled=true AND the current node has `data.breakpoint=true` (or StepOverArmed
    /// was left active by a previous step-over resume command).
    /// </summary>
    Paused,
}
