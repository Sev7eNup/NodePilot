using NodePilot.ServiceSwitcher.Models;

namespace NodePilot.ServiceSwitcher.Services;

internal sealed class SwitchCoordinator
{
    private static readonly HashSet<string> ImmediateForcedStopServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "omonitor",
        "oremoting",
    };

    private readonly IServiceControlGateway _gateway;
    private readonly ServiceDiscovery _discovery;
    private readonly IActivityLogger _logger;
    private readonly IWorkloadReconciler _workloads;
    private readonly IProcessPresenceProbe _processes;
    private readonly SwitcherOptions _options;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public SwitchCoordinator(
        IServiceControlGateway gateway,
        ServiceDiscovery discovery,
        IActivityLogger logger,
        IWorkloadReconciler workloads,
        SwitcherOptions? options = null,
        IProcessPresenceProbe? processes = null)
    {
        _gateway = gateway;
        _discovery = discovery;
        _logger = logger;
        _options = options ?? SwitcherOptions.Default;
        _workloads = workloads;
        _processes = processes ?? new WindowsProcessPresenceProbe();
    }

    public ManagedEnvironmentSnapshot Refresh() => _discovery.Discover();

    public async Task<SwitchResult> SwitchAsync(
        SwitchTarget target,
        IProgress<SwitchProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ManagedEnvironmentSnapshot? initial = null;
        var serviceMutationStarted = false;
        try
        {
            progress?.Report(new SwitchProgress(SwitchProgressKind.Preparing));
            initial = _discovery.Discover();
            EnsureTargetAvailable(initial, target);
            EnsureNoConflictingClient(target);
            _logger.Info($"Switch to {TargetName(target)} requested by {Environment.UserDomainName}\\{Environment.UserName} on {Environment.MachineName}.");

            progress?.Report(new SwitchProgress(SwitchProgressKind.LoadingAllowList));
            var workloadPlan = await _workloads.PreflightAsync(target, cancellationToken).ConfigureAwait(false);

            var allServices = initial.AllServices.ToArray();
            serviceMutationStarted = true;
            await _workloads.DeactivateSourceAsync(workloadPlan, progress, cancellationToken).ConfigureAwait(false);
            foreach (var service in allServices)
            {
                progress?.Report(new SwitchProgress(SwitchProgressKind.SettingManual, service.Name));
                await _gateway.SetStartModeAsync(
                    service.Name,
                    ServiceStartMode.Manual,
                    delayedAutoStart: false,
                    cancellationToken).ConfigureAwait(false);
                _logger.Info("Start mode set to Manual.", service.Name);
            }

            var source = target == SwitchTarget.NodePilot
                ? initial.SystemCenterServices
                : initial.NodePilot is null ? [] : [initial.NodePilot];

            foreach (var service in OrderByDependencies(source, dependenciesFirst: false))
            {
                progress?.Report(new SwitchProgress(SwitchProgressKind.Stopping, service.Name));
                if (RequiresImmediateForcedStop(service.Name))
                {
                    _logger.Info("Force-stopping service due to the known SCOrch shutdown issue.", service.Name);
                    await _gateway.ForceStopAsync(
                        service.Name,
                        _options.ForcedStopTimeout,
                        cancellationToken).ConfigureAwait(false);
                    _logger.Info("Service process terminated and service stopped.", service.Name);
                }
                else
                {
                    _logger.Info("Stopping service.", service.Name);
                    await _gateway.StopAsync(
                        service.Name,
                        _options.GracefulStopTimeout,
                        _options.ForcedStopTimeout,
                        cancellationToken).ConfigureAwait(false);
                    _logger.Info("Service stopped.", service.Name);
                }
            }

            var targetServices = target == SwitchTarget.NodePilot
                ? new[] { initial.NodePilot! }
                : initial.SystemCenterServices;

            foreach (var service in OrderByDependencies(targetServices, dependenciesFirst: true))
            {
                progress?.Report(new SwitchProgress(SwitchProgressKind.SettingAutomatic, service.Name));
                await _gateway.SetStartModeAsync(
                    service.Name,
                    ServiceStartMode.Automatic,
                    delayedAutoStart: target == SwitchTarget.NodePilot,
                    cancellationToken).ConfigureAwait(false);
                _logger.Info(
                    target == SwitchTarget.NodePilot
                        ? "Start mode set to Automatic (Delayed)."
                        : "Start mode set to Automatic.",
                    service.Name);

                progress?.Report(new SwitchProgress(SwitchProgressKind.Starting, service.Name));
                _logger.Info("Starting service.", service.Name);
                await _gateway.StartAsync(service.Name, _options.StartTimeout, cancellationToken).ConfigureAwait(false);
                _logger.Info("Service reached Running.", service.Name);
            }

            await _workloads.ReconcileAsync(workloadPlan, progress, cancellationToken).ConfigureAwait(false);

            progress?.Report(new SwitchProgress(SwitchProgressKind.Verifying));
            VerifyFinalState(target, targetServices, source);
            var final = _discovery.Discover();
            progress?.Report(new SwitchProgress(SwitchProgressKind.Completed));
            _logger.Success($"Switch to {TargetName(target)} completed successfully.");
            return new SwitchResult(true, final);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Error($"Switch failed: {exception.Message}");
            if (initial is not null && serviceMutationStarted)
            {
                progress?.Report(new SwitchProgress(SwitchProgressKind.FailClosed));
                await FailClosedAsync(initial.AllServices.ToArray()).ConfigureAwait(false);
            }

            ManagedEnvironmentSnapshot final;
            try { final = _discovery.Discover(); }
            catch { final = initial ?? new ManagedEnvironmentSnapshot(null, []); }
            return new SwitchResult(false, final, exception.Message);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void VerifyFinalState(
        SwitchTarget target,
        IReadOnlyList<ServiceSnapshot> targetServices,
        IReadOnlyList<ServiceSnapshot> sourceServices)
    {
        foreach (var expected in targetServices)
        {
            var current = _gateway.TryGetService(expected.Name)
                          ?? throw new InvalidOperationException($"Target service '{expected.Name}' disappeared.");
            if (current.State != ServiceRuntimeState.Running)
                throw new InvalidOperationException($"Target service '{expected.Name}' is {current.State} during final verification.");
            if (current.StartMode != ServiceStartMode.Automatic)
                throw new InvalidOperationException($"Target service '{expected.Name}' is not configured for automatic start.");
            if (target == SwitchTarget.NodePilot && !current.DelayedAutoStart)
                throw new InvalidOperationException("NodePilot is not configured for delayed automatic start.");
        }

        foreach (var expected in sourceServices)
        {
            var current = _gateway.TryGetService(expected.Name);
            if (current is null) continue;
            if (current.State != ServiceRuntimeState.Stopped)
                throw new InvalidOperationException($"Source service '{expected.Name}' returned to {current.State}.");
            if (current.StartMode != ServiceStartMode.Manual)
                throw new InvalidOperationException($"Source service '{expected.Name}' is not configured for manual start.");
        }

        _logger.Info($"{TargetName(target)} service and workload state verified.");
    }

    private async Task FailClosedAsync(IReadOnlyList<ServiceSnapshot> services)
    {
        foreach (var service in services)
        {
            try
            {
                await _gateway.SetStartModeAsync(
                    service.Name,
                    ServiceStartMode.Manual,
                    delayedAutoStart: false,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.Error($"Fail-closed start-mode update failed: {exception.Message}", service.Name);
            }
        }

        foreach (var service in OrderByDependencies(services, dependenciesFirst: false))
        {
            try
            {
                if (RequiresImmediateForcedStop(service.Name))
                {
                    await _gateway.ForceStopAsync(
                        service.Name,
                        _options.ForcedStopTimeout,
                        CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await _gateway.StopAsync(
                        service.Name,
                        _options.GracefulStopTimeout,
                        _options.ForcedStopTimeout,
                        CancellationToken.None).ConfigureAwait(false);
                }
                _logger.Info("Fail-closed cleanup stopped service.", service.Name);
            }
            catch (Exception exception)
            {
                _logger.Error($"Fail-closed stop failed: {exception.Message}", service.Name);
            }
        }
    }

    internal static IReadOnlyList<ServiceSnapshot> OrderByDependencies(
        IReadOnlyList<ServiceSnapshot> services,
        bool dependenciesFirst)
    {
        var byName = services.ToDictionary(service => service.Name, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<ServiceSnapshot>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(ServiceSnapshot service)
        {
            if (visited.Contains(service.Name)) return;
            if (!visiting.Add(service.Name)) return;
            foreach (var dependencyName in service.Dependencies)
                if (byName.TryGetValue(dependencyName, out var dependency)) Visit(dependency);
            visiting.Remove(service.Name);
            visited.Add(service.Name);
            ordered.Add(service);
        }

        foreach (var service in services) Visit(service);
        if (!dependenciesFirst) ordered.Reverse();
        return ordered;
    }

    private static void EnsureTargetAvailable(ManagedEnvironmentSnapshot snapshot, SwitchTarget target)
    {
        if (target == SwitchTarget.NodePilot && snapshot.NodePilot is null)
            throw new InvalidOperationException("No valid local NodePilot service was found.");
        if (target == SwitchTarget.SystemCenterOrchestrator && snapshot.SystemCenterServices.Count == 0)
            throw new InvalidOperationException("No supported local System Center Orchestrator service was found.");
    }

    private void EnsureNoConflictingClient(SwitchTarget target)
    {
        if (target == SwitchTarget.NodePilot && _processes.IsRunning("RunbookDesigner"))
        {
            throw new InvalidOperationException(
                "System Center Orchestrator Runbook Designer is open. Save and close it before switching to NodePilot; while open it restarts 'omanagement'.");
        }
    }

    private static string TargetName(SwitchTarget target) =>
        target == SwitchTarget.NodePilot ? "NodePilot" : "System Center Orchestrator";

    private static bool RequiresImmediateForcedStop(string serviceName) =>
        ImmediateForcedStopServices.Contains(serviceName);
}
