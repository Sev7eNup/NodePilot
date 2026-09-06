using NodePilot.Switcher.Configuration;
using NodePilot.Switcher.Models;
using NodePilot.Switcher.Services;

namespace NodePilot.Switcher.Tests;

internal sealed class FakeServiceControlGateway : IServiceControlGateway
{
    public Dictionary<string, ServiceSnapshot> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Operations { get; } = [];
    public string? FailStart { get; set; }
    public string? FailStop { get; set; }

    public ServiceSnapshot? TryGetService(string serviceName) =>
        Services.TryGetValue(serviceName, out var service) ? service : null;

    public Task SetStartModeAsync(
        string serviceName,
        ServiceStartMode mode,
        bool delayedAutoStart,
        CancellationToken cancellationToken)
    {
        Operations.Add($"mode:{serviceName}:{mode}:{delayedAutoStart}");
        Services[serviceName] = Services[serviceName] with
        {
            StartMode = mode,
            DelayedAutoStart = mode == ServiceStartMode.Automatic && delayedAutoStart,
        };
        return Task.CompletedTask;
    }

    public Task StartAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Operations.Add($"start:{serviceName}");
        if (serviceName.Equals(FailStart, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"start failed: {serviceName}");
        Services[serviceName] = Services[serviceName] with { State = ServiceRuntimeState.Running, ProcessId = 100 };
        return Task.CompletedTask;
    }

    public Task StopAsync(
        string serviceName,
        TimeSpan gracefulTimeout,
        TimeSpan forcedTimeout,
        CancellationToken cancellationToken)
    {
        Operations.Add($"stop:{serviceName}");
        if (serviceName.Equals(FailStop, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"stop failed: {serviceName}");
        Services[serviceName] = Services[serviceName] with { State = ServiceRuntimeState.Stopped, ProcessId = 0 };
        return Task.CompletedTask;
    }

    public Task ForceStopAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Operations.Add($"force-stop:{serviceName}");
        if (serviceName.Equals(FailStop, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"force stop failed: {serviceName}");
        Services[serviceName] = Services[serviceName] with { State = ServiceRuntimeState.Stopped, ProcessId = 0 };
        return Task.CompletedTask;
    }
}

internal sealed class RecordingLogger : IActivityLogger
{
    private readonly List<ActivityEntry> _entries = [];
    public event EventHandler<ActivityEntry>? EntryWritten;
    public IReadOnlyList<ActivityEntry> Entries => _entries.ToArray();
    public void Info(string message, string? serviceName = null) => Write("INFO", message, serviceName);
    public void Success(string message, string? serviceName = null) => Write("SUCCESS", message, serviceName);
    public void Error(string message, string? serviceName = null) => Write("ERROR", message, serviceName);
    private void Write(string level, string message, string? serviceName)
    {
        var entry = new ActivityEntry(DateTimeOffset.Now, level, message, serviceName);
        _entries.Add(entry);
        EntryWritten?.Invoke(this, entry);
    }
}

internal sealed class FakeProcessPresenceProbe(params string[] runningProcesses) : IProcessPresenceProbe
{
    private readonly HashSet<string> _running = new(runningProcesses, StringComparer.OrdinalIgnoreCase);
    public bool IsRunning(string processName) => _running.Contains(processName);
}

internal sealed class NoOpWorkloadReconciler : IWorkloadReconciler
{
    private static readonly SwitcherConfiguration Configuration = new(
        new NodePilotWorkloadConfiguration(string.Empty, string.Empty),
        new ScorchWorkloadConfiguration(string.Empty, "http://localhost"));

    public Task<WorkloadSwitchPlan> PreflightAsync(SwitchTarget target, CancellationToken cancellationToken) =>
        Task.FromResult(new WorkloadSwitchPlan(target, Configuration, []));

    public Task DeactivateSourceAsync(
        WorkloadSwitchPlan plan,
        IProgress<SwitchProgress>? progress,
        Action onMutationStarted,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ReconcileAsync(
        WorkloadSwitchPlan plan,
        IProgress<SwitchProgress>? progress,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class FakeConfigurationProbe : ISwitcherConfigurationProbe
{
    public string? Error { get; set; }
    public int Calls { get; private set; }

    public string? Probe()
    {
        Calls++;
        return Error;
    }
}

internal static class TestServices
{
    public static ServiceSnapshot Service(
        string name,
        ServiceRuntimeState state = ServiceRuntimeState.Stopped,
        ServiceStartMode startMode = ServiceStartMode.Manual,
        bool delayed = false,
        params string[] dependencies) => new(
        name,
        name.Equals("NodePilot", StringComparison.OrdinalIgnoreCase)
            ? @"""C:\Program Files\NodePilot\NodePilot.Api.exe"""
            : $@"C:\Program Files\Microsoft System Center\{name}.exe",
        state,
        startMode,
        delayed,
        state == ServiceRuntimeState.Running ? 100 : 0,
        dependencies);

    public static SwitcherOptions FastOptions => new(
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero);
}
