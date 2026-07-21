using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.Enums;
using NodePilot.Core.WorkflowDefinitions;
using NodePilot.Scheduler.Gauge;
using NodePilot.Scheduler.Notifications;
using Xunit;

namespace NodePilot.Engine.Tests.Notifications;

// (The gauge provider↔event-type metadata tests were removed along with the legacy gauge
// notification path, when infra/signal alerts were rearchitected into modular "system alert
// sources" (tracked as ADR 0008). The scheduled-workflow cron helper below survives; it now
// feeds the ScheduleMissed/NoRecentSuccess sources.)

/// <summary>
/// Pure JSON-shape extraction of a schedule trigger's cron expression. Feeds the
/// ScheduleMissed / NoRecentSuccess gauges — a mis-parse here means those alerts never fire.
/// Cover every guarded branch: non-object config, missing property, non-string value, blank.
/// </summary>
public class ScheduledWorkflowSignalHelpersTests
{
    private static WorkflowTriggerDescriptor Desc(JsonElement config) =>
        new("node-1", "scheduleTrigger", config, "hash", false, false);

    [Fact]
    public void CronExpression_ObjectWithStringCron_ReturnsValue()
    {
        var d = Desc(JsonSerializer.SerializeToElement(new { cronExpression = "0 0 * * ?" }));
        ScheduledWorkflowSignalHelpers.CronExpression(d).Should().Be("0 0 * * ?");
    }

    [Fact]
    public void CronExpression_NonObjectConfig_ReturnsNull()
    {
        var d = Desc(JsonSerializer.SerializeToElement("i am a scalar, not an object"));
        ScheduledWorkflowSignalHelpers.CronExpression(d).Should().BeNull();
    }

    [Fact]
    public void CronExpression_ObjectMissingCronProperty_ReturnsNull()
    {
        var d = Desc(JsonSerializer.SerializeToElement(new { somethingElse = 5 }));
        ScheduledWorkflowSignalHelpers.CronExpression(d).Should().BeNull();
    }

    [Fact]
    public void CronExpression_CronPropertyNotString_ReturnsNull()
    {
        var d = Desc(JsonSerializer.SerializeToElement(new { cronExpression = 123 }));
        ScheduledWorkflowSignalHelpers.CronExpression(d).Should().BeNull();
    }

    [Fact]
    public void CronExpression_BlankCron_ReturnsNull()
    {
        var d = Desc(JsonSerializer.SerializeToElement(new { cronExpression = "   " }));
        ScheduledWorkflowSignalHelpers.CronExpression(d).Should().BeNull();
    }
}

/// <summary>
/// The credential-failure classifier decides whether a failed execution also emits a
/// <c>CredentialFailure</c> alert. A false positive spams operators; a false negative hides
/// a real auth outage. Pin the gate (must be Failed + non-empty error) and the needle match.
/// </summary>
public class ExecutionEventSupportTests
{
    private static ExecRow Row(ExecutionStatus status, string? error) =>
        new(
            Id: Guid.NewGuid(),
            WorkflowId: Guid.NewGuid(),
            Status: status,
            StartedAt: DateTime.UtcNow.AddMinutes(-5),
            CompletedAt: DateTime.UtcNow,
            TriggeredBy: "schedule",
            ErrorMessage: error,
            ParentExecutionId: null,
            WorkflowName: "Nightly Backup",
            FolderId: Guid.NewGuid(),
            FolderPath: "/ops",
            CancelledBy: null,
            TargetMachine: null);

    [Theory]
    [InlineData("Kerberos authentication failed")]
    [InlineData("Access denied opening the runspace")]
    [InlineData("Server returned HTTP 401 Unauthorized")]
    [InlineData("Invalid password for the service account")]
    [InlineData("NTLM logon failure")]
    public void LooksLikeCredentialFailure_FailedWithCredentialNeedle_ReturnsTrue(string error)
    {
        ExecutionEventSupport.LooksLikeCredentialFailure(Row(ExecutionStatus.Failed, error))
            .Should().BeTrue();
    }

    [Fact]
    public void LooksLikeCredentialFailure_NonFailedStatus_ReturnsFalse()
    {
        // Even if the message looks credential-shaped, a non-Failed run never counts.
        ExecutionEventSupport.LooksLikeCredentialFailure(Row(ExecutionStatus.Succeeded, "kerberos"))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LooksLikeCredentialFailure_FailedButBlankError_ReturnsFalse(string? error)
    {
        ExecutionEventSupport.LooksLikeCredentialFailure(Row(ExecutionStatus.Failed, error))
            .Should().BeFalse();
    }

    [Fact]
    public void LooksLikeCredentialFailure_FailedNonCredentialError_ReturnsFalse()
    {
        ExecutionEventSupport.LooksLikeCredentialFailure(Row(ExecutionStatus.Failed, "Disk C: is full"))
            .Should().BeFalse();
    }

    [Fact]
    public void BuildCredentialFailureContext_ProjectsWarningLevelCredentialEvent()
    {
        var row = Row(ExecutionStatus.Failed, "login failed");
        var ctx = ExecutionEventSupport.BuildCredentialFailureContext(row);

        ctx.EventType.Should().Be(NotificationEventType.CredentialFailure);
        ctx.Severity.Should().Be(NotificationSeverity.Warning);
        ctx.ExecutionId.Should().Be(row.Id);
        ctx.WorkflowName.Should().Be("Nightly Backup");
        ctx.EventKey.Should().Contain(row.Id.ToString("N"));
    }

    [Theory]
    [InlineData(ExecutionStatus.Succeeded, NotificationEventType.ExecutionSucceeded, NotificationSeverity.Info)]
    [InlineData(ExecutionStatus.Cancelled, NotificationEventType.ExecutionCancelled, NotificationSeverity.Info)]
    [InlineData(ExecutionStatus.Failed, NotificationEventType.ExecutionFailed, NotificationSeverity.Warning)]
    public void BuildContext_MapsStatusToEventTypeAndSeverity(
        ExecutionStatus status, NotificationEventType expectedType, NotificationSeverity expectedSeverity)
    {
        var ctx = ExecutionEventSupport.BuildContext(Row(status, status == ExecutionStatus.Failed ? "boom" : null));
        ctx.EventType.Should().Be(expectedType);
        ctx.Severity.Should().Be(expectedSeverity);
    }
}
