namespace NodePilot.Api.Dtos;

public record CreateMachineRequest(string Name, string Hostname, int WinRmPort = 5985, bool UseSsl = false, Guid? DefaultCredentialId = null, string? Tags = null);
public record UpdateMachineRequest(string Name, string Hostname, int WinRmPort, bool UseSsl, Guid? DefaultCredentialId, string? Tags);
public record TestConnectionRequest(Guid? CredentialId);

public record MachineResponse(
    Guid Id, string Name, string Hostname, int WinRmPort, bool UseSsl,
    Guid? DefaultCredentialId, string? Tags, DateTime? LastConnectivityCheck, bool IsReachable,
    // Operational stats — computed per request from workflows + step executions.
    // UsedByWorkflowCount: distinct workflows whose definition references this machine
    // (via any node's data.targetMachineId). Drives the "check before deleting"
    // signal on the machines list.
    int UsedByWorkflowCount,
    // RecentStepCount / RecentFailedStepCount: step executions in the last 7 days
    // whose resolved target matches this machine's Id. Surfaces a success-rate cell
    // analogous to WorkflowsPage so operators see "this host has 18/20 ok last week".
    int RecentStepCount,
    int RecentFailedStepCount,
    // ActiveRunCount: step executions currently in Running state targeting this
    // machine. Operators check this before disabling/rebooting a host. 0 = idle.
    int ActiveRunCount);
