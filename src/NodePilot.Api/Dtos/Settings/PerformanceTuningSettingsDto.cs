using System.ComponentModel.DataAnnotations;

namespace NodePilot.Api.Dtos.Settings;

// Performance tuning DTOs. Flat per-root sections grouped in the UI's "Performance"
// tab. All of these are strict-startup (Restart required); the values are read once
// in Program.cs / hosting setup and cached for the process lifetime.

public sealed class EngineSettingsDto : IValidatableObject
{
    [Required] public DebugSettingsDto Debug { get; set; } = new();
    [Required] public MaxConcurrentExecutionsDto MaxConcurrentExecutions { get; set; } = new();

    [Range(1, 10_000)]
    public int MaxConcurrentSteps { get; set; } = 600;

    [Required] public RunspaceSettingsDto Runspace { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var r in LoggingSettingsDto.ValidateChild(Debug, nameof(Debug))) yield return r;
        foreach (var r in LoggingSettingsDto.ValidateChild(MaxConcurrentExecutions, nameof(MaxConcurrentExecutions))) yield return r;
        foreach (var r in LoggingSettingsDto.ValidateChild(Runspace, nameof(Runspace))) yield return r;

        if (Runspace.MinRunspaces > Runspace.MaxRunspaces)
            yield return new ValidationResult(
                "Runspace.MinRunspaces must be <= Runspace.MaxRunspaces.",
                new[] { "Runspace.MinRunspaces", "Runspace.MaxRunspaces" });
    }
}

public sealed class DebugSettingsDto
{
    [Range(1, 1440)]
    public int MaxPauseMinutes { get; set; } = 10;
}

public sealed class MaxConcurrentExecutionsDto
{
    [Range(1, 100_000)]
    public int Global { get; set; } = 5000;

    [Range(1, 100_000)]
    public int PerUser { get; set; } = 2000;
}

public sealed class RunspaceSettingsDto
{
    [Range(1, 10_000)]
    public int MinRunspaces { get; set; } = 256;

    [Range(1, 10_000)]
    public int MaxRunspaces { get; set; } = 768;
}

public sealed class ExecutionDispatchSettingsDto
{
    [Range(1, 100_000)]
    public int Capacity { get; set; } = 2048;

    [Range(1, 10_000)]
    public int WorkerCount { get; set; } = 600;
}

public sealed class ThreadingSettingsDto
{
    [Range(1, 10_000)]
    public int MinWorkerThreads { get; set; } = 768;

    [Range(1, 10_000)]
    public int MinIoCompletionThreads { get; set; } = 768;
}

/// <summary>
/// Unified Remote section — exposes both the security flag (RequireWinRmSsl) and the
/// connection-pool tuning (Pool.*) plus per-operation timeouts. Grouped together so
/// the save round-trip writes the whole "Remote" subtree atomically without two
/// separate sections fighting over its layout.
///
/// <para>Despite RequireWinRmSsl being security-relevant, the whole block lives in
/// the Performance tab because Pool tuning is the dominant operator concern here.
/// The Security tab has a one-line link/hint pointing at it.</para>
/// </summary>
public sealed class RemoteSettingsDto : IValidatableObject
{
    public bool RequireWinRmSsl { get; set; } = true;

    [Required] public WinRmSubSettingsDto WinRm { get; set; } = new();
    [Required] public RemotePoolDto Pool { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var r in LoggingSettingsDto.ValidateChild(WinRm, nameof(WinRm))) yield return r;
        foreach (var r in LoggingSettingsDto.ValidateChild(Pool, nameof(Pool))) yield return r;
    }
}

public sealed class WinRmSubSettingsDto
{
    [Range(1, 3600)]
    public int OperationTimeoutSeconds { get; set; } = 300;

    [Range(1, 600)]
    public int OpenTimeoutSeconds { get; set; } = 30;
}

public sealed class RemotePoolDto
{
    public bool Enabled { get; set; } = true;

    [Range(1, 1000)]
    public int MaxConcurrentPerMachine { get; set; } = 5;

    [Range(1, 1000)]
    public int MaxIdlePerKey { get; set; } = 5;

    [Range(1, 3600)]
    public int IdleTtlSeconds { get; set; } = 120;
}
