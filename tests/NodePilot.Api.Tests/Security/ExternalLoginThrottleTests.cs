using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Security;

public sealed class ExternalLoginThrottleTests
{
    [Fact]
    public async Task SixthReservation_IsBlockedAcrossThrottleInstances()
    {
        var (connection, db) = TestDbFactory.CreateWithConnection();
        await using var db2 = new NodePilot.Data.NodePilotDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<NodePilot.Data.NodePilotDbContext>()
                .UseSqlite(connection)
                .Options);
        var firstNode = new ExternalLoginThrottle(db);
        var secondNode = new ExternalLoginThrottle(db2);
        var now = DateTime.UtcNow;

        for (var i = 0; i < 5; i++)
        {
            var reservation = await (i % 2 == 0 ? firstNode : secondNode)
                .TryReserveAsync("alice@example.test", now.AddSeconds(i));
            reservation.IsAllowed.Should().BeTrue();
        }

        var blocked = await secondNode.TryReserveAsync("ALICE@example.test", now.AddMinutes(1));
        blocked.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Success_ClearsFailureWindow()
    {
        var db = TestDbFactory.Create();
        var throttle = new ExternalLoginThrottle(db);
        var now = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
            (await throttle.TryReserveAsync("alice", now)).IsAllowed.Should().BeTrue();

        await throttle.RecordSuccessAsync("alice");

        (await throttle.TryReserveAsync("alice", now)).IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task ExpiredWindow_DoesNotRemainBlocked()
    {
        var db = TestDbFactory.Create();
        var throttle = new ExternalLoginThrottle(db);
        var now = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
            await throttle.TryReserveAsync("alice", now);

        var afterExpiry = await throttle.TryReserveAsync("alice", now.AddMinutes(31));

        afterExpiry.IsAllowed.Should().BeTrue();
        (await throttle.TrackedAttemptCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task InfrastructureFailureRelease_DoesNotConsumeSlot()
    {
        var db = TestDbFactory.Create();
        var throttle = new ExternalLoginThrottle(db);
        var reservation = await throttle.TryReserveAsync("alice", DateTime.UtcNow);

        await throttle.ReleaseAsync(reservation);

        (await throttle.TrackedAttemptCountAsync()).Should().Be(0);
    }

    [Fact]
    public void IdentityPrefix_BoundsInputBeforeNormalization()
    {
        var prefix = new string('a', ExternalLoginThrottle.MaximumUsernameLength);

        ExternalLoginThrottle.BuildIdentityPrefix(prefix + new string('z', 10_000))
            .Should().Be(ExternalLoginThrottle.BuildIdentityPrefix(prefix));
    }
}
