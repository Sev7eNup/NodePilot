using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Security;
using NodePilot.Api.Telemetry;
using NodePilot.Core.Interfaces;
using NodePilot.Data;

namespace NodePilot.Api.Hubs;

// All execution streams are sensitive: step output/error can contain secrets passed via
// workflow variables or leaked by a script. Require authentication for every hub method.
// Output is already run through <c>OutputRedactor</c> before being broadcast so the
// surface area here is bounded, but we still cap how many groups a single connection
// can join to prevent a misbehaving (or compromised) client from hoovering up every
// execution stream at once.
[Authorize]
public class ExecutionHub : Hub
{
    // Cap per connection. A normal UI user joins one execution group when they open a
    // detail view and maybe a handful of workflow groups when browsing lists — 50 is
    // well above legitimate usage while small enough to stop mass-subscription.
    private const int MaxGroupsPerConnection = 50;

    private readonly NodePilotDbContext _db;
    private readonly IResourceAuthorizationService _authz;

    public ExecutionHub(NodePilotDbContext db, IResourceAuthorizationService authz)
    {
        _db = db;
        _authz = authz;
    }

    // ConnectionId → set of group names the connection has joined. ConcurrentDictionary
    // is safe for the hub lifetime; entries are cleaned up in OnDisconnectedAsync.
    private static readonly ConcurrentDictionary<string, HashSet<string>> _joinedGroups = new();
    private static readonly ConcurrentDictionary<string, int> _groupSubscriberCounts = new(StringComparer.Ordinal);

    // ConnectionId → the RBAC scope captured when the connection joined the live-ops "NOC"
    // feed. The ops-feed is NOT a SignalR group: a flat group cannot be filtered per-user at
    // broadcast time, which would leak the existence + status of out-of-scope workflows to
    // every authenticated viewer. Instead we record the accessible-folder set per connection
    // and the notifier resolves the matching connection ids for each event's workflow folder,
    // sending only to those. Scope is a snapshot taken at join (consistent with how
    // JoinExecution/JoinWorkflow resolve RBAC once); folder/permission changes mid-connection
    // are not re-evaluated, and the only thing carried on this feed is a status transition
    // (no step output), so the residual exposure is bounded.
    private static readonly ConcurrentDictionary<string, OpsFeedScope> _opsFeed = new();

    internal sealed record OpsFeedScope(bool IsUnrestricted, HashSet<Guid> FolderIds);

    // ConnectionId → { Jti, UserId, Context }. Used by the background job that periodically
    // sweeps for revoked tokens / deactivated users, so it can find and disconnect any live
    // SignalR connections that are still authenticated with a now-invalid token or account.
    // We stash the full HubCallerContext so the sweeper can call
    // Abort() — HubLifetimeManager does not expose a public server-initiated disconnect,
    // so this is the supported path to actually hang up the transport. Cleared in
    // OnDisconnectedAsync.
    private static readonly ConcurrentDictionary<string, ConnectionAuthEntry> _connectionAuth = new();

    internal sealed record ConnectionAuthEntry(
        string? Jti,
        Guid UserId,
        int SecurityStamp,
        Guid? SessionId,
        DateTime TokenExpiresAt,
        HubCallerContext Context);

    internal sealed record ServerSessionState(
        Guid UserId,
        string CurrentJti,
        DateTime ExpiresAt,
        DateTime? RevokedAt);

    /// <summary>
    /// Iterates every live connection and returns those whose jti is in <paramref name="revokedJtis"/>
    /// or whose user id is in <paramref name="deactivatedUsers"/>. The sweeper calls this,
    /// then iterates <see cref="TryGetContext"/> to call <c>Abort()</c> on each. Living in
    /// the hub keeps the auth map private to the type that owns it.
    /// </summary>
    internal static IEnumerable<string> FindConnectionsToDrop(
        HashSet<string> revokedJtis,
        HashSet<Guid> deactivatedUsers,
        IReadOnlyDictionary<Guid, int>? currentSecurityStamps = null,
        IReadOnlyDictionary<Guid, ServerSessionState>? serverSessions = null,
        DateTime? validityDeadline = null)
    {
        foreach (var (connId, entry) in _connectionAuth)
        {
            if (entry.Jti is not null && revokedJtis.Contains(entry.Jti)) yield return connId;
            else if (deactivatedUsers.Contains(entry.UserId)) yield return connId;
            else if (currentSecurityStamps is not null
                     && currentSecurityStamps.TryGetValue(entry.UserId, out var currentStamp)
                     && currentStamp != entry.SecurityStamp)
                yield return connId;
            else if (validityDeadline is { } deadline
                     && entry.TokenExpiresAt <= deadline)
                yield return connId;
            else if (serverSessions is not null && entry.SessionId is { } sessionId
                     && (!serverSessions.TryGetValue(sessionId, out var session)
                         || session.UserId != entry.UserId
                         || session.RevokedAt is not null
                         || session.ExpiresAt <= (validityDeadline ?? DateTime.UtcNow)
                         || !string.Equals(session.CurrentJti, entry.Jti, StringComparison.Ordinal)))
                yield return connId;
        }
    }

