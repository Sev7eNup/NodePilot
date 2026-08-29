namespace NodePilot.ServiceSwitcher.Models;

internal enum ServiceRuntimeState
{
    Unknown,
    Stopped,
    StartPending,
    StopPending,
    Running,
    ContinuePending,
    PausePending,
    Paused,
}

internal enum ServiceStartMode
{
    Unknown,
    Automatic,
    Manual,
    Disabled,
}

internal sealed record ServiceSnapshot(
    string Name,
    string BinaryPath,
    ServiceRuntimeState State,
    ServiceStartMode StartMode,
    bool DelayedAutoStart,
    int ProcessId,
    IReadOnlyList<string> Dependencies);

internal sealed record ManagedEnvironmentSnapshot(
    ServiceSnapshot? NodePilot,
    IReadOnlyList<ServiceSnapshot> SystemCenterServices)
{
    public IEnumerable<ServiceSnapshot> AllServices =>
        NodePilot is null ? SystemCenterServices : SystemCenterServices.Prepend(NodePilot);
}

internal enum SwitchTarget
{
    NodePilot,
    SystemCenterOrchestrator,
}

internal enum EnvironmentState
{
    Unavailable,
    NodePilotActive,
    SystemCenterActive,
    BothStopped,
    Conflict,
    SystemCenterPartial,
    Transitioning,
}

internal enum SwitchProgressKind
{
    Preparing,
    LoadingAllowList,
    SettingManual,
    Stopping,
    SettingAutomatic,
    Starting,
    ReconcilingWorkloads,
    Verifying,
    Completed,
    FailClosed,
}

internal sealed record SwitchProgress(SwitchProgressKind Kind, string? ServiceName = null);

internal sealed record SwitchResult(bool Succeeded, ManagedEnvironmentSnapshot Snapshot, string? Error = null);
