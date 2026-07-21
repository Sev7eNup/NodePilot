using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Api.Security;

/// <summary>
/// Resolves the effective principal of a workflow run and checks whether they may invoke
/// a sub-workflow in a different folder. Defense-in-Depth backstop for the publish-time
/// PrePublishChecklist check — folder permissions can change between publish and run, and
/// trigger-driven runs need a fresh check at fire time.
/// <para>
/// Effective principal resolution:
/// <list type="number">
///   <item><description>Manual run: <see cref="WorkflowExecution.StartedByUserId"/>.</description></item>
///   <item><description>Trigger-driven: parent workflow's stable
///   <see cref="Workflow.PublishedByUserId"/>. Routine metadata mutations cannot lend
///   a different user's privileges to a scheduled run.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class SubWorkflowAuthorizationResolver : ISubWorkflowAuthorizationResolver
{
    private readonly NodePilotDbContext _db;
    private readonly AuthenticationPolicyOptions _authenticationPolicy;
    private readonly ExternalAuthorizationEvaluator? _externalAuthorization;

    public SubWorkflowAuthorizationResolver(
        NodePilotDbContext db,
        IOptions<AuthenticationPolicyOptions>? authenticationPolicy = null,
        ExternalAuthorizationEvaluator? externalAuthorization = null)
    {
        _db = db;
        _authenticationPolicy = authenticationPolicy?.Value ?? new AuthenticationPolicyOptions();
        _externalAuthorization = externalAuthorization;
    }

    public async Task<string?> IsBlockedAsync(WorkflowExecution parentExecution, Workflow childWorkflow, CancellationToken ct)
    {
        // Same-folder calls bypass the check — a workflow that fires a sibling in its own
        // folder is not crossing a permission boundary.
        if (parentExecution is null) return null;
        var parentWorkflow = await _db.Workflows.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == parentExecution.WorkflowId, ct);
        if (parentWorkflow is null) return null;
        var sameFolder = parentWorkflow.FolderId == childWorkflow.FolderId;

        // Resolve the effective principal user.
        var effectiveUserId = parentExecution.StartedByUserId
            ?? parentWorkflow.PublishedByUserId;
        if (effectiveUserId is null)
            return $"sub-workflow call to '{childWorkflow.Name}' in a different folder requires an effective principal — none could be resolved (no StartedByUserId on the run and no PublishedByUserId on the parent workflow; re-publish it)";

        // Apply the same account-state and global-role cap as the HTTP authorization
        // service. A retained folder grant must never outlive deactivation or a global
        // demotion, including for trigger-driven executions that have no live request.
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == effectiveUserId.Value, ct);
        if (user is null)
            return $"effective principal user not found for sub-workflow call to '{childWorkflow.Name}'";
        if (!user.IsActive || user.IsTombstoned)
            return $"effective principal '{user.Username}' is inactive — sub-workflow call to '{childWorkflow.Name}' denied";
        if (user.Provider != AuthProvider.Local)
        {
            var authorizationCurrent = _externalAuthorization is not null
                ? (await _externalAuthorization.EvaluateAsync(user, DateTime.UtcNow, ct)).IsCurrent
                : user.LastDirectorySyncAt is { } observedAt
                  && DateTime.UtcNow - observedAt <= TimeSpan.FromMinutes(Math.Clamp(
                      _authenticationPolicy.MaxAuthorizationStalenessMinutes, 1, 15));
            if (!authorizationCurrent)
            {
                return $"effective principal '{user.Username}' has a stale directory authorization snapshot; sub-workflow call to '{childWorkflow.Name}' denied";
            }
        }
        // Same-folder calls do not cross an RBAC boundary, but they still run under an
        // effective principal. Account deactivation and stale external authorization must
        // stop them just like cross-folder automation.
        if (sameFolder) return null;
        if (user.Role == UserRole.Admin) return null;
        if (user.Role != UserRole.Operator)
            return $"effective principal '{user.Username}' has global role '{user.Role}' — sub-workflow calls require global role 'Operator' or 'Admin'";

        // Walk the folder ancestry of the child to find the highest grant for this user.
        var allFolders = await _db.SharedWorkflowFolders.AsNoTracking().ToListAsync(ct);
        var byId = allFolders.ToDictionary(f => f.Id);
        var chain = new List<Guid>();
        var current = childWorkflow.FolderId;
        var depth = 0;
        while (depth <= SharedWorkflowFolder.MaxDepth + 1 && byId.TryGetValue(current, out var folder))
        {
            chain.Add(folder.Id);
            if (folder.ParentFolderId is null) break;
            current = folder.ParentFolderId.Value;
            depth++;
        }
        if (chain.Count == 0) return $"sub-workflow folder chain unresolvable for '{childWorkflow.Name}'";

        var userKey = effectiveUserId.Value.ToString("D");
        // Group-aware grant lookup uses the same normalized, server-side snapshot as HTTP
        // authorization. Scheduled/triggered work must never depend on group claims from an
        // old browser token or the legacy JSON cache on User.
        var directoryGroups = await DirectoryGroupPrincipal.LoadAsync(_db, user, ct);
        var groupKeys = directoryGroups.Select(group => group.GroupKey).Distinct().ToList();
        var groupAuthorities = directoryGroups.Select(group => group.Authority).Distinct().ToList();
        if (groupAuthorities.Contains(ExternalIdentity.ActiveDirectoryAuthority, StringComparer.Ordinal))
            groupAuthorities.Add(string.Empty);
        var candidateGrants = await _db.SharedFolderPermissions.AsNoTracking()
            .Where(p => chain.Contains(p.FolderId)
                     && ((p.PrincipalType == FolderPrincipalType.User && p.PrincipalKey == userKey)
                         || (p.PrincipalType == FolderPrincipalType.Group
                             && groupKeys.Contains(p.PrincipalKey)
                             && groupAuthorities.Contains(p.PrincipalAuthority))))
            .ToListAsync(ct);
        var grants = candidateGrants
            .Where(permission => permission.PrincipalType == FolderPrincipalType.User
                              || directoryGroups.Any(group => group.Matches(permission)))
            .Select(permission => permission.Role)
            .ToList();
        if (grants.Count == 0)
            return $"effective principal '{user.Username}' has no folder-permission grant on the chain leading to '{childWorkflow.Name}' — sub-workflow call denied";

        var highest = grants.Max();
        if (highest >= SharedFolderRole.FolderOperator) return null;

        return $"effective principal '{user.Username}' has only '{highest}' on '{childWorkflow.Name}'s folder chain — sub-workflow call requires at least 'FolderOperator'";
    }

}
