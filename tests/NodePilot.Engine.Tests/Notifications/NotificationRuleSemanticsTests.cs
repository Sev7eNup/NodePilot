using FluentAssertions;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Engine.Notifications;
using Xunit;

namespace NodePilot.Engine.Tests.Notifications;

/// <summary>
/// F8: <see cref="NotificationRuleTarget"/>.<c>TargetId</c> is a soft reference (no FK) to a
/// folder/workflow, mirroring <see cref="MaintenanceWindowTarget"/>. <see cref="NotificationRuleSemantics.ScopeMatches"/>
/// is a pure in-memory id matcher, so a target pointing at a deleted folder/workflow must simply be
/// inert — it can never match a different, live folder/workflow. These tests pin that no-op down
/// directly (the store cannot dangle a target through code, so the guarantee is asserted at the matcher).
/// </summary>
public class NotificationRuleSemanticsTests
{
    private static NotificationRule Rule(NotificationScopeKind scope, params NotificationRuleTarget[] targets) => new()
    {
        Id = Guid.NewGuid(),
        Name = "r",
        EventTypes = "ExecutionFailed",
        ScopeKind = scope,
        Targets = targets.ToList(),
    };

    private static NotificationContext Ctx(Guid? workflowId = null, Guid? folderId = null) => new(
        EventType: NotificationEventType.ExecutionFailed,
        Severity: NotificationSeverity.Warning,
        EventKey: "evt-1",
        WorkflowId: workflowId,
        WorkflowName: null,
        FolderId: folderId,
        FolderPath: null,
        ExecutionId: null,
        Status: null,
        ErrorMessage: null,
        DurationMs: null,
        OccurredAt: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        TriggeredBy: null,
        CallDepth: 0,
        IsSubWorkflow: false,
        TargetMachine: null,
        SourceKey: null,
        Title: null,
        Summary: null,
        DeepLinkPath: null);

    [Fact]
    public void WorkflowScope_DanglingTarget_DoesNotMatchLiveWorkflow()
    {
        var deletedWorkflow = Guid.NewGuid();
        var rule = Rule(NotificationScopeKind.Workflows,
            new NotificationRuleTarget { Id = Guid.NewGuid(), TargetKind = NotificationTargetKind.Workflow, TargetId = deletedWorkflow });

        // A target pointing at a deleted workflow must never match a different, live workflow.
        NotificationRuleSemantics.ScopeMatches(rule, Ctx(workflowId: Guid.NewGuid())).Should().BeFalse();

        // Sanity: it DOES match its own id (pure id matcher), so the no-op above is a real
        // non-match, not a matcher that always returns false.
        NotificationRuleSemantics.ScopeMatches(rule, Ctx(workflowId: deletedWorkflow)).Should().BeTrue();
    }

    [Fact]
    public void FolderScope_DanglingTarget_DoesNotMatchLiveFolder()
    {
        var deletedFolder = Guid.NewGuid();
        var rule = Rule(NotificationScopeKind.Folders,
            new NotificationRuleTarget { Id = Guid.NewGuid(), TargetKind = NotificationTargetKind.Folder, TargetId = deletedFolder });

        NotificationRuleSemantics.ScopeMatches(rule, Ctx(folderId: Guid.NewGuid())).Should().BeFalse();
        NotificationRuleSemantics.ScopeMatches(rule, Ctx(folderId: deletedFolder)).Should().BeTrue();
    }

    [Fact]
    public void Scope_NullContextIds_NeverMatchScopedRule()
    {
        // A scoped rule with no matching context id (e.g. a system event carrying no workflow/folder)
        // is inert regardless of its targets.
        var rule = Rule(NotificationScopeKind.Workflows,
            new NotificationRuleTarget { Id = Guid.NewGuid(), TargetKind = NotificationTargetKind.Workflow, TargetId = Guid.NewGuid() });

        NotificationRuleSemantics.ScopeMatches(rule, Ctx(workflowId: null)).Should().BeFalse();
    }
}
