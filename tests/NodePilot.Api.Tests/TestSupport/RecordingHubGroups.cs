using Microsoft.AspNetCore.SignalR;
using NodePilot.Api.Hubs;

namespace NodePilot.Api.Tests.TestSupport;

/// <summary>
/// <see cref="IHubContext{THub}"/> double that records group add/remove calls, for tests
/// where the assertion is "which subscriptions were revoked" rather than anything about
/// message delivery. <see cref="Clients"/> is deliberately not implemented and throws if a
/// test ever reaches for it.
/// </summary>
public sealed class RecordingHubContext : IHubContext<ExecutionHub>
{
    private readonly RecordingGroupManager _groups = new();

    public IHubClients Clients =>
        throw new NotSupportedException("RecordingHubContext only models group membership.");

    public IGroupManager Groups => _groups;

    /// <summary>(connectionId, groupName) pairs passed to RemoveFromGroupAsync, in call
    /// order.</summary>
    public IReadOnlyList<(string ConnectionId, string Group)> Removed => _groups.Removed;

    /// <summary>(connectionId, groupName) pairs passed to AddToGroupAsync, in call order.</summary>
    public IReadOnlyList<(string ConnectionId, string Group)> Added => _groups.Added;

    private sealed class RecordingGroupManager : IGroupManager
    {
        private readonly List<(string, string)> _added = [];
        private readonly List<(string, string)> _removed = [];

        public IReadOnlyList<(string ConnectionId, string Group)> Added
        {
            get { lock (_added) return _added.ToArray(); }
        }

        public IReadOnlyList<(string ConnectionId, string Group)> Removed
        {
            get { lock (_removed) return _removed.ToArray(); }
        }

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            lock (_added) _added.Add((connectionId, groupName));
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            lock (_removed) _removed.Add((connectionId, groupName));
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// Records the workflow ids whose cached folder projection was invalidated.
/// </summary>
public sealed class RecordingFolderProjection : IWorkflowFolderProjection
{
    private readonly List<Guid> _invalidated = [];

    public IReadOnlyList<Guid> Invalidated
    {
        get { lock (_invalidated) return _invalidated.ToArray(); }
    }

    public void InvalidateWorkflowFolder(Guid workflowId)
    {
        lock (_invalidated) _invalidated.Add(workflowId);
    }
}