    /// <summary>
    /// Look up the live <see cref="HubCallerContext"/> for a connection id so the sweeper
    /// can abort the underlying transport. Returns null if the connection has already closed.
    /// </summary>
    internal static HubCallerContext? TryGetContext(string connectionId)
        => _connectionAuth.TryGetValue(connectionId, out var entry) ? entry.Context : null;

    internal static IReadOnlyCollection<Guid> GetConnectedUserIds()
        => _connectionAuth.Values.Select(e => e.UserId).Distinct().ToArray();

    internal static IReadOnlyCollection<Guid> GetConnectedSessionIds()
        => _connectionAuth.Values.Where(entry => entry.SessionId is not null)
            .Select(entry => entry.SessionId!.Value).Distinct().ToArray();

    /// <summary>
    /// Remove a connection from the auth map after the sweeper has aborted it. Prevents a
    /// subsequent sweep from trying to abort an already-dead connection (which is a no-op
    /// but pollutes the debug log).
    /// </summary>
    internal static void ForgetConnection(string connectionId)
        => _connectionAuth.TryRemove(connectionId, out _);

    /// <summary>
    /// Test seam: register a synthetic connection in the auth map. Real connections are
    /// registered via <see cref="OnConnectedAsync"/> when the hub accepts a handshake;
    /// tests can't drive that path without spinning up the SignalR runtime, so this hook
    /// lets <c>HubRevocationSweeperTests</c> seed the map directly.
    /// </summary>
    internal static void RegisterAuthForTest(
        string connectionId, string? jti, Guid userId, HubCallerContext context,
        int securityStamp = 0,
        Guid? sessionId = null,
        DateTime? tokenExpiresAt = null)
        => _connectionAuth[connectionId] = new ConnectionAuthEntry(
            jti, userId, securityStamp, sessionId, tokenExpiresAt ?? DateTime.MaxValue, context);

    /// <summary>Test seam: drop every entry in the auth map. Tests reset between cases.</summary>
    internal static void ClearAuthMapForTest() => _connectionAuth.Clear();

    internal static bool HasSubscribers(Guid executionId, Guid workflowId)
        => HasGroupSubscribers(executionId.ToString()) || HasGroupSubscribers($"workflow-{workflowId}");

    internal static bool HasGroupSubscribers(string groupName)
        => _groupSubscriberCounts.TryGetValue(groupName, out var count) && count > 0;

    /// <summary>True when at least one connection is watching the live-ops feed.</summary>
    internal static bool HasOpsFeedSubscribers() => !_opsFeed.IsEmpty;

    /// <summary>
    /// Returns the ops-feed connection ids whose captured RBAC scope permits seeing a workflow
    /// that lives in <paramref name="folderId"/>. This is the broadcast-time RBAC filter that
    /// keeps the live-ops feed from leaking out-of-scope workflow status to viewers who only
    /// have access to other folders.
    /// </summary>
    internal static IReadOnlyList<string> GetOpsFeedConnections(Guid folderId)
    {
        if (_opsFeed.IsEmpty) return [];
        var result = new List<string>();
        foreach (var (connId, scope) in _opsFeed)
        {
            if (scope.IsUnrestricted || scope.FolderIds.Contains(folderId))
                result.Add(connId);
        }
        return result;
    }

    internal static void RegisterOpsFeedForTest(string connectionId, bool unrestricted, HashSet<Guid> folderIds)
        => _opsFeed[connectionId] = new OpsFeedScope(unrestricted, folderIds);

    internal static void ClearOpsFeedForTest() => _opsFeed.Clear();

    internal static void RegisterGroupForTest(string connectionId, string groupName)
    {
        var set = _joinedGroups.GetOrAdd(connectionId, _ => new HashSet<string>(StringComparer.Ordinal));
        lock (set)
        {
            if (set.Add(groupName))
                IncrementGroupSubscriber(groupName);
        }
    }

    internal static void ClearGroupsForTest()
    {
        _joinedGroups.Clear();
        _groupSubscriberCounts.Clear();
    }

