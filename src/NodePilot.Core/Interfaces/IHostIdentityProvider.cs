using NodePilot.Core.Models;

namespace NodePilot.Core.Interfaces;

/// <summary>
/// Supplies the <see cref="HostIdentity"/> of the machine the API runs on. Implementations
/// resolve this once from the local OS network configuration and cache it — the host name and
/// domain don't change over a process lifetime, so there's no need to re-read them per request.
/// </summary>
public interface IHostIdentityProvider
{
    /// <summary>The cached identity of the current host. Never throws.</summary>
    HostIdentity Current { get; }
}
