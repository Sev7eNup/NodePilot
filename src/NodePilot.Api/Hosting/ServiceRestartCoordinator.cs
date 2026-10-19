using Serilog;

namespace NodePilot.Api.Hosting;

/// <summary>Whether this process can restart itself through the Service Control Manager.</summary>
/// <param name="Available">True when the process runs as a Windows service whose recovery
/// actions restart it.</param>
/// <param name="UnavailableReason">Stable code when <paramref name="Available"/> is false:
/// <c>NOT_A_WINDOWS_SERVICE</c>, <c>SERVICE_NOT_RESOLVED</c> or <c>RECOVERY_NOT_CONFIGURED</c>.</param>
public sealed record ServiceRestartCapability(bool Available, string? UnavailableReason)
{
    public static ServiceRestartCapability Yes { get; } = new(true, null);
    public static ServiceRestartCapability No(string reason) => new(false, reason);
}

/// <summary>
/// Restarts the Windows service by ending the process the way a crash does, so the SCM
/// recovery actions (<c>sc.exe failure … actions= restart/…</c>) start it again. No SCM
/// rights are needed and no helper process has to outlive its parent service.
/// <para>
/// The host is stopped first so hosted services drain; the exit then happens in
/// <c>ApplicationStopped</c>, before the service lifetime reports <c>SERVICE_STOPPED</c>.
/// A reported stop is a clean stop to the SCM and would not trigger recovery.
/// </para>
/// </summary>
public sealed class ServiceRestartCoordinator
{
    /// <summary>Non-zero exit code of the self-restart; visible in the Windows event log.</summary>
    public const int RestartExitCode = 75;

    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ServiceRestartCoordinator> _logger;
    private readonly Func<ServiceRestartCapability> _probe;
    private readonly TimeSpan _stopDelay;
    private readonly Action<int> _exit;
    private int _scheduled;

    public ServiceRestartCoordinator(
        IHostApplicationLifetime lifetime,
        ILogger<ServiceRestartCoordinator> logger,
        Func<ServiceRestartCapability> probe,
        TimeSpan? stopDelay = null,
        Action<int>? exit = null)
    {
        _lifetime = lifetime;
        _logger = logger;
        _probe = probe;
        _stopDelay = stopDelay ?? TimeSpan.FromSeconds(1);
        _exit = exit ?? ExitProcess;
    }

    public ServiceRestartCapability Capability => _probe();

    public bool IsRestartScheduled => Volatile.Read(ref _scheduled) == 1;

    /// <summary>
    /// Schedules the restart after a short delay so the HTTP response can still be written.
    /// Returns false when the capability is missing; repeated calls schedule only once.
    /// </summary>
    public bool TryScheduleRestart(string requestedBy)
    {
        if (!Capability.Available) return false;
        if (Interlocked.Exchange(ref _scheduled, 1) == 1) return true;

        _logger.LogWarning("Service restart requested by {User}; stopping the host in {Delay}.", requestedBy, _stopDelay);
        _lifetime.ApplicationStopped.Register(() => _exit(RestartExitCode));
        _ = Task.Run(async () =>
        {
            await Task.Delay(_stopDelay);
            _lifetime.StopApplication();
        });
        return true;
    }

    private static void ExitProcess(int code)
    {
        Log.CloseAndFlush();
        Environment.Exit(code);
    }
}