    private static void IncrementGroupSubscriber(string groupName)
        => _groupSubscriberCounts.AddOrUpdate(groupName, 1, (_, count) => count + 1);

    private static void DecrementGroupSubscriber(string groupName)
    {
        _groupSubscriberCounts.AddOrUpdate(groupName, 0, (_, count) => count <= 1 ? 0 : count - 1);
        if (_groupSubscriberCounts.TryGetValue(groupName, out var remaining) && remaining <= 0)
            _groupSubscriberCounts.TryRemove(groupName, out _);
    }

    private static bool TryRegisterGroup(string connectionId, string groupName)
    {
        var set = _joinedGroups.GetOrAdd(connectionId, _ => new HashSet<string>(StringComparer.Ordinal));
        lock (set)
        {
            if (set.Contains(groupName)) return true;      // already in → no-op, not a new slot
            if (set.Count >= MaxGroupsPerConnection) return false;
            set.Add(groupName);
            IncrementGroupSubscriber(groupName);
            return true;
        }
    }

    private static void UnregisterGroup(string connectionId, string groupName)
    {
        if (_joinedGroups.TryGetValue(connectionId, out var set))
        {
            lock (set)
            {
                if (set.Remove(groupName))
                    DecrementGroupSubscriber(groupName);
            }
        }
    }

    public async Task<object> JoinExecution(string executionId)
    {
        // Validate shape — executionId is used as a SignalR group name. Reject anything that
        // is not a GUID so callers cannot join a group by spelling it however they like.
        if (!Guid.TryParse(executionId, out var parsed))
            throw new HubException("executionId must be a GUID");

        // RBAC: prevent connection-time leak of execution events. Resolve the execution's
        // workflow folder, then check Read against the caller. Reject with HubException
        // (the SignalR client receives a clean error event) rather than silently joining
        // and then failing on every Send — the latter would still leak presence info.
        if (Context.User is null) throw new HubException("not authenticated");
        var folderId = await _db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Id == parsed)
            .Select(e => (Guid?)e.Workflow.FolderId)
            .FirstOrDefaultAsync();
        if (folderId is null) throw new HubException("execution not found");
        if (!await _authz.CanAccessWorkflowAsync(Context.User, folderId.Value, ResourceOp.Read))
            throw new HubException("execution not found");  // mask existence

