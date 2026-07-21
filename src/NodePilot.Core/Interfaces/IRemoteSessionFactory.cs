using NodePilot.Core.Models;

namespace NodePilot.Core.Interfaces;

public class RemoteExecutionResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public string ErrorOutput { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
}

public interface IRemoteSession : IAsyncDisposable
{
    /// <summary>
    /// Execute a PowerShell script on the remote target. <paramref name="timeoutSeconds"/>
    /// null = no timeout enforcement (script runs until completion or caller cancels via
    /// <paramref name="ct"/>). Implementations call ps.Stop() on cancellation.
    /// </summary>
    Task<RemoteExecutionResult> ExecuteScriptAsync(string script, int? timeoutSeconds = null, CancellationToken ct = default);
}

public interface IRemoteSessionFactory
{
    /// <summary>
    /// Create a WinRM session. If credential is null, the NodePilot process identity is used
    /// (implicit/integrated Windows authentication).
    /// </summary>
    Task<IRemoteSession> CreateSessionAsync(ManagedMachine machine, Credential? credential, CancellationToken ct);
}
