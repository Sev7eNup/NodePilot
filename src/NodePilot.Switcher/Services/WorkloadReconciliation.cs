using NodePilot.Switcher.Configuration;
using NodePilot.Switcher.Models;

namespace NodePilot.Switcher.Services;

internal sealed record WorkloadSwitchPlan(
    SwitchTarget Target,
    SwitcherConfiguration Configuration,
    IReadOnlyList<string> AllowList);

internal interface IWorkloadReconciler
{
    Task<WorkloadSwitchPlan> PreflightAsync(SwitchTarget target, CancellationToken cancellationToken);
    /// <param name="onMutationStarted">
    /// Called before the first change to the source workload, so the caller can tell a failed query
    /// from a partially completed deactivation.
    /// </param>
    Task DeactivateSourceAsync(
        WorkloadSwitchPlan plan,
        IProgress<SwitchProgress>? progress,
        Action onMutationStarted,
        CancellationToken cancellationToken);
    Task ReconcileAsync(
        WorkloadSwitchPlan plan,
        IProgress<SwitchProgress>? progress,
        CancellationToken cancellationToken);
}

internal sealed class WorkloadReconciler : IWorkloadReconciler
{
    private readonly SwitcherConfigurationLoader _configurationLoader;
    private readonly AllowListReader _allowListReader;
    private readonly NodePilotWorkflowReconciler _nodePilot;
    private readonly ScorchRunbookReconciler _scorch;
    private readonly IActivityLogger _logger;

    public WorkloadReconciler(
        SwitcherConfigurationLoader configurationLoader,
        AllowListReader allowListReader,
        NodePilotWorkflowReconciler nodePilot,
        ScorchRunbookReconciler scorch,
        IActivityLogger logger)
    {
        _configurationLoader = configurationLoader;
        _allowListReader = allowListReader;
        _nodePilot = nodePilot;
        _scorch = scorch;
        _logger = logger;
    }

    public async Task<WorkloadSwitchPlan> PreflightAsync(
        SwitchTarget target,
        CancellationToken cancellationToken)
    {
        var configuration = _configurationLoader.Load();
        if (target == SwitchTarget.NodePilot)
            SwitcherConfigurationValidator.ValidateNodePilot(configuration.NodePilot);
        else
            SwitcherConfigurationValidator.ValidateScorch(configuration.SystemCenterOrchestrator);
        var path = target == SwitchTarget.NodePilot
            ? configuration.NodePilot.WorkflowAllowListPath
            : configuration.SystemCenterOrchestrator.RunbookAllowListPath;
        var allowList = await _allowListReader.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        _logger.Info($"Allowlist loaded and validated: {allowList.Count} entries from {path}.");
        return new WorkloadSwitchPlan(target, configuration, allowList);
    }

    public Task ReconcileAsync(
        WorkloadSwitchPlan plan,
        IProgress<SwitchProgress>? progress,
        CancellationToken cancellationToken) =>
        plan.Target == SwitchTarget.NodePilot
            ? _nodePilot.ReconcileAsync(plan.Configuration.NodePilot, plan.AllowList, progress, cancellationToken)
            : _scorch.ReconcileAsync(plan.Configuration.SystemCenterOrchestrator, plan.AllowList, progress, cancellationToken);

    public Task DeactivateSourceAsync(
        WorkloadSwitchPlan plan,
        IProgress<SwitchProgress>? progress,
        Action onMutationStarted,
        CancellationToken cancellationToken) =>
        plan.Target == SwitchTarget.NodePilot
            ? _scorch.StopAllManagedJobsAsync(
                plan.Configuration.SystemCenterOrchestrator,
                progress,
                onMutationStarted,
                cancellationToken)
            : Task.CompletedTask;
}
