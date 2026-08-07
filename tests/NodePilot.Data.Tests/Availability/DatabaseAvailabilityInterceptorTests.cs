using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Data.Availability;
using Xunit;

namespace NodePilot.Data.Tests.Availability;

public sealed class DatabaseAvailabilityInterceptorTests
{
    [Fact]
    public void ConnectionOpenFailure_UnknownShape_ArmsProbeWithoutOpeningBreaker()
    {
        var availability = new DatabaseAvailabilityTracker(
            NullLogger<DatabaseAvailabilityTracker>.Instance);
        availability.MarkBootComplete();
        var interceptor = new DatabaseConnectionAvailabilityInterceptor(availability);

        interceptor.Report(new InvalidOperationException("provider-specific open failure"));

        availability.State.Should().Be(DatabaseAvailabilityState.Armed);
        availability.IsServable.Should().BeTrue();
    }
}
