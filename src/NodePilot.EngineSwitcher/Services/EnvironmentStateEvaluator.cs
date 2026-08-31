using NodePilot.EngineSwitcher.Models;

namespace NodePilot.EngineSwitcher.Services;

internal static class EnvironmentStateEvaluator
{
    public static EnvironmentState Assess(ManagedEnvironmentSnapshot snapshot)
    {
        if (snapshot.NodePilot is null || snapshot.SystemCenterServices.Count == 0)
            return EnvironmentState.Unavailable;

        if (snapshot.AllServices.Any(service => service.State is
                ServiceRuntimeState.StartPending or
                ServiceRuntimeState.StopPending or
                ServiceRuntimeState.ContinuePending or
                ServiceRuntimeState.PausePending))
            return EnvironmentState.Transitioning;

        var nodePilotRunning = snapshot.NodePilot.State == ServiceRuntimeState.Running;
        var scorchRunning = snapshot.SystemCenterServices.Count(service =>
            service.State == ServiceRuntimeState.Running);

        if (nodePilotRunning && scorchRunning > 0)
            return EnvironmentState.Conflict;
        if (nodePilotRunning)
            return EnvironmentState.NodePilotActive;
        if (scorchRunning == snapshot.SystemCenterServices.Count)
            return EnvironmentState.SystemCenterActive;
        if (scorchRunning > 0)
            return EnvironmentState.SystemCenterPartial;
        return EnvironmentState.BothStopped;
    }
}
