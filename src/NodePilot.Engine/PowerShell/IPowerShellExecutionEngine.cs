namespace NodePilot.Engine.PowerShell;

public sealed class PowerShellExecutionRequest
{
    public string ScriptText { get; init; } = "";
    /// <summary>auto | pwsh | powershell | runspace</summary>
    public string Engine { get; init; } = "auto";
    public Dictionary<string, string> Parameters { get; init; } = new();
    public string? WorkingDirectory { get; init; }
    /// <summary>
    /// Per-script timeout. Null = no timeout enforcement (script runs until it completes or
    /// the parent CancellationToken is signalled). When set, ps.Stop() is called via a CT
    /// registration once the timeout elapses, which makes EndInvoke throw PipelineStoppedException.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Opt-in process isolation (Windows only). When true the script is launched in a
    /// dedicated child process wrapped in a Windows Job Object with KILL_ON_JOB_CLOSE +
    /// DIE_ON_UNHANDLED_EXCEPTION — a host crash/restart guarantees the whole process tree
    /// is reaped (no orphans), and a native crash/leak stays contained to the child instead
    /// of taking down the API host. Ignored by the in-process runspace engine (which cannot
    /// isolate) — the factory routes isolated requests to a process engine. No-op on the
    /// remote (WinRM) path, which already runs off-host.
    /// </summary>
    public bool Isolated { get; init; }

    /// <summary>Optional OS-enforced resource caps applied when <see cref="Isolated"/> is true.</summary>
    public ProcessIsolationLimits? IsolationLimits { get; init; }

    /// <summary>
    /// Restricts the wrapper's output-parameter capture to exactly these variable names. Null (the
    /// default, used by <c>runScript</c>) keeps the legacy behaviour of capturing every new local.
    /// Set by <c>CustomActivityExecutor</c> to the definition's declared output names, so injected
    /// inputs and undeclared helper locals never leak into <c>{{node.param.X}}</c>. The always-present
    /// <c>exitCode</c> is emitted separately and is unaffected.
    /// </summary>
    public IReadOnlyCollection<string>? OutputCaptureAllowlist { get; init; }
}

/// <summary>
/// Per-step resource caps for an isolated script, enforced by the Windows Job Object.
/// Null members receive safe launcher defaults when isolated execution is requested.
/// </summary>
public sealed record ProcessIsolationLimits
{
    /// <summary>
    /// Aggregate committed-memory cap across the whole job (all processes in the tree), in MiB.
    /// Maps to JOB_OBJECT_LIMIT_JOB_MEMORY. NB: the limit makes allocations/commits *fail*
    /// (the script sees an OutOfMemory condition) — it does not forcibly terminate the process.
    /// </summary>
    public int? MemoryLimitMb { get; init; }

    /// <summary>
    /// Maximum number of concurrently active processes in the job (fork-bomb guard).
    /// Maps to JOB_OBJECT_LIMIT_ACTIVE_PROCESS.
    /// </summary>
    public int? MaxProcesses { get; init; }
}

public sealed class PowerShellExecutionResult
{
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string Output { get; init; } = "";
    public string Error { get; init; } = "";
    public string Warning { get; init; } = "";
    public string Verbose { get; init; } = "";
    public bool TimedOut { get; init; }
    public TimeSpan Duration { get; init; }
}

public interface IPowerShellExecutionEngine
{
    /// <summary>
    /// The message carried by the <see cref="OperationCanceledException"/> every engine throws
    /// when the caller's token is signalled. Caller cancellation is not a script failure — it is
    /// how a waitAny/waitNofM junction stands down its losing branches — so the exception has to
    /// reach StepRunner, which records the step as Cancelled. An engine that swallowed it into a
    /// failed result marked those branches Failed, and a single Failed row fails the whole run.
    /// A timeout is a different matter and still comes back as a failed result with TimedOut set.
    /// </summary>
    public const string CancelledMessage = "Script execution cancelled";

    string EngineType { get; }
    bool IsAvailable { get; }
    Task<PowerShellExecutionResult> ExecuteAsync(PowerShellExecutionRequest request, CancellationToken ct);
}