        var group = parsed.ToString();
        if (!TryRegisterGroup(Context.ConnectionId, group))
            throw new HubException($"too many active subscriptions (max {MaxGroupsPerConnection})");
        try
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, group);
        }
        catch
        {
            UnregisterGroup(Context.ConnectionId, group);
            throw;
        }
        return new { executionId = group };
    }

    public async Task LeaveExecution(string executionId)
    {
        if (!Guid.TryParse(executionId, out var parsed)) return;
        var group = parsed.ToString();
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group);
        UnregisterGroup(Context.ConnectionId, group);
    }

    /// <summary>Join a workflow channel to receive all execution updates for that workflow.</summary>
    public async Task<object> JoinWorkflow(string workflowId)
    {
        if (!Guid.TryParse(workflowId, out var parsed))
            throw new HubException("workflowId must be a GUID");

        // RBAC: same gate as JoinExecution — Read on the workflow's folder. Mask
        // existence with the same error message so id-probing via the hub is blocked.
        if (Context.User is null) throw new HubException("not authenticated");
        var folderId = await _db.Workflows.AsNoTracking()
            .Where(w => w.Id == parsed)
            .Select(w => (Guid?)w.FolderId)
            .FirstOrDefaultAsync();
        if (folderId is null) throw new HubException("workflow not found");
        if (!await _authz.CanAccessWorkflowAsync(Context.User, folderId.Value, ResourceOp.Read))
            throw new HubException("workflow not found");

        var group = $"workflow-{parsed}";
        if (!TryRegisterGroup(Context.ConnectionId, group))
            throw new HubException($"too many active subscriptions (max {MaxGroupsPerConnection})");
        try
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, group);
        }
        catch
        {
            UnregisterGroup(Context.ConnectionId, group);
            throw;
        }
        return new { workflowId = parsed.ToString() };
    }

    public async Task LeaveWorkflow(string workflowId)
    {
        if (!Guid.TryParse(workflowId, out var parsed)) return;
        var group = $"workflow-{parsed}";
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group);
        UnregisterGroup(Context.ConnectionId, group);
    }

    /// <summary>
    /// Subscribe to the live-ops "NOC" feed: status transitions for every workflow the caller
    /// can read. RBAC is resolved once here (the caller's accessible-folder set is captured)
    /// and enforced again at broadcast time by <see cref="GetOpsFeedConnections"/> — there is no
    /// flat group, so an event for a workflow outside the caller's folders never reaches them.
    /// A caller with zero accessible folders is rejected (they would receive nothing anyway).
    /// </summary>
    public async Task<object> JoinOperationsFeed()
    {
        if (Context.User is null) throw new HubException("not authenticated");
        var accessible = await _authz.GetAccessibleFolderIdsAsync(Context.User);
        if (!accessible.IsUnrestricted && accessible.FolderIds.Count == 0)
            throw new HubException("no accessible workflows");

        _opsFeed[Context.ConnectionId] = new OpsFeedScope(accessible.IsUnrestricted, accessible.FolderIds);
        return new { subscribed = true };
    }

    public Task LeaveOperationsFeed()
    {
        _opsFeed.TryRemove(Context.ConnectionId, out _);
        return Task.CompletedTask;
    }

    public override Task OnConnectedAsync()
    {
        ApiMetrics.SignalRConnectionsActive.Add(1);
        // Register the caller's jti + user id so the revocation sweeper can find us later.
        // Long-lived WebSocket connections authenticate once at handshake; without this map
        // a revoked or disabled user would keep receiving live step output for hours until
        // the 12 h JWT lifetime finally forced a reconnect.
        var jti = Context.User?.FindFirstValue(JwtRegisteredClaimNames.Jti);
        var uidStr = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        var stampStr = Context.User?.FindFirstValue("np_secstamp");
        var sessionStr = Context.User?.FindFirstValue(AuthSessionIssuer.SessionIdClaim);
        var expStr = Context.User?.FindFirstValue(JwtRegisteredClaimNames.Exp);
        _ = int.TryParse(stampStr, out var securityStamp);
        var sessionId = Guid.TryParse(sessionStr, out var parsedSession)
            ? parsedSession
            : Guid.Empty;
        var tokenExpiresAt = long.TryParse(expStr, out var expSeconds)
            ? DateTimeOffset.FromUnixTimeSeconds(expSeconds).UtcDateTime
            : DateTime.MinValue;
        if (Guid.TryParse(uidStr, out var uid))
            _connectionAuth[Context.ConnectionId] = new ConnectionAuthEntry(
                jti, uid, securityStamp, sessionId, tokenExpiresAt, Context);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        ApiMetrics.SignalRConnectionsActive.Add(-1);
        if (_joinedGroups.TryRemove(Context.ConnectionId, out var groups))
        {
            lock (groups)
                foreach (var group in groups)
                    DecrementGroupSubscriber(group);
        }
        _connectionAuth.TryRemove(Context.ConnectionId, out _);
        _opsFeed.TryRemove(Context.ConnectionId, out _);
        return base.OnDisconnectedAsync(exception);
    }
}

// DTOs broadcast via IHubContext from the engine.
// TraceId/SpanId are populated from Activity.Current when OpenTelemetry is enabled,
// so the UI can deep-link from a live step update directly to its trace.
public record StepStartedEvent(
    Guid ExecutionId, Guid WorkflowId, string StepId, string? StepName, string StepType, DateTime StartedAt,
    string? TraceId = null, string? SpanId = null);

public record StepCompletedEvent(
    Guid ExecutionId, Guid WorkflowId, string StepId, string? StepName, string Status,
    string? Output, string? ErrorOutput, DateTime CompletedAt,
    string? TraceId = null, string? SpanId = null,
    Dictionary<string, string>? OutputParameters = null,
    string? TraceOutput = null,
    string? StepType = null,
    DateTime? StartedAt = null,
    string? OutputVariable = null);

public record ExecutionStatusEvent(
    Guid ExecutionId, Guid WorkflowId, string Status, string? ErrorMessage, DateTime? CompletedAt,
    string? TraceId = null);

/// <summary>Sent when a debug run hits a breakpoint and pauses. <c>Variables</c> is a
/// snapshot of the variable dictionary at pause time (already masked by OutputRedactor).
/// <c>Reason</c> is <c>"breakpoint"</c> or <c>"stepOver"</c>.</summary>
public record StepPausedEvent(
    Guid ExecutionId, Guid WorkflowId, string StepId, string? StepName,
    Dictionary<string, string> Variables, DateTime PausedAt, string Reason);

/// <summary>Sent after a paused step resumes, so the frontend can clear its debug overlay.</summary>
public record StepResumedEvent(Guid ExecutionId, Guid WorkflowId, string StepId);

public record LiveEventBatchItem(string Type, object Event);

public record LiveEventsBatch(IReadOnlyList<LiveEventBatchItem> Events);
