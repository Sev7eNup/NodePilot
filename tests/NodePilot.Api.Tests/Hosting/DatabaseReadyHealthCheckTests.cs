using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Hosting;
using NodePilot.Api.HealthChecks;
using NodePilot.Data;
using NodePilot.Data.Availability;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

public sealed class DatabaseReadyHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_Armed_ReturnsUnhealthyWithoutTouchingDatabase()
    {
        var availability = new DatabaseAvailabilityTracker(
            NullLogger<DatabaseAvailabilityTracker>.Instance);
        availability.MarkBootComplete();
        availability.Arm();
        await using var db = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>().Options);
        var check = new DatabaseReadyHealthCheck(
            availability,
            db,
            new DatabaseAvailabilityOptions());

        var result = await check.CheckHealthAsync(
            new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("adjudicat");
        result.Exception.Should().BeNull("Armed readiness is answered from memory");
    }
}
