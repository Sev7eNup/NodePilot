using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NodePilot.Data;
using Xunit;

namespace NodePilot.Data.Tests;

public class WebhookReplayStoreTests
{
    [Fact]
    public async Task TryClaimAsync_SeparateContextsContendOnDatabaseUniqueIndex()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var firstDb = new NodePilotDbContext(options);
        await firstDb.Database.EnsureCreatedAsync();
        await using var secondDb = new NodePilotDbContext(options);

        var workflowId = Guid.NewGuid();
        const string deliveryId = "4bccd724-0317-4115-b6c6-4f777dac6a8a";
        var claimToken = WebhookReplayStore.CreateClaimToken(new byte[32], deliveryId);
        var now = DateTime.UtcNow;

        var first = await new WebhookReplayStore(firstDb)
            .TryClaimAsync(workflowId, claimToken, now, CancellationToken.None);
        var second = await new WebhookReplayStore(secondDb)
            .TryClaimAsync(workflowId, claimToken, now, CancellationToken.None);

        first.Should().BeTrue();
        second.Should().BeFalse("the unique constraint is shared by every API/HA node");
        (await secondDb.IdempotencyKeys.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task TryClaimAsync_StoresOnlyDigestAndAllowsSameDeliveryForDifferentWorkflow()
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        const string deliveryId = "partner-ticket-20260713-000001";
        var claimToken = WebhookReplayStore.CreateClaimToken(new byte[32], deliveryId);
        var now = DateTime.UtcNow;

        (await new WebhookReplayStore(db).TryClaimAsync(
            Guid.NewGuid(), claimToken, now, CancellationToken.None)).Should().BeTrue();
        (await new WebhookReplayStore(db).TryClaimAsync(
            Guid.NewGuid(), claimToken, now, CancellationToken.None)).Should().BeTrue();

        var rows = await db.IdempotencyKeys.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(x => x.Key.StartsWith("webhook-replay:v1:", StringComparison.Ordinal));
        rows.Should().OnlyContain(x => !x.Key.Contains(deliveryId, StringComparison.Ordinal));
        rows.Should().OnlyContain(x => x.ExecutionId == Guid.Empty);
        rows.Should().OnlyContain(x => x.ExpiresAt > now.AddMinutes(10));
    }
}
