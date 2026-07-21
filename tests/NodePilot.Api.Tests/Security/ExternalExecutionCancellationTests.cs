using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NodePilot.Api.Security;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Security;

public sealed class ExternalExecutionCancellationTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly NodePilotDbContext _db;

    public ExternalExecutionCancellationTests()
        => (_connection, _db) = TestDbFactory.CreateWithConnection();

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task CancelAsync_DurableFirst_AndPostCommitSignalRetriesMissedEngineToken()
    {
        var user = new User
        {
            Id = Guid.NewGuid(), Username = "offboard@example.test", Provider = AuthProvider.Ldap,
            Role = UserRole.Operator, IsActive = false,
        };
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "offboarding", DefinitionJson = "{}" };
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, StartedByUserId = user.Id,
            Status = ExecutionStatus.Pending, StartedAt = DateTime.UtcNow,
            TriggeredBy = "scheduleTrigger",
        };
        _db.AddRange(user, workflow, execution);
        await _db.SaveChangesAsync();

        var signalCount = 0;
        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(candidate => candidate.CancelAsync(
                execution.Id, "directory-offboarding", It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                signalCount++;
                await Task.Yield();
                return signalCount > 1;
            });

        var now = DateTime.UtcNow;
        var cancelled = await ExternalExecutionCancellation.CancelAsync(
            _db,
            [user.Id],
            now,
            "directory-offboarding",
            "Execution cancelled because its directory principal was offboarded.",
            CancellationToken.None);

        cancelled.Should().Equal(execution.Id);
        signalCount.Should().Be(0,
            "durable cancellation must not touch in-memory work before the caller commits");
        _db.ChangeTracker.Clear();
        (await _db.WorkflowExecutions.SingleAsync(candidate => candidate.Id == execution.Id))
            .Should().Match<WorkflowExecution>(candidate =>
                candidate.Status == ExecutionStatus.Cancelled
                && candidate.CancelledBy == "directory-offboarding"
                && candidate.CompletedAt == now);

        await ExternalExecutionCancellation.SignalAfterCommitAsync(
            engine.Object,
            cancelled,
            "directory-offboarding",
            CancellationToken.None);
        signalCount.Should().Be(2,
            "a token registration racing the first lookup gets one bounded post-commit retry");
    }

    [Fact]
    public async Task CancelAsync_StaleLeaderEpoch_CannotCancelNewLeadersExecution()
    {
        var user = new User
        {
            Id = Guid.NewGuid(), Username = "stale-sweep@example.test", Provider = AuthProvider.Ldap,
            Role = UserRole.Operator, IsActive = false,
        };
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "new leader", DefinitionJson = "{}" };
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, StartedByUserId = user.Id,
            Status = ExecutionStatus.Running, StartedAt = DateTime.UtcNow,
            OwnerNodeId = "node-b",
        };
        var now = DateTime.UtcNow;
        _db.AddRange(user, workflow, execution, new ClusterLeader
        {
            Resource = "primary", OwnerNodeId = "node-b", LeaseEpoch = 8,
            AcquiredAt = now, LastRenewedAt = now, ExpiresAt = now.AddMinutes(1),
        });
        await _db.SaveChangesAsync();

        var staleAttempt = await ExternalExecutionCancellation.CancelAsync(
            _db,
            [user.Id],
            now,
            "authorization-stale",
            "Execution cancelled because its external authorization snapshot expired.",
            CancellationToken.None,
            expectedLeaderNodeId: "node-a",
            expectedLeaseEpoch: 7);

        staleAttempt.Should().BeEmpty();
        _db.ChangeTracker.Clear();
        (await _db.WorkflowExecutions.SingleAsync(candidate => candidate.Id == execution.Id)).Status
            .Should().Be(ExecutionStatus.Running,
                "the epoch/owner check is part of the cancellation UPDATE itself");
    }
}
